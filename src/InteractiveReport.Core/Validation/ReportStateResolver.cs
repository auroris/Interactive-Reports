using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Resolves a partial request over a report's default state. Search and page resolve
/// property-wise (null inherits; explicit empty clears); the pipeline, shelf, and
/// schema snapshot replace the default wholesale when present — stage arrays do not
/// merge. Everything is deep-copied so validation never mutates a cached default.
/// </summary>
public static class ReportStateResolver
{
    public static ReportState Resolve(ReportState? defaults, ReportState requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        return new ReportState
        {
            V = requested.V,
            Schema = Copy(requested.Schema ?? defaults?.Schema),
            Search = requested.Search ?? defaults?.Search,
            Page = requested.Page ?? defaults?.Page,
            Pipeline = CopyPipeline(requested.Pipeline ?? defaults?.Pipeline),
            Shelf = CopyShelf(requested.Shelf ?? defaults?.Shelf),
        };
    }

    private static List<T>? Copy<T>(List<T>? values) => values is null ? null : [.. values];

    private static Dictionary<string, string>? Copy(Dictionary<string, string>? values)
        => values is null ? null : new(values);

    internal static List<PipelineStage>? CopyPipeline(List<PipelineStage>? stages)
        => stages?.Select(Copy).ToList();

    private static Dictionary<string, List<PipelineStage>>? CopyShelf(
        Dictionary<string, List<PipelineStage>>? shelf)
        => shelf?.ToDictionary(
            entry => entry.Key,
            entry => CopyPipeline(entry.Value) ?? [],
            StringComparer.OrdinalIgnoreCase);

    private static PipelineStage Copy(PipelineStage stage)
        => new()
        {
            Shape = Copy(stage.Shape),
            Layer = Copy(stage.Layer),
        };

    private static StageShape? Copy(StageShape? shape)
        => shape is null
            ? null
            : new StageShape
            {
                Kind = shape.Kind,
                By = Copy(shape.By),
                Values = shape.Values?.Select(value => new MetricRule
                {
                    Id = value.Id,
                    Col = value.Col,
                    Fn = value.Fn,
                }).ToList(),
                Cols = Copy(shape.Cols),
                Totals = shape.Totals,
                Type = shape.Type,
                Label = shape.Label,
                Value = shape.Value,
                Fn = shape.Fn,
                Orientation = shape.Orientation,
                Sort = shape.Sort is null
                    ? null
                    : new ChartSortSpec { By = shape.Sort.By, Dir = shape.Sort.Dir },
                LabelAxisTitle = shape.LabelAxisTitle,
                ValueAxisTitle = shape.ValueAxisTitle,
            };

    private static StageLayer? Copy(StageLayer? layer)
        => layer is null
            ? null
            : new StageLayer
            {
                Columns = Copy(layer.Columns),
                Labels = Copy(layer.Labels),
                Formats = Copy(layer.Formats),
                Computed = layer.Computed?.Select(rule => new ComputedColumn
                {
                    Id = rule.Id,
                    Label = rule.Label,
                    Enabled = rule.Enabled,
                    Expr = rule.Expr,
                }).ToList(),
                Filters = layer.Filters?.Select(rule => new FilterRule
                {
                    Enabled = rule.Enabled,
                    Expr = rule.Expr,
                }).ToList(),
                Sorts = layer.Sorts?.Select(rule => new SortRule
                {
                    Col = rule.Col,
                    Dir = rule.Dir,
                    Nulls = rule.Nulls,
                }).ToList(),
                Highlights = layer.Highlights?.Select(rule => new HighlightRule
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
                Breaks = Copy(layer.Breaks),
                Aggregates = layer.Aggregates?.Select(rule => new AggregateRule
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
                });
}
