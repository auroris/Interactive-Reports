using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Answers the definition's per-column sort and filter permissions for bound columns.
/// Restrictions attach to definition-schema columns only — computed columns (and stage
/// synthetics like metrics) are always allowed, so a report stays predictable
/// without transitive expression analysis. Enforcement is saved-state courtesy,
/// not a security boundary: violations degrade into ignored[], mirroring how
/// unknown columns behave.
/// </summary>
internal sealed class ColumnPolicy
{
    /// <summary>A reusable policy that permits sorting and filtering every column.</summary>
    public static readonly ColumnPolicy Unrestricted = new(null);

    private readonly IReadOnlyDictionary<string, ReportColumnOverride>? _overrides;

    /// <summary>
    /// Creates a policy over a normalized, case-insensitive override map.
    /// </summary>
    /// <param name="overrides">The per-column capability overrides applied over discovered defaults.</param>
    private ColumnPolicy(IReadOnlyDictionary<string, ReportColumnOverride>? overrides)
        => _overrides = overrides;

    /// <summary>
    /// Normalizes the non-empty column overrides from a report definition into a policy view.
    /// </summary>
    /// <param name="def">The report definition containing optional per-column overrides.</param>
    /// <returns>An unrestricted policy when no effective overrides exist; otherwise, a policy over the normalized overrides.</returns>
    public static ColumnPolicy From(ReportDefinition def)
    {
        if (def.Columns is not { Count: > 0 }) return Unrestricted;
        var overrides = new Dictionary<string, ReportColumnOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, over) in def.Columns)
        {
            if (!string.IsNullOrWhiteSpace(name) && over is not null)
                overrides[name] = over;
        }
        return overrides.Count > 0 ? new ColumnPolicy(overrides) : Unrestricted;
    }

    /// <summary>
    /// Determines whether the column may be sorted for report-state validation.
    /// </summary>
    /// <param name="column">The bound column whose sort capability is required.</param>
    /// <returns><see langword="true"/> when the column may be sorted; otherwise, <see langword="false"/>.</returns>
    public bool IsSortable(ColumnModel column)
        => column.IsComputed
            || _overrides is null
            || !_overrides.TryGetValue(column.Name, out var over)
            || over.Sortable != false;

    /// <summary>
    /// Determines whether the column may be filtered for report-state validation.
    /// </summary>
    /// <param name="column">The bound column whose filter capability is required.</param>
    /// <returns><see langword="true"/> when the column may be filtered; otherwise, <see langword="false"/>.</returns>
    public bool IsFilterable(ColumnModel column)
        => column.IsComputed
            || _overrides is null
            || !_overrides.TryGetValue(column.Name, out var over)
            || over.Filterable != false;

    /// <summary>Gets whether at least one definition column explicitly disables filtering.</summary>
    public bool HasFilterRestrictions
        => _overrides is not null && _overrides.Values.Any(over => over.Filterable == false);
}
