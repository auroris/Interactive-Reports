using System.Data.Common;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class ConfiguredReportDocumentSynchronizerTests : IDisposable
{
    private readonly string _root;
    private readonly string _defaultPath;
    private readonly string _regionalPath;

    public ConfiguredReportDocumentSynchronizerTests()
    {
        _root = Directory.CreateTempSubdirectory("ir-sync-tests-").FullName;
        _defaultPath = Path.Combine(_root, "orders.default.json");
        _regionalPath = Path.Combine(_root, "orders.regional.json");
        File.WriteAllText(_defaultPath, """
            { "title": "Committed Default", "default": true,
              "state": { "activeTable": "base", "tables": { "base": { "from": "definition", "composables": [] } } } }
            """);
        File.WriteAllText(_regionalPath, """
            { "title": "Regional View",
              "state": { "activeTable": "regional", "tables": { "regional": { "from": "definition", "composables": [ { "kind": "filter", "filters": [ { "expr": "ID = 1" } ] } ] } } } }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static InteractiveReportOptions OptionsWith(params string[] documentFiles)
    {
        var options = new InteractiveReportOptions
        {
            SavedReports = new SavedReportsOptions { Connection = "recording" },
        };
        options.Reports["orders"] = new ReportDefinition { DocumentFiles = [.. documentFiles] };
        return options;
    }

    private (ConfiguredReportDocumentSynchronizer Synchronizer, RecordingStore Store, MonitorStub Monitor)
        Build(params string[] documentFiles)
    {
        var monitor = new MonitorStub(OptionsWith(documentFiles));
        var documents = new ConfiguredReportDocumentStore(monitor, new EnvStub(_root));
        var store = new RecordingStore();
        return (new ConfiguredReportDocumentSynchronizer(documents, store), store, monitor);
    }

    [Fact]
    public async Task Sync_creates_numeric_file_identities_and_marks_the_configured_default()
    {
        var (synchronizer, store, _) = Build(_defaultPath, _regionalPath);

        await synchronizer.EnsureSynced();

        Assert.Equal(2, store.Rows.Count);
        var row = store.Rows.Values.Single(report => report.Title == "Regional View");
        Assert.True(row.Id > 0);
        Assert.Equal("orders", row.ReportName);
        Assert.Equal("Regional View", row.Title);
        Assert.Null(row.Owner);
        Assert.True(row.IsGlobal);
        Assert.Equal(SavedReportOrigin.Configured, row.Origin);
        Assert.Equal(_regionalPath, row.SourceFile);
        Assert.Null(row.StateJson);
        Assert.NotEqual(default, row.ModifiedUtc);
        var defaultRow = store.Rows.Values.Single(report => report.Title == "Committed Default");
        Assert.True(defaultRow.IsDefault);
        Assert.Null(defaultRow.StateJson);
    }

    [Fact]
    public async Task Configured_file_titles_may_duplicate_each_other()
    {
        File.WriteAllText(_regionalPath, """
            { "title": "Committed Default",
              "state": { "activeTable": "regional", "tables": { "regional": { "from": "definition", "composables": [] } } } }
            """);
        var (synchronizer, store, _) = Build(_defaultPath, _regionalPath);

        await synchronizer.EnsureSynced();

        var duplicates = store.Rows.Values
            .Where(report => report.Title == "Committed Default")
            .ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.Equal(2, duplicates.Select(report => report.SourceFile).Distinct().Count());
    }

    [Fact]
    public async Task Existing_database_identities_are_not_reprobed_when_a_file_changes()
    {
        var (synchronizer, store, _) = Build(_defaultPath, _regionalPath);
        await synchronizer.EnsureSynced();
        store.Calls.Clear();

        await synchronizer.EnsureSynced();
        Assert.Equal(["listAll"], store.Calls);
        store.Calls.Clear();

        File.WriteAllText(_regionalPath, """
            { "title": "Regional View v2",
              "state": { "activeTable": "base", "tables": { "base": { "from": "definition", "composables": [] } } } }
            """);
        File.SetLastWriteTimeUtc(_regionalPath, DateTime.UtcNow.AddMinutes(1));

        await synchronizer.EnsureSynced();
        Assert.Equal(["listAll"], store.Calls);
        Assert.Contains(store.Rows.Values, row => row.Title == "Regional View");
        Assert.DoesNotContain(store.Rows.Values, row => row.Title == "Regional View v2");
    }

    [Fact]
    public async Task Existing_database_metadata_remains_authoritative_when_file_length_changes()
    {
        var originalTimestamp = File.GetLastWriteTimeUtc(_regionalPath);
        var (synchronizer, store, _) = Build(_defaultPath, _regionalPath);
        await synchronizer.EnsureSynced();
        store.Calls.Clear();

        File.WriteAllText(_regionalPath, """
            { "title": "A substantially longer regional view title",
              "state": { "activeTable": "base", "tables": { "base": { "from": "definition", "composables": [] } } } }
            """);
        File.SetLastWriteTimeUtc(_regionalPath, originalTimestamp);

        await synchronizer.EnsureSynced();

        Assert.Equal(["listAll"], store.Calls);
        Assert.Contains(store.Rows.Values, row => row.Title == "Regional View");
    }

    [Fact]
    public async Task Family_reconciliation_starts_from_every_private_and_public_database_row()
    {
        var (synchronizer, store, _) = Build(_defaultPath);
        await synchronizer.EnsureSynced();
        var anchor = store.Rows.Values.Single(report => report.Origin == SavedReportOrigin.Configured);
        var otherUsersPrivate = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            Title = "Bob's private report",
            Owner = "bob",
            StateJson = "{\"v\":3}",
            ModifiedUtc = DateTime.UtcNow,
        };
        await store.Create(otherUsersPrivate);
        store.Calls.Clear();

        var family = await synchronizer.ReconcileFamily(anchor.ReportName);

        Assert.Equal([ $"listFamily:{anchor.ReportName}" ], store.Calls);
        Assert.Contains(family, report => report.Id == otherUsersPrivate.Id);
    }

    [Fact]
    public async Task Sync_removes_configured_orphans_but_never_user_rows()
    {
        var (synchronizer, store, monitor) = Build(_defaultPath, _regionalPath);
        var userRow = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            Title = "Mine",
            Owner = "alice",
            StateJson = "{\"v\":3}",
            ModifiedUtc = DateTime.UtcNow,
        };
        await store.Put(userRow);
        await synchronizer.EnsureSynced();
        Assert.Equal(3, store.Rows.Count);

        // The definition drops the regional document (an options reload); its synced
        // row is an orphan now — the user row must survive the cleanup.
        monitor.Swap(OptionsWith());
        await synchronizer.EnsureSynced();

        var remaining = Assert.Single(store.Rows.Values);
        Assert.Equal(userRow.Id, remaining.Id);
        Assert.Equal(SavedReportOrigin.User, remaining.Origin);
    }

    [Fact]
    public async Task Configured_default_supersedes_the_synthetic_database_default()
    {
        var (synchronizer, store, _) = Build(_defaultPath);
        var synthetic = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            Title = "Default",
            Owner = null,
            IsGlobal = true,
            IsDefault = true,
            StateJson = "{\"v\":3}",
            ModifiedUtc = DateTime.UtcNow,
            Origin = SavedReportOrigin.Synthetic,
        };
        await store.Create(synthetic);

        await synchronizer.EnsureSynced();

        var configured = Assert.Single(store.Rows.Values);
        Assert.NotEqual(synthetic.Id, configured.Id);
        Assert.Equal(SavedReportOrigin.Configured, configured.Origin);
        Assert.True(configured.IsDefault);
    }

    [Fact]
    public async Task Failed_configured_default_insert_leaves_bootstrap_retryable()
    {
        var (synchronizer, store, _) = Build(_defaultPath);
        var synthetic = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            Title = "Default",
            Owner = null,
            IsGlobal = true,
            IsDefault = true,
            StateJson = "{\"v\":3}",
            ModifiedUtc = DateTime.UtcNow,
            Origin = SavedReportOrigin.Synthetic,
        };
        await store.Create(synthetic);
        store.FailCreates = true;

        await Assert.ThrowsAsync<ReportDocumentBootstrapException>(
            () => synchronizer.ReconcileFamily("orders"));
        Assert.Empty(store.Rows);

        store.FailCreates = false;
        var retried = await synchronizer.ReconcileFamily("orders");
        var configured = Assert.Single(retried);
        Assert.True(configured.IsDefault);
        Assert.Equal(SavedReportOrigin.Configured, configured.Origin);
    }

    [Fact]
    public async Task Configured_default_demotes_but_preserves_a_user_selected_default()
    {
        var (synchronizer, store, _) = Build(_defaultPath);
        var selected = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            Title = "Selected by administrator",
            Owner = "alice",
            IsGlobal = true,
            IsDefault = true,
            StateJson = "{\"v\":3}",
            ModifiedUtc = DateTime.UtcNow,
            Origin = SavedReportOrigin.User,
        };
        await store.Create(selected);

        await synchronizer.EnsureSynced();

        Assert.Equal(2, store.Rows.Count);
        var configured = Assert.Single(store.Rows.Values, row => row.IsDefault);
        Assert.Equal(SavedReportOrigin.Configured, configured.Origin);
        var preserved = store.Rows[selected.Id];
        Assert.Equal(SavedReportOrigin.User, preserved.Origin);
        Assert.True(preserved.IsGlobal);
        Assert.False(preserved.IsDefault);
        Assert.Equal("alice", preserved.Owner);
    }

    [Fact]
    public async Task Existing_database_default_selection_is_not_rewritten_from_file_edits()
    {
        var (synchronizer, store, monitor) = Build(_defaultPath, _regionalPath);
        await synchronizer.EnsureSynced();
        var originalIds = store.Rows.Values.ToDictionary(row => row.SourceFile!, row => row.Id);

        File.WriteAllText(_defaultPath, """
            { "title": "Committed Default",
              "state": { "activeTable": "base", "tables": { "base": { "from": "definition", "composables": [] } } } }
            """);
        File.WriteAllText(_regionalPath, """
            { "title": "Regional View", "default": true,
              "state": { "activeTable": "base", "tables": { "base": { "from": "definition", "composables": [] } } } }
            """);
        File.SetLastWriteTimeUtc(_defaultPath, DateTime.UtcNow.AddMinutes(1));
        File.SetLastWriteTimeUtc(_regionalPath, DateTime.UtcNow.AddMinutes(1));
        monitor.Swap(OptionsWith(_defaultPath, _regionalPath));

        await synchronizer.EnsureSynced();

        Assert.Equal(2, store.Rows.Count);
        Assert.All(store.Rows.Values, row => Assert.Equal(originalIds[row.SourceFile!], row.Id));
        Assert.Equal(_defaultPath, Assert.Single(store.Rows.Values, row => row.IsDefault).SourceFile);
    }

    private sealed class RecordingStore : ISavedReportStore
    {
        private long _nextId;
        public Dictionary<long, SavedReport> Rows { get; } = [];
        public List<string> Calls { get; } = [];
        public bool FailCreates { get; set; }

        public Task<SavedReport?> Get(long id, CancellationToken ct = default)
        {
            Calls.Add($"get:{id}");
            return Task.FromResult(Rows.TryGetValue(id, out var row) ? row with { } : null);
        }

        public Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SavedReport>>(Rows.Values
                .Where(row => row.ReportName == reportName
                    && (row.IsPublic || row.Owner == identity))
                .Select(row => row with { })
                .ToArray());

        public Task<IReadOnlyList<SavedReport>> ListFamily(string reportName, CancellationToken ct = default)
        {
            Calls.Add($"listFamily:{reportName}");
            return Task.FromResult<IReadOnlyList<SavedReport>>(Rows.Values
                .Where(row => row.ReportName == reportName)
                .Select(row => row with { })
                .ToArray());
        }

        public Task<SavedReport?> FindDefault(string reportName, CancellationToken ct = default)
            => Task.FromResult(Rows.Values.SingleOrDefault(row =>
                row.ReportName == reportName && row.IsDefault) is { } row ? row with { } : null);

        public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
        {
            Calls.Add("listAll");
            return Task.FromResult<IReadOnlyList<SavedReport>>([.. Rows.Values.Select(row => row with { })]);
        }

        public Task Create(SavedReport report, CancellationToken ct = default)
        {
            if (FailCreates) throw new TestDbException();
            report.Id = Interlocked.Increment(ref _nextId);
            report.ModifiedUtc = DateTime.UtcNow;
            Rows.Add(report.Id, report with { });
            Calls.Add($"create:{report.Id}");
            return Task.CompletedTask;
        }

        public Task<bool> Update(
            SavedReport report,
            SavedReport expected,
            CancellationToken ct = default)
        {
            Calls.Add($"update:{report.Id}");
            if (!Rows.TryGetValue(report.Id, out var current) || !SameSnapshot(current, expected))
                return Task.FromResult(false);
            report.ModifiedUtc = current.ModifiedUtc.AddTicks(1);
            Rows[report.Id] = report with { };
            return Task.FromResult(true);
        }

        public Task<bool> ReplaceDefault(
            SavedReport report,
            SavedReport expected,
            SavedReport currentDefault,
            CancellationToken ct = default)
        {
            if (!Rows.TryGetValue(report.Id, out var current)
                || !SameSnapshot(current, expected)
                || !Rows.TryGetValue(currentDefault.Id, out var selected)
                || !SameSnapshot(selected, currentDefault))
                return Task.FromResult(false);
            Rows[currentDefault.Id] = currentDefault with
            {
                IsDefault = false,
                IsGlobal = true,
                ModifiedUtc = currentDefault.ModifiedUtc.AddTicks(1),
            };
            report.ModifiedUtc = current.ModifiedUtc.AddTicks(1);
            Rows[report.Id] = report with { };
            return Task.FromResult(true);
        }

        public async Task Put(SavedReport report, CancellationToken ct = default)
        {
            if (report.Id == 0)
            {
                await Create(report, ct);
                return;
            }
            Rows.TryGetValue(report.Id, out var expected);
            if (!await Put(report, expected, ct))
                throw new InvalidOperationException("The recording-store write raced unexpectedly.");
        }

        public Task<bool> Put(
            SavedReport report,
            SavedReport? expected,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add($"put:{report.Id}");
            var exists = Rows.TryGetValue(report.Id, out var current);
            if (expected is null)
            {
                if (exists) return Task.FromResult(false);
            }
            else if (!exists || !SameSnapshot(current!, expected))
            {
                return Task.FromResult(false);
            }

            if (current is not null && report.ModifiedUtc <= current.ModifiedUtc)
                report.ModifiedUtc = current.ModifiedUtc.AddTicks(1);
            Rows[report.Id] = report with { };
            return Task.FromResult(true);
        }

        public Task<bool> Delete(long id, CancellationToken ct = default)
        {
            Calls.Add($"delete:{id}");
            return Task.FromResult(Rows.Remove(id));
        }

        public Task<bool> Delete(SavedReport expected, CancellationToken ct = default)
            => Task.FromResult(Rows.TryGetValue(expected.Id, out var current)
                && SameSnapshot(current, expected)
                && Rows.Remove(expected.Id));

        private static bool SameSnapshot(SavedReport current, SavedReport expected)
            => current == expected;
    }

    private sealed class TestDbException : DbException
    {
    }

    private sealed class MonitorStub(InteractiveReportOptions initial) : IOptionsMonitor<InteractiveReportOptions>
    {
        private readonly List<Action<InteractiveReportOptions, string?>> _listeners = [];

        public InteractiveReportOptions CurrentValue { get; private set; } = initial;

        public InteractiveReportOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Swap(InteractiveReportOptions next)
        {
            CurrentValue = next;
            foreach (var listener in _listeners.ToArray()) listener(next, null);
        }
    }

    private sealed class EnvStub(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SynchronizerTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
