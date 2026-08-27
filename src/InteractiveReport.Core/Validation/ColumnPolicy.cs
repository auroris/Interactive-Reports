using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// The definition's per-column sort/filter permissions, answered per bound column.
/// Restrictions attach to base schema columns only — computed columns (and stage
/// synthetics like metrics) are always allowed, so a report stays predictable
/// without transitive expression analysis. Enforcement is saved-state courtesy,
/// not a security boundary: violations degrade into ignored[], mirroring how
/// unknown columns behave.
/// </summary>
internal sealed class ColumnPolicy
{
    public static readonly ColumnPolicy Unrestricted = new(null);

    private readonly IReadOnlyDictionary<string, ReportColumnOverride>? _overrides;

    private ColumnPolicy(IReadOnlyDictionary<string, ReportColumnOverride>? overrides)
        => _overrides = overrides;

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

    public bool IsSortable(ColumnModel column)
        => column.IsComputed
            || _overrides is null
            || !_overrides.TryGetValue(column.Name, out var over)
            || over.Sortable != false;

    public bool IsFilterable(ColumnModel column)
        => column.IsComputed
            || _overrides is null
            || !_overrides.TryGetValue(column.Name, out var over)
            || over.Filterable != false;

    public bool HasFilterRestrictions
        => _overrides is not null && _overrides.Values.Any(over => over.Filterable == false);
}
