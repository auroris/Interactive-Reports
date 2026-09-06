using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The report-level gate and the built-in administrator fallback: a public report admits anyone,
/// any other report needs an authenticated caller, an optional policy narrows further, and the
/// administrator list is the configured entries plus a database list set as a whole.
/// </summary>
public sealed class AuthorizationHttpTests : IAsyncLifetime
{
    private const string Administrators = "/api/reports/admin/administrators";

    private string _tempRoot = "";
    private string _connectionString = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-authorization-table-").FullName;
        _connectionString = $"Data Source={Path.Combine(_tempRoot, "reports.db")};Pooling=False";
        await using (var connection = new SqliteConnection(_connectionString))
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
            ["InteractiveReport:Administrators:0"] = "configured-admin",
            ["InteractiveReport:WhoamiEnabled"] = "true",
            ["InteractiveReport:SavedReports:Connection"] = "Data",
            ["InteractiveReport:SavedReports:TablePrefix"] = "TEST_",
            ["InteractiveReport:Reports:policy:Connection"] = "Data",
            ["InteractiveReport:Reports:policy:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:policy:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:policy:Authorization:Policy"] = "OrdersReaders",
            ["InteractiveReport:Reports:authenticated:Connection"] = "Data",
            ["InteractiveReport:Reports:authenticated:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:authenticated:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:anonymous:Connection"] = "Data",
            ["InteractiveReport:Reports:anonymous:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:anonymous:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:anonymous:Authorization:AllowAnonymous"] = "true",
        });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("OrdersReaders", policy => policy.RequireRole("readers")));
        builder.Services.AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(_connectionString));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                && !string.IsNullOrEmpty(identity))
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, identity!) };
                foreach (var role in context.Request.Headers["X-Test-Role"])
                    claims.Add(new Claim(ClaimTypes.Role, role!));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    claims,
                    authenticationType: "AuthorizationTableTest"));
            }
            await next();
        });
        _app.MapInteractiveReportJson("/api/reports");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task Public_reports_admit_anyone_and_every_other_report_needs_an_authenticated_caller()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("anonymous", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("anonymous", "anyone")).StatusCode);

        using var anonymous = await GetSchema("authenticated", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("application/json", anonymous.Content.Headers.ContentType?.MediaType);
        var authentication = await ReadJson(anonymous);
        Assert.Equal("IR-1000", authentication.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(authentication.GetProperty("description").GetString()));

        // Who among the authenticated may see a report is the application's business, so the
        // engine admits every authenticated caller.
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("authenticated", "ordinary-user")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("authenticated", "another-user")).StatusCode);
    }

    [Fact]
    public async Task A_report_policy_hides_the_report_from_callers_it_rejects()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetSchema("policy", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("policy", "reader", roles: ["readers"])).StatusCode);

        using var hidden = await GetSchema("policy", "ordinary-user");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var notFound = await ReadJson(hidden);
        Assert.Equal("IR-1001", notFound.GetProperty("code").GetString());
        Assert.False(notFound.TryGetProperty("detail", out _));
        Assert.False(notFound.TryGetProperty("status", out _));
        Assert.False(notFound.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task Saved_report_queries_and_title_checks_require_report_access()
    {
        var reportId = await ReportDocumentTestIds.Default(_app!.Services, "policy");
        using var created = await Send(
            HttpMethod.Post,
            $"/api/reports/{reportId}/saved",
            "reader",
            new { title = "Private title", state = new { v = 3 } },
            roles: ["readers"]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var list = await Send(HttpMethod.Get, "/api/reports/policy", "ordinary-user");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);

        using var collisionProbe = await Send(
            HttpMethod.Post,
            $"/api/reports/{reportId}/saved",
            "ordinary-user",
            new { title = "PRIVATE TITLE", state = new { v = 3 } });
        Assert.Equal(HttpStatusCode.NotFound, collisionProbe.StatusCode);
        Assert.Equal("no-store", collisionProbe.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Database_administrators_are_set_as_a_list_and_take_effect_immediately()
    {
        using var ordinaryBefore = await Send(HttpMethod.Get, Administrators, "database-admin");
        Assert.Equal(HttpStatusCode.NotFound, ordinaryBefore.StatusCode);

        using var set = await Send(
            HttpMethod.Put, Administrators, "configured-admin", new { identities = new[] { "database-admin" } });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        using var listed = await Send(HttpMethod.Get, Administrators, "database-admin");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var lists = await ReadJson(listed);
        Assert.False(lists.GetProperty("managedByApplication").GetBoolean());
        Assert.Equal(["configured-admin"], Strings(lists.GetProperty("configured")));
        Assert.Equal(["database-admin"], Strings(lists.GetProperty("database")));

        using var whoami = await Send(HttpMethod.Get, "/api/reports/whoami", "database-admin");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var identity = await ReadJson(whoami);
        Assert.True(identity.GetProperty("isAdministrator").GetBoolean());
        Assert.Equal("database", identity.GetProperty("administratorSource").GetString());
        Assert.False(identity.GetProperty("administratorsManagedByApplication").GetBoolean());

        using var configuredWhoami = await Send(HttpMethod.Get, "/api/reports/whoami", "configured-admin");
        Assert.Equal("configuration", (await ReadJson(configuredWhoami)).GetProperty("administratorSource").GetString());

        using var ordinary = await Send(HttpMethod.Get, Administrators, "ordinary-user");
        Assert.Equal(HttpStatusCode.NotFound, ordinary.StatusCode);

        using var cleared = await Send(
            HttpMethod.Put, Administrators, "configured-admin", new { identities = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        using var revoked = await Send(HttpMethod.Get, Administrators, "database-admin");
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        using var revokedWhoami = await Send(HttpMethod.Get, "/api/reports/whoami", "database-admin");
        var revokedIdentity = await ReadJson(revokedWhoami);
        Assert.False(revokedIdentity.GetProperty("isAdministrator").GetBoolean());
        Assert.Equal("none", revokedIdentity.GetProperty("administratorSource").GetString());
    }

    [Fact]
    public async Task Administrator_store_creates_only_its_own_prefixed_table()
    {
        using var response = await Send(HttpMethod.Get, Administrators, "configured-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.Contains("TEST_IR_ADMINISTRATORS", tables);
        Assert.DoesNotContain("TEST_IR_SAVED_REPORTS", tables);
        Assert.DoesNotContain("IR_ADMINISTRATORS", tables);
        Assert.DoesNotContain("IR_SAVED_REPORTS", tables);
    }

    [Fact]
    public async Task Administrator_list_input_failures_have_distinct_message_codes()
    {
        using var malformed = new HttpRequestMessage(HttpMethod.Put, Administrators)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.Add("X-Test-Identity", "configured-admin");
        using var malformedResponse = await _client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Equal("IR-1400", (await ReadJson(malformedResponse)).GetProperty("code").GetString());

        using var missing = await Send(HttpMethod.Put, Administrators, "configured-admin", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("IR-1406", (await ReadJson(missing)).GetProperty("code").GetString());

        using var invalidIdentity = await Send(
            HttpMethod.Put, Administrators, "configured-admin", new { identities = new[] { " " } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidIdentity.StatusCode);
        Assert.Equal("IR-1402", (await ReadJson(invalidIdentity)).GetProperty("code").GetString());
    }

    private async Task<HttpResponseMessage> GetSchema(
        string report,
        string? identity,
        IReadOnlyCollection<string>? roles = null)
        => await Send(HttpMethod.Get, $"/api/reports/{report}/schema", identity, roles: roles);

    private async Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        string? identity,
        object? body = null,
        IReadOnlyCollection<string>? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        foreach (var role in roles ?? []) request.Headers.Add("X-Test-Role", role);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static string[] Strings(JsonElement array)
        => array.EnumerateArray().Select(element => element.GetString()!).ToArray();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
