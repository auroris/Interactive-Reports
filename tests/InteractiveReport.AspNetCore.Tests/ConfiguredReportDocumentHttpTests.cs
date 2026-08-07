using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
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

public sealed class ConfiguredReportDocumentHttpTests : IAsyncLifetime
{
    private const string ReportName = "orders";
    private const string Identity = "file-doc-admin";
    private string _tempRoot = "";
    private string _primaryPath = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-documents-").FullName;
        var documentDirectory = Path.Combine(_tempRoot, "ReportDocuments");
        Directory.CreateDirectory(documentDirectory);
        _primaryPath = Path.Combine(documentDirectory, "orders.primary.json");
        await File.WriteAllTextAsync(_primaryPath, """
            {
              "title": "Committed Primary",
              "primary": true,
              "state": {
                "v": 2,
                "columns": [ "LABEL" ],
                "sorts": [ { "col": "ID", "dir": "desc" } ]
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(documentDirectory, "orders.regional.json"), """
            {
              "title": "Regional View",
              "state": {
                "v": 2,
                "columns": [ "ID", "LABEL" ],
                "filters": [ { "expr": "ID = 1" } ]
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
                INSERT INTO ORDERS (ID, LABEL) VALUES (1, 'first'), (2, 'second');
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
            ["InteractiveReport:WhoamiEnabled"] = "true",
            ["InteractiveReport:Administrators:0"] = Identity,
            [$"InteractiveReport:Reports:{ReportName}:Connection"] = "Data",
            [$"InteractiveReport:Reports:{ReportName}:Dialect"] = "Sqlite",
            [$"InteractiveReport:Reports:{ReportName}:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            [$"InteractiveReport:Reports:{ReportName}:Authorization:AllowAnonymous"] = "true",
            // The checked-in primary must supersede this legacy inline default.
            [$"InteractiveReport:Reports:{ReportName}:DefaultState:Search"] = "inline default",
            [$"InteractiveReport:Reports:{ReportName}:DocumentFiles:0"] = "ReportDocuments/orders.primary.json",
            [$"InteractiveReport:Reports:{ReportName}:DocumentFiles:1"] = "ReportDocuments/orders.regional.json",
        });
        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Identity)],
                authenticationType: "ConfiguredDocumentTest"));
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

    [Fact]
    public async Task Configured_primary_replaces_inline_default_for_schema_and_query_ingestion()
    {
        var schema = await GetJson($"/api/reports/{ReportName}/schema");
        var defaultState = schema.GetProperty("defaultState");
        Assert.False(defaultState.TryGetProperty("search", out _));
        Assert.Equal(["LABEL"], defaultState.GetProperty("columns").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal("ID", defaultState.GetProperty("sorts")[0].GetProperty("col").GetString());

        using var response = await _client.PostAsync(
            $"/api/reports/{ReportName}/query", JsonContent.Create(new { }));
        var query = await ReadJson(response);
        Assert.Equal(["LABEL"], query.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetProperty("name").GetString()).ToArray());
        Assert.Equal("second", query.GetProperty("rows")[0].GetProperty("LABEL").GetString());
    }

    [Fact]
    public async Task Configured_alternatives_are_global_read_only_and_database_reports_remain_editable()
    {
        using var saveResponse = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "Editable", state = new { v = 2 } }));
        Assert.Equal(HttpStatusCode.Created, saveResponse.StatusCode);
        var saved = await ReadJson(saveResponse);
        Assert.False(saved.GetProperty("isReadOnly").GetBoolean());

        var visible = await GetJson($"/api/reports/{ReportName}/saved");
        var configured = visible.EnumerateArray()
            .Single(summary => summary.GetProperty("title").GetString() == "Regional View");
        Assert.True(configured.GetProperty("isGlobal").GetBoolean());
        Assert.True(configured.GetProperty("isReadOnly").GetBoolean());
        Assert.False(configured.GetProperty("mine").GetBoolean());
        Assert.Equal(2, visible.GetArrayLength());

        var id = configured.GetProperty("id").GetString()!;
        var loaded = await GetJson($"/api/reports/saved/{id}");
        Assert.True(loaded.GetProperty("summary").GetProperty("isReadOnly").GetBoolean());
        Assert.Equal("ID = 1", loaded.GetProperty("state").GetProperty("filters")[0]
            .GetProperty("expr").GetString());

        using var update = await _client.PutAsJsonAsync(
            $"/api/reports/saved/{id}", new { title = "Changed", state = new { v = 2 } });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        using var delete = await _client.DeleteAsync($"/api/reports/saved/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.True(File.Exists(_primaryPath));
    }

    [Fact]
    public async Task Configured_title_shadows_existing_database_row_and_rejects_new_collision()
    {
        var store = _app!.Services.GetRequiredService<ISavedReportStore>();
        await store.Create(new SavedReport
        {
            Id = SavedReport.NewId(),
            ReportName = ReportName,
            Title = "regional view",
            Owner = Identity,
            StateJson = "{\"v\":2,\"search\":\"database\"}",
        });

        var visible = await GetJson($"/api/reports/{ReportName}/saved");
        var matching = visible.EnumerateArray()
            .Where(summary => string.Equals(
                summary.GetProperty("title").GetString(), "Regional View", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(matching);
        Assert.True(matching[0].GetProperty("isReadOnly").GetBoolean());

        using var collision = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "REGIONAL VIEW", state = new { v = 2 } }));
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        var admin = await GetJson("/api/reports/admin/saved");
        Assert.Equal(2, admin.EnumerateArray().Count(summary => string.Equals(
            summary.GetProperty("title").GetString(), "Regional View", StringComparison.OrdinalIgnoreCase)));
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
