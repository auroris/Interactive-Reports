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

/// <summary>
/// The definition edit link and per-column overrides over real HTTP: the schema
/// payload carries the canonical-cased template with resolved defaults and the
/// behavior-flag map, an unresolvable template disables the pencil instead of
/// failing, template columns ride query rows as hidden projection data, and the
/// query payload itself stays free of definition presentation.
/// </summary>
public sealed class ColumnConfigurationHttpTests : IAsyncLifetime
{
    private string _tempRoot = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-columns-").FullName;
        var dataPath = Path.Combine(_tempRoot, "column-data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IR_COLUMN_TEST (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL, NOTES TEXT);
                INSERT INTO IR_COLUMN_TEST (ID, LABEL, NOTES) VALUES (1, 'first', 'keep'), (2, 'second', NULL);
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
            ["InteractiveReport:Reports:managed:Connection"] = "ColumnData",
            ["InteractiveReport:Reports:managed:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:managed:Sql"] = "SELECT ID, LABEL, NOTES FROM IR_COLUMN_TEST",
            ["InteractiveReport:Reports:managed:Authorization:AllowAnonymous"] = "true",
            ["InteractiveReport:Reports:managed:Consistency"] = "snapshot",
            // Lowercase placeholder on purpose: the schema payload canonicalizes it.
            ["InteractiveReport:Reports:managed:EditLink:UrlTemplate"] = "/rows/{id}/edit",
            ["InteractiveReport:Reports:managed:EditLink:Label"] = "Edit row",
            ["InteractiveReport:Reports:managed:EditLink:Target"] = "_blank",
            ["InteractiveReport:Reports:managed:Columns:NOTES:HideLabel"] = "true",
            ["InteractiveReport:Reports:managed:Columns:NOTES:Sortable"] = "false",
            ["InteractiveReport:Reports:managed:Columns:NOTES:Filterable"] = "false",
            ["InteractiveReport:Reports:managed:Columns:NOTES:HelpText"] = "Free-form notes.",
            ["InteractiveReport:Reports:managed:Columns:LABEL:Label"] = "Caption",
            ["InteractiveReport:Reports:broken:Connection"] = "ColumnData",
            ["InteractiveReport:Reports:broken:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:broken:Sql"] = "SELECT ID, LABEL FROM IR_COLUMN_TEST",
            ["InteractiveReport:Reports:broken:Authorization:AllowAnonymous"] = "true",
            ["InteractiveReport:Reports:broken:EditLink:UrlTemplate"] = "/rows/{GHOST}/edit",
        });

        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("ColumnData", _ => new SqliteConnection(connectionString));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "column-test-user")],
                authenticationType: "ColumnTest"));
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
    public async Task Schema_delivers_the_canonical_edit_link_and_behavior_flags()
    {
        var schema = await GetJson("/api/reports/managed/schema");
        Assert.False(schema.TryGetProperty("consistency", out _));

        var editLink = schema.GetProperty("editLink");
        Assert.Equal("/rows/{ID}/edit", editLink.GetProperty("urlTemplate").GetString());
        Assert.Equal("Edit row", editLink.GetProperty("label").GetString());
        Assert.Equal("_blank", editLink.GetProperty("target").GetString());

        var overrides = schema.GetProperty("columnOverrides");
        var notes = overrides.GetProperty("NOTES");
        Assert.True(notes.GetProperty("hideLabel").GetBoolean());
        Assert.False(notes.GetProperty("sortable").GetBoolean());
        Assert.False(notes.GetProperty("filterable").GetBoolean());
        Assert.Equal("Free-form notes.", notes.GetProperty("helpText").GetString());
        // Label-only entries deliver nothing here — the label rides the default
        // report's labels channel instead.
        Assert.False(overrides.TryGetProperty("LABEL", out _));
        var labels = schema.GetProperty("defaultState").GetProperty("pipeline")[0]
            .GetProperty("layer").GetProperty("labels");
        Assert.Equal("Caption", labels.GetProperty("LABEL").GetString());
    }

    [Fact]
    public async Task An_unresolvable_edit_link_is_omitted_from_the_schema_and_ignored_by_queries()
    {
        var schema = await GetJson("/api/reports/broken/schema");
        Assert.False(schema.TryGetProperty("editLink", out _));

        using var response = await _client.PostAsync(
            "/api/reports/broken/query", JsonContent.Create(new { v = 3 }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJson(response);
        var item = Assert.Single(result.GetProperty("ignored").EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString() == "editLink"));
        Assert.Contains("GHOST", item.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Template_columns_ride_query_rows_as_hidden_projection_data()
    {
        var state = new
        {
            v = 3,
            pipeline = new object[]
            {
                new { shape = new { kind = "source" }, layer = new { columns = new[] { "LABEL" } } },
            },
        };
        using var response = await _client.PostAsync(
            "/api/reports/managed/query", JsonContent.Create(state));
        var result = await ReadJson(response);

        Assert.Equal(
            ["LABEL"],
            result.GetProperty("columns").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()).ToArray());
        var row = result.GetProperty("rows")[0];
        Assert.Equal("first", row.GetProperty("LABEL").GetString());
        Assert.True(row.TryGetProperty("ID", out _));
        // Definition presentation never enters query payloads.
        Assert.False(result.TryGetProperty("editLink", out _));
        Assert.False(result.TryGetProperty("columnOverrides", out _));
        Assert.False(result.TryGetProperty("consistency", out _));
    }

    [Fact]
    public async Task Stale_sorts_on_a_restricted_column_degrade_into_ignored()
    {
        var state = new
        {
            v = 3,
            pipeline = new object[]
            {
                new
                {
                    shape = new { kind = "source" },
                    layer = new { sorts = new[] { new { col = "NOTES" } } },
                },
            },
        };
        using var response = await _client.PostAsync(
            "/api/reports/managed/query", JsonContent.Create(state));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJson(response);

        var item = Assert.Single(result.GetProperty("ignored").EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString() == "sort"));
        Assert.Contains("not sortable", item.GetProperty("detail").GetString());
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
