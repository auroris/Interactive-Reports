using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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

public sealed class AuthorizationHttpTests : IAsyncLifetime
{
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
            ["InteractiveReport:Reports:configured:Connection"] = "Data",
            ["InteractiveReport:Reports:configured:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:configured:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:configured:Authorization:Restricted"] = "true",
            ["InteractiveReport:Reports:configured:Authorization:Users:0"] = "configured-user",
            ["InteractiveReport:Reports:database:Connection"] = "Data",
            ["InteractiveReport:Reports:database:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:database:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:database:Authorization:Users:0"] = "preconfigured-user",
            ["InteractiveReport:Reports:anonymous:Connection"] = "Data",
            ["InteractiveReport:Reports:anonymous:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:anonymous:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:anonymous:Authorization:AllowAnonymous"] = "true",
        });
        builder.Services.AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(_connectionString));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                && !string.IsNullOrEmpty(identity))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, identity!)],
                    authenticationType: "AuthorizationTableTest"));
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
    public async Task Configuration_and_database_report_users_are_additive()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("configured", "configured-user")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetSchema("configured", "database-user")).StatusCode);

        using var grant = await Send(
            HttpMethod.Post,
            "/api/reports/admin/authorization/reports/configured/users",
            "configured-admin",
            new { identity = "database-user" });
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetSchema("configured", "configured-user")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("configured", "database-user")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetSchema("configured", "ordinary-user")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetSchema("configured", null)).StatusCode);
    }

    [Fact]
    public async Task Saved_report_queries_and_title_checks_require_report_access()
    {
        using var created = await Send(
            HttpMethod.Post,
            "/api/reports/configured/saved",
            "configured-user",
            new { title = "Private title", state = new { v = 3 } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var list = await Send(
            HttpMethod.Get,
            "/api/reports/configured/saved",
            "ordinary-user");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);

        using var collisionProbe = await Send(
            HttpMethod.Post,
            "/api/reports/configured/saved",
            "ordinary-user",
            new { title = "PRIVATE TITLE", state = new { v = 3 } });
        Assert.Equal(HttpStatusCode.NotFound, collisionProbe.StatusCode);
        Assert.Equal("no-store", collisionProbe.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Administration_can_restrict_and_grant_an_existing_report()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("database", "ordinary-user")).StatusCode);

        using var restrict = await Send(
            HttpMethod.Put,
            "/api/reports/admin/authorization/reports/database",
            "configured-admin",
            new { restricted = true });
        Assert.Equal(HttpStatusCode.NoContent, restrict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetSchema("database", "ordinary-user")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("database", "preconfigured-user")).StatusCode);

        using var grant = await Send(
            HttpMethod.Post,
            "/api/reports/admin/authorization/reports/database/users",
            "configured-admin",
            new { identity = "ordinary-user" });
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("database", "ordinary-user")).StatusCode);

        using var unrestrict = await Send(
            HttpMethod.Put,
            "/api/reports/admin/authorization/reports/database",
            "configured-admin",
            new { restricted = false });
        Assert.Equal(HttpStatusCode.NoContent, unrestrict.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetSchema("database", "another-user")).StatusCode);
    }

    [Fact]
    public async Task Database_administrators_are_additive_and_revocable()
    {
        using var grant = await Send(
            HttpMethod.Post,
            "/api/reports/admin/authorization/administrators",
            "configured-admin",
            new { identity = "database-admin" });
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);

        using var databaseAdmin = await Send(
            HttpMethod.Get,
            "/api/reports/admin/authorization",
            "database-admin");
        Assert.Equal(HttpStatusCode.OK, databaseAdmin.StatusCode);
        using var whoami = await Send(
            HttpMethod.Get,
            "/api/reports/whoami",
            "database-admin");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var identity = await ReadJson(whoami);
        Assert.True(identity.GetProperty("isAdministrator").GetBoolean());
        Assert.True(identity.GetProperty("databaseAdministrator").GetBoolean());
        using var ordinary = await Send(
            HttpMethod.Get,
            "/api/reports/admin/authorization",
            "ordinary-user");
        Assert.Equal(HttpStatusCode.NotFound, ordinary.StatusCode);

        using var revoke = await Send(
            HttpMethod.Delete,
            "/api/reports/admin/authorization/administrators",
            "configured-admin",
            new { identity = "database-admin" });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var revoked = await Send(
            HttpMethod.Get,
            "/api/reports/admin/authorization",
            "database-admin");
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task Listing_distinguishes_configuration_and_database_sources()
    {
        using var adminGrant = await Send(
            HttpMethod.Post,
            "/api/reports/admin/authorization/administrators",
            "configured-admin",
            new { identity = "database-admin" });
        Assert.Equal(HttpStatusCode.NoContent, adminGrant.StatusCode);
        using var userGrant = await Send(
            HttpMethod.Post,
            "/api/reports/admin/authorization/reports/configured/users",
            "configured-admin",
            new { identity = "database-user" });
        Assert.Equal(HttpStatusCode.NoContent, userGrant.StatusCode);

        using var response = await Send(
            HttpMethod.Get,
            "/api/reports/admin/authorization",
            "configured-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await ReadJson(response);
        Assert.Contains("configured-admin", document.GetProperty("configuredAdministrators")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("database-admin", document.GetProperty("databaseAdministrators")
            .EnumerateArray().Select(item => item.GetString()));
        var configured = document.GetProperty("reports").EnumerateArray()
            .Single(report => report.GetProperty("name").GetString() == "configured");
        Assert.True(configured.GetProperty("configuredRestricted").GetBoolean());
        Assert.True(configured.GetProperty("restricted").GetBoolean());
        Assert.Contains("configured-user", configured.GetProperty("configuredUsers")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("database-user", configured.GetProperty("databaseUsers")
            .EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Authorization_endpoint_creates_only_its_own_prefixed_table()
    {
        using var response = await Send(
            HttpMethod.Get,
            "/api/reports/admin/authorization",
            "configured-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.DoesNotContain("TEST_IR_SAVED_REPORTS", tables);
        Assert.Contains("TEST_IR_REPORT_AUTHORIZATION", tables);
        Assert.DoesNotContain("IR_SAVED_REPORTS", tables);
        Assert.DoesNotContain("IR_REPORT_AUTHORIZATION", tables);
    }

    [Fact]
    public async Task Anonymous_reports_reject_database_user_restrictions()
    {
        using var response = await Send(
            HttpMethod.Put,
            "/api/reports/admin/authorization/reports/anonymous",
            "configured-admin",
            new { restricted = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<HttpResponseMessage> GetSchema(string report, string? identity)
        => Send(HttpMethod.Get, $"/api/reports/{report}/schema", identity);

    private async Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        string? identity,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
