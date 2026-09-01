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

public sealed class OptionalPersistenceHttpTests
{
    [Fact]
    public async Task Report_document_catalogue_requires_configured_persistence()
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-no-persistence-").FullName;
        var dataPath = Path.Combine(tempRoot, "report-data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ITEMS (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);"
                + "INSERT INTO ITEMS VALUES (1, 'one');";
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await using var host = await Start(tempRoot, connectionString);

            using var schema = await host.Client.GetAsync("/api/reports/items/schema");
            Assert.Equal(HttpStatusCode.OK, schema.StatusCode);

            using var query = await host.Client.PostAsJsonAsync(
                "/api/reports/items/query",
                new { v = 3 });
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);

            using var lov = await host.Client.PostAsJsonAsync(
                "/api/reports/items/lov",
                new
                {
                    document = new { v = 3 },
                    table = "definition",
                    column = "LABEL",
                });
            Assert.Equal(HttpStatusCode.OK, lov.StatusCode);

            using var export = await host.Client.PostAsJsonAsync(
                "/api/reports/items/export?format=csv",
                new { v = 3 });
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);

            using var catalogue = await host.Client.GetAsync("/api/reports");
            Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
            using var family = await host.Client.GetAsync("/api/reports/items");
            await AssertStorageFailure(family);

            using var whoami = await host.Client.GetAsync("/api/reports/whoami");
            Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
            var identity = await ReadJson(whoami);
            Assert.True(identity.GetProperty("configuredAdministrator").GetBoolean());
            Assert.False(identity.GetProperty("databaseAdministrator").GetBoolean());

            using var administration = await host.Client.GetAsync(
                "/api/reports/admin/authorization");
            await AssertStorageFailure(administration);

            using var directory = await host.Client.GetAsync("/api/reports/admin/users");
            Assert.Equal(HttpStatusCode.OK, directory.StatusCode);
            Assert.Equal(0, (await ReadJson(directory)).GetArrayLength());

            Assert.False(Directory.Exists(Path.Combine(tempRoot, "App_Data")));
            Assert.Equal(
                [Path.GetFullPath(dataPath)],
                Directory.GetFiles(tempRoot, "*.db", SearchOption.AllDirectories)
                    .Select(Path.GetFullPath)
                    .ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Unreachable_explicit_storage_fails_when_persistence_is_used()
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-bad-persistence-").FullName;
        var dataPath = Path.Combine(tempRoot, "report-data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ITEMS (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);"
                + "INSERT INTO ITEMS VALUES (1, 'one');";
            await command.ExecuteNonQueryAsync();
        }

        var inaccessibleDirectory = Path.Combine(tempRoot, "does-not-exist");
        var inaccessibleStore = $"Data Source={Path.Combine(inaccessibleDirectory, "saved.db")};Pooling=False";
        try
        {
            await using var host = await Start(tempRoot, connectionString, inaccessibleStore);

            // Catalogue authorization still resolves database-backed administrator access.
            using var catalogue = await host.Client.GetAsync("/api/reports");
            await AssertStorageFailure(
                catalogue,
                "IR-1005",
                "Report authorization failed");
            using var family = await host.Client.GetAsync("/api/reports/items");
            await AssertStorageFailure(
                family,
                "IR-1005",
                "Report authorization failed");

            using var administration = await host.Client.GetAsync(
                "/api/reports/admin/authorization");
            await AssertStorageFailure(administration);

            Assert.False(Directory.Exists(inaccessibleDirectory));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<RunningHost> Start(
        string tempRoot,
        string connectionString,
        string? savedReportsDataSource = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var settings = new Dictionary<string, string?>
        {
            ["InteractiveReport:WhoamiEnabled"] = "true",
            ["InteractiveReport:Administrators:0"] = "admin",
            ["InteractiveReport:Reports:items:Connection"] = "Data",
            ["InteractiveReport:Reports:items:Sql"] = "SELECT ID, LABEL FROM ITEMS",
        };
        if (savedReportsDataSource is not null)
        {
            settings["InteractiveReport:SavedReports:DataSource"] = savedReportsDataSource;
            settings["InteractiveReport:SavedReports:Provider"] = "sqlite";
        }
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "admin")],
                authenticationType: "OptionalPersistenceTest"));
            await next();
        });
        app.MapInteractiveReports("/api/reports");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static async Task AssertStorageFailure(
        HttpResponseMessage response,
        string code = "IR-1202",
        string title = "Report execution failed")
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(title, problem.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            problem.RootElement.GetProperty("description").GetString()));
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed class RunningHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
