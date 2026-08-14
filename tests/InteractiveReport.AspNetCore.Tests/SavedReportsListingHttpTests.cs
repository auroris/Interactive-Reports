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

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-listing-").FullName;
        var documentDirectory = Path.Combine(_tempRoot, "ReportDocuments");
        Directory.CreateDirectory(documentDirectory);
        await File.WriteAllTextAsync(Path.Combine(documentDirectory, "orders.regional.json"), """
            {
              "title": "Regional View",
              "state": {
                "v": 3,
                "pipeline": [ { "shape": { "kind": "source" }, "layer": { "columns": [ "ID", "LABEL" ] } } ]
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
            Request(HttpMethod.Get, "/api/reports/__saved-reports/schema", identity: null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var nonAdmin = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/reports/__saved-reports/schema", User));
        Assert.Equal(HttpStatusCode.NotFound, nonAdmin.StatusCode);

        using var nonAdminQuery = await _client.SendAsync(
            Request(HttpMethod.Post, "/api/reports/__saved-reports/query", User, new { v = 3 }));
        Assert.Equal(HttpStatusCode.NotFound, nonAdminQuery.StatusCode);

        using var admin = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/reports/__saved-reports/schema", Admin));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var schema = await ReadJson(admin);
        Assert.Equal("Saved Reports", schema.GetProperty("title").GetString());
        Assert.Equal("action", schema.GetProperty("defaultState").GetProperty("pipeline")[0]
            .GetProperty("layer").GetProperty("formats").GetProperty("ACTION_PUBLISH")
            .GetProperty("displayAs").GetString());
    }

    [Fact]
    public async Task Listing_serves_both_origins_with_action_labels_and_hidden_keys()
    {
        // First-ever request on a fresh content root: the saved-report table does not
        // exist yet — resolution must sync (and thereby create) it before discovery.
        using var schemaResponse = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/reports/__saved-reports/schema", Admin));
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        var state = (await ReadJson(schemaResponse)).GetProperty("defaultState");

        var first = await Query(state);
        var configured = Assert.Single(first.GetProperty("rows").EnumerateArray());
        Assert.Equal("Regional View", configured.GetProperty("TITLE").GetString());
        Assert.Equal("Read only", configured.GetProperty("SCOPE").GetString());
        Assert.Equal(JsonValueKind.Null, configured.GetProperty("ACTION_PUBLISH").ValueKind);
        Assert.Equal("Make primary", configured.GetProperty("ACTION_PRIMARY").GetString());
        Assert.Equal(JsonValueKind.Null, configured.GetProperty("ACTION_REASSIGN").ValueKind);
        Assert.Equal(JsonValueKind.Null, configured.GetProperty("ACTION_DELETE").ValueKind);
        Assert.Equal("State", configured.GetProperty("ACTION_STATE").GetString());
        Assert.Equal("Download", configured.GetProperty("ACTION_DOWNLOAD").GetString());
        var configuredId = configured.GetProperty("ID").GetString()!;
        Assert.StartsWith("cfg_", configuredId);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$"),
            configured.GetProperty("MODIFIED").GetString()!);
        Assert.DoesNotContain(
            first.GetProperty("columns").EnumerateArray(),
            column => column.GetProperty("name").GetString() == "ID");

        using var save = await _client.SendAsync(Request(
            HttpMethod.Post, "/api/reports/orders/saved", Admin,
            new { title = "Mine", isGlobal = true, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var savedId = (await ReadJson(save)).GetProperty("id").GetString()!;

        var second = await Query(state);
        var user = second.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("TITLE").GetString() == "Mine");
        Assert.Equal("Global", user.GetProperty("SCOPE").GetString());
        Assert.Equal("No", user.GetProperty("PRIMARY_STATUS").GetString());
        Assert.Equal("Unpublish", user.GetProperty("ACTION_PUBLISH").GetString());
        Assert.Equal("Reassign", user.GetProperty("ACTION_REASSIGN").GetString());
        Assert.Equal("Delete", user.GetProperty("ACTION_DELETE").GetString());
        Assert.Equal(savedId, user.GetProperty("ID").GetString());

        // The wrapper's toggleGlobal contract: PUT with the id the action row carried.
        using var unpublish = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/saved/{savedId}", Admin, new { isGlobal = false }));
        Assert.Equal(HttpStatusCode.OK, unpublish.StatusCode);

        var third = await Query(state);
        var republished = third.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("TITLE").GetString() == "Mine");
        Assert.Equal("Private", republished.GetProperty("SCOPE").GetString());
        Assert.Equal("Publish", republished.GetProperty("ACTION_PUBLISH").GetString());

        using var makePrimary = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/saved/{savedId}", Admin, new { isPrimary = true }));
        Assert.Equal(HttpStatusCode.OK, makePrimary.StatusCode);
        var fourth = await Query(state);
        var primary = fourth.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("TITLE").GetString() == "Mine");
        Assert.Equal("Yes", primary.GetProperty("PRIMARY_STATUS").GetString());
        Assert.Equal("Unflag", primary.GetProperty("ACTION_PRIMARY").GetString());

        using var mutateConfigured = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/saved/{configuredId}", Admin, new { title = "Takeover" }));
        Assert.Equal(HttpStatusCode.Forbidden, mutateConfigured.StatusCode);

        using var flagConfigured = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/saved/{configuredId}", Admin, new { isPrimary = true }));
        Assert.Equal(HttpStatusCode.OK, flagConfigured.StatusCode);
    }

    [Fact]
    public async Task Primary_reports_are_visible_to_dataset_users_and_only_admins_can_unflag_them()
    {
        using var save = await _client.SendAsync(Request(
            HttpMethod.Post, "/api/reports/orders/saved", Admin,
            new { title = "Executive", isPrimary = true, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetString()!;

        using var visibleResponse = await _client.SendAsync(Request(
            HttpMethod.Get, "/api/reports/orders/saved", User));
        Assert.Equal(HttpStatusCode.OK, visibleResponse.StatusCode);
        var visible = await ReadJson(visibleResponse);
        Assert.Contains(visible.EnumerateArray(), report =>
            report.GetProperty("id").GetString() == id
            && report.GetProperty("isPrimary").GetBoolean());

        using var denied = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/saved/{id}", User, new { isPrimary = false }));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using var unflag = await _client.SendAsync(Request(
            HttpMethod.Put, $"/api/reports/saved/{id}", Admin, new { isPrimary = false }));
        Assert.Equal(HttpStatusCode.OK, unflag.StatusCode);

        using var hiddenResponse = await _client.SendAsync(Request(
            HttpMethod.Get, "/api/reports/orders/saved", User));
        var hidden = await ReadJson(hiddenResponse);
        Assert.DoesNotContain(hidden.EnumerateArray(), report => report.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Listing_exports_action_labels_as_plain_csv()
    {
        using var schemaResponse = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/reports/__saved-reports/schema", Admin));
        var state = (await ReadJson(schemaResponse)).GetProperty("defaultState");

        using var export = await _client.SendAsync(Request(
            HttpMethod.Post, "/api/reports/__saved-reports/export?format=csv", Admin, state));
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
            HttpMethod.Post, "/api/reports/__saved-reports/query", Admin, state));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response);
    }
}
