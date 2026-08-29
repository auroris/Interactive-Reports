using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.GraphQL;
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

public sealed class GraphQLHttpTests : IAsyncLifetime
{
    private const string ReportName = "orders";
    private readonly ConcurrentQueue<InteractiveReportAuthorizationRequest> _authorization = new();
    private string _tempRoot = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-graphql-").FullName;
        var documentDirectory = Path.Combine(_tempRoot, "ReportDocuments");
        Directory.CreateDirectory(documentDirectory);
        await File.WriteAllTextAsync(Path.Combine(documentDirectory, "orders.file.json"), """
            {
              "title": "File View",
              "state": {
                "v": 3,
                "pipeline": [
                  {
                    "shape": { "kind": "source" },
                    "layer": {
                      "columns": [ "ID", "LABEL" ],
                      "filters": [ { "expr": "ID = 2" } ]
                    }
                  }
                ]
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
                INSERT INTO ORDERS (ID, LABEL) VALUES (1, 'first'), (2, 'second'), (3, 'third');
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
            ["InteractiveReport:Administrators:0"] = "admin",
            [$"InteractiveReport:Reports:{ReportName}:Connection"] = "Data",
            [$"InteractiveReport:Reports:{ReportName}:Dialect"] = "Sqlite",
            [$"InteractiveReport:Reports:{ReportName}:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            [$"InteractiveReport:Reports:{ReportName}:Authorization:AllowAnonymous"] = "true",
            [$"InteractiveReport:Reports:{ReportName}:DocumentFiles:0"] = "ReportDocuments/orders.file.json",
            ["InteractiveReport:SavedReports:Connection"] = "Data",
        });

        var reports = builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));
        reports.UseAuthorization((request, _) =>
        {
            _authorization.Enqueue(request);
            return ValueTask.FromResult(true);
        });
        builder.Services.AddInteractiveReportGraphQL();

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                && !string.IsNullOrEmpty(identity))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, identity!)],
                    authenticationType: "GraphQLTest"));
            }
            await next();
        });
        _app.MapInteractiveReports("/api/reports");
        _app.MapInteractiveReportGraphQL("/graphql");
        await _app.StartAsync();
        _app.Services.GetRequiredService<InteractiveReportGraphQLSchema>().Initialize();

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
    public async Task Database_report_executes_for_its_owner_and_uses_resource_authorization()
    {
        using var save = await Send(
            HttpMethod.Post,
            $"/api/reports/{ReportName}/saved",
            "alice",
            new
            {
                title = "Alice View",
                state = new
                {
                    v = 3,
                    pipeline = new[]
                    {
                        new
                        {
                            shape = new { kind = "source" },
                            layer = new
                            {
                                columns = new[] { "LABEL" },
                                sorts = new[] { new { col = "ID", dir = "desc" } },
                            },
                        },
                    },
                },
            });
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetString()!;
        _authorization.Clear();

        using var response = await GraphQL(id, "alice", page: 2, pageSize: 1);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ReadJson(response);
        var result = body.GetProperty("data").GetProperty("report");
        Assert.Equal(3, result.GetProperty("totalRows").GetInt64());
        Assert.Equal(2, result.GetProperty("page").GetProperty("index").GetInt32());
        Assert.Equal(1, result.GetProperty("page").GetProperty("size").GetInt32());
        Assert.Equal("second", result.GetProperty("rows")[0].GetProperty("LABEL").GetString());

        var decisions = _authorization.ToArray();
        Assert.Equal(
            [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.Query],
            decisions.Select(item => item.Action).ToArray());
        Assert.All(decisions, item =>
        {
            Assert.Equal(ReportName, item.Resource.ReportName);
            Assert.Equal(id, item.Resource.SavedReport!.Id);
            Assert.Equal(SavedReportOrigin.User, item.Resource.SavedReport.Origin);
        });
    }

    [Fact]
    public async Task Private_database_report_is_hidden_from_other_callers_but_available_to_an_administrator()
    {
        var id = await CreatePrivateReport("alice", "Private View");

        using var denied = await GraphQL(id, "bob");
        var deniedBody = await ReadJson(denied);
        Assert.Equal("NOT_FOUND", deniedBody.GetProperty("errors")[0]
            .GetProperty("extensions").GetProperty("code").GetString());

        using var allowed = await GraphQL(id, "admin");
        var allowedBody = await ReadJson(allowed);
        Assert.Equal(3, allowedBody.GetProperty("data").GetProperty("report")
            .GetProperty("totalRows").GetInt64());
    }

    [Fact]
    public async Task Configured_report_uses_the_same_saved_report_lookup_and_executes_anonymously()
    {
        using var list = await Send(HttpMethod.Get, $"/api/reports/{ReportName}/saved", identity: null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var reports = await ReadJson(list);
        var configured = reports.EnumerateArray().Single(item =>
            item.GetProperty("title").GetString() == "File View");
        var id = configured.GetProperty("id").GetString()!;
        Assert.True(configured.GetProperty("isReadOnly").GetBoolean());

        _authorization.Clear();
        using var response = await GraphQL(id, identity: null);
        var body = await ReadJson(response);
        var result = body.GetProperty("data").GetProperty("report");
        Assert.Equal(1, result.GetProperty("totalRows").GetInt64());
        Assert.Equal("second", result.GetProperty("rows")[0].GetProperty("LABEL").GetString());
        Assert.All(_authorization, item =>
            Assert.Equal(SavedReportOrigin.Configured, item.Resource.SavedReport!.Origin));
    }

    [Fact]
    public async Task Unknown_report_and_invalid_pagination_return_stable_GraphQL_error_codes()
    {
        using var missing = await GraphQL("missing", "alice");
        var missingBody = await ReadJson(missing);
        Assert.Equal("NOT_FOUND", missingBody.GetProperty("errors")[0]
            .GetProperty("extensions").GetProperty("code").GetString());

        var id = await CreatePrivateReport("alice", "Page View");
        using var invalid = await GraphQL(id, "alice", page: 0);
        var invalidBody = await ReadJson(invalid);
        Assert.Equal("BAD_USER_INPUT", invalidBody.GetProperty("errors")[0]
            .GetProperty("extensions").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Row_values_carry_the_rest_protocols_exact_number_semantics()
    {
        var id = await CreatePrivateReport("alice", "Exact Numbers");

        using var response = await GraphQL(id, "alice");
        var body = await ReadJson(response);
        var result = body.GetProperty("data").GetProperty("report");

        // SQLite INTEGER surfaces as Int64: a string on the wire (columns still say
        // "number") so JavaScript clients cannot lose digits to doubles — the same
        // contract IrJson gives REST. Typed Long scalars keep number semantics.
        var id0 = result.GetProperty("rows")[0].GetProperty("ID");
        Assert.Equal(JsonValueKind.String, id0.ValueKind);
        Assert.True(long.TryParse(id0.GetString(), out _));
        Assert.Equal(JsonValueKind.Number, result.GetProperty("totalRows").ValueKind);
        Assert.Equal(
            "number",
            result.GetProperty("columns").EnumerateArray()
                .Single(column => column.GetProperty("name").GetString() == "ID")
                .GetProperty("type").GetString());
    }

    [Fact]
    public async Task Unsupported_transport_method_returns_405_without_falling_off_the_pipeline()
    {
        using var response = await Send(HttpMethod.Delete, "/graphql", identity: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
        Assert.Contains("POST", response.Content.Headers.Allow);
        var body = await ReadJson(response);
        Assert.Equal(
            "Unsupported GraphQL transport",
            body.GetProperty("title").GetString());
    }

    private async Task<string> CreatePrivateReport(string owner, string title)
    {
        using var response = await Send(
            HttpMethod.Post,
            $"/api/reports/{ReportName}/saved",
            owner,
            new { title, state = new { v = 3 } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> GraphQL(
        string id,
        string? identity,
        int? page = null,
        int? pageSize = null)
        => Send(
            HttpMethod.Post,
            "/graphql",
            identity,
            new
            {
                query = """
                    query ExecuteSavedReport($id: ID!, $page: Int, $pageSize: Int) {
                      report(id: $id, page: $page, pageSize: $pageSize) {
                        columns { name label type computed }
                        rows
                        page { index size }
                        totalRows
                        elapsedMs
                      }
                    }
                    """,
                variables = new { id, page, pageSize },
            });

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
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }
}
