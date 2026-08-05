using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Definitions;

/// <summary>
/// Source of report definitions. Config-backed in v1; the interface exists so a
/// database-backed store (runtime-editable reports) can arrive without touching the engine.
/// </summary>
public interface IReportDefinitionStore
{
    ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default);

    /// <summary>All definitions; callers apply authorization filtering before exposure.</summary>
    ValueTask<IReadOnlyList<ReportDefinition>> List(CancellationToken ct = default);
}
