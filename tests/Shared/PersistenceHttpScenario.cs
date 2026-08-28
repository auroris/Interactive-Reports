using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Tests;

/// <summary>
/// Starts the real HTTP application twice per persistence target. The restart makes
/// save/load a persistence assertion rather than an in-process store assertion.
/// </summary>
internal static class PersistenceHttpScenario
{
    private const string ReportName = "persistence-test";
    private const string DataConnectionName = "PersistenceData";
    private const string Owner = "persistence-test-user";

    public static string ExplicitFileStorePath(string contentRoot)
        => Path.Combine(contentRoot, "explicit-saved-reports.db");

    public static async Task Run(
        ReportDialect dialect,
        Func<DbConnection> createDataConnection,
        string reportSql,
        IReadOnlyCollection<string> expectedColumns,
        string contentRoot,
        string defaultStoreTable,
        string explicitStoreTable)
    {
        Assert.False(await TableExists(createDataConnection, dialect, defaultStoreTable));
        Assert.False(await TableExists(createDataConnection, dialect, explicitStoreTable));

        var fileSave = await SaveRestartAndLoad(
            dialect,
            createDataConnection,
            reportSql,
            expectedColumns,
            contentRoot,
            savedReportsConnection: null,
            defaultStoreTable,
            idThatMustBeAbsent: null);

        Assert.True(File.Exists(ExplicitFileStorePath(contentRoot)));
        Assert.False(await TableExists(createDataConnection, dialect, defaultStoreTable));
        Assert.False(await TableExists(createDataConnection, dialect, explicitStoreTable));

        await SaveRestartAndLoad(
            dialect,
            createDataConnection,
            reportSql,
            expectedColumns,
            contentRoot,
            savedReportsConnection: DataConnectionName,
            explicitStoreTable,
            idThatMustBeAbsent: fileSave.Id);

        Assert.True(await TableExists(createDataConnection, dialect, explicitStoreTable));
    }

    public static async Task<bool> TableExists(
        Func<DbConnection> createConnection,
        ReportDialect dialect,
        string tableName)
    {
        ValidateIdentifier(tableName);
        await using var connection = createConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = dialect switch
        {
            ReportDialect.Sqlite =>
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name COLLATE NOCASE",
            ReportDialect.SqlServer =>
                "SELECT COUNT(*) FROM sys.tables WHERE name = @name",
            ReportDialect.Oracle =>
                "SELECT COUNT(*) FROM user_tables WHERE table_name = :name",
            ReportDialect.Postgres =>
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = dialect == ReportDialect.Postgres ? tableName : tableName.ToUpperInvariant();
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    public static async Task DropTableIfExists(
        Func<DbConnection> createConnection,
        ReportDialect dialect,
        string tableName)
    {
        ValidateIdentifier(tableName);
        await using var connection = createConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = dialect switch
        {
            ReportDialect.Sqlite => $"DROP TABLE IF EXISTS \"{tableName}\"",
            ReportDialect.SqlServer =>
                $"IF OBJECT_ID(N'{tableName}', N'U') IS NOT NULL DROP TABLE [{tableName}]",
            ReportDialect.Oracle => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'DROP TABLE "{tableName}"';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE != -942 THEN RAISE; END IF;
                END;
                """,
            ReportDialect.Postgres => $"DROP TABLE IF EXISTS \"{tableName}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SavedDocument> SaveRestartAndLoad(
        ReportDialect dialect,
        Func<DbConnection> createDataConnection,
        string reportSql,
        IReadOnlyCollection<string> expectedColumns,
        string contentRoot,
        string? savedReportsConnection,
        string savedReportsTable,
        string? idThatMustBeAbsent)
    {
        var title = $"Persistence {Guid.NewGuid():N}";
        JsonElement defaultState;
        string id;

        await using (var host = await Start(
                         dialect,
                         createDataConnection,
                         reportSql,
                         contentRoot,
                         savedReportsConnection,
                         savedReportsTable))
        {
            var schema = await GetJson(host.Client, $"/api/reports/{ReportName}/schema");
            AssertColumns(expectedColumns, schema.GetProperty("columns"));
            defaultState = schema.GetProperty("defaultState").Clone();

            using (var queryResponse = await host.Client.PostAsync(
                       $"/api/reports/{ReportName}/query",
                       JsonContent.Create(defaultState)))
            {
                var query = await ReadJson(queryResponse);
                AssertColumns(expectedColumns, query.GetProperty("columns"));
            }

            using var saveResponse = await host.Client.PostAsync(
                $"/api/reports/{ReportName}/saved",
                JsonContent.Create(new { title, state = defaultState }));
            Assert.Equal(HttpStatusCode.Created, saveResponse.StatusCode);
            var saved = await ReadJson(saveResponse);
            id = saved.GetProperty("id").GetString()!;
        }

        await using (var restarted = await Start(
                         dialect,
                         createDataConnection,
                         reportSql,
                         contentRoot,
                         savedReportsConnection,
                         savedReportsTable))
        {
            if (idThatMustBeAbsent is not null)
            {
                using var absent = await restarted.Client.GetAsync($"/api/reports/saved/{idThatMustBeAbsent}");
                Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            }

            var visible = await GetJson(restarted.Client, $"/api/reports/{ReportName}/saved");
            Assert.Contains(visible.EnumerateArray(), item => item.GetProperty("id").GetString() == id);

            var loaded = await GetJson(restarted.Client, $"/api/reports/saved/{id}");
            Assert.Equal(title, loaded.GetProperty("summary").GetProperty("title").GetString());
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(defaultState.GetRawText()),
                JsonNode.Parse(loaded.GetProperty("state").GetRawText())));
        }

        return new SavedDocument(id);
    }

    private static async Task<RunningHost> Start(
        ReportDialect dialect,
        Func<DbConnection> createDataConnection,
        string reportSql,
        string contentRoot,
        string? savedReportsConnection,
        string savedReportsTable)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var settings = new Dictionary<string, string?>
        {
            [$"InteractiveReport:Reports:{ReportName}:Connection"] = DataConnectionName,
            [$"InteractiveReport:Reports:{ReportName}:Dialect"] = dialect.ToString(),
            [$"InteractiveReport:Reports:{ReportName}:Sql"] = reportSql,
            [$"InteractiveReport:Reports:{ReportName}:Authorization:AllowAnonymous"] = "true",
        };
        settings["InteractiveReport:SavedReports:TableName"] = savedReportsTable;
        if (savedReportsConnection is null)
        {
            settings["InteractiveReport:SavedReports:DataSource"] =
                $"Data Source={ExplicitFileStorePath(contentRoot)};Pooling=False";
            settings["InteractiveReport:SavedReports:Provider"] = "sqlite";
        }
        else
        {
            settings["InteractiveReport:SavedReports:Connection"] = savedReportsConnection;
        }
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection(DataConnectionName, _ => createDataConnection());

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Owner)],
                authenticationType: "PersistenceTest"));
            await next();
        });
        app.MapInteractiveReports("/api/reports");
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static async Task<JsonElement> GetJson(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static void AssertColumns(IReadOnlyCollection<string> expected, JsonElement columns)
    {
        var actual = columns.EnumerateArray()
            .Select(column => column.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(expected.Count, actual.Length);
        foreach (var name in expected)
            Assert.Contains(actual, actualName => actualName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !(char.IsLetter(value[0]) || value[0] == '_')
            || value.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Table names must be plain SQL identifiers.", nameof(value));
    }

    private sealed record SavedDocument(string Id);

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
