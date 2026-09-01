using System.Data.Common;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Reconciles configured report-document file references with the database catalogue. Listing
/// supplies the database's complete, unfiltered view before caller visibility is applied; the
/// in-memory appsettings comparison opens only configured files whose identities are absent.
/// </summary>
public sealed class ConfiguredReportDocumentSynchronizer : IDisposable
{
    private readonly ConfiguredReportDocumentStore _documents;
    private readonly ISavedReportStore _store;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Initializes configured report-document reconciliation.</summary>
    internal ConfiguredReportDocumentSynchronizer(
        ConfiguredReportDocumentStore documents,
        ISavedReportStore store,
        ILogger<ConfiguredReportDocumentSynchronizer>? logger = null)
    {
        _documents = documents;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles every report family from one complete database query. Retained for explicit
    /// host and administration synchronization; normal document listings consume the returned
    /// snapshot through <see cref="ReconcileAll"/>.
    /// </summary>
    public async Task EnsureSynced(CancellationToken ct = default)
        => _ = await ReconcileAll(ct);

    /// <summary>
    /// Loads every database report once, reconciles configured identities, and returns the
    /// corrected unfiltered snapshot for authorization and owner/public filtering in memory.
    /// </summary>
    internal async Task<IReadOnlyList<SavedReport>> ReconcileAll(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var databaseReports = await _store.ListAll(ct);
            return await Reconcile(databaseReports, reportName: null, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Loads the complete family selected by <paramref name="reportName"/> in one query and
    /// reconciles that configured family even when the database does not contain a document yet.
    /// </summary>
    internal async Task<IReadOnlyList<SavedReport>> ReconcileFamily(
        string reportName,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var databaseReports = await _store.ListFamily(reportName, ct);
            return await Reconcile(databaseReports, reportName, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Compares one authoritative database snapshot with configured file references, applying
    /// only the missing, superseded-default, and orphan discrepancies it finds.
    /// </summary>
    private async Task<IReadOnlyList<SavedReport>> Reconcile(
        IReadOnlyList<SavedReport> databaseReports,
        string? reportName,
        CancellationToken ct)
    {
        var references = _documents.ListReferences(reportName).ToArray();
        var desired = references
            .Select(reference => (reference.ReportName, reference.SourceFile))
            .ToHashSet();
        var current = databaseReports.ToDictionary(report => report.Id, report => report with { });
        var bySourceFile = current.Values
            .Where(row => row.Origin == SavedReportOrigin.Configured && row.SourceFile is not null)
            .ToDictionary(row => (row.ReportName, row.SourceFile!));
        var upserted = 0;
        var deleted = 0;

        // Existing database identities are the optimistic truth. A configured reference absent
        // from that complete snapshot is the only case that opens its file to seed metadata.
        var missingDocuments = new List<ConfiguredReportDocument>();
        foreach (var reference in references)
        {
            if (bySourceFile.ContainsKey((reference.ReportName, reference.SourceFile)))
                continue;
            if (_documents.Find(reference.ReportName, reference.SourceFile) is { } document)
                missingDocuments.Add(document);
            else
                _logger?.LogWarning(
                    "Configured report document {ReportName}/{SourceFile} has no database identity because its file is absent",
                    reference.ReportName,
                    reference.SourceFile);
        }

        // A newly discovered configured default replaces the database selection. Internal
        // removals are unconditional by numeric id; deleting an already-absent row is harmless.
        foreach (var configuredDefault in missingDocuments.Where(document => document.Default))
        {
            var currentDefault = CurrentDefault(current.Values, configuredDefault.ReportName);
            while (currentDefault is not null)
            {
                if (currentDefault.Origin == SavedReportOrigin.Configured
                    && string.Equals(
                        currentDefault.SourceFile,
                        configuredDefault.SourceFile,
                        StringComparison.Ordinal))
                    break;

                if (currentDefault.Origin == SavedReportOrigin.Synthetic)
                {
                    await _store.Delete(currentDefault.Id, ct);
                    current.Remove(currentDefault.Id);
                    deleted++;
                    break;
                }

                var demoted = currentDefault with { IsDefault = false, IsGlobal = true };
                if (await _store.Update(demoted, currentDefault, ct))
                {
                    current[demoted.Id] = demoted;
                    if (demoted.SourceFile is not null)
                        bySourceFile[(demoted.ReportName, demoted.SourceFile)] = demoted;
                    upserted++;
                    break;
                }

                // A concurrent default change is exceptional. Refresh only this fact and retry;
                // the normal reconciliation path remains one database snapshot query.
                currentDefault = await _store.FindDefault(configuredDefault.ReportName, ct);
                if (currentDefault is not null) current[currentDefault.Id] = currentDefault;
            }

            foreach (var synthetic in current.Values.Where(report =>
                         report.Origin == SavedReportOrigin.Synthetic
                         && string.Equals(
                             report.ReportName,
                             configuredDefault.ReportName,
                             StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                await _store.Delete(synthetic.Id, ct);
                current.Remove(synthetic.Id);
                deleted++;
            }
        }

        foreach (var document in missingDocuments)
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
                current[row.Id] = row with { };
                bySourceFile[sourceIdentity] = row;
                upserted++;
            }
            catch (DbException ex)
            {
                var winner = await FindConcurrentConfiguredWinner(document, ct);
                if (winner is null)
                    throw LogBootstrapFailure(
                        document.ReportName,
                        $"configured report document '{document.SourceFile}'",
                        ex);
                current[winner.Id] = winner;
                bySourceFile[sourceIdentity] = winner;
            }
        }

        foreach (var orphan in current.Values.Where(report =>
                     report.Origin == SavedReportOrigin.Configured
                     && (report.SourceFile is null
                         || !desired.Contains((report.ReportName, report.SourceFile)))).ToArray())
        {
            await _store.Delete(orphan.Id, ct);
            current.Remove(orphan.Id);
            deleted++;
        }

        if (upserted == 0 && deleted == 0)
            _logger?.LogDebug(
                "Configured report documents already match {DocumentCount} configured references",
                references.Length);
        else
            _logger?.LogInformation(
                "Reconciled {DocumentCount} configured report documents: {UpsertedCount} upserted, {DeletedCount} deleted",
                references.Length,
                upserted,
                deleted);

        return current.Values
            .OrderBy(report => report.ReportName, StringComparer.Ordinal)
            .ThenByDescending(report => report.IsDefault)
            .ThenByDescending(report => report.IsGlobal)
            .ThenBy(report => report.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SavedReport? CurrentDefault(
        IEnumerable<SavedReport> reports,
        string reportName)
        => reports.SingleOrDefault(report =>
            report.IsDefault
            && string.Equals(report.ReportName, reportName, StringComparison.OrdinalIgnoreCase));

    private async Task<SavedReport?> FindConcurrentConfiguredWinner(
        ConfiguredReportDocument document,
        CancellationToken ct)
    {
        try
        {
            return await _store.FindConfiguredFile(
                document.ReportName, document.SourceFile, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw LogBootstrapFailure(
                document.ReportName,
                $"configured report document '{document.SourceFile}'",
                ex);
        }
    }

    private ReportDocumentBootstrapException LogBootstrapFailure(
        string reportName,
        string document,
        Exception? exception = null)
    {
        var failure = new ReportDocumentBootstrapException(reportName, document, exception);
        _logger?.LogError(
            failure,
            "Report {ReportName}: failed to insert {Document}; the family has no loadable default document",
            reportName,
            document);
        return failure;
    }

    /// <summary>Deletes a configured identity whose referenced body was absent.</summary>
    internal Task RemoveMissing(SavedReport report, CancellationToken ct)
        => _store.Delete(report.Id, ct);

    /// <summary>Logs and deletes a configured identity whose body failed loading or processing.</summary>
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
        await _store.Delete(report.Id, ct);
    }

    /// <summary>Releases the synchronization lock.</summary>
    public void Dispose() => _lock.Dispose();
}

/// <summary>Signals that a family bootstrap insert failed after no durable default remained.</summary>
internal sealed class ReportDocumentBootstrapException(
    string reportName,
    string document,
    Exception? innerException = null)
    : Exception($"Report '{reportName}' could not persist its {document}.", innerException)
{
    internal string ReportName { get; } = reportName;
}
