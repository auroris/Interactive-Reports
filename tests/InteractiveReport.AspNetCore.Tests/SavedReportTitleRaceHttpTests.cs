using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
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

/// <summary>
/// Reproduces the save/save title race the advisory pre-check cannot close: a rival
/// row lands between the endpoint's collision check and its insert. The store's
/// unique index catches it and the endpoint translates that into the same 409 the
/// pre-check produces — never a sanitized 500.
/// </summary>
public sealed class SavedReportTitleRaceHttpTests : IAsyncLifetime
{
    private const string ReportName = "orders";
    private string _tempRoot = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-race-").FullName;
        var dataPath = Path.Combine(_tempRoot, "data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ORDERS (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL); INSERT INTO ORDERS VALUES (1, 'x')";
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
            [$"InteractiveReport:Reports:{ReportName}:Connection"] = "Data",
            [$"InteractiveReport:Reports:{ReportName}:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            [$"InteractiveReport:Reports:{ReportName}:Authorization:AllowAnonymous"] = "true",
            ["InteractiveReport:SavedReports:Connection"] = "Data",
        });

        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));

        // Wrap the registered store so the FIRST create sees a rival win the race
        // after the endpoint's pre-check has already passed.
        var registered = builder.Services.Last(d => d.ServiceType == typeof(ISavedReportStore));
        builder.Services.AddSingleton<ISavedReportStore>(sp =>
            new RacingStore((ISavedReportStore)registered.ImplementationFactory!(sp)));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "alice")],
                authenticationType: "RaceTest"));
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
    public async Task A_save_losing_the_title_race_gets_the_same_409_as_the_pre_check()
    {
        using var raced = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "Contested", state = new { v = 3 } }));

        Assert.Equal(HttpStatusCode.Conflict, raced.StatusCode);
        using var problem = JsonDocument.Parse(await raced.Content.ReadAsStringAsync());
        Assert.Equal(
            "IR-1309",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Saved report title conflict", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "A saved report with this title already exists.",
            problem.RootElement.GetProperty("description").GetString());
        Assert.Contains("Contested", problem.RootElement.GetProperty("details").GetString());

        // The rival's row survives; a save under a fresh title still works.
        using var retry = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "Uncontested", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    /// <summary>On the first Create only, a rival row with the same title lands first.</summary>
    private sealed class RacingStore(ISavedReportStore inner) : ISavedReportStore
    {
        private int _raced;

        public async Task Create(SavedReport report, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _raced, 1) == 0)
            {
                await inner.Create(new SavedReport
                {
                    Id = SavedReport.NewId(),
                    ReportName = report.ReportName,
                    Title = report.Title,
                    Owner = "rival",
                    StateJson = "{}",
                }, ct);
            }
            await inner.Create(report, ct);
        }

        public Task<SavedReport?> Get(string id, CancellationToken ct = default) => inner.Get(id, ct);

        public Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default)
            => inner.ListVisible(reportName, identity, ct);

        public Task<SavedReport?> FindByTitle(
            string reportName,
            string title,
            string? exceptId = null,
            CancellationToken ct = default)
            => inner.FindByTitle(reportName, title, exceptId, ct);

        public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default) => inner.ListAll(ct);

        public Task<bool> Update(SavedReport report, CancellationToken ct = default) => inner.Update(report, ct);

        public Task Put(SavedReport report, CancellationToken ct = default) => inner.Put(report, ct);

        public Task<bool> Delete(string id, CancellationToken ct = default) => inner.Delete(id, ct);
    }
}
