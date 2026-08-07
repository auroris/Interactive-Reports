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
            View = Copy(requested.View ?? defaults?.View),
            Views = CopyViews(requested.Views ?? defaults?.Views),
            Page = requested.Page ?? defaults?.Page,
            Formats = Copy(requested.Formats ?? defaults?.Formats),
        };
    }

    private static List<T>? Copy<T>(List<T>? values) => values is null ? null : [.. values];

    private static Dictionary<string, string>? Copy(Dictionary<string, string>? values)
        => values is null ? null : new(values);

    private static Dictionary<string, ViewSpec>? CopyViews(Dictionary<string, ViewSpec>? values)
        => values?.ToDictionary(
            entry => entry.Key,
            entry => Copy(entry.Value) ?? new ViewSpec(),
            StringComparer.OrdinalIgnoreCase);

    private static ViewSpec? Copy(ViewSpec? view)
        => view is null
            ? null
            : new ViewSpec
            {
                Mode = view.Mode,
                GroupBy = Copy(view.GroupBy),
                Rows = Copy(view.Rows),
                Cols = Copy(view.Cols),
                Values = view.Values?.Select(value => new AggregateRule
                {
                    Col = value.Col,
                    Fn = value.Fn,
                }).ToList(),
                Totals = view.Totals,
                Type = view.Type,
                Label = view.Label,
                Value = view.Value,
                Fn = view.Fn,
                Orientation = view.Orientation,
                Sort = view.Sort is null
                    ? null
                    : new ChartSortSpec { By = view.Sort.By, Dir = view.Sort.Dir },
                LabelAxisTitle = view.LabelAxisTitle,
                ValueAxisTitle = view.ValueAxisTitle,
            };

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
                    DisplayAs = entry.Value.DisplayAs,
                    UrlColumn = entry.Value.UrlColumn,
                    TextColumn = entry.Value.TextColumn,
                });
}
