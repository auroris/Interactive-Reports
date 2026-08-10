using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.Core.Tests;

/// <summary>Dialect-neutral saved-report persistence contract.</summary>
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
        StateJson = """{"v":3,"pipeline":[{"shape":{"kind":"source"},"layer":{"filters":[]}}]}""",
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
