using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class ConfiguredReportDocumentSynchronizerTests : IDisposable
{
    private readonly string _root;
    private readonly string _primaryPath;
    private readonly string _regionalPath;

    public ConfiguredReportDocumentSynchronizerTests()
    {
        _root = Directory.CreateTempSubdirectory("ir-sync-tests-").FullName;
        _primaryPath = Path.Combine(_root, "orders.primary.json");
        _regionalPath = Path.Combine(_root, "orders.regional.json");
        File.WriteAllText(_primaryPath, """
            { "title": "Committed Primary", "primary": true,
              "state": { "v": 3, "pipeline": [ { "shape": { "kind": "source" }, "layer": {} } ] } }
            """);
        File.WriteAllText(_regionalPath, """
            { "title": "Regional View",
              "state": { "v": 3, "pipeline": [ { "shape": { "kind": "source" }, "layer": { "filters": [ { "expr": "ID = 1" } ] } } ] } }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static InteractiveReportOptions OptionsWith(params string[] documentFiles)
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition { DocumentFiles = [.. documentFiles] };
        return options;
    }

    private (ConfiguredReportDocumentSynchronizer Synchronizer, RecordingStore Store, MonitorStub Monitor)
        Build(params string[] documentFiles)
    {
        var monitor = new MonitorStub(OptionsWith(documentFiles));
        var documents = new ConfiguredReportDocumentStore(monitor, new EnvStub(_root));
        var store = new RecordingStore();
        // Default SavedReports options resolve to the sentinel store target, so the
        // registry never needs a real connection here.
        var registry = new ReportConnectionRegistry(
            new Dictionary<string, Func<IServiceProvider, System.Data.Common.DbConnection>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ReportDialect>(StringComparer.OrdinalIgnoreCase),
            EmptyServices.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        return (new ConfiguredReportDocumentSynchronizer(documents, store, monitor, registry), store, monitor);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public async Task Sync_upserts_all_documents_and_seeds_the_primary_flag()
    {
        var (synchronizer, store, _) = Build(_primaryPath, _regionalPath);

        await synchronizer.EnsureSynced();

        Assert.Equal(2, store.Rows.Count);
        var row = store.Rows.Values.Single(report => report.Title == "Regional View");
        Assert.StartsWith("cfg_", row.Id);
        Assert.Equal("orders", row.ReportName);
        Assert.Equal("Regional View", row.Title);
        Assert.Null(row.Owner);
        Assert.True(row.IsGlobal);
        Assert.False(row.IsPrimary);
        Assert.Equal(SavedReportOrigin.Configured, row.Origin);
        Assert.Contains("ID = 1", row.StateJson);
        Assert.Equal(File.GetLastWriteTimeUtc(_regionalPath), row.ModifiedUtc);
        Assert.True(store.Rows.Values.Single(report => report.Title == "Committed Primary").IsPrimary);
    }

    [Fact]
    public async Task Sync_is_signature_gated_until_a_file_changes()
    {
        var (synchronizer, store, _) = Build(_primaryPath, _regionalPath);
        await synchronizer.EnsureSynced();
        store.Calls.Clear();

        await synchronizer.EnsureSynced();
        Assert.Empty(store.Calls);

        File.WriteAllText(_regionalPath, """
            { "title": "Regional View v2",
              "state": { "v": 3, "pipeline": [ { "shape": { "kind": "source" }, "layer": {} } ] } }
            """);
        File.SetLastWriteTimeUtc(_regionalPath, DateTime.UtcNow.AddMinutes(1));

        await synchronizer.EnsureSynced();
        var put = Assert.Single(store.Calls, call => call.StartsWith("put:", StringComparison.Ordinal));
        Assert.Equal("Regional View v2", store.Rows[put["put:".Length..]].Title);
    }

    [Fact]
    public async Task Sync_detects_a_length_change_when_the_file_timestamp_is_preserved()
    {
        var originalTimestamp = File.GetLastWriteTimeUtc(_regionalPath);
        var (synchronizer, store, _) = Build(_primaryPath, _regionalPath);
        await synchronizer.EnsureSynced();
        store.Calls.Clear();

        File.WriteAllText(_regionalPath, """
            { "title": "A substantially longer regional view title",
              "state": { "v": 3, "pipeline": [ { "shape": { "kind": "source" }, "layer": {} } ] } }
            """);
        File.SetLastWriteTimeUtc(_regionalPath, originalTimestamp);

        await synchronizer.EnsureSynced();

        var put = Assert.Single(store.Calls, call => call.StartsWith("put:", StringComparison.Ordinal));
        Assert.Equal("A substantially longer regional view title", store.Rows[put["put:".Length..]].Title);
    }

    [Fact]
    public async Task Sync_removes_configured_orphans_but_never_user_rows()
    {
        var (synchronizer, store, monitor) = Build(_primaryPath, _regionalPath);
        var userRow = new SavedReport
        {
            Id = SavedReport.NewId(),
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
    public async Task Sync_preserves_an_administrator_primary_override()
    {
        var (synchronizer, store, _) = Build(_primaryPath);
        await synchronizer.EnsureSynced();
        var row = Assert.Single(store.Rows.Values);
        Assert.True(row.IsPrimary);

        row.IsPrimary = false;
        File.WriteAllText(_primaryPath, """
            { "title": "Committed Primary v2", "primary": true,
              "state": { "v": 3, "pipeline": [ { "shape": { "kind": "source" }, "layer": {} } ] } }
            """);
        File.SetLastWriteTimeUtc(_primaryPath, DateTime.UtcNow.AddMinutes(1));

        await synchronizer.EnsureSynced();

        var updated = Assert.Single(store.Rows.Values);
        Assert.Equal("Committed Primary v2", updated.Title);
        Assert.False(updated.IsPrimary);
    }

    private sealed class RecordingStore : ISavedReportStore
    {
        public Dictionary<string, SavedReport> Rows { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];

        public Task<SavedReport?> Get(string id, CancellationToken ct = default)
        {
            Calls.Add($"get:{id}");
            return Task.FromResult(Rows.TryGetValue(id, out var row) ? row with { } : null);
        }

        public Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
        {
            Calls.Add("listAll");
            return Task.FromResult<IReadOnlyList<SavedReport>>([.. Rows.Values.Select(row => row with { })]);
        }

        public Task Create(SavedReport report, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> Update(SavedReport report, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task Put(SavedReport report, CancellationToken ct = default)
        {
            Calls.Add($"put:{report.Id}");
            Rows[report.Id] = report with { };
            return Task.CompletedTask;
        }

        public Task<bool> Delete(string id, CancellationToken ct = default)
        {
            Calls.Add($"delete:{id}");
            return Task.FromResult(Rows.Remove(id));
        }
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
