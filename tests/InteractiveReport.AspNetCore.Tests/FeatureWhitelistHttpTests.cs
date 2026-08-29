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
/// The feature whitelist over real HTTP: the schema payload always carries the
/// resolved set, download 403s at the export endpoint, saved-report creation 403s,
/// and — deliberately — the query endpoint stays feature-blind (§4: presentation
/// tokens are not a data boundary).
/// </summary>
public sealed class FeatureWhitelistHttpTests : IAsyncLifetime
{
    private string _tempRoot = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-features-").FullName;
        var dataPath = Path.Combine(_tempRoot, "feature-data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IR_FEATURE_TEST (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);
                INSERT INTO IR_FEATURE_TEST (ID, LABEL) VALUES (1, 'first'), (2, 'second');
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
            ["InteractiveReport:Reports:open:Connection"] = "FeatureData",
            ["InteractiveReport:Reports:open:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:open:Sql"] = "SELECT * FROM IR_FEATURE_TEST",
            ["InteractiveReport:Reports:open:Authorization:AllowAnonymous"] = "true",
            ["InteractiveReport:Reports:locked:Connection"] = "FeatureData",
            ["InteractiveReport:Reports:locked:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:locked:Sql"] = "SELECT * FROM IR_FEATURE_TEST",
            ["InteractiveReport:Reports:locked:Authorization:AllowAnonymous"] = "true",
            ["InteractiveReport:Reports:locked:StyleSheet"] = "/styles/locked-report.css?v=2",
            // Mixed casing on purpose: tokens are case-insensitive in config.
            ["InteractiveReport:Reports:locked:Features:0"] = "SEARCH",
            ["InteractiveReport:Reports:locked:Features:1"] = "sort",
            ["InteractiveReport:SavedReports:Connection"] = "FeatureData",
        });

        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("FeatureData", _ => new SqliteConnection(connectionString));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "feature-test-user")],
                authenticationType: "FeatureTest"));
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

    [Fact]
    public async Task Schema_carries_the_resolved_feature_set()
    {
        var open = await GetJson("/api/reports/open/schema");
        Assert.Equal(
            ReportFeatures.All,
            open.GetProperty("features").EnumerateArray().Select(f => f.GetString()!).ToArray());

        var locked = await GetJson("/api/reports/locked/schema");
        Assert.Equal(
            ["search", "sort"],
            locked.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray());
        Assert.Equal("/styles/locked-report.css?v=2", locked.GetProperty("styleSheet").GetString());
        Assert.False(open.TryGetProperty("styleSheet", out _));
    }

    [Fact]
    public async Task Export_is_refused_when_download_is_not_whitelisted()
    {
        using var refused = await _client.PostAsync(
            "/api/reports/locked/export?format=csv", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await ReadJson(refused);
        Assert.Equal("IR-1100", problem.GetProperty("code").GetString());
        Assert.Contains("download", problem.GetProperty("details").GetString());

        using var allowed = await _client.PostAsync(
            "/api/reports/open/export?format=csv", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("text/csv", allowed.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Saved_report_creation_is_refused_when_savedReports_is_not_whitelisted()
    {
        var body = new { title = "Blocked", state = new { } };
        using var refused = await _client.PostAsync(
            "/api/reports/locked/saved", JsonContent.Create(body));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await ReadJson(refused);
        Assert.Equal("IR-1100", problem.GetProperty("code").GetString());
        Assert.Contains("savedReports", problem.GetProperty("details").GetString());

        using var allowed = await _client.PostAsync(
            "/api/reports/open/saved", JsonContent.Create(new { title = "Allowed", state = new { } }));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Query_stays_feature_blind_for_presentation_tokens()
    {
        // Filters and breaks are not whitelisted on "locked", yet the document is
        // valid — the whitelist gates chrome and the two enforced endpoints, not state.
        var state = new
        {
            v = 3,
            pipeline = new object[]
            {
                new
                {
                    shape = new { kind = "source" },
                    layer = new
                    {
                        filters = new[] { new { expr = "ID = 1" } },
                        breaks = new[] { "LABEL" },
                    },
                },
            },
        };
        using var response = await _client.PostAsync(
            "/api/reports/locked/query", JsonContent.Create(state));
        var result = await ReadJson(response);
        Assert.Equal(1, result.GetProperty("rows").GetArrayLength());
    }

    private async Task<JsonElement> GetJson(string path)
    {
        using var response = await _client.GetAsync(path);
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }
}
