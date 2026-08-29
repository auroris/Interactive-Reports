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

/// <summary>
/// Deterministically changes a row immediately after the endpoint's authoritative Get.
/// These are authorization races, not load tests: the interposed store provides the
/// exact ordering that two real database clients can produce.
/// </summary>
public sealed class SavedReportConcurrencyHttpTests
{
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
            $"/api/reports/saved/{report.Id}",
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
            $"/api/reports/saved/{report.Id}",
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
            $"/api/reports/saved/{report.Id}",
            "mallory"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(host.Store.ReplacementApplied);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "public-snapshot",
            body.RootElement.GetProperty("state").GetProperty("search").GetString());

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
        bool global = false)
    {
        var report = new SavedReport
        {
            Id = SavedReport.NewId(),
            ReportName = "orders",
            Title = title,
            Owner = owner,
            IsGlobal = global,
            StateJson = State(search),
        };
        await store.Create(report);
        return report;
    }

    private static string State(string search)
        => JsonSerializer.Serialize(new { search });

    private static string? Search(string stateJson)
    {
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

    private static async Task<RunningHost> Start()
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
            ["InteractiveReport:SavedReports:Connection"] = "Data",
        });
        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));
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
        app.MapInteractiveReports("/api/reports");
        app.MapInteractiveReportGraphQL("/graphql");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new RunningHost(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            app.Services.GetRequiredService<RacingStore>(),
            tempRoot);
    }

    private sealed class RacingStore(ISavedReportStore inner) : ISavedReportStore
    {
        private readonly object _gate = new();
        private string? _replaceId;
        private Func<SavedReport, SavedReport>? _replacement;

        public bool ReplacementApplied { get; private set; }

        public void ReplaceAfterNextRead(
            string id,
            Func<SavedReport, SavedReport> replacement)
        {
            lock (_gate)
            {
                _replaceId = id;
                _replacement = replacement;
                ReplacementApplied = false;
            }
        }

        public async Task<SavedReport?> Get(string id, CancellationToken ct = default)
        {
            var snapshot = await inner.Get(id, ct);
            Func<SavedReport, SavedReport>? replace = null;
            lock (_gate)
            {
                if (snapshot is not null && string.Equals(id, _replaceId, StringComparison.Ordinal))
                {
                    replace = _replacement;
                    _replaceId = null;
                    _replacement = null;
                }
            }

            if (replace is not null)
            {
                var current = replace(snapshot!);
                // Submit the same revision deliberately. A replacement Put must
                // advance it before the endpoint's stale CAS reaches the database.
                current.ModifiedUtc = snapshot!.ModifiedUtc;
                await inner.Put(current, ct);
                ReplacementApplied = true;
            }
            return snapshot;
        }

        public Task<IReadOnlyList<SavedReport>> ListVisible(
            string reportName,
            string? identity,
            CancellationToken ct = default)
            => inner.ListVisible(reportName, identity, ct);

        public Task<SavedReport?> FindPrimaryDefault(
            string reportName,
            CancellationToken ct = default)
            => inner.FindPrimaryDefault(reportName, ct);

        public Task<SavedReport?> FindByTitle(
            string reportName,
            string title,
            string? exceptId = null,
            CancellationToken ct = default)
            => inner.FindByTitle(reportName, title, exceptId, ct);

        public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
            => inner.ListAll(ct);

        public Task Create(SavedReport report, CancellationToken ct = default)
            => inner.Create(report, ct);

        public Task<bool> Update(
            SavedReport report,
            SavedReport expected,
            CancellationToken ct = default)
            => inner.Update(report, expected, ct);

        public Task Put(SavedReport report, CancellationToken ct = default)
            => inner.Put(report, ct);

        public Task<bool> Put(
            SavedReport report,
            SavedReport? expected,
            CancellationToken ct = default)
            => inner.Put(report, expected, ct);

        public Task<bool> Delete(SavedReport expected, CancellationToken ct = default)
            => inner.Delete(expected, ct);

        public Task<bool> Delete(string id, CancellationToken ct = default)
            => inner.Delete(id, ct);
    }

    private sealed class RunningHost(
        WebApplication app,
        HttpClient client,
        RacingStore store,
        string tempRoot) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public RacingStore Store { get; } = store;

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
