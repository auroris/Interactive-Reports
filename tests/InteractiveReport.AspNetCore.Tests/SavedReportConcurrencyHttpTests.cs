using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Client.GraphQL;
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
/// Deterministically changes a row immediately after the endpoint's authoritative Get.
/// These are authorization races, not load tests: the interposed store provides the
/// exact ordering that two real database clients can produce.
/// </summary>
public sealed class SavedReportConcurrencyHttpTests
{
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"activeTable\":\"bad\",\"tables\":{\"bad\":null}}")]
    [InlineData("{\"activeTable\":\"bad\",\"tables\":{\"bad\":{\"from\":\"missing\"}}}")]
    public async Task Invalid_saved_documents_hydrate_the_stored_default_without_mutating_either_row(string invalidState)
    {
        await using var host = await Start();
        var fallback = await Create(host.Store, "Default", "alice", "one", isDefault: true);
        var requested = await Create(host.Store, "Broken", "alice", "", rawState: invalidState);
        host.Store.RejectWrites = true;

        var loaded = await host.Server.LoadDocument("orders", requested.Id, host.Context());

        Assert.Null(loaded.Failure);
        Assert.Equal(fallback.Id, loaded.Value!.Metadata!.Id);
        Assert.Equal("one", loaded.Value.Result.Document!.Search);
        Assert.Single(loaded.Value.Result.Rows);
        Assert.Equal(1, host.Store.DefaultReads);
        Assert.Equal(requested, await host.Store.Get(requested.Id));
        Assert.Equal(fallback, await host.Store.Get(fallback.Id));
    }

    [Fact]
    public async Task Invalid_requested_and_stored_default_documents_use_a_transient_final_default_once()
    {
        var queriedIds = new List<long?>();
        await using var host = await Start((request, _) =>
        {
            if (request.Action == InteractiveReportAction.Query) queriedIds.Add(request.Resource.SavedReport?.Id);
            return ValueTask.FromResult(true);
        });
        var fallback = await Create(host.Store, "Broken default", "alice", "", isDefault: true, rawState: "{bad");
        var requested = await Create(host.Store, "Broken request", "alice", "", rawState: "{bad");
        host.Store.RejectWrites = true;

        var loaded = await host.Server.LoadDocument(requested.Id, host.Context());

        Assert.Null(loaded.Failure);
        Assert.Null(loaded.Value!.Metadata);
        Assert.Single(loaded.Value.Result.Rows);
        Assert.Equal([requested.Id, fallback.Id, null], queriedIds);
        Assert.Equal(1, host.Store.DefaultReads);
        Assert.Equal(requested, await host.Store.Get(requested.Id));
        Assert.Equal(fallback, await host.Store.Get(fallback.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_invalid_stored_default_is_not_retried_as_its_own_fallback(bool defaultRoute)
    {
        var queriedIds = new List<long?>();
        await using var host = await Start((request, _) =>
        {
            if (request.Action == InteractiveReportAction.Query) queriedIds.Add(request.Resource.SavedReport?.Id);
            return ValueTask.FromResult(true);
        });
        var stored = await Create(host.Store, "Broken default", "alice", "", isDefault: true, rawState: "{bad");
        host.Store.RejectWrites = true;

        var loaded = defaultRoute
            ? await host.Server.LoadDefaultDocument("orders", host.Context())
            : await host.Server.LoadDocument(stored.Id, host.Context());

        Assert.Null(loaded.Failure);
        Assert.Null(loaded.Value!.Metadata);
        Assert.Equal([stored.Id, null], queriedIds);
        Assert.Equal(1, host.Store.DefaultReads);
        Assert.Equal(stored, await host.Store.Get(stored.Id));
    }

    [Fact]
    public async Task Client_queries_never_read_saved_documents_or_fallback_on_validation_failure()
    {
        await using var host = await Start();
        host.Store.RejectReads = host.Store.RejectWrites = true;
        var valid = await host.Server.Query("orders", new ReportState { Search = "one" }, host.Context());
        Assert.Null(valid.Failure);
        Assert.Single(valid.Value!.Rows);

        var invalid = await host.Server.Query("orders", new ReportState
        {
            ActiveTable = "bad", Tables = new() { ["bad"] = null! },
        }, host.Context());
        Assert.Equal(InteractiveReportErrorCodes.ReportStateInvalid, invalid.Failure!.Code);
    }

    [Fact]
    public async Task An_empty_family_loads_a_transient_default_and_only_explicit_save_creates_a_row()
    {
        await using var host = await Start();
        host.Store.RejectWrites = true;
        var listed = await host.Server.ListSavedReports("orders", host.Context());
        Assert.Null(listed.Failure);
        Assert.Empty(listed.Value!);
        var loaded = await host.Server.LoadDefaultDocument("orders", host.Context());
        Assert.Null(loaded.Failure);
        Assert.Null(loaded.Value!.Metadata);
        Assert.Single(loaded.Value.Result.Rows);
        Assert.Empty(await host.Store.ListAll());

        var queried = await host.Server.Query("orders", loaded.Value.Result.Document!, host.Context());
        Assert.Null(queried.Failure);
        Assert.Equal(JsonSerializer.Serialize(loaded.Value.Result.Document, IrJson.Options),
            JsonSerializer.Serialize(queried.Value!.Document, IrJson.Options));
        Assert.Equal(JsonSerializer.Serialize(loaded.Value.Result.Rows, IrJson.Options),
            JsonSerializer.Serialize(queried.Value.Rows, IrJson.Options));

        host.Store.RejectWrites = false;
        using var save = await host.Client.SendAsync(Request(HttpMethod.Post, "/api/reports/orders/saved", "alice",
            new { title = "First saved report", state = loaded.Value.Result.Document }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        Assert.Single(await host.Store.ListAll());
    }

    [Fact]
    public async Task Fallback_reauthorizes_the_stored_default_and_does_not_bypass_a_denial()
    {
        long deniedId = 0;
        await using var host = await Start((request, _) =>
            ValueTask.FromResult(request.Resource.SavedReport?.Id != deniedId));
        var fallback = await Create(host.Store, "Denied default", "bob", "one", isDefault: true);
        deniedId = fallback.Id;
        var requested = await Create(host.Store, "Broken request", "alice", "", rawState: "{bad");
        host.Store.RejectWrites = true;
        host.Execution.Attempts = 0;

        var loaded = await host.Server.LoadDocument(requested.Id, host.Context());

        Assert.Equal(InteractiveReportFailureKind.NotFound, loaded.Failure!.Kind);
        Assert.Equal(1, host.Store.DefaultReads);
        Assert.Equal(0, host.Execution.Attempts);
        Assert.Equal(requested, await host.Store.Get(requested.Id));
        Assert.Equal(fallback, await host.Store.Get(fallback.Id));
    }

    [Fact]
    public async Task Query_authorization_denial_does_not_try_a_default()
    {
        await using var host = await Start((request, _) =>
            ValueTask.FromResult(request.Action != InteractiveReportAction.Query));
        var requested = await Create(host.Store, "Broken request", "alice", "", rawState: "{bad");
        host.Store.RejectWrites = true;

        var loaded = await host.Server.LoadDocument(requested.Id, host.Context());

        Assert.Equal(InteractiveReportFailureKind.Forbidden, loaded.Failure!.Kind);
        Assert.Equal(0, host.Store.DefaultReads);
        Assert.Equal(requested, await host.Store.Get(requested.Id));
    }

    [Fact]
    public async Task Cancellation_during_hydration_propagates_without_trying_a_default()
    {
        await using var host = await Start();
        var requested = await Create(host.Store, "Canceled request", "alice", "one");
        host.Store.RejectWrites = true;
        using var canceled = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Server.LoadDocument(
            requested.Id, host.Context(), canceled.Token, _ => canceled.Cancel()));

        Assert.Equal(0, host.Store.DefaultReads);
        Assert.Equal(requested, await host.Store.Get(requested.Id));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public async Task Execution_failure_tries_the_next_document_and_returns_the_final_error_when_all_fail(
        int failures, bool success)
    {
        await using var host = await Start();
        var fallback = await Create(host.Store, "Default", "alice", "one", isDefault: true);
        var requested = await Create(host.Store, "Requested", "alice", "missing");
        host.Store.RejectWrites = true;
        host.Execution.FailuresRemaining = failures;
        host.Execution.Attempts = 0;

        var loaded = await host.Server.LoadDocument(requested.Id, host.Context());

        if (success)
        {
            Assert.Null(loaded.Failure);
            Assert.Equal(fallback.Id, loaded.Value!.Metadata!.Id);
            Assert.Single(loaded.Value.Result.Rows);
        }
        else
        {
            Assert.Equal(InteractiveReportErrorCodes.ReportExecutionFailed, loaded.Failure!.Code);
            Assert.Equal(3, host.Execution.Attempts);
        }
        Assert.Equal(1, host.Store.DefaultReads);
        Assert.Equal(requested, await host.Store.Get(requested.Id));
        Assert.Equal(fallback, await host.Store.Get(fallback.Id));
    }

    [Fact]
    public async Task Stale_owner_snapshot_cannot_overwrite_a_case_only_reassigned_report()
    {
        await using var host = await Start();
        var report = await Create(host.Store, "Update race", "alice", "before");
        host.Store.ReplaceAfterNextRead(report.Id, current => current with
        {
            Owner = "ALICE",
            StateJson = State("winner"),
        });

        using var response = await host.Client.SendAsync(Request(
            HttpMethod.Put,
            $"/api/reports/{report.Id}",
            "alice",
            new { title = "Alice's stale write" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(host.Store.ReplacementApplied);
        var current = (await host.Store.Get(report.Id))!;
        Assert.Equal("ALICE", current.Owner);
        Assert.Equal("Update race", current.Title);
        Assert.Equal("winner", Search(current.StateJson));
        Assert.True(current.ModifiedUtc > report.ModifiedUtc);
    }

    [Fact]
    public async Task Stale_owner_snapshot_cannot_delete_a_reassigned_report()
    {
        await using var host = await Start();
        var report = await Create(host.Store, "Delete race", "alice", "before");
        host.Store.ReplaceAfterNextRead(report.Id, current => current with
        {
            Owner = "bob",
            StateJson = State("winner"),
        });

        using var response = await host.Client.SendAsync(Request(
            HttpMethod.Delete,
            $"/api/reports/{report.Id}",
            "alice"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(host.Store.ReplacementApplied);
        var current = (await host.Store.Get(report.Id))!;
        Assert.Equal("bob", current.Owner);
        Assert.Equal("winner", Search(current.StateJson));
    }

    [Fact]
    public async Task Load_returns_the_same_public_snapshot_that_was_authorized()
    {
        await using var host = await Start();
        var report = await Create(host.Store, "Read race", "alice", "public-snapshot", global: true);
        host.Store.ReplaceAfterNextRead(report.Id, current => current with
        {
            Owner = "bob",
            IsGlobal = false,
            StateJson = State("private-current"),
        });

        using var response = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/{report.ReportName}/{report.Id}",
            "mallory"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(host.Store.ReplacementApplied);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "public-snapshot",
            body.RootElement.GetProperty("result").GetProperty("document").GetProperty("search").GetString());

        var current = (await host.Store.Get(report.Id))!;
        Assert.Equal("bob", current.Owner);
        Assert.False(current.IsGlobal);
        Assert.Equal("private-current", Search(current.StateJson));
    }

    [Fact]
    public async Task GraphQL_executes_the_same_public_snapshot_that_was_authorized()
    {
        await using var host = await Start();
        var report = await Create(host.Store, "GraphQL race", "alice", "public-snapshot", global: true);
        host.Store.ReplaceAfterNextRead(report.Id, current => current with
        {
            Owner = "bob",
            IsGlobal = false,
            StateJson = State("one"),
        });

        using var response = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            "/graphql",
            "mallory",
            new
            {
                query = "query Execute($id: ID!) { report(id: $id) { totalRows } }",
                variables = new { id = report.Id },
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("data").GetProperty("report")
            .GetProperty("totalRows").GetInt64());

        var current = (await host.Store.Get(report.Id))!;
        Assert.Equal("bob", current.Owner);
        Assert.False(current.IsGlobal);
        Assert.Equal("one", Search(current.StateJson));
    }

    private static async Task<SavedReport> Create(
        RacingStore store,
        string title,
        string owner,
        string search,
        bool global = false,
        bool isDefault = false,
        string? rawState = null)
    {
        var report = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            Title = title,
            Owner = owner,
            IsGlobal = global || isDefault,
            IsDefault = isDefault,
            StateJson = rawState ?? State(search),
        };
        await store.Create(report);
        return report;
    }

    private static string State(string search)
        => JsonSerializer.Serialize(new { search });

    /// <summary>
    /// Reads the search text out of a stored state document. The store types StateJson as nullable,
    /// so a missing document is asserted here rather than surfacing as a null-reference throw from
    /// the parser: every caller is checking what a committed write left behind.
    /// </summary>
    private static string? Search(string? stateJson)
    {
        Assert.NotNull(stateJson);
        using var state = JsonDocument.Parse(stateJson);
        return state.RootElement.GetProperty("search").GetString();
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string identity,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-Identity", identity);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<RunningHost> Start(InteractiveReportAuthorizationCallback? authorize = null)
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-concurrency-").FullName;
        var dataPath = Path.Combine(tempRoot, "data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ORDERS (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);"
                + "INSERT INTO ORDERS VALUES (1, 'one');";
            await command.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InteractiveReport:Reports:orders:Connection"] = "Data",
            ["InteractiveReport:Reports:orders:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:SavedReports:Connection"] = "Store",
        });
        var execution = new ExecutionControl();
        var reports = builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ =>
            {
                execution.Attempts++;
                if (execution.FailuresRemaining > 0)
                {
                    execution.FailuresRemaining--;
                    throw new InvalidOperationException("Simulated report data provider failure.");
                }
                return new SqliteConnection(connectionString);
            })
            .AddConnection("Store", _ => new SqliteConnection(connectionString));
        if (authorize is not null) reports.UseAuthorization(authorize);
        builder.Services.AddInteractiveReportGraphQL();

        var registered = builder.Services.Last(descriptor =>
            descriptor.ServiceType == typeof(ISavedReportStore));
        builder.Services.AddSingleton(sp => new RacingStore(
            (ISavedReportStore)registered.ImplementationFactory!(sp)));
        builder.Services.AddSingleton<ISavedReportStore>(sp =>
            sp.GetRequiredService<RacingStore>());

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, identity!)],
                    authenticationType: "ConcurrencyTest"));
            }
            await next();
        });
        app.MapInteractiveReportJson("/api/reports");
        app.MapInteractiveReportGraphQL("/graphql");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new RunningHost(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            app.Services.GetRequiredService<RacingStore>(),
            execution,
            tempRoot);
    }

    private sealed class RacingStore(ISavedReportStore inner) : ISavedReportStore
    {
        private readonly object _gate = new();
        private long? _replaceId;
        private Func<SavedReport, SavedReport>? _replacement;

        public bool ReplacementApplied { get; private set; }
        public bool RejectWrites { get; set; }
        public bool RejectReads { get; set; }
        public int DefaultReads { get; private set; }

        private void CheckReads()
        {
            if (RejectReads) throw new InvalidOperationException("Unexpected saved-document read.");
        }

        private void CheckWrites()
        {
            if (RejectWrites) throw new InvalidOperationException("Unexpected saved-document mutation.");
        }

        public void ReplaceAfterNextRead(
            long id,
            Func<SavedReport, SavedReport> replacement)
        {
            lock (_gate)
            {
                _replaceId = id;
                _replacement = replacement;
                ReplacementApplied = false;
            }
        }

        public async Task<SavedReport?> Get(long id, CancellationToken ct = default)
        {
            CheckReads();
            var snapshot = await inner.Get(id, ct);
            Func<SavedReport, SavedReport>? replace = null;
            lock (_gate)
            {
                if (snapshot is not null && id == _replaceId)
                {
                    replace = _replacement;
                    _replaceId = null;
                    _replacement = null;
                }
            }

            if (replace is not null)
            {
                var current = replace(snapshot!);
                // The rival writes against the snapshot the endpoint just read, so its
                // update commits and advances the revision before the endpoint's now-stale
                // CAS reaches the database.
                if (!await inner.Update(current, snapshot!, ct))
                    throw new InvalidOperationException("The simulated rival write raced unexpectedly.");
                ReplacementApplied = true;
            }
            return snapshot;
        }

        public Task<SavedReport?> FindDefault(
            string reportName,
            CancellationToken ct = default)
        {
            CheckReads();
            DefaultReads++;
            return inner.FindDefault(reportName, ct);
        }

        public Task<SavedReport?> FindConfiguredFile(
            string reportName,
            string sourceFile,
            CancellationToken ct = default)
        {
            CheckReads();
            return inner.FindConfiguredFile(reportName, sourceFile, ct);
        }

        public Task<IReadOnlyList<SavedReport>> ListFamily(
            string reportName,
            CancellationToken ct = default)
        {
            CheckReads();
            return inner.ListFamily(reportName, ct);
        }

        public Task<IReadOnlyList<string>> ListOwners(CancellationToken ct = default)
        {
            CheckReads();
            return inner.ListOwners(ct);
        }

        public Task<SavedReport?> FindTitleCollision(
            string reportName,
            string title,
            string? owner,
            bool isPublic,
            long? exceptId = null,
            CancellationToken ct = default)
            => inner.FindTitleCollision(reportName, title, owner, isPublic, exceptId, ct);

        public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
        {
            CheckReads();
            return inner.ListAll(ct);
        }

        public Task Create(SavedReport report, CancellationToken ct = default)
        {
            CheckWrites();
            return inner.Create(report, ct);
        }

        public Task<bool> Update(
            SavedReport report,
            SavedReport expected,
            CancellationToken ct = default)
        {
            CheckWrites();
            return inner.Update(report, expected, ct);
        }

        public Task<bool> ReplaceDefault(
            SavedReport report,
            SavedReport expected,
            SavedReport currentDefault,
            CancellationToken ct = default)
        {
            CheckWrites();
            return inner.ReplaceDefault(report, expected, currentDefault, ct);
        }

        public Task<bool> Delete(SavedReport expected, CancellationToken ct = default)
        {
            CheckWrites();
            return inner.Delete(expected, ct);
        }

        public Task<bool> Delete(long id, CancellationToken ct = default)
        {
            CheckWrites();
            return inner.Delete(id, ct);
        }
    }

    private sealed class ExecutionControl
    {
        public int Attempts { get; set; }
        public int FailuresRemaining { get; set; }
    }

    private sealed class RunningHost(
        WebApplication app,
        HttpClient client,
        RacingStore store,
        ExecutionControl execution,
        string tempRoot) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public RacingStore Store { get; } = store;
        public ExecutionControl Execution { get; } = execution;
        public IInteractiveReportServer Server => app.Services.GetRequiredService<IInteractiveReportServer>();
        public InteractiveReportRequestContext Context() => new()
        {
            TraceIdentifier = "hydration-test",
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "alice")], "HydrationTest")),
        };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
