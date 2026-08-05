using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

public sealed class SavedReportStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly SqlSavedReportStore _store;

    public SavedReportStoreTests()
    {
        var cs = $"Data Source=saved-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        _store = new SqlSavedReportStore(
            () => new SavedReportStoreConfig("Saved", ReportDialect.Sqlite),
            new FixedConnectionFactory(cs));
    }

    public void Dispose() => _keepAlive.Dispose();

    private static SavedReport Make(string title, string owner, bool global = false, string report = "orders") => new()
    {
        Id = SavedReport.NewId(),
        ReportName = report,
        Title = title,
        Owner = owner,
        IsGlobal = global,
        StateJson = """{"v":1,"filters":[]}""",
    };

    [Fact]
    public async Task Create_get_roundtrip_preserves_fields_and_stamps_modified()
    {
        var report = Make("West region", "alice");
        await _store.Create(report);

        var loaded = await _store.Get(report.Id);

        Assert.NotNull(loaded);
        Assert.Equal(report.Title, loaded.Title);
        Assert.Equal("alice", loaded.Owner);
        Assert.False(loaded.IsGlobal);
        Assert.Equal(report.StateJson, loaded.StateJson);
        Assert.True(loaded.ModifiedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ListVisible_is_globals_plus_own_scoped_to_report()
    {
        await _store.Create(Make("Mine", "alice"));
        await _store.Create(Make("Theirs", "bob"));
        await _store.Create(Make("Published", "bob", global: true));
        await _store.Create(Make("Other report", "alice", report: "big-orders"));

        var alice = await _store.ListVisible("orders", "alice");
        Assert.Equal(["Published", "Mine"], alice.Select(r => r.Title));

        var anonymous = await _store.ListVisible("orders", null);
        Assert.Equal(["Published"], anonymous.Select(r => r.Title));
    }

    [Fact]
    public async Task ListAll_spans_reports_and_owners()
    {
        await _store.Create(Make("A", "alice"));
        await _store.Create(Make("B", "bob", report: "big-orders"));

        var all = await _store.ListAll();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Update_rewrites_row_including_owner_reassignment()
    {
        var report = Make("Original", "alice");
        await _store.Create(report);

        report.Title = "Renamed";
        report.Owner = "bob";
        report.IsGlobal = true;
        Assert.True(await _store.Update(report));

        var loaded = await _store.Get(report.Id);
        Assert.Equal("Renamed", loaded!.Title);
        Assert.Equal("bob", loaded.Owner);
        Assert.True(loaded.IsGlobal);
    }

    [Fact]
    public async Task Delete_removes_and_reports_missing_honestly()
    {
        var report = Make("Doomed", "alice");
        await _store.Create(report);

        Assert.True(await _store.Delete(report.Id));
        Assert.False(await _store.Delete(report.Id));
        Assert.Null(await _store.Get(report.Id));
    }

    private sealed class FixedConnectionFactory(string cs) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => new SqliteConnection(cs);
    }
}
