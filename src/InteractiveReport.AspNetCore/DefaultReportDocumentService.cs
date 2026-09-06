using System.Text.Json;
using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Creates missing default documents and reads their persisted state without implicit repairs.
/// Configuration supplies execution rules; the database document supplies the client-visible identity
/// and initial state.
/// </summary>
internal sealed class DefaultReportDocumentService(
    ISavedReportStore store,
    ILogger? logger = null)
{
    /// <summary>Loads and validates the stored default without changing its persistent document.</summary>
    internal async Task<ReportState> LoadState(
        SavedReport report,
        ReportDefinition definition,
        ReportExecutor executor,
        IReadOnlyDictionary<string, object?> contextParameters,
        CancellationToken ct)
    {
        var stored = JsonSerializer.Deserialize<ReportState>(
                report.StateJson ?? throw new JsonException("The default report document has no state."),
                IrJson.Options)
            ?? throw new JsonException("The default report document has no state.");
        return await executor.RefreshSchemaCaches(definition, stored, contextParameters, ct);
    }
    /// <summary>
    /// Creates the missing default for the configured report family. A concurrent winner is reloaded.
    /// </summary>
    internal async Task<SavedReport> CreateMissing(
        ReportDefinition definition,
        CancellationToken ct)
    {
        var family = await store.ListFamily(definition.Name, ct);
        return await CreateMissing(definition, family, ct);
    }

    /// <summary>
    /// Creates a missing default from a complete family snapshot already loaded for listing.
    /// The ordinary path performs no second database read; only a concurrent write race is re-read.
    /// </summary>
    internal async Task<SavedReport> CreateMissing(
        ReportDefinition definition,
        IReadOnlyCollection<SavedReport> databaseFamily,
        CancellationToken ct)
    {
        if (databaseFamily.SingleOrDefault(report => report.IsDefault) is { } existing)
            return existing;

        var dormant = databaseFamily.SingleOrDefault(report =>
            report.Origin == SavedReportOrigin.Synthetic
            && string.Equals(report.ReportName, definition.Name, StringComparison.OrdinalIgnoreCase));
        if (dormant is not null)
        {
            var restored = dormant with { IsDefault = true, IsGlobal = true };
            if (await store.Update(restored, dormant, ct)) return restored;
            if (await store.FindDefault(definition.Name, ct) is { } winner) return winner;
            return await CreateMissing(definition, ct);
        }

        var report = Synthetic(definition);
        try
        {
            await store.Create(report, ct);
            return report;
        }
        catch (Exception insertException)
            when (insertException is DbException or SavedReportTitleConflictException)
        {
            try
            {
                if (await store.FindDefault(definition.Name, ct) is { } winner) return winner;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw BootstrapFailure(definition.Name, ex);
            }
            throw BootstrapFailure(definition.Name, insertException);
        }
    }

    /// <summary>Builds the first persisted form of a configured report's synthetic document.</summary>
    private static SavedReport Synthetic(ReportDefinition definition) => new()
    {
        Id = 0,
        ReportName = definition.Name,
        Title = definition.Title ?? ColumnModel.Prettify(definition.Name),
        Owner = null,
        IsGlobal = true,
        IsDefault = true,
        StateJson = JsonSerializer.Serialize(
            ReportDocumentDefaults.Create(definition),
            IrJson.Options),
        ModifiedUtc = DateTime.UtcNow,
        Origin = SavedReportOrigin.Synthetic,
    };

    private ReportDocumentBootstrapException BootstrapFailure(
        string reportName,
        Exception? exception = null)
    {
        var failure = new ReportDocumentBootstrapException(
            reportName,
            "synthetic default report document",
            exception);
        logger?.LogError(
            failure,
            "Report {ReportName}: failed to insert the synthetic default report document; the family has no loadable default document",
            reportName);
        return failure;
    }
}
