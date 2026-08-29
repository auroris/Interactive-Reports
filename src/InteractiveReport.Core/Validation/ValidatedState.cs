using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// The typed, schema-checked form of a state document. Only this — never the raw DTO —
/// reaches the query composer. The flat properties are the source stage's layer; View
/// carries the validated independent view (group/pivot/chart) including its own layer.
/// </summary>
public sealed class ValidatedState
{
    public required ReportSchema Schema { get; init; }
    public required ExpressionRulePlan Rules { get; init; }
    public string? Search { get; init; }
    public required IReadOnlyList<ValidSort> Sorts { get; init; }
    public required IReadOnlyList<ColumnModel> SelectColumns { get; init; }

    /// <summary>
    /// Grid row projection: displayed columns plus hidden source columns required by
    /// link/image renderers. Response and export column metadata use SelectColumns;
    /// export rendering may consume the additional row values.
    /// </summary>
    public required IReadOnlyList<ColumnModel> ProjectionColumns { get; init; }
    public required IReadOnlyDictionary<string, ColumnFormat> Formats { get; init; }
    public required IReadOnlyList<ValidAggregate> Aggregates { get; init; }
    /// <summary>Control-break columns; always members of SelectColumns so renderers can group.</summary>
    public required IReadOnlyList<ColumnModel> Breaks { get; init; }
    public required ValidView View { get; init; }
    public required int PageIndex { get; init; }
    /// <summary>Effective SQL page size; ignored when PageAll is true.</summary>
    public required int PageSize { get; init; }
    public required bool PageAll { get; init; }
    public required IReadOnlyList<IgnoredItem> Ignored { get; init; }

    /// <summary>
    /// The document's source-layer display labels (real column name → label), resolved
    /// during ingestion: request ?? default state ?? the definition's columnLabels.
    /// Query responses never consume these — the client renders its own labels — but a
    /// server-rendered artifact (export) applies them via WithDisplayLabels. Later
    /// stages' label overrides ride on the View and apply to export column metadata.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    /// <summary>
    /// Returns this state with source Labels applied to every column surface that feeds
    /// response metadata, so server-rendered output (CSV headers, synthetic
    /// sum(…) labels, Pivot cells) shows the document's names exactly as the client
    /// displays them. Column names are untouched — composition and row keys are
    /// unaffected — and query paths never call this. Stage-layer label overrides are
    /// applied by the export shaper on top of the metadata this produces.
    /// </summary>
    public ValidatedState WithDisplayLabels()
    {
        if (Labels.Count == 0) return this;

        ColumnModel Relabel(ColumnModel column)
            => Labels.TryGetValue(column.Name, out var label) && label != column.Label
                ? new ColumnModel
                {
                    Name = column.Name,
                    Label = label,
                    ClrType = column.ClrType,
                    IsNullable = column.IsNullable,
                    IsComputed = column.IsComputed,
                }
                : column;

        var view = View with
        {
            GroupBy = View.GroupBy.Select(Relabel).ToList(),
            PivotRows = View.PivotRows.Select(Relabel).ToList(),
            PivotCols = View.PivotCols.Select(Relabel).ToList(),
            Values = View.Values.Select(v => v with { Column = Relabel(v.Column) }).ToList(),
            Chart = View.Chart is null
                ? null
                : View.Chart with
                {
                    Label = Relabel(View.Chart.Label),
                    Value = View.Chart.Value is null ? null : Relabel(View.Chart.Value),
                },
        };
        if (View.GroupLayer is { } layer)
        {
            // Dims in the stage layer are pass-through source columns; metric and
            // computed labels rebuild from the relabeled view metadata downstream.
            view = view with
            {
                GroupLayer = layer with
                {
                    SelectColumns = layer.SelectColumns.Select(Relabel).ToList(),
                    Aggregates = layer.Aggregates
                        .Select(aggregate => aggregate with { Column = Relabel(aggregate.Column) })
                        .ToList(),
                    Breaks = layer.Breaks.Select(Relabel).ToList(),
                },
            };
        }

        return new ValidatedState
        {
            Schema = ReportSchema.Create("display", Schema.Columns.Select(Relabel)),
            Rules = Rules,
            Search = Search,
            Sorts = Sorts,
            SelectColumns = SelectColumns.Select(Relabel).ToList(),
            ProjectionColumns = ProjectionColumns.Select(Relabel).ToList(),
            Formats = Formats,
            Aggregates = Aggregates.Select(a => a with { Column = Relabel(a.Column) }).ToList(),
            Breaks = Breaks.Select(Relabel).ToList(),
            View = view,
            PageIndex = PageIndex,
            PageSize = PageSize,
            PageAll = PageAll,
            Ignored = Ignored,
            Labels = Labels,
        };
    }
}

/// <summary>
/// The validated pipeline tail. Grid is the bare source stage; GroupBy pushes a GROUP BY
/// down and paginates groups; Pivot uses the same grouped query (row + column dims) and
/// pivots in memory; Chart collapses the whole filtered set to (label, metric) points.
/// Values fall back to the implicit __count when empty.
/// </summary>
public sealed record ValidView(
    ViewMode Mode,
    IReadOnlyList<ColumnModel> GroupBy,
    IReadOnlyList<ColumnModel> PivotRows,
    IReadOnlyList<ColumnModel> PivotCols,
    IReadOnlyList<ValidMetric> Values,
    bool Totals = false,
    ValidChart? Chart = null,
    ValidStageLayer? GroupLayer = null,
    StageLayer? PivotLayer = null)
{
    public static readonly ValidView Grid = new(ViewMode.Grid, [], [], [], []);
}

/// <summary>
/// The validated layer of a group stage, bound to that stage's derived output schema
/// (dims + __count + metrics + layer computed). Computed and decoration rules push down
/// through one more SQL wrap; SelectColumns are the visible set when the stage is
/// terminal. Aggregates and control breaks operate over the completed, post-filter
/// group table, exactly as their source-layer counterparts operate over the filtered
/// source table. Labels apply to export metadata only.
/// </summary>
public sealed record ValidStageLayer(
    ReportSchema StageSchema,
    IReadOnlyList<CompiledRule<DefineColumnEffect>> Computed,
    IReadOnlyList<CompiledRule<IncludeRowEffect>> RowPredicates,
    IReadOnlyList<CompiledRule<HighlightEffect>> Decorations,
    IReadOnlyList<ValidSort> Sorts,
    IReadOnlyList<ColumnModel> SelectColumns,
    IReadOnlyList<ValidAggregate> Aggregates,
    IReadOnlyList<ColumnModel> Breaks,
    IReadOnlyDictionary<string, string> Labels)
{
    public static ValidStageLayer Empty(ReportSchema stageSchema, IReadOnlyList<ColumnModel> selectColumns)
        => new(stageSchema, [], [], [], [], selectColumns, [], [], new Dictionary<string, string>());
}

public enum ViewMode
{
    Grid,
    GroupBy,
    Pivot,
    Chart,
}

/// <summary>
/// The validated chart request: one label dimension and one numeric metric. Fn present
/// = group by label and aggregate value; Fn null = one point per filtered row. Value is
/// null only for count, which the composer turns into COUNT(*).
/// </summary>
public sealed record ValidChart(
    ChartType Type,
    ColumnModel Label,
    ColumnModel? Value,
    AggregateFn? Fn,
    ChartOrientation Orientation,
    ChartSortBy SortBy,
    SortDir SortDir,
    string? LabelAxisTitle,
    string? ValueAxisTitle);

public enum ChartType
{
    Bar,
    Line,
    Area,
    Pie,
}

public enum ChartOrientation
{
    Vertical,
    Horizontal,
}

public enum ChartSortBy
{
    Label,
    Value,
}

public sealed record ValidSort(ColumnModel Column, SortDir Dir, NullPlacement? Nulls = null);

public sealed record ValidAggregate(ColumnModel Column, AggregateFn Fn);

/// <summary>
/// A group-stage metric with its stable output column name. Id is the SQL alias, the
/// response column name, and the key every downstream reference uses.
/// </summary>
public sealed record ValidMetric(string Id, ColumnModel Column, AggregateFn Fn)
{
    public ValidAggregate ToAggregate() => new(Column, Fn);
}

/// <summary>A schema-bound expression shared by every expression-backed rule.</summary>
public sealed record BoundExpression(Expressions.ExprNode Ast)
{
    public ColumnKind Kind => Ast.Kind;
}

/// <summary>
/// One compiled expression plus the effect that consumes its value. The effect type
/// preserves domain metadata without splitting parsing and binding into separate paths.
/// </summary>
public sealed record CompiledRule<TEffect>(BoundExpression Expression, TEffect Effect)
    where TEffect : RuleEffect;

public abstract record RuleEffect;

public sealed record DefineColumnEffect(ColumnModel Column) : RuleEffect;

public sealed record IncludeRowEffect : RuleEffect;

public sealed record HighlightEffect(
    string Id,
    string Name,
    int Sequence,
    HighlightScope Scope,
    ColumnModel? Column,
    string ProjectionName) : RuleEffect;

/// <summary>
/// Explicit execution phases for the unified expression-rule pipeline. Definitions
/// extend the schema, row predicates shape the dataset, and decorations annotate the
/// final page.
/// </summary>
public sealed record ExpressionRulePlan(
    IReadOnlyList<CompiledRule<DefineColumnEffect>> Definitions,
    IReadOnlyList<CompiledRule<IncludeRowEffect>> RowPredicates,
    IReadOnlyList<CompiledRule<HighlightEffect>> Decorations);

public enum HighlightScope
{
    Row,
    Cell,
}
