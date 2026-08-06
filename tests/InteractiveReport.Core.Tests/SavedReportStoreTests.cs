using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The saved-report store corpus, dialect-agnostic. Concrete classes below supply the
/// store: SQLite runs always (in-memory); Postgres runs live against IR_TEST_POSTGRES,
/// exercising the quoted-identifier DDL through AutoCreate on a dedicated table.
/// </summary>
public abstract class SavedReportStoreCorpus
{
    private SqlSavedReportStore? _store;

    /// <summary>Build (or skip) the store. Called once per test via the Store property.</summary>
    protected abstract SqlSavedReportStore CreateStore();

    private SqlSavedReportStore Store => _store ??= CreateStore();

    private static SavedReport Make(string title, string owner, bool global = false, string report = "orders") => new()
    {
        Id = SavedReport.NewId(),
        ReportName = report,
        Title = title,
        Owner = owner,
        IsGlobal = global,
        StateJson = """{"v":1,"filters":[]}""",
    };

    [SkippableFact]
    public async Task Create_get_roundtrip_preserves_fields_and_stamps_modified()
    {
        var report = Make("West region", "alice");
        await Store.Create(report);

        var loaded = await Store.Get(report.Id);

        Assert.NotNull(loaded);
        Assert.Equal(report.Title, loaded.Title);
        Assert.Equal("alice", loaded.Owner);
        Assert.False(loaded.IsGlobal);
        Assert.Equal(report.StateJson, loaded.StateJson);
        Assert.True(loaded.ModifiedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [SkippableFact]
    public async Task ListVisible_is_globals_plus_own_scoped_to_report()
    {
        await Store.Create(Make("Mine", "alice"));
        await Store.Create(Make("Theirs", "bob"));
        await Store.Create(Make("Published", "bob", global: true));
        await Store.Create(Make("Other report", "alice", report: "big-orders"));

        var alice = await Store.ListVisible("orders", "alice");
        Assert.Equal(["Published", "Mine"], alice.Select(r => r.Title));

        var anonymous = await Store.ListVisible("orders", null);
        Assert.Equal(["Published"], anonymous.Select(r => r.Title));
    }

    [SkippableFact]
    public async Task ListAll_spans_reports_and_owners()
    {
        await Store.Create(Make("A", "alice"));
        await Store.Create(Make("B", "bob", report: "big-orders"));

        var all = await Store.ListAll();

        Assert.Equal(2, all.Count);
    }

    [SkippableFact]
    public async Task Update_rewrites_row_including_owner_reassignment()
    {
        var report = Make("Original", "alice");
        await Store.Create(report);

        report.Title = "Renamed";
        report.Owner = "bob";
        report.IsGlobal = true;
        Assert.True(await Store.Update(report));

        var loaded = await Store.Get(report.Id);
        Assert.Equal("Renamed", loaded!.Title);
        Assert.Equal("bob", loaded.Owner);
        Assert.True(loaded.IsGlobal);
    }

    [SkippableFact]
    public async Task Delete_removes_and_reports_missing_honestly()
    {
        var report = Make("Doomed", "alice");
        await Store.Create(report);

        Assert.True(await Store.Delete(report.Id));
        Assert.False(await Store.Delete(report.Id));
        Assert.Null(await Store.Get(report.Id));
    }

    protected sealed class FixedConnectionFactory(Func<DbConnection> open) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => open();
    }
}

public sealed class SqliteSavedReportStoreTests : SavedReportStoreCorpus, IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _cs;

    public SqliteSavedReportStoreTests()
    {
        _cs = $"Data Source=saved-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
    }

    protected override SqlSavedReportStore CreateStore() => new(
        () => new SavedReportStoreConfig("Saved", ReportDialect.Sqlite),
        new FixedConnectionFactory(() => new SqliteConnection(_cs)));

    public void Dispose() => _keepAlive.Dispose();
}

/// <summary>
/// Live: every corpus test drops the dedicated table first, so AutoCreate re-runs the
/// Postgres DDL each time — the quoted identifiers are load-bearing there (unquoted
/// names would fold to lowercase and never match SqlKata's quoted queries).
/// </summary>
public sealed class PostgresSavedReportStoreTests : SavedReportStoreCorpus
{
    private const string TableName = "IR_SAVED_REPORTS_TEST";

    protected override SqlSavedReportStore CreateStore()
    {
        var cs = Environment.GetEnvironmentVariable("IR_TEST_POSTGRES");
        Skip.If(string.IsNullOrWhiteSpace(cs), "set IR_TEST_POSTGRES to run live Postgres saved-report verification");

        using (var conn = new NpgsqlConnection(cs))
        {
            conn.Open();
            using var drop = conn.CreateCommand();
            drop.CommandText = $"""DROP TABLE IF EXISTS "{TableName}" """;
            drop.ExecuteNonQuery();
        }

        return new SqlSavedReportStore(
            () => new SavedReportStoreConfig("Saved", ReportDialect.Postgres, AutoCreate: true, TableName: TableName),
            new FixedConnectionFactory(() => new NpgsqlConnection(cs!)));
    }
}
