using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Mirrors configured report documents into the saved-report store as
/// read-only rows (Origin = Configured), so the store is the single listing surface
/// and the built-in saved-reports report can query everything with plain SQL. The
/// files remain the source of truth: rows are upserted under their stable cfg_ ids
/// whenever a file signature changes, and configured rows whose file is gone are
/// removed (which also self-handles moved or renamed files — their id changes).
/// A new row starts at the file mtime; subsequent content replacements advance that
/// value as an optimistic-concurrency revision, including when an mtime is preserved.
/// A file's primary value seeds a new row. After that the database flag is preserved,
/// making the administrator's flag/unflag action authoritative over file metadata.
/// </summary>
public sealed class ConfiguredReportDocumentSynchronizer : IDisposable
{
    private readonly ConfiguredReportDocumentStore _documents;
    private readonly ISavedReportStore _store;
    private readonly IOptionsMonitor<InteractiveReportOptions> _options;
    private readonly ReportConnectionRegistry _registry;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDisposable? _reloadSubscription;
    private string? _applied;

    /// <summary>
    /// Initializes a synchronizer and watches option reloads so the next request
    /// reconciles the newly configured document set.
    /// </summary>
    /// <param name="documents">The configured document source to mirror.</param>
    /// <param name="store">The saved-report store that receives configured rows.</param>
    /// <param name="options">The monitored options that select the store and documents.</param>
    /// <param name="registry">Resolves the concrete saved-report store target.</param>
    /// <param name="logger">Receives synchronization diagnostics; <see langword="null"/> disables logging.</param>
    /// <remarks>Subscribes to option changes. Dispose the synchronizer to release that subscription.</remarks>
    internal ConfiguredReportDocumentSynchronizer(
        ConfiguredReportDocumentStore documents,
        ISavedReportStore store,
        IOptionsMonitor<InteractiveReportOptions> options,
        ReportConnectionRegistry registry,
        ILogger<ConfiguredReportDocumentSynchronizer>? logger = null)
    {
        _documents = documents;
        _store = store;
        _options = options;
        _registry = registry;
        _logger = logger;
        _reloadSubscription = options.OnChange(_ => Volatile.Write(ref _applied, null));
    }

    /// <summary>
    /// Brings the store's configured rows up to date with the document files. It is cheap when
    /// nothing changed: one file stat per document plus an in-memory signature compare. The first pass per
    /// process also triggers the store's lazy table creation, so callers (including built-in definition
    /// resolution) need no separate readiness step. Failures propagate and retry on the next request.
    /// </summary>
    /// <param name="ct">Cancels lock acquisition and database operations.</param>
    /// <returns>A task that completes after all configured rows and orphans have been reconciled.</returns>
    /// <remarks>May insert, update, or delete saved-report rows and emit diagnostic log events.</remarks>
    public async Task EnsureSynced(CancellationToken ct = default)
    {
        if (Signature() == Volatile.Read(ref _applied))
        {
            _logger?.LogDebug("Configured report documents are already synchronized");
            return;
        }

        await _lock.WaitAsync(ct);
        try
        {
            // Recompute under the lock because another request may have applied it,
            // and the files may have changed again while we waited.
            var signature = Signature();
            if (signature == _applied) return;

            var documents = _documents.ListAll().ToArray();
            var desired = documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
            var existing = await _store.ListAll(ct);
            var byId = existing.ToDictionary(row => row.Id, StringComparer.Ordinal);
            var upserted = 0;
            var deleted = 0;

            foreach (var document in documents)
            {
                byId.TryGetValue(document.Id, out var current);
                while (true)
                {
                    var row = new SavedReport
                    {
                        Id = document.Id,
                        ReportName = document.ReportName,
                        Title = document.Title,
                        Owner = null,
                        IsGlobal = true,
                        IsPrimary = current?.IsPrimary ?? document.Primary,
                        StateJson = document.StateJson,
                        ModifiedUtc = document.ModifiedUtc,
                        Origin = SavedReportOrigin.Configured,
                    };
                    if (current is not null && !Differs(current, row)) break;

                    if (await _store.Put(row, current, ct))
                    {
                        byId[document.Id] = row;
                        upserted++;
                        break;
                    }

                    // Re-evaluate the administrator-owned primary bit after a concurrent
                    // mutation instead of applying the stale value that ListAll observed at the
                    // start of synchronization.
                    current = await _store.Get(document.Id, ct);
                }
            }

            foreach (var orphan in existing)
            {
                if (orphan.Origin == SavedReportOrigin.Configured && !desired.Contains(orphan.Id))
                {
                    await _store.Delete(orphan.Id, ct);
                    deleted++;
                }
            }

            _applied = signature;
            _logger?.LogInformation(
                "Synchronized {DocumentCount} configured report documents: {UpsertedCount} upserted, {DeletedCount} deleted",
                documents.Length,
                upserted,
                deleted);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Builds a signature from the store target and every document's identity, length, and modification time.
    /// The document store caches parses
    /// by length + mtime, so computing this costs one FileInfo stat per document and uses the same
    /// invalidation boundary.
    /// </summary>
    /// <returns>A deterministic string that changes when the target store or a configured file changes.</returns>
    private string Signature()
    {
        var cfg = _registry.ResolveStoreConfig(_options.CurrentValue.SavedReports);
        var parts = _documents.ListAll()
            .Select(document => $"{document.Id}:{document.Length}:{document.ModifiedUtc.Ticks}")
            .OrderBy(part => part, StringComparer.Ordinal);
        return $"{cfg.ConnectionName}|{cfg.Dialect}|{cfg.TableName}|{string.Join(";", parts)}";
    }

    /// <summary>
    /// Determines whether synchronization must replace an existing configured row.
    /// </summary>
    /// <param name="current">The row currently stored in the database.</param>
    /// <param name="desired">The row derived from the current configured document.</param>
    /// <returns><see langword="true"/> when persisted fields differ from the configured document; otherwise, <see langword="false"/>.</returns>
    private static bool Differs(SavedReport current, SavedReport desired)
        => current.Origin != desired.Origin
            || !current.IsGlobal
            || current.IsPrimary != desired.IsPrimary
            || current.Owner is not null
            || !string.Equals(current.ReportName, desired.ReportName, StringComparison.Ordinal)
            || !string.Equals(current.Title, desired.Title, StringComparison.Ordinal)
            || !string.Equals(current.StateJson, desired.StateJson, StringComparison.Ordinal);

    /// <summary>
    /// Releases the options-reload subscription and synchronization lock.
    /// </summary>
    public void Dispose()
    {
        _reloadSubscription?.Dispose();
        _lock.Dispose();
    }
}
