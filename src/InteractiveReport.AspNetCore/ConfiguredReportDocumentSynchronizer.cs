using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Mirrors configured report documents into the saved-report store as
/// read-only rows (Origin = Configured), so the store is the single listing surface
/// and the built-in saved-reports report can query everything with plain SQL. The
/// files remain the source of truth: rows are upserted under their stable cfg_ ids
/// whenever a file signature changes, and configured rows whose file is gone are
/// removed (which also self-handles moved or renamed files — their id changes).
/// A file's primary value seeds a new row. After that the database flag is preserved,
/// making the administrator's flag/unflag action authoritative over file metadata.
/// </summary>
public sealed class ConfiguredReportDocumentSynchronizer : IDisposable
{
    private readonly ConfiguredReportDocumentStore _documents;
    private readonly ISavedReportStore _store;
    private readonly IOptionsMonitor<InteractiveReportOptions> _options;
    private readonly ReportConnectionRegistry _registry;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDisposable? _reloadSubscription;
    private string? _applied;

    internal ConfiguredReportDocumentSynchronizer(
        ConfiguredReportDocumentStore documents,
        ISavedReportStore store,
        IOptionsMonitor<InteractiveReportOptions> options,
        ReportConnectionRegistry registry)
    {
        _documents = documents;
        _store = store;
        _options = options;
        _registry = registry;
        _reloadSubscription = options.OnChange(_ => Volatile.Write(ref _applied, null));
    }

    /// <summary>
    /// Brings the store's configured rows up to date with the document files. Cheap
    /// when nothing changed: one file stat per document plus an in-memory signature
    /// compare. The first pass per process also triggers the store's lazy table
    /// creation, so callers (including built-in definition resolution) need no
    /// separate readiness step. Failures propagate and retry on the next request.
    /// </summary>
    public async Task EnsureSynced(CancellationToken ct = default)
    {
        if (Signature() == Volatile.Read(ref _applied)) return;

        await _lock.WaitAsync(ct);
        try
        {
            // Recompute under the lock: another request may have applied it, and the
            // files may have changed again while we waited.
            var signature = Signature();
            if (signature == _applied) return;

            var documents = _documents.ListAll().ToArray();
            var desired = documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
            var existing = await _store.ListAll(ct);
            var byId = existing.ToDictionary(row => row.Id, StringComparer.Ordinal);

            foreach (var document in documents)
            {
                var isPrimary = byId.TryGetValue(document.Id, out var current)
                    ? current.IsPrimary
                    : document.Primary;
                var row = new SavedReport
                {
                    Id = document.Id,
                    ReportName = document.ReportName,
                    Title = document.Title,
                    Owner = null,
                    IsGlobal = true,
                    IsPrimary = isPrimary,
                    StateJson = document.StateJson,
                    ModifiedUtc = document.ModifiedUtc,
                    Origin = SavedReportOrigin.Configured,
                };
                if (current is null || Differs(current, row))
                    await _store.Put(row, ct);
            }

            foreach (var orphan in existing)
            {
                if (orphan.Origin == SavedReportOrigin.Configured && !desired.Contains(orphan.Id))
                    await _store.Delete(orphan.Id, ct);
            }

            _applied = signature;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The store target plus every document's (id, mtime) pair. The
    /// document store caches parses by length + mtime, so computing this costs one
    /// FileInfo stat per document and uses the same invalidation boundary.
    /// </summary>
    private string Signature()
    {
        var cfg = _registry.ResolveStoreConfig(_options.CurrentValue.SavedReports);
        var parts = _documents.ListAll()
            .Select(document => $"{document.Id}:{document.Length}:{document.ModifiedUtc.Ticks}")
            .OrderBy(part => part, StringComparer.Ordinal);
        return $"{cfg.ConnectionName}|{cfg.Dialect}|{cfg.TableName}|{string.Join(";", parts)}";
    }

    private static bool Differs(SavedReport current, SavedReport desired)
        => current.Origin != desired.Origin
            || current.ModifiedUtc != desired.ModifiedUtc
            || !current.IsGlobal
            || current.IsPrimary != desired.IsPrimary
            || current.Owner is not null
            || !string.Equals(current.ReportName, desired.ReportName, StringComparison.Ordinal)
            || !string.Equals(current.Title, desired.Title, StringComparison.Ordinal)
            || !string.Equals(current.StateJson, desired.StateJson, StringComparison.Ordinal);

    public void Dispose()
    {
        _reloadSubscription?.Dispose();
        _lock.Dispose();
    }
}
