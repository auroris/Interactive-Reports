using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.Client.GraphQL;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
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

public sealed class RowRestrictionHttpTests : IAsyncLifetime
{
    private const string Sql = "SELECT p.ID, p.NAME, p.CATEGORY, p.AMOUNT FROM PRODUCTS p WHERE (p.ACTIVE = 1) AND {{RowRestriction}}";
    private readonly ConcurrentQueue<InteractiveReportRowRestrictionRequest> _requests = new();
    private readonly CaptureLogger _log = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _root = "";
    private int _opens;
    private bool _authorize = true;
    private InteractiveReportRowRestrictionCallback _callback = (request, _) => ValueTask.FromResult(
        request.UserId == "admin" ? RowRestriction.Unrestricted : RowRestriction.Where("p.CG = ?", 0));
    private InteractiveReportRowRestrictionCallback _additional = (_, _) => ValueTask.FromResult(RowRestriction.NotApplicable);

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("ir-row-restrictions-").FullName;
        var connectionString = $"Data Source={Path.Combine(_root, "data.db")};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE PRODUCTS (ID INTEGER PRIMARY KEY, NAME TEXT NOT NULL, CATEGORY TEXT NOT NULL,
                    AMOUNT INTEGER NOT NULL, CG INTEGER NOT NULL, OWNER TEXT NOT NULL, ACTIVE INTEGER NOT NULL);
                INSERT INTO PRODUCTS VALUES
                    (1, 'public-a', 'Public', 10, 0, 'alice', 1),
                    (2, 'public-b', 'Public', 20, 0, 'bob', 1),
                    (3, 'secret-cg', 'Controlled', 90, 1, 'alice', 1),
                    (4, 'inactive', 'Public', 1000, 0, 'alice', 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root, EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var configuration = new Dictionary<string, string?>
        {
            ["InteractiveReport:SavedReports:Connection"] = "data",
            ["InteractiveReport:IdentityClaim"] = "account_id",
            ["InteractiveReport:Administrators:0"] = "admin",
        };
        foreach (var (name, sql) in new Dictionary<string, string>
        {
            ["products"] = Sql,
            ["public"] = Sql.Replace(" AND {{RowRestriction}}", ""),
            ["comment"] = Sql.Replace(" AND {{RowRestriction}}", "") + "\n/* {{RowRestriction}} */",
            ["grouped"] = "SELECT SUM(p.AMOUNT) AS TOTAL FROM PRODUCTS p WHERE p.ACTIVE = 1 AND {{RowRestriction}}",
            ["context"] = Sql + " AND p.OWNER = @__ir_row_0",
        })
        {
            configuration[$"InteractiveReport:Reports:{name}:Connection"] = "data";
            configuration[$"InteractiveReport:Reports:{name}:Sql"] = sql;
            configuration[$"InteractiveReport:Reports:{name}:Authorization:AllowAnonymous"] = "true";
        }
        configuration["InteractiveReport:Reports:context:ContextParams:__ir_row_0:Claim"] = "account_id";
        builder.Configuration.AddInMemoryCollection(configuration);
        var reports = builder.Services.AddInteractiveReports(builder.Configuration)
            .AddConnection("data", _ =>
            {
                var connection = new SqliteConnection(connectionString);
                connection.StateChange += (_, change) =>
                {
                    if (change.CurrentState == ConnectionState.Open) Interlocked.Increment(ref _opens);
                };
                return connection;
            })
            .UseLogger(_log)
            .UseAuthorization((_, _) => ValueTask.FromResult(_authorize))
            .UseRowRestrictions((request, ct) =>
            {
                _requests.Enqueue(request);
                Assert.NotNull(request.RequestServices.GetRequiredService<RequestService>());
                return _callback(request, ct);
            })
            .UseRowRestrictions((request, ct) => _additional(request, ct));
        builder.Services.AddScoped<RequestService>();
        builder.Services.AddInteractiveReportFileDownload();
        builder.Services.AddInteractiveReportGraphQL();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.TryGetValue("X-User", out var identity))
                http.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("account_id", identity.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, "other-" + identity),
                ], "test"));
            await next();
        });
        _app.MapInteractiveReportJson("/reports");
        _app.MapInteractiveReportFileDownload("/download");
        _app.MapInteractiveReportGraphQL("/graphql");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("comment")]
    public async Task Unmarked_public_reports_never_call_row_authorization(string report)
    {
        _callback = (_, _) => throw new InvalidOperationException("must not be invoked");
        using var response = await Send($"/reports/{report}/query", new ReportState(), user: null);
        var result = await Success(response);
        Assert.Equal(3, result.GetProperty("totalRows").Deserialize<long>(IrJson.Options));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Public_marked_report_receives_an_anonymous_principal_and_null_identity()
    {
        using var response = await Send("/reports/PRODUCTS/query", new ReportState(), user: null);
        var result = await Success(response);
        Assert.Equal(2, result.GetProperty("totalRows").Deserialize<long>(IrJson.Options));
        var request = Assert.Single(_requests);
        Assert.Equal("products", request.ReportName);
        Assert.Null(request.UserId);
        Assert.NotNull(request.User);
        Assert.False(request.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task Filtering_precedes_base_aggregation_and_context_bindings_cannot_collide()
    {
        using var grouped = await Send("/reports/grouped/query", new ReportState());
        Assert.Equal(30, (await Success(grouped)).GetProperty("rows")[0].GetProperty("TOTAL").Deserialize<long>(IrJson.Options));
        using var owned = await Send("/reports/context/query", new ReportState());
        var row = Assert.Single((await Success(owned)).GetProperty("rows").EnumerateArray());
        Assert.Equal(1, row.GetProperty("ID").Deserialize<long>(IrJson.Options));
        Assert.All(_requests, request => Assert.Equal("alice", request.UserId));
    }

    [Fact]
    public async Task Counts_totals_pages_exports_and_value_lookups_share_the_restricted_source()
    {
        var state = BaseState(new TableComposable { Kind = "aggregate", Aggregates = [new() { Col = "AMOUNT", Fn = AggregateFn.Sum }] });
        state.Page = new() { Index = 1, Size = 1 };
        using var query = await Send("/reports/products/query", state);
        var result = await Success(query);
        Assert.Equal(2, result.GetProperty("totalRows").Deserialize<long>(IrJson.Options));
        Assert.Single(result.GetProperty("rows").EnumerateArray());
        Assert.Equal(30, result.GetProperty("aggregates").GetProperty("AMOUNT").GetProperty("sum").Deserialize<long>(IrJson.Options));
        Assert.Single(_requests); // Includes schema discovery, count, totals, and page commands.

        using var csv = await Send("/download/products/csv", state);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("public-a", text);
        Assert.Contains("public-b", text);
        Assert.DoesNotContain("secret-cg", text);

        using var lov = await Send("/reports/products/lov", new ReportLovRequest { Document = state, Table = "base", Column = "NAME" });
        var items = (await Success(lov)).GetProperty("items").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["public-a", "public-b"], items.Order().ToArray());
        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public async Task Unrestricted_and_restricted_requests_do_not_share_identity_or_mutate_configuration()
    {
        _callback = (request, _) => ValueTask.FromResult(request.UserId == "admin"
            ? RowRestriction.Unrestricted : RowRestriction.Where("p.OWNER = ? AND p.CG = ?", request.UserId, 0));
        await Task.WhenAll(Enumerable.Range(0, 18).Select(async index =>
        {
            var user = index % 3 == 0 ? "admin" : index % 3 == 1 ? "alice" : "bob";
            using var response = await Send("/reports/products/query", new ReportState(), user);
            var result = await Success(response);
            Assert.Equal(user == "admin" ? 3 : 1, result.GetProperty("totalRows").Deserialize<long>(IrJson.Options));
            if (user != "admin") Assert.Equal(user == "alice" ? 1 : 2, result.GetProperty("rows")[0].GetProperty("ID").Deserialize<long>(IrJson.Options));
        }));
        var definition = await _app.Services.GetRequiredService<IReportDefinitionStore>().Find("products");
        Assert.Equal(Sql, definition!.Sql);
    }

    [Fact]
    public async Task Multiple_callbacks_narrow_each_other_and_unrestricted_cannot_erase_a_predicate()
    {
        _additional = (_, _) => ValueTask.FromResult(RowRestriction.Where("p.OWNER = ? OR p.ID = ?", "alice", 3));
        using var narrowed = await Send("/reports/products/query", new ReportState());
        Assert.Equal(1, (await Success(narrowed)).GetProperty("totalRows").Deserialize<long>(IrJson.Options));
        _additional = (_, _) => ValueTask.FromResult(RowRestriction.Unrestricted);
        using var retained = await Send("/reports/products/query", new ReportState());
        Assert.Equal(2, (await Success(retained)).GetProperty("totalRows").Deserialize<long>(IrJson.Options));
    }

    [Fact]
    public async Task Binding_values_never_become_sql_or_override_the_predicate()
    {
        const string attack = "secret-cg' OR 1=1 --";
        _callback = (_, _) => ValueTask.FromResult(RowRestriction.Where("p.NAME = ?", attack));
        using var response = await Send("/reports/products/query", BaseState(new TableComposable
        {
            Kind = "filter", Filters = [new() { Expr = "ID > 0 OR ID < 0" }],
        }));
        Assert.Equal(0, (await Success(response)).GetProperty("totalRows").Deserialize<long>(IrJson.Options));
        Assert.DoesNotContain(_log.Messages, message => message.Contains(attack, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("admin", HttpStatusCode.Forbidden)]
    public async Task Row_denial_stops_before_any_report_connection_opens(string? user, HttpStatusCode status)
    {
        _callback = (_, _) => ValueTask.FromResult(RowRestriction.Deny);
        var before = _opens;
        using var response = await Send("/reports/products/query", new ReportState(), user);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(before, _opens);
    }

    [Theory]
    [InlineData("abstain")]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("bindings")]
    [InlineData("cross-bindings")]
    public async Task Missing_or_invalid_decisions_fail_without_executing_the_report(string mode)
    {
        _callback = (_, _) => mode switch
        {
            "abstain" => ValueTask.FromResult(RowRestriction.NotApplicable),
            "null" => ValueTask.FromResult<RowRestriction>(null!),
            "bindings" or "cross-bindings" => ValueTask.FromResult(RowRestriction.Where("p.CG = ?")),
            _ => throw new InvalidOperationException("sensitive permission-service details"),
        };
        if (mode == "cross-bindings")
            _additional = (_, _) => ValueTask.FromResult(RowRestriction.Where("1 = 1", 0));
        var before = _opens;
        using var response = await Send("/reports/products/query", new ReportState());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("sensitive", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, _opens);
    }

    [Fact]
    public async Task Ordinary_authorization_runs_before_row_restriction()
    {
        _authorize = false;
        using var response = await Send("/reports/products/query", new ReportState());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Callback_cancellation_propagates_without_opening_report_data()
    {
        using var cancellation = new CancellationTokenSource();
        _callback = (_, _) => { cancellation.Cancel(); return ValueTask.FromResult(RowRestriction.Unrestricted); };
        using var scope = _app.Services.CreateScope();
        var before = _opens;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _app.Services.GetRequiredService<IInteractiveReportServer>().Query(
            "products", new ReportState(), new InteractiveReportRequestContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity()), RequestServices = scope.ServiceProvider, TraceIdentifier = "canceled",
            }, cancellation.Token));
        Assert.Equal(before, _opens);
    }

    [Fact]
    public async Task Cached_dormant_pivot_columns_are_refreshed_for_the_current_caller()
    {
        var state = BaseState();
        state.Tables!["pivot"] = PivotTable();
        using var administrator = await Send("/reports/products/query", state, "admin");
        var adminDocument = (await Success(administrator)).GetProperty("document");
        Assert.Contains("Controlled", adminDocument.GetRawText());
        using var restricted = await Send("/reports/products/query", adminDocument);
        Assert.DoesNotContain("Controlled", (await Success(restricted)).GetRawText());
    }

    [Fact]
    public async Task Saved_pivot_validation_and_loading_use_the_callers_scope_without_persisting_schema_values()
    {
        var id = await ReportDocumentTestIds.Default(_app.Services, "products");
        var state = BaseState();
        state.Tables!["pivot"] = PivotTable();
        using var save = await Send($"/reports/{id}/saved", new { title = "Shared pivot", isGlobal = true, state }, "admin");
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var savedId = (await Json(save)).GetProperty("id").Deserialize<long>(IrJson.Options);
        var stored = await _app.Services.GetRequiredService<ISavedReportStore>().Get(savedId);
        Assert.DoesNotContain("Controlled", stored!.StateJson);
        using var load = await Send($"/reports/products/{savedId}", body: null, method: HttpMethod.Get);
        Assert.DoesNotContain("Controlled", (await Success(load)).GetRawText());
        using var export = await Send($"/reports/admin/saved/{savedId}/document", body: null, user: "admin", method: HttpMethod.Get);
        Assert.DoesNotContain("Controlled", (await Success(export)).GetRawText());
    }

    [Fact]
    public async Task Graphql_executes_saved_reports_through_the_same_restriction()
    {
        var id = await ReportDocumentTestIds.Default(_app.Services, "products");
        using var response = await Send("/graphql", new
        {
            query = "query($id: ID!) { report(id: $id) { rows totalRows } }", variables = new { id },
        });
        var result = await Success(response);
        Assert.False(result.TryGetProperty("errors", out _), result.GetRawText());
        Assert.Equal(2, result.GetProperty("data").GetProperty("report").GetProperty("totalRows").Deserialize<long>(IrJson.Options));
        Assert.DoesNotContain("secret-cg", result.GetRawText());
    }

    [Fact]
    public async Task A_scope_dependent_validation_failure_does_not_repair_the_shared_default()
    {
        var state = new ReportState { ActiveTable = "pivot", Tables = new() { ["pivot"] = PivotTable() } };
        using var discover = await Send("/reports/products/query", state, "admin");
        var columns = (await Success(discover)).GetProperty("availableColumns").EnumerateArray();
        var controlled = columns.First(column => column.GetProperty("label").GetString()!.Contains("Controlled", StringComparison.Ordinal))
            .GetProperty("name").GetString()!;
        state.Tables["dependent"] = new ReportTable
        {
            From = "pivot", Composables = [new() { Kind = "group", By = [controlled] }],
        };
        state.ActiveTable = "dependent";
        using var permitted = await Send("/reports/products/query", state, "admin");
        _ = await Success(permitted);

        var id = await ReportDocumentTestIds.Default(_app.Services, "products");
        var store = _app.Services.GetRequiredService<ISavedReportStore>();
        var original = (await store.Get(id))!;
        var configured = original with { StateJson = JsonSerializer.Serialize(state, IrJson.Options) };
        Assert.True(await store.Update(configured, original));
        using var restricted = await Send($"/reports/products/{id}", body: null, method: HttpMethod.Get);
        Assert.Equal(HttpStatusCode.BadRequest, restricted.StatusCode);
        Assert.Equal(configured.StateJson, (await store.Get(id))!.StateJson);
    }

    private static ReportState BaseState(params TableComposable[] composables) => new()
    {
        ActiveTable = "base", Tables = new() { ["base"] = new() { From = "definition", Composables = composables.ToList() } },
    };

    private static ReportTable PivotTable() => new()
    {
        From = "definition", Composables = [new() { Kind = "pivot", Rows = ["ID"], Cols = ["CATEGORY"], Values = [new() { Id = "ir1", Col = "AMOUNT", Fn = AggregateFn.Sum }] }],
    };

    private async Task<HttpResponseMessage> Send(string path, object? body, string? user = "alice", HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
        if (user is not null) request.Headers.Add("X-User", user);
        if (body is not null) request.Content = JsonContent.Create(body, options: IrJson.Options);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Success(HttpResponseMessage response)
    {
        var result = await Json(response);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {result}");
        return result;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed class RequestService;

    private sealed class CaptureLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }
}
