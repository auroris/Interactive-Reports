using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// The typed, schema-checked form of a state document. Only this — never the raw DTO —
/// reaches the query composer.
/// </summary>
public sealed class ValidatedState
{
    public required ReportSchema Schema { get; init; }
    public required ExpressionRulePlan Rules { get; init; }
    public string? Search { get; init; }
    public required IReadOnlyList<ValidSort> Sorts { get; init; }
    public required IReadOnlyList<ColumnModel> SelectColumns { get; init; }
    public required IReadOnlyList<ValidAggregate> Aggregates { get; init; }
    /// <summary>Control-break columns; always members of SelectColumns so renderers can group.</summary>
    public required IReadOnlyList<ColumnModel> Breaks { get; init; }
    public required ValidView View { get; init; }
    public required int PageIndex { get; init; }
    public required int PageSize { get; init; }
    public required IReadOnlyList<IgnoredItem> Ignored { get; init; }
}

/// <summary>
/// The validated alternate-view request. Grid is the default; groupBy pushes a GROUP BY
/// down and paginates groups; pivot uses the same grouped query (Rows+Cols dims) and is
/// transformed in memory. Values fall back to an implicit row count when empty. Chart
/// collapses the whole filtered set to (label, metric) points.
/// </summary>
public sealed record ValidView(
    ViewMode Mode,
    IReadOnlyList<ColumnModel> GroupBy,
    IReadOnlyList<ColumnModel> PivotRows,
    IReadOnlyList<ColumnModel> PivotCols,
    IReadOnlyList<ValidAggregate> Values,
    ValidChart? Chart = null)
{
    public static readonly ValidView Grid = new(ViewMode.Grid, [], [], [], []);
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

public sealed record ValidSort(ColumnModel Column, SortDir Dir);

public sealed record ValidAggregate(ColumnModel Column, AggregateFn Fn);

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
