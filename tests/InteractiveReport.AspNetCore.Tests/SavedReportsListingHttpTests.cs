using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The built-in __saved-reports listing end to end: administrator gating, both row
/// origins with their action labels, hidden action keys, CSV behavior, and the
/// fresh-store readiness path (the first request creates and syncs the table).
/// </summary>
public sealed class SavedReportsListingHttpTests : IAsyncLifetime
{
    private const string Admin = "listing-admin";
    private const string User = "listing-user";
    private string _tempRoot = "";
    private WebApplication? _app;
    private HttpClient _client = null!;
    private long _ordersId;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-listing-").FullName;
        var documentDirectory = Path.Combine(_tempRoot, "ReportDocuments");
        Directory.CreateDirectory(documentDirectory);
        await File.WriteAllTextAsync(Path.Combine(documentDirectory, "orders.regional.json"), """
            {
              "title": "Regional View",
              "state": {
                "activeTable": "regional",
                "tables": {
                  "regional": {
                    "from": "definition",
                    "composables": [ { "kind": "select", "columns": [ "ID", "LABEL" ] } ]
                  }
                }
              }
            }
            """);

        var dataPath = Path.Combine(_tempRoot, "data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ORDERS (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);
                INSERT INTO ORDERS (ID, LABEL) VALUES (1, 'first');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InteractiveReport:Administrators:0"] = Admin,
            ["InteractiveReport:Reports:orders:Connection"] = "Data",
            ["InteractiveReport:Reports:orders:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:orders:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:orders:DocumentFiles:0"] = "ReportDocuments/orders.regional.json",
            ["InteractiveReport:SavedReports:Connection"] = "Data",
        });
        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                && !string.IsNullOrEmpty(identity))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, identity!)],
                    authenticationType: "ListingTest"));
            }
            await next();
        });
        _app.MapInteractiveReports("/api/reports");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
        _ordersId = await ReportDocumentTestIds.Default(_app.Services, "orders");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string? identity, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Listing_is_administrators_only_with_non_disclosure()
    {
        using var anonymous = await _client.SendAsync(
            Request(HttpMethod.Get,
                $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
                identity: null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var nonAdmin = await _client.SendAsync(
            Request(HttpMethod.Get,
                $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
                User));
        Assert.Equal(HttpStatusCode.NotFound, nonAdmin.StatusCode);

        using var nonAdminQuery = await _client.SendAsync(
            Request(HttpMethod.Post,
                $"/api/reports/{SavedReportsListingDefinition.Name}/query",
                User,
                new { v = 3 }));
        Assert.Equal(HttpStatusCode.NotFound, nonAdminQuery.StatusCode);

        using var admin = await _client.SendAsync(
            Request(HttpMethod.Get,
                $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
                Admin));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var schema = await ReadJson(admin);
        Assert.Equal("Saved Reports", schema.GetProperty("title").GetString());
        var state = schema.GetProperty("defaultState");
        Assert.Equal("action", state.GetProperty("tables")
            .GetProperty(state.GetProperty("activeTable").GetString()!)
            .GetProperty("composables").EnumerateArray()
            .Single(c => c.GetProperty("kind").GetString() == "formats")
            .GetProperty("formats").GetProperty("ACTION_PUBLISH")
            .GetProperty("displayAs").GetString());
    }

    [Fact]
    public async Task Listing_serves_both_origins_with_action_labels_and_hidden_keys()
    {
        // First-ever request on a fresh content root: the saved-report table does not
        // exist yet — resolution must sync (and thereby create) it before discovery.
        using var schemaResponse = await _client.SendAsync(
            Request(HttpMethod.Get,
                $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
                Admin));
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        var state = (await ReadJson(schemaResponse)).GetProperty("defaultState");

        var first = await Query(state);
        var configured = first.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("TITLE").GetString() == "Regional View");
        Assert.Equal("Regional View", configured.GetProperty("TITLE").GetString());
        Assert.Equal("Read only", configured.GetProperty("SCOPE").GetString());
        Assert.Equal(JsonValueKind.Null, configured.GetProperty("ACTION_PUBLISH").ValueKind);
        Assert.Equal("Make primary", configured.GetProperty("ACTION_PRIMARY").GetString());
        Assert.Equal(JsonValueKind.Null, configured.GetProperty("ACTION_REASSIGN").ValueKind);
        Assert.Equal(JsonValueKind.Null, configured.GetProperty("ACTION_DELETE").ValueKind);
        Assert.Equal("State", configured.GetProperty("ACTION_STATE").GetString());
        Assert.Equal("Download", configured.GetProperty("ACTION_DOWNLOAD").GetString());
        var configuredId = configured.GetProperty("ID").GetString()!;
        Assert.True(long.TryParse(configuredId, out var configuredNumericId));
        Assert.True(configuredNumericId > 0);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$"),
            configured.GetProperty("MODIFIED").GetString()!);
        Assert.DoesNotContain(
            first.GetProperty("columns").EnumerateArray(),
            column => column.GetProperty("name").GetString() == "ID");

        using var save = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", Admin,
            new { title = "Mine", isGlobal = true, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var savedId = (await ReadJson(save)).GetProperty("id").GetInt64();

        var second = await Query(state);
        var user = second.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("TITLE").GetString() == "Mine");
        Assert.Equal("Global", user.GetProperty("SCOPE").GetString());
        Assert.Equal("No", user.GetProperty("PRIMARY_STATUS").GetString());
        Assert.Equal("Unpublish", user.GetProperty("ACTION_PUBLISH").GetString());
        Assert.Equal("Reassign", user.GetProperty("ACTION_REASSIGN").GetString());
        Assert.Equal("Delete", user.GetProperty("ACTION_DELETE").GetString());
        Assert.Equal(savedId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            user.GetProperty("ID").GetString());

        // The wrapper's toggleGlobal contract: PUT with the id the action row carried.
        using var unpublish = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{savedId}", Admin, new { isGlobal = false }));
        Assert.Equal(HttpStatusCode.OK, unpublish.StatusCode);

        var third = await Query(state);
        var republished = third.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("TITLE").GetString() == "Mine");
        Assert.Equal("Private", republished.GetProperty("SCOPE").GetString());
        Assert.Equal("Publish", republished.GetProperty("ACTION_PUBLISH").GetString());

        using var makePrimary = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{savedId}", Admin, new { isPrimary = true }));
        Assert.Equal(HttpStatusCode.OK, makePrimary.StatusCode);
        var fourth = await Query(state);
        var primary = fourth.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("TITLE").GetString() == "Mine");
        Assert.Equal("Yes", primary.GetProperty("PRIMARY_STATUS").GetString());
        Assert.Equal("Unflag", primary.GetProperty("ACTION_PRIMARY").GetString());

        using var mutateConfigured = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{configuredId}", Admin, new { title = "Takeover" }));
        Assert.Equal(HttpStatusCode.Forbidden, mutateConfigured.StatusCode);

        using var flagConfigured = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{configuredId}", Admin, new { isPrimary = true }));
        Assert.Equal(HttpStatusCode.OK, flagConfigured.StatusCode);
    }

    [Fact]
    public async Task Primary_reports_are_visible_to_dataset_users_and_only_admins_can_unflag_them()
    {
        using var save = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", Admin,
            new { title = "Executive", isPrimary = true, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetInt64();

        using var visibleResponse = await _client.SendAsync(Request(
            HttpMethod.Get, $"/api/reports/{_ordersId}/saved", User));
        Assert.Equal(HttpStatusCode.OK, visibleResponse.StatusCode);
        var visible = await ReadJson(visibleResponse);
        Assert.Contains(visible.EnumerateArray(), report =>
            report.GetProperty("id").GetInt64() == id
            && report.GetProperty("isPrimary").GetBoolean());

        using var denied = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{id}", User, new { isPrimary = false }));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using var unflag = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{id}", Admin, new { isPrimary = false }));
        Assert.Equal(HttpStatusCode.OK, unflag.StatusCode);

        using var hiddenResponse = await _client.SendAsync(Request(
            HttpMethod.Get, $"/api/reports/{_ordersId}/saved", User));
        var hidden = await ReadJson(hiddenResponse);
        Assert.DoesNotContain(hidden.EnumerateArray(), report => report.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task Owner_can_update_and_delete_their_published_report_without_changing_publication()
    {
        using var save = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", User,
            new { title = "Owned", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetInt64();

        using var publish = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{id}", Admin,
            new { isGlobal = true, isPrimary = true }));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        using var update = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{id}", User,
            new { title = "Owned Updated", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await ReadJson(update);
        Assert.True(updated.GetProperty("isGlobal").GetBoolean());
        Assert.True(updated.GetProperty("isPrimary").GetBoolean());

        using var delete = await _client.SendAsync(Request(
            HttpMethod.Delete, $"/api/reports/{id}", User));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Invalid_synthetic_default_is_rebuilt_in_place_from_current_configuration()
    {
        var store = _app!.Services.GetRequiredService<InteractiveReport.Core.SavedReports.ISavedReportStore>();
        var current = await store.Get(_ordersId);
        Assert.NotNull(current);
        Assert.True(current.IsDefault);
        Assert.Null(current.SourceFile);

        var invalid = current with
        {
            StateJson = "{not valid json",
        };
        Assert.True(await store.Update(invalid, current));

        using var response = await _client.SendAsync(Request(
            HttpMethod.Get, $"/api/reports/{_ordersId}", Admin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var loaded = await ReadJson(response);
        Assert.Equal(_ordersId, loaded.GetProperty("summary").GetProperty("id").GetInt64());
        var state = loaded.GetProperty("state");
        var activeTable = state.GetProperty("activeTable").GetString();
        Assert.False(string.IsNullOrWhiteSpace(activeTable));
        Assert.True(state.GetProperty("tables").TryGetProperty(activeTable!, out _));

        var repaired = await store.Get(_ordersId);
        Assert.NotNull(repaired);
        Assert.Equal(_ordersId, repaired.Id);
        Assert.NotEqual(invalid.StateJson, repaired.StateJson);
    }

    [Fact]
    public async Task Visible_title_scopes_allow_publication_after_private_duplicates_without_stranding_them()
    {
        const string otherUser = "listing-other-user";
        const string title = "Shared Name";

        using var firstPrivate = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", User,
            new { title, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, firstPrivate.StatusCode);
        var firstId = (await ReadJson(firstPrivate)).GetProperty("id").GetInt64();

        using var secondPrivate = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", otherUser,
            new { title = title.ToUpperInvariant(), state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, secondPrivate.StatusCode);

        using var publishLater = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", Admin,
            new { title = title.ToLowerInvariant(), isGlobal = true, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, publishLater.StatusCode);
        var publicId = (await ReadJson(publishLater)).GetProperty("id").GetInt64();

        using var unchangedPrivateUpdate = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{firstId}", User,
            new { title = title.ToUpperInvariant(), state = new { v = 3, search = "updated" } }));
        Assert.Equal(HttpStatusCode.OK, unchangedPrivateUpdate.StatusCode);

        using var duplicatePrivate = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", User,
            new { title, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Conflict, duplicatePrivate.StatusCode);

        using var visibleResponse = await _client.SendAsync(Request(
            HttpMethod.Get, $"/api/reports/{_ordersId}/saved", User));
        Assert.Equal(HttpStatusCode.OK, visibleResponse.StatusCode);
        var visible = await ReadJson(visibleResponse);
        var matches = visible.EnumerateArray()
            .Where(report => string.Equals(
                report.GetProperty("title").GetString(), title, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(2, matches.Length);
        Assert.Contains(matches, report => report.GetProperty("id").GetInt64() == firstId
            && report.GetProperty("mine").GetBoolean());
        Assert.Contains(matches, report => report.GetProperty("id").GetInt64() == publicId
            && report.GetProperty("isGlobal").GetBoolean());
    }

    [Fact]
    public async Task Processing_uses_the_client_copy_after_document_access_changes()
    {
        using var save = await _client.SendAsync(Request(
            HttpMethod.Post, $"/api/reports/{_ordersId}/saved", User,
            new { title = "Client copy", state = new { v = 3, search = "first" } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetInt64();

        using var retrieve = await _client.SendAsync(Request(
            HttpMethod.Get, $"/api/reports/{id}", User));
        Assert.Equal(HttpStatusCode.OK, retrieve.StatusCode);
        var clientCopy = (await ReadJson(retrieve)).GetProperty("state").Clone();

        using var reassign = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/{id}", Admin,
            new { owner = "listing-new-owner" }));
        Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);

        using var hiddenOriginal = await _client.SendAsync(Request(
            HttpMethod.Get, $"/api/reports/{id}", User));
        Assert.Equal(HttpStatusCode.NotFound, hiddenOriginal.StatusCode);

        using var query = await _client.SendAsync(Request(
            HttpMethod.Post, "/api/reports/orders/query", User, clientCopy));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var result = await ReadJson(query);
        Assert.Equal("first", result.GetProperty("document").GetProperty("search").GetString());
    }

    [Fact]
    public async Task Listing_exports_action_labels_as_plain_csv()
    {
        using var schemaResponse = await _client.SendAsync(
            Request(HttpMethod.Get,
                $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
                Admin));
        var state = (await ReadJson(schemaResponse)).GetProperty("defaultState");

        using var export = await _client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{SavedReportsListingDefinition.Name}/export?format=csv",
            Admin,
            state));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var csv = await export.Content.ReadAsStringAsync();

        var line = csv.Split('\n').Select(l => l.TrimEnd('\r'))
            .Single(l => l.Contains("Regional View"));
        // Plain labels, never HTML; NULL labels are empty fields; no owner.
        Assert.Contains("Regional View,,Read only,", line);
        Assert.Contains(",State,Download,", line);
        Assert.DoesNotContain("Delete", line);
        Assert.DoesNotContain("<", line);
    }

    private async Task<JsonElement> Query(JsonElement state)
    {
        using var response = await _client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{SavedReportsListingDefinition.Name}/query",
            Admin,
            state));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response);
    }
}
