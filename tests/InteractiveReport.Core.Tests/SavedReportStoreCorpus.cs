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
        Id = 0,
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
    public async Task Get_returns_a_detached_snapshot()
    {
        var report = Make("Detached", "alice");
        await Store.Create(report);

        var first = (await Store.Get(report.Id))!;
        first.Owner = "mallory";
        first.StateJson = """{"search":"mutated locally"}""";

        var second = (await Store.Get(report.Id))!;
        Assert.Equal("alice", second.Owner);
        Assert.Equal(report.StateJson, second.StateJson);
    }

    [SkippableFact]
    public async Task Configured_rows_roundtrip_generated_id_origin_file_reference_and_null_state()
    {
        var stamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var report = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            SourceFile = "ReportDocuments/regional.json",
            Title = "Regional View",
            Owner = null,
            IsGlobal = true,
            StateJson = null,
            ModifiedUtc = stamp,
            Origin = SavedReportOrigin.Configured,
        };
        await Store.Create(report);

        var loaded = await Store.Get(report.Id);

        Assert.NotNull(loaded);
        Assert.True(loaded.Id > 0);
        Assert.Equal(report.SourceFile, loaded.SourceFile);
        Assert.Null(loaded.Owner);
        Assert.Null(loaded.StateJson);
        Assert.True(loaded.IsGlobal);
        Assert.Equal(SavedReportOrigin.Configured, loaded.Origin);
        Assert.True(loaded.ModifiedUtc >= stamp);
    }

    [SkippableFact]
    public async Task Put_preserves_an_insert_revision_and_advances_a_replacement_revision()
    {
        var report = Make("Synced", "alice");
        var initialRevision = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        report.ModifiedUtc = initialRevision;
        await Store.Put(report);
        Assert.Equal(initialRevision, (await Store.Get(report.Id))!.ModifiedUtc);

        report.Title = "Synced v2";
        report.ModifiedUtc = initialRevision;
        await Store.Put(report);

        var updated = await Store.Get(report.Id);
        Assert.Equal("Synced v2", updated!.Title);
        Assert.Equal(report.ModifiedUtc, updated.ModifiedUtc);
        Assert.True(updated.ModifiedUtc > initialRevision);
        Assert.Equal(SavedReportOrigin.User, updated.Origin);
    }

    [SkippableFact]
    public async Task Conditional_Put_inserts_with_a_generated_id()
    {
        var winner = Make("Insert winner", "alice");
        winner.ModifiedUtc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Assert.True(await Store.Put(winner, expected: null));

        var current = (await Store.Get(winner.Id))!;
        Assert.Equal("Insert winner", current.Title);
        Assert.Equal(winner.ModifiedUtc, current.ModifiedUtc);
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
    public async Task FindTitleCollision_is_normalized_visibility_scoped_and_can_exclude_the_current_row()
    {
        var orders = Make("West region", "alice");
        var other = Make("West region", "bob", report: "big-orders");
        await Store.Create(orders);
        await Store.Create(other);

        var found = await Store.FindTitleCollision(
            "orders", "  WEST REGION  ", owner: "alice", isPublic: false);

        Assert.Equal(orders.Id, found?.Id);
        Assert.Null(await Store.FindTitleCollision(
            "orders", "West region", "alice", isPublic: false, exceptId: orders.Id));
        Assert.Equal(other.Id, (await Store.FindTitleCollision(
            "big-orders", "west region", "bob", isPublic: false))?.Id);
    }

    [SkippableFact]
    public async Task Update_rewrites_row_including_owner_reassignment()
    {
        var report = Make("Original", "alice");
        await Store.Create(report);

        var expected = report with { };
        report.Title = "Renamed";
        report.Owner = "bob";
        report.IsGlobal = true;
        Assert.True(await Store.Update(report, expected));

        var loaded = await Store.Get(report.Id);
        Assert.Equal("Renamed", loaded!.Title);
        Assert.Equal("bob", loaded.Owner);
        Assert.True(loaded.IsGlobal);
    }

    [SkippableFact]
    public async Task Update_and_delete_reject_a_stale_authoritative_snapshot()
    {
        var report = Make("Concurrent", "alice");
        await Store.Create(report);
        var expected = (await Store.Get(report.Id))!;

        // A state-only write proves ModifiedUtc is a real concurrency predicate, not
        // merely a repeat of the authorization metadata comparisons.
        var winner = expected with { StateJson = """{"search":"winner"}""" };
        Assert.True(await Store.Update(winner, expected));
        Assert.NotEqual(expected.ModifiedUtc, winner.ModifiedUtc);

        var stale = expected with { Title = "Stale overwrite", Owner = "mallory" };
        Assert.False(await Store.Update(stale, expected));
        Assert.False(await Store.Delete(expected));

        var current = (await Store.Get(report.Id))!;
        Assert.Equal("Concurrent", current.Title);
        Assert.Equal("alice", current.Owner);
        Assert.Equal(winner.StateJson, current.StateJson);
        Assert.True(await Store.Delete(current));
        Assert.Null(await Store.Get(report.Id));
    }

    [SkippableFact]
    public async Task Same_revision_Put_replacement_invalidates_the_prior_snapshot()
    {
        var report = Make("Put race", "alice");
        await Store.Create(report);
        var expected = (await Store.Get(report.Id))!;

        var replacement = expected with
        {
            StateJson = """{"search":"replacement"}""",
            ModifiedUtc = expected.ModifiedUtc,
        };
        Assert.True(await Store.Put(replacement, expected));
        Assert.True(replacement.ModifiedUtc > expected.ModifiedUtc);

        var staleWrite = expected with { Title = "stale" };
        Assert.False(await Store.Update(staleWrite, expected));
        Assert.False(await Store.Delete(expected));
        Assert.Equal(
            replacement.StateJson,
            (await Store.Get(report.Id))!.StateJson);
    }

    [SkippableFact]
    public async Task Snapshot_matching_uses_ordinal_authorization_strings()
    {
        var report = Make("Ordinal owner", "Alice@Example.test");
        await Store.Create(report);
        var current = (await Store.Get(report.Id))!;

        // These snapshots are not authoritative even on a database whose default
        // collation equates their strings. Application ownership is ordinal.
        var wrongOwner = current with { Owner = "alice@example.test" };
        var wrongReport = current with { ReportName = "ORDERS" };
        var wrongTitle = current with { Title = "ordinal OWNER" };

        Assert.False(await Store.Update(wrongOwner with { IsGlobal = true }, wrongOwner));
        Assert.False(await Store.Delete(wrongOwner));
        Assert.False(await Store.Update(wrongReport with { IsGlobal = true }, wrongReport));
        Assert.False(await Store.Delete(wrongReport));
        Assert.False(await Store.Update(wrongTitle with { IsGlobal = true }, wrongTitle));
        Assert.False(await Store.Delete(wrongTitle));

        Assert.Equal(current, await Store.Get(report.Id));
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
            () => Store.Create(Make("  west REGION ", "alice")));

        // Private names owned by different users do not share a visible namespace.
        await Store.Create(Make("West region", "bob"));

        // A different report definition keeps its own title namespace.
        await Store.Create(Make("West region", "bob", report: "big-orders"));
    }

    [SkippableFact]
    public async Task Renaming_onto_an_existing_title_is_rejected()
    {
        await Store.Create(Make("Keep", "alice"));
        var other = Make("Rename me", "alice");
        await Store.Create(other);

        var expected = other with { };
        other.Title = "keep";

        await Assert.ThrowsAsync<SavedReportTitleConflictException>(() => Store.Update(other, expected));
    }

    [SkippableFact]
    public async Task Configured_rows_may_duplicate_user_and_configured_titles_without_tripping_uniqueness()
    {
        // A checked-in document is a deployment declaration. Its title does not
        // participate in the user-facing uniqueness constraint, even when another
        // configured file or a visible user row already has that title.
        await Store.Create(Make("Shared title", "alice"));
        var configured = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            SourceFile = "ReportDocuments/shared.json",
            Title = "Shared title",
            Owner = null,
            IsGlobal = true,
            StateJson = null,
            ModifiedUtc = DateTime.UtcNow,
            Origin = SavedReportOrigin.Configured,
        };
        await Store.Create(configured);
        await Store.Create(configured with
        {
            Id = 0,
            SourceFile = "ReportDocuments/also-shared.json",
        });

        Assert.Equal(3, (await Store.ListVisible("orders", "alice")).Count);
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

        await Assert.ThrowsAsync<ArgumentException>(() => store.Put(broken));
        Assert.Equal(0, broken.Id);
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
