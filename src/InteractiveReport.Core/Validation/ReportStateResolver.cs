using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Resolves a partial request over a report's default state. Null means inherit;
/// an explicitly supplied empty string/list means clear the corresponding default.
/// </summary>
public static class ReportStateResolver
{
    public static ReportState Resolve(ReportState? defaults, ReportState requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        return new ReportState
        {
            V = requested.V,
            Search = requested.Search ?? defaults?.Search,
            Filters = Copy(requested.Filters ?? defaults?.Filters),
            Sorts = Copy(requested.Sorts ?? defaults?.Sorts),
            Columns = Copy(requested.Columns ?? defaults?.Columns),
            Labels = Copy(requested.Labels ?? defaults?.Labels),
            Computed = Copy(requested.Computed ?? defaults?.Computed),
            Breaks = Copy(requested.Breaks ?? defaults?.Breaks),
            Aggregates = Copy(requested.Aggregates ?? defaults?.Aggregates),
            Highlights = Copy(requested.Highlights ?? defaults?.Highlights),
            View = requested.View ?? defaults?.View,
            Page = requested.Page ?? defaults?.Page,
            Formats = Copy(requested.Formats ?? defaults?.Formats),
        };
    }

    private static List<T>? Copy<T>(List<T>? values) => values is null ? null : [.. values];

    private static Dictionary<string, string>? Copy(Dictionary<string, string>? values)
        => values is null ? null : new(values);

    private static Dictionary<string, ColumnFormat>? Copy(Dictionary<string, ColumnFormat>? values)
        => values?.ToDictionary(
            entry => entry.Key,
            entry => entry.Value is null
                ? new ColumnFormat()
                : new ColumnFormat
                {
                    Mask = entry.Value.Mask,
                    Align = entry.Value.Align,
                    Bold = entry.Value.Bold,
                    Italic = entry.Value.Italic,
                    Fg = entry.Value.Fg,
                    Bg = entry.Value.Bg,
                    Classes = Copy(entry.Value.Classes),
                });
}
