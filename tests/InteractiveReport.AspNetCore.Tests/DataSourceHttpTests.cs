using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.Core.Model;
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
/// The redistributable connection story over real HTTP: dataSource in both forms,
/// sniffed dialects for code-registered factories, the declared-dialect wrapper
/// escape hatch, a dataSource-backed saved-report store, startup fail-fast for an
/// unrecognizable wrapper, and error shaping when a live config edit breaks a
/// definition after startup. Every report omits dialect — that is the point.
/// </summary>
public sealed class DataSourceHttpTests : IAsyncLifetime
{
    private string _tempRoot = "";
    private string _dataPath = "";
    private string _savedPath = "";
    private string _mutableConfigPath = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-datasource-").FullName;
        _dataPath = Path.Combine(_tempRoot, "data.db");
        _savedPath = Path.Combine(_tempRoot, "saved-by-datasource.db");
        var connectionString = $"Data Source={_dataPath};Pooling=False";

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IR_DS_TEST (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);
                INSERT INTO IR_DS_TEST (ID, LABEL) VALUES (1, 'first'), (2, 'second');
                """;
            await command.ExecuteNonQueryAsync();
        }

        // A report whose definition can be broken by a live configuration edit (the
        // startup validator passes; the per-request path must then shape the error).
        _mutableConfigPath = Path.Combine(_tempRoot, "mutable.json");
        await File.WriteAllTextAsync(_mutableConfigPath, MutableConfig("MutableDb"));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AppDb"] = connectionString,
            ["ConnectionStrings:AppDb_ProviderName"] = "Microsoft.Data.Sqlite",
            ["ConnectionStrings:MutableDb"] = connectionString,
            ["ConnectionStrings:MutableDb_ProviderName"] = "Microsoft.Data.Sqlite",

            ["InteractiveReport:Reports:literal:DataSource"] = connectionString,
            ["InteractiveReport:Reports:literal:Provider"] = "sqlite",
            ["InteractiveReport:Reports:literal:Sql"] = "SELECT ID, LABEL FROM IR_DS_TEST",
            ["InteractiveReport:Reports:literal:Authorization:AllowAnonymous"] = "true",

            ["InteractiveReport:Reports:named:DataSource"] = "AppDb",
            ["InteractiveReport:Reports:named:Sql"] = "SELECT ID, LABEL FROM IR_DS_TEST",
            ["InteractiveReport:Reports:named:Authorization:AllowAnonymous"] = "true",

            ["InteractiveReport:Reports:sniffed:Connection"] = "SniffedDb",
            ["InteractiveReport:Reports:sniffed:Sql"] = "SELECT ID, LABEL FROM IR_DS_TEST",
            ["InteractiveReport:Reports:sniffed:Authorization:AllowAnonymous"] = "true",

            ["InteractiveReport:Reports:wrapped:Connection"] = "WrappedDb",
            ["InteractiveReport:Reports:wrapped:Sql"] = "SELECT ID, LABEL FROM IR_DS_TEST",
            ["InteractiveReport:Reports:wrapped:Authorization:AllowAnonymous"] = "true",

            // The saved-report store itself rides a dataSource.
            ["InteractiveReport:SavedReports:DataSource"] = $"Data Source={_savedPath};Pooling=False",
            ["InteractiveReport:SavedReports:Provider"] = "sqlite",
        });
        builder.Configuration.AddJsonFile(_mutableConfigPath, optional: false, reloadOnChange: false);

        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("SniffedDb", _ => new SqliteConnection(connectionString))
            .AddConnection("WrappedDb", _ => new DelegatingConnection(new SqliteConnection(connectionString)),
                ReportDialect.Sqlite);

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "datasource-test-user")],
                authenticationType: "DataSourceTest"));
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
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static string MutableConfig(
        string dataSource,
        bool allowAnonymous = true,
        bool restricted = false) => $$"""
        {
          "InteractiveReport": {
            "Reports": {
              "mutable": {
                "dataSource": "{{dataSource}}",
                "sql": "SELECT ID, LABEL FROM IR_DS_TEST",
                "authorization": {
                  "allowAnonymous": {{allowAnonymous.ToString().ToLowerInvariant()}},
                  "restricted": {{restricted.ToString().ToLowerInvariant()}}
                }
              }
            }
          }
        }
        """;

    [Theory]
    [InlineData("literal")]
    [InlineData("named")]
    [InlineData("sniffed")]
    [InlineData("wrapped")]
    public async Task Reports_round_trip_without_a_dialect_anywhere(string report)
    {
        var schema = await GetJson($"/api/reports/{report}/schema");
        Assert.Equal(
            ["ID", "LABEL"],
            schema.GetProperty("columns").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()).ToArray());

        using var response = await _client.PostAsync(
            $"/api/reports/{report}/query", JsonContent.Create(new { v = 3 }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJson(response);
        Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task The_saved_report_store_lands_in_its_data_source()
    {
        var body = new { title = "Kept", state = new { v = 3 } };
        using var created = await _client.PostAsync(
            "/api/reports/literal/saved", JsonContent.Create(body));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Assert.True(File.Exists(_savedPath), "the dataSource-backed store file was not created");
        await using var connection = new SqliteConnection($"Data Source={_savedPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM IR_SAVED_REPORTS";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_definition_broken_by_a_live_config_edit_returns_a_shaped_problem_document()
    {
        // Passes at startup and on a first request…
        using var before = await _client.PostAsync(
            "/api/reports/mutable/query", JsonContent.Create(new { v = 3 }));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // …then the edit points it at a ConnectionStrings name that does not exist.
        await File.WriteAllTextAsync(_mutableConfigPath, MutableConfig("GhostDb"));
        ((IConfigurationRoot)_app!.Services.GetRequiredService<IConfiguration>()).Reload();

        using var after = await _client.PostAsync(
            "/api/reports/mutable/query", JsonContent.Create(new { v = 3 }));
        Assert.Equal(HttpStatusCode.InternalServerError, after.StatusCode);
        var problem = await ReadJson(after);
        Assert.Equal("Report execution failed", problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));

        // Restore for the other tests (fixture instances are per-test-class).
        await File.WriteAllTextAsync(_mutableConfigPath, MutableConfig("MutableDb"));
        ((IConfigurationRoot)_app.Services.GetRequiredService<IConfiguration>()).Reload();
    }

    [Fact]
    public async Task Authorization_is_resolved_before_a_broken_definition_is_hydrated()
    {
        using var before = await _client.PostAsync(
            "/api/reports/mutable/query", JsonContent.Create(new { v = 3 }));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Both changes arrive in one reload. The report is now protected and its
        // executable connection is invalid; authentication must win before connection
        // resolution attempts to hydrate the definition.
        await File.WriteAllTextAsync(
            _mutableConfigPath,
            MutableConfig("GhostDb", allowAnonymous: false, restricted: true));
        ((IConfigurationRoot)_app!.Services.GetRequiredService<IConfiguration>()).Reload();

        using var denied = await _client.PostAsync(
            "/api/reports/mutable/query", JsonContent.Create(new { v = 3 }));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());

        await File.WriteAllTextAsync(_mutableConfigPath, MutableConfig("MutableDb"));
        ((IConfigurationRoot)_app.Services.GetRequiredService<IConfiguration>()).Reload();
    }

    [Fact]
    public async Task An_unrecognizable_wrapper_without_a_declared_dialect_fails_startup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InteractiveReport:Reports:broken:Connection"] = "Opaque",
            ["InteractiveReport:Reports:broken:Sql"] = "SELECT 1 AS ID",
        });
        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Opaque", _ => new DialectSniffTests.FakeConnection());

        await using var app = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("FakeConnection", error.Message);
        Assert.Contains("AddConnection(\"Opaque\", factory, ReportDialect.", error.Message);
    }

    private async Task<JsonElement> GetJson(string path)
    {
        using var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    /// <summary>An unrecognizable but fully functional connection wrapper (profiler-style).</summary>
    private sealed class DelegatingConnection(SqliteConnection inner) : DbConnection
    {
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource!;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Close() => inner.Close();
        public override void Open() => inner.Open();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => inner.CreateCommand();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
