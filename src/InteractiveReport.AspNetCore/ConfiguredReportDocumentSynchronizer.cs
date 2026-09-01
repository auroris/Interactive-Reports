using InteractiveReport.Core.SavedReports;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Creates database identities for configured report-document files so persistence is the
/// single listing surface and every endpoint can use a numeric document id. The database row
/// is the optimistic authority for existence, title, and default selection; the JSON file is
/// dereferenced only for its state body. Removing or renaming a configured source removes its
/// old identity. A configured default supersedes the synthetic default for its report family.
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
    /// Brings configured source references and database identities into agreement. Existing
    /// identities are trusted without probing their files. Only a configured source lacking an
    /// identity is opened so its initial database metadata can be recorded.
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

            var references = _documents.ListReferences().ToArray();
            var desired = references
                .Select(reference => (reference.ReportName, reference.SourceFile))
                .ToHashSet();
            var existing = await _store.ListAll(ct);
            var bySourceFile = existing
                .Where(row => row.SourceFile is not null)
                .ToDictionary(row => (row.ReportName, row.SourceFile!));
            var upserted = 0;
            var deleted = 0;
            var unresolved = false;

            // The database is the optimistic catalogue. Do not touch files for identities
            // that already exist. A new configured reference must be read once to seed its
            // title and default bit; an absent new file remains absent from the database and
            // is retried on a later synchronization.
            var documents = new List<ConfiguredReportDocument>();
            foreach (var reference in references)
            {
                if (bySourceFile.ContainsKey((reference.ReportName, reference.SourceFile)))
                    continue;
                if (_documents.Find(reference.ReportName, reference.SourceFile) is { } document)
                    documents.Add(document);
                else
                {
                    unresolved = true;
                    _logger?.LogWarning(
                        "Configured report document {ReportName}/{SourceFile} has no database identity because its file is absent",
                        reference.ReportName,
                        reference.SourceFile);
                }
            }

            // A configured file explicitly marked as default supersedes the database selection.
            // User-authored predecessors remain global; configured predecessors are demoted; every
            // synthetic fallback is removed before inserting the configured public identity.
            foreach (var configuredDefault in documents.Where(document => document.Default))
            {
                while (await _store.FindDefault(configuredDefault.ReportName, ct) is { } currentDefault)
                {
                    if (currentDefault.Origin == SavedReportOrigin.Configured
                        && string.Equals(
                            currentDefault.SourceFile,
                            configuredDefault.SourceFile,
                            StringComparison.Ordinal))
                        break;

                    if (currentDefault.Origin == SavedReportOrigin.Synthetic)
                    {
                        if (await _store.Delete(currentDefault, ct))
                        {
                            deleted++;
                            break;
                        }
                        continue;
                    }

                    var demoted = currentDefault with { IsDefault = false, IsGlobal = true };
                    if (!await _store.Update(demoted, currentDefault, ct)) continue;
                    if (demoted.SourceFile is not null)
                        bySourceFile[(demoted.ReportName, demoted.SourceFile)] = demoted;
                    upserted++;
                    break;
                }

                foreach (var synthetic in existing.Where(report =>
                             report.Origin == SavedReportOrigin.Synthetic
                             && string.Equals(
                                 report.ReportName,
                                 configuredDefault.ReportName,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    if (await _store.Delete(synthetic.Id, ct)) deleted++;
                }
            }

            foreach (var document in documents)
            {
                var sourceIdentity = (document.ReportName, document.SourceFile);
                var row = new SavedReport
                {
                    Id = 0,
                    ReportName = document.ReportName,
                    SourceFile = document.SourceFile,
                    Title = document.Title,
                    Owner = null,
                    IsGlobal = true,
                    IsDefault = document.Default,
                    StateJson = null,
                    ModifiedUtc = document.ModifiedUtc,
                    Origin = SavedReportOrigin.Configured,
                };
                try
                {
                    await _store.Create(row, ct);
                    bySourceFile[sourceIdentity] = row;
                    upserted++;
                }
                catch (DbException)
                {
                    var winner = await _store.FindConfiguredFile(
                        document.ReportName, document.SourceFile, ct);
                    if (winner is null) throw;
                    bySourceFile[sourceIdentity] = winner;
                }
            }

            foreach (var orphan in existing)
            {
                if (orphan.Origin == SavedReportOrigin.Configured
                    && (orphan.SourceFile is null
                        || !desired.Contains((orphan.ReportName, orphan.SourceFile))))
                {
                    await _store.Delete(orphan.Id, ct);
                    deleted++;
                }
            }

            _applied = unresolved ? null : signature;
            _logger?.LogInformation(
                "Synchronized {DocumentCount} configured report documents: {UpsertedCount} upserted, {DeletedCount} deleted",
                references.Length,
                upserted,
                deleted);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Builds a signature from the store target and configured source references without
    /// probing any file body.
    /// </summary>
    /// <returns>A deterministic string that changes when the target store or a configured file changes.</returns>
    private string Signature()
    {
        var cfg = _registry.ResolveStoreConfig(_options.CurrentValue.SavedReports);
        var parts = _documents.ListReferences()
            .Select(document => $"{document.ReportName}:{document.SourceFile}")
            .OrderBy(part => part, StringComparer.Ordinal);
        return $"{cfg.ConnectionName}|{cfg.Dialect}|{cfg.TableName}|{string.Join(";", parts)}";
    }

    /// <summary>
    /// Deletes a configured identity whose referenced body was absent and invalidates the
    /// reconciliation cache so a later deployment of that file can create a new identity.
    /// </summary>
    internal async Task RemoveMissing(SavedReport report, CancellationToken ct)
    {
        await _store.Delete(report, ct);
        Volatile.Write(ref _applied, null);
    }

    /// <summary>
    /// Logs and deletes a configured identity whose file body could not be loaded or processed.
    /// The configured reference remains authoritative, so invalidating reconciliation causes the
    /// next synchronization attempt to create a fresh optimistic identity for another load attempt.
    /// </summary>
    internal async Task RemoveInvalid(
        SavedReport report,
        Exception exception,
        CancellationToken ct)
    {
        _logger?.LogWarning(
            exception,
            "Configured report document {ReportName}/{SourceFile} (id {SavedReportId}) threw while loading; deleting its optimistic database identity",
            report.ReportName,
            report.SourceFile,
            report.Id);
        await _store.Delete(report, ct);
        Volatile.Write(ref _applied, null);
    }

    /// <summary>
    /// Releases the options-reload subscription and synchronization lock.
    /// </summary>
    public void Dispose()
    {
        _reloadSubscription?.Dispose();
        _lock.Dispose();
    }
}
