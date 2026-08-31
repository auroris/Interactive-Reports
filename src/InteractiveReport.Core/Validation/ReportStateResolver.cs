using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Resolves a partial request over a report's default document. Each supplied top-level value, including
/// the complete page object and table map, replaces its default counterpart. Every
/// value is deep-copied so validation never mutates a cached default document.
/// </summary>
public static class ReportStateResolver
{
    /// <summary>
    /// Overlays a partial request on report defaults and returns a detached state document.
    /// </summary>
    /// <param name="defaults">The optional definition-owned default state.</param>
    /// <param name="requested">The caller-supplied state whose non-null properties override defaults.</param>
    /// <returns>A detached state containing requested values with null top-level properties filled from defaults.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requested"/> is <see langword="null"/>.</exception>
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

    /// <summary>
    /// Creates a detached copy of an optional page request.
    /// </summary>
    /// <param name="page">The page request to copy.</param>
    /// <returns>A new page request, or <see langword="null"/>.</returns>
    private static PageRequest? Copy(PageRequest? page)
        => page is null ? null : new PageRequest { Index = page.Index, Size = page.Size };

    /// <summary>
    /// Creates a detached copy of an optional list.
    /// </summary>
    /// <typeparam name="T">The list element type.</typeparam>
    /// <param name="values">The optional list to copy.</param>
    /// <returns>A new list with the same elements, or <see langword="null"/>.</returns>
    private static List<T>? Copy<T>(List<T>? values) => values is null ? null : [.. values];

    /// <summary>
    /// Creates a detached, case-sensitive copy of an optional string dictionary.
    /// </summary>
    /// <param name="values">The string map to copy.</param>
    /// <returns>A new dictionary with the same comparer-independent entries, or <see langword="null"/>.</returns>
    private static Dictionary<string, string>? Copy(Dictionary<string, string>? values)
        => values is null ? null : new(values);

    /// <summary>
    /// Creates a detached deep copy of the report table dictionary.
    /// </summary>
    /// <param name="tables">The case-insensitive table map to copy.</param>
    /// <returns>A new case-insensitive map containing deep-copied tables, or <see langword="null"/>.</returns>
    internal static Dictionary<string, ReportTable>? CopyTables(
        Dictionary<string, ReportTable>? tables)
        => tables?.ToDictionary(
            entry => entry.Key,
            entry => Copy(entry.Value),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a deep copy of one report table and its composables.
    /// </summary>
    /// <param name="table">The table definition to detach from its source document.</param>
    /// <returns>A new table with copied schema entries, composables, and nested rules.</returns>
    private static ReportTable Copy(ReportTable table)
        => new()
        {
            From = table.From,
            Schema = table.Schema?.Select(column => column with { }).ToList(),
            Composables = table.Composables?.Select(Copy).ToList(),
        };

    /// <summary>
    /// Creates a deep copy of one composable and all nested rule collections.
    /// </summary>
    /// <param name="composable">The composable declaration to detach from its source table.</param>
    /// <returns>A new composable with copied lists, maps, chart settings, and nested rule objects.</returns>
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

    /// <summary>
    /// Creates a deep copy of optional per-column format mappings.
    /// </summary>
    /// <param name="values">The per-column formats to copy.</param>
    /// <returns>A new map containing detached formats and class lists, or <see langword="null"/>.</returns>
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
