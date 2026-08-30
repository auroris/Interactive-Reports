using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>The schema-bound chart transformation and its renderer instructions.</summary>
internal sealed record ValidChart(
    ChartType Type,
    ColumnModel Label,
    ColumnModel? Value,
    AggregateFn? Fn,
    ChartOrientation Orientation,
    ChartSortBy SortBy,
    SortDir SortDir,
    string? LabelAxisTitle,
    string? ValueAxisTitle);

internal enum ChartType
{
    Bar,
    Line,
    Area,
    Pie,
}

internal enum ChartOrientation
{
    Vertical,
    Horizontal,
}

internal enum ChartSortBy
{
    Label,
    Value,
}

internal sealed record ValidSort(ColumnModel Column, SortDir Dir, NullPlacement? Nulls = null);

internal sealed record ValidAggregate(ColumnModel Column, AggregateFn Fn);

/// <summary>
/// A shape metric with a stable logical output identity shared by downstream relation
/// binding, response metadata, and document expressions.
/// </summary>
internal sealed record ValidMetric(string Id, ColumnModel Column, AggregateFn Fn)
{
    public ValidAggregate ToAggregate() => new(Column, Fn);
}

/// <summary>A schema-bound expression shared by every expression-backed rule.</summary>
internal sealed record BoundExpression(Expressions.ExprNode Ast)
{
    public ColumnKind Kind => Ast.Kind;
}

/// <summary>
/// One bound expression plus the typed effect that consumes its value. Keeping the
/// effect type explicit prevents relation definitions, predicates, and presentation
/// decorations from being interchanged accidentally.
/// </summary>
internal sealed record CompiledRule<TEffect>(BoundExpression Expression, TEffect Effect)
    where TEffect : RuleEffect;

internal abstract record RuleEffect;

internal sealed record DefineColumnEffect(ColumnModel Column) : RuleEffect;

internal sealed record IncludeRowEffect : RuleEffect;

internal sealed record HighlightEffect(
    string Id,
    string Name,
    int Sequence,
    HighlightScope Scope,
    ColumnModel? Column,
    string ProjectionName) : RuleEffect;

internal enum HighlightScope
{
    Row,
    Cell,
}
