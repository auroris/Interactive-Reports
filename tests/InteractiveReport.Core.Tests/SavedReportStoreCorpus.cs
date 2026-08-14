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
        Assert.False(loaded.IsPrimary);
        Assert.Equal(report.StateJson, loaded.StateJson);
        Assert.True(loaded.ModifiedUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(SavedReportOrigin.User, loaded.Origin);
    }

    [SkippableFact]
    public async Task Configured_rows_roundtrip_origin_null_owner_and_cfg_length_ids()
    {
        // Configured-document ids are 68 chars ("cfg_" + SHA-256 hex); the DDL must
        // fit them on every dialect, not just typeless SQLite.
        var id = "cfg_" + new string('a', 64);
        var stamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        await Store.Put(new SavedReport
        {
            Id = id,
            ReportName = "orders",
            Title = "Regional View",
            Owner = null,
            IsGlobal = true,
            StateJson = """{"v":3,"pipeline":[{"shape":{"kind":"source"},"layer":{}}]}""",
            ModifiedUtc = stamp,
            Origin = SavedReportOrigin.Configured,
        });

        var loaded = await Store.Get(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Id);
        Assert.Null(loaded.Owner);
        Assert.True(loaded.IsGlobal);
        Assert.Equal(SavedReportOrigin.Configured, loaded.Origin);
        Assert.Equal(stamp, loaded.ModifiedUtc);
    }

    [SkippableFact]
    public async Task Put_inserts_then_updates_and_never_stamps_the_given_timestamp()
    {
        var report = Make("Synced", "alice");
        report.ModifiedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await Store.Put(report);
        Assert.Equal(report.ModifiedUtc, (await Store.Get(report.Id))!.ModifiedUtc);

        report.Title = "Synced v2";
        report.ModifiedUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        await Store.Put(report);

        var updated = await Store.Get(report.Id);
        Assert.Equal("Synced v2", updated!.Title);
        Assert.Equal(report.ModifiedUtc, updated.ModifiedUtc);
        Assert.Equal(SavedReportOrigin.User, updated.Origin);
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
    public async Task Primary_reports_are_visible_without_identity_and_roundtrip_the_flag()
    {
        var primary = Make("Executive", "bob");
        primary.IsPrimary = true;
        await Store.Create(primary);

        var anonymous = await Store.ListVisible("orders", null);

        var loaded = Assert.Single(anonymous);
        Assert.Equal(primary.Id, loaded.Id);
        Assert.True(loaded.IsPrimary);
        Assert.False(loaded.IsGlobal);
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
