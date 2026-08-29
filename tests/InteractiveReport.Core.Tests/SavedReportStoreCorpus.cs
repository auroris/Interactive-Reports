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
        StateJson = """{"activeTable":"orders","tables":{"orders":{"from":"definition","composables":[{"kind":"filter","filters":[]}]}}}""",
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
            StateJson = """{"activeTable":"orders","tables":{"orders":{"from":"definition","composables":[]}}}""",
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
    public async Task FindByTitle_is_normalized_scoped_and_can_exclude_the_current_row()
    {
        var orders = Make("West region", "alice");
        var other = Make("West region", "bob", report: "big-orders");
        await Store.Create(orders);
        await Store.Create(other);

        var found = await Store.FindByTitle("orders", "  WEST REGION  ");

        Assert.Equal(orders.Id, found?.Id);
        Assert.Null(await Store.FindByTitle("orders", "West region", orders.Id));
        Assert.Equal(other.Id, (await Store.FindByTitle("big-orders", "west region"))?.Id);
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

    [SkippableFact]
    public async Task Duplicate_user_titles_for_one_report_are_rejected_atomically()
    {
        await Store.Create(Make("West region", "alice"));

        // Same normalized title (trim + case): the unique index is the atomic
        // backstop behind the endpoints' advisory pre-check.
        await Assert.ThrowsAsync<SavedReportTitleConflictException>(
            () => Store.Create(Make("  west REGION ", "bob")));

        // A different report definition keeps its own title namespace.
        await Store.Create(Make("West region", "bob", report: "big-orders"));
    }

    [SkippableFact]
    public async Task Renaming_onto_an_existing_title_is_rejected()
    {
        await Store.Create(Make("Keep", "alice"));
        var other = Make("Rename me", "alice");
        await Store.Create(other);

        other.Title = "keep";

        await Assert.ThrowsAsync<SavedReportTitleConflictException>(() => Store.Update(other));
    }

    [SkippableFact]
    public async Task Configured_rows_may_shadow_a_user_title_without_tripping_uniqueness()
    {
        // A checked-in document deliberately wins over a same-titled user row (the
        // listing dedupes it); synchronization must never fail on that collision,
        // so only user-origin rows live under the unique index.
        await Store.Create(Make("Shared title", "alice"));
        await Store.Put(new SavedReport
        {
            Id = "cfg_" + new string('b', 64),
            ReportName = "orders",
            Title = "Shared title",
            Owner = null,
            IsGlobal = true,
            StateJson = "{}",
            ModifiedUtc = DateTime.UtcNow,
            Origin = SavedReportOrigin.Configured,
        });

        Assert.Equal(2, (await Store.ListVisible("orders", "alice")).Count);
    }

    [SkippableFact]
    public async Task Put_propagates_non_uniqueness_insert_failures()
    {
        // A broken row (null STATE_JSON) must never be reported as applied: treating
        // every DbException as a lost insert race would let the synchronizer mark a
        // missing row as synced. (Store is touched outside the assertion lambda so
        // an unconfigured live target skips instead of failing the type check.)
        var store = Store;
        var broken = Make("Broken", "alice");
        broken.StateJson = null!;

        await Assert.ThrowsAnyAsync<DbException>(() => store.Put(broken));
        Assert.Null(await store.Get(broken.Id));
    }

    [SkippableFact]
    public async Task ListVisible_matches_the_owner_exactly_on_every_dialect()
    {
        // Database equality is collation-dependent; visibility must use the same
        // ordinal semantics as direct-resource authorization.
        await Store.Create(Make("Cased", "Alice@Example.test"));

        var wrongCase = await Store.ListVisible("orders", "alice@example.TEST");
        var visible = await Store.ListVisible("orders", "Alice@Example.test");

        Assert.Empty(wrongCase);
        Assert.Equal("Cased", Assert.Single(visible).Title);
    }

    protected sealed class FixedConnectionFactory(Func<DbConnection> open) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => open();
    }
}
