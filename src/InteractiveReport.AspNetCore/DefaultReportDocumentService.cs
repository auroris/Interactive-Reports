using System.Text.Json;
using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Lazily creates and repairs the durable default document associated with one configured report family.
/// Configuration supplies execution rules; the database document supplies the client-visible identity
/// and initial state.
/// </summary>
internal sealed class DefaultReportDocumentService(ISavedReportStore store)
{
    /// <summary>Returns a stored document without attempting definition resolution.</summary>
    internal Task<SavedReport?> Get(long id, CancellationToken ct)
        => store.Get(id, ct);

    /// <summary>
    /// Processes a stored default against the current definition. Invalid stored JSON or report-state
    /// validation causes the row to be rebuilt in place from current appsettings.
    /// </summary>
    internal async Task<(SavedReport Report, ReportState State)> LoadState(
        SavedReport report,
        ReportDefinition definition,
        ReportExecutor executor,
        IReadOnlyDictionary<string, object?> contextParameters,
        CancellationToken ct)
    {
        if (!report.IsDefault)
            throw new InvalidOperationException("Only a flagged default report document can be auto-repaired.");

        try
        {
            var stored = JsonSerializer.Deserialize<ReportState>(
                    report.StateJson ?? throw new JsonException("The default report document has no state."),
                    IrJson.Options)
                ?? throw new JsonException("The default report document has no state.");
            var refreshed = await executor.RefreshSchemaCaches(
                definition, stored, contextParameters, ct);
            return (report, refreshed);
        }
        catch (JsonException)
        {
            return await Rebuild(report, definition, executor, contextParameters, ct);
        }
        catch (ReportValidationException)
        {
            return await Rebuild(report, definition, executor, contextParameters, ct);
        }
    }

    /// <summary>
    /// Creates the missing default under the configured report id. A concurrent winner is reloaded.
    /// </summary>
    internal async Task<SavedReport> CreateMissing(
        ReportDefinition definition,
        CancellationToken ct)
    {
        if (await store.FindDefault(definition.Name, ct) is { } existing) return existing;

        var report = Synthetic(definition);
        try
        {
            await store.Create(report, ct);
            return report;
        }
        catch (DbException)
        {
            if (await store.FindDefault(definition.Name, ct) is { } winner) return winner;
            throw;
        }
    }

    /// <summary>
    /// Replaces an invalid default state while retaining its stable id and presentation metadata.
    /// A concurrent replacement wins and is returned to the caller.
    /// </summary>
    internal async Task<SavedReport> Repair(
        SavedReport expected,
        ReportDefinition definition,
        ReportState state,
        CancellationToken ct)
    {
        if (!expected.IsDefault)
            throw new InvalidOperationException("Only a flagged default report document can be repaired.");

        var replacement = expected with
        {
            ReportName = definition.Name,
            Owner = null,
            IsGlobal = true,
            IsDefault = true,
            StateJson = JsonSerializer.Serialize(state, IrJson.Options),
        };
        if (await store.Update(replacement, expected, ct)) return replacement;
        return await store.Get(expected.Id, ct)
            ?? throw new InvalidOperationException(
                $"Default report document '{expected.Id}' disappeared during repair.");
    }

    private async Task<(SavedReport Report, ReportState State)> Rebuild(
        SavedReport expected,
        ReportDefinition definition,
        ReportExecutor executor,
        IReadOnlyDictionary<string, object?> contextParameters,
        CancellationToken ct)
    {
        var synthetic = EndpointExtensions.SchemaDefaultState(definition);
        var refreshed = await executor.RefreshSchemaCaches(
            definition, synthetic, contextParameters, ct);
        var repaired = await Repair(expected, definition, refreshed, ct);
        return (repaired, refreshed);
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
        IsPrimary = false,
        StateJson = JsonSerializer.Serialize(
            EndpointExtensions.SchemaDefaultState(definition),
            IrJson.Options),
        ModifiedUtc = DateTime.UtcNow,
        Origin = SavedReportOrigin.User,
    };
}
