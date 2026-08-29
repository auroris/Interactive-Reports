using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Definitions;

/// <summary>
/// Source of report definitions. Config-backed in v1; the interface exists so a
/// database-backed store (runtime-editable reports) can arrive without touching the engine.
/// </summary>
public interface IReportDefinitionStore
{
    ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default);
}

/// <summary>
/// Optional companion for definition stores that can resolve the small authorization
/// envelope without hydrating, validating, or connecting the executable report.
/// Endpoint authorization uses this first when available, then loads the full
/// definition only for callers that pass the report-level gate.
/// </summary>
public interface IReportDefinitionAuthorizationStore
{
    ValueTask<ReportDefinitionAuthorization?> FindAuthorization(
        string name,
        CancellationToken ct = default);
}

/// <summary>The report metadata required by the report-level authorization gate.</summary>
public sealed record ReportDefinitionAuthorization(
    string Name,
    ReportAuthorization? Authorization);
