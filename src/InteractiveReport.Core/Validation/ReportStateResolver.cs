using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Resolves a partial request over a report's default document. Page resolves
/// property-wise; activeTable and tables replace their defaults when supplied. Every
/// value is deep-copied so validation never mutates a cached default document.
/// </summary>
public static class ReportStateResolver
{
    public static ReportState Resolve(ReportState? defaults, ReportState requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        return new ReportState
        {
            Search = requested.Search ?? defaults?.Search,
            Page = Copy(requested.Page ?? defaults?.Page),
            ActiveTable = requested.ActiveTable ?? defaults?.ActiveTable,
            Tables = CopyTables(requested.Tables ?? defaults?.Tables),
        };
    }

    private static PageRequest? Copy(PageRequest? page)
        => page is null ? null : new PageRequest { Index = page.Index, Size = page.Size };

    private static List<T>? Copy<T>(List<T>? values) => values is null ? null : [.. values];

    private static Dictionary<string, string>? Copy(Dictionary<string, string>? values)
        => values is null ? null : new(values);

    internal static Dictionary<string, ReportTable>? CopyTables(
        Dictionary<string, ReportTable>? tables)
        => tables?.ToDictionary(
            entry => entry.Key,
            entry => Copy(entry.Value),
            StringComparer.OrdinalIgnoreCase);

    private static ReportTable Copy(ReportTable table)
        => new()
        {
            From = table.From,
            Schema = table.Schema?.Select(column => column with { }).ToList(),
            Composables = table.Composables?.Select(Copy).ToList(),
        };

    private static TableComposable Copy(TableComposable composable)
        => new()
        {
            Kind = composable.Kind,
            By = Copy(composable.By),
            Rows = Copy(composable.Rows),
            Cols = Copy(composable.Cols),
            Values = composable.Values?.Select(value => new MetricRule
            {
                Id = value.Id,
                Col = value.Col,
                Fn = value.Fn,
            }).ToList(),
            Totals = composable.Totals,
            Type = composable.Type,
            Label = composable.Label,
            Value = composable.Value,
            Fn = composable.Fn,
            Orientation = composable.Orientation,
            Sort = composable.Sort is null
                ? null
                : new ChartSortSpec { By = composable.Sort.By, Dir = composable.Sort.Dir },
            LabelAxisTitle = composable.LabelAxisTitle,
            ValueAxisTitle = composable.ValueAxisTitle,
            Columns = Copy(composable.Columns),
            Labels = Copy(composable.Labels),
            Formats = Copy(composable.Formats),
            Computed = composable.Computed?.Select(rule => new ComputedColumn
            {
                Id = rule.Id,
                Label = rule.Label,
                Enabled = rule.Enabled,
                Expr = rule.Expr,
            }).ToList(),
            Filters = composable.Filters?.Select(rule => new FilterRule
            {
                Enabled = rule.Enabled,
                Expr = rule.Expr,
            }).ToList(),
            Sorts = composable.Sorts?.Select(rule => new SortRule
            {
                Col = rule.Col,
                Dir = rule.Dir,
                Nulls = rule.Nulls,
            }).ToList(),
            Highlights = composable.Highlights?.Select(rule => new HighlightRule
            {
                Id = rule.Id,
                Name = rule.Name,
                Sequence = rule.Sequence,
                Enabled = rule.Enabled,
                Expr = rule.Expr,
                Scope = rule.Scope,
                Col = rule.Col,
                Style = rule.Style is null
                    ? null
                    : new HighlightStyle { Bg = rule.Style.Bg, Fg = rule.Style.Fg },
            }).ToList(),
            Breaks = Copy(composable.Breaks),
            Aggregates = composable.Aggregates?.Select(rule => new AggregateRule
            {
                Col = rule.Col,
                Fn = rule.Fn,
            }).ToList(),
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
                    Command = entry.Value.Command,
                    KeyColumn = entry.Value.KeyColumn,
                });
}
