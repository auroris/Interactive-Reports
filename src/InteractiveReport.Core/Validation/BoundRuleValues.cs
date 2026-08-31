using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>Contains a schema-bound chart transformation and its renderer instructions.</summary>
/// <param name="Type">The chart family to render.</param>
/// <param name="Label">The column supplying category labels.</param>
/// <param name="Value">The optional column supplying metric inputs.</param>
/// <param name="Fn">The aggregate applied to <paramref name="Value"/>, or <see langword="null"/> for an implicit row count.</param>
/// <param name="Orientation">The requested axis orientation.</param>
/// <param name="SortBy">Whether chart points are ordered by label or value.</param>
/// <param name="SortDir">The requested point sort direction.</param>
/// <param name="LabelAxisTitle">The optional category-axis title.</param>
/// <param name="ValueAxisTitle">The optional value-axis title.</param>
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

/// <summary>Names the chart renderer selected by a validated chart shape.</summary>
internal enum ChartType
{
    /// <summary>Bar chart.</summary>
    Bar,
    /// <summary>Line chart.</summary>
    Line,
    /// <summary>Area chart.</summary>
    Area,
    /// <summary>Pie chart.</summary>
    Pie,
}

/// <summary>Specifies how a bar chart assigns categories and values to axes.</summary>
internal enum ChartOrientation
{
    /// <summary>Categories run along the horizontal axis.</summary>
    Vertical,
    /// <summary>Categories run along the vertical axis.</summary>
    Horizontal,
}

/// <summary>Specifies which chart field controls point ordering.</summary>
internal enum ChartSortBy
{
    /// <summary>Order by category label.</summary>
    Label,
    /// <summary>Order by computed metric value.</summary>
    Value,
}

/// <summary>Contains a schema-bound sort rule.</summary>
/// <param name="Column">The column to order by.</param>
/// <param name="Dir">The sort direction.</param>
/// <param name="Nulls">The explicit null placement, or <see langword="null"/> for the dialect default.</param>
internal sealed record ValidSort(ColumnModel Column, SortDir Dir, NullPlacement? Nulls = null);

/// <summary>Contains a schema-bound aggregate rule.</summary>
/// <param name="Column">The aggregate input column.</param>
/// <param name="Fn">The validated aggregate function.</param>
internal sealed record ValidAggregate(ColumnModel Column, AggregateFn Fn);

/// <summary>
/// A shape metric with a stable logical output identity shared by downstream relation
/// binding, response metadata, and document expressions.
/// </summary>
/// <param name="Id">The authored metric id used as the downstream logical column name.</param>
/// <param name="Column">The aggregate input column.</param>
/// <param name="Fn">The validated aggregate function.</param>
internal sealed record ValidMetric(string Id, ColumnModel Column, AggregateFn Fn)
{
    /// <summary>
    /// Drops binding-only metadata and returns the validated aggregate rule used by execution.
    /// </summary>
    /// <returns>An aggregate over the same bound column and function.</returns>
    public ValidAggregate ToAggregate() => new(Column, Fn);
}

/// <summary>Wraps a schema-bound expression shared by every expression-backed rule.</summary>
/// <param name="Ast">The typed expression tree.</param>
internal sealed record BoundExpression(Expressions.ExprNode Ast)
{
    /// <summary>Gets the portable result kind inferred by the expression binder.</summary>
    public ColumnKind Kind => Ast.Kind;
}

/// <summary>
/// One bound expression plus the typed effect that consumes its value. Keeping the
/// effect type explicit prevents relation definitions, predicates, and presentation
/// decorations from being interchanged accidentally.
/// </summary>
/// <typeparam name="TEffect">The relation or presentation effect produced by the expression.</typeparam>
/// <param name="Expression">The bound expression to emit or evaluate.</param>
/// <param name="Effect">The typed consumer of the expression result.</param>
internal sealed record CompiledRule<TEffect>(BoundExpression Expression, TEffect Effect)
    where TEffect : RuleEffect;

/// <summary>Base type for the effects attached to compiled expression rules.</summary>
internal abstract record RuleEffect;

/// <summary>Defines a projected computed column from an expression result.</summary>
/// <param name="Column">The synthetic column introduced by the rule.</param>
internal sealed record DefineColumnEffect(ColumnModel Column) : RuleEffect;

/// <summary>Uses a boolean expression as a row-inclusion predicate.</summary>
internal sealed record IncludeRowEffect : RuleEffect;

/// <summary>Projects a private boolean marker used to return one row or cell highlight hit.</summary>
/// <param name="Id">The stable authored highlight id.</param>
/// <param name="Name">The display name used by presentation controls.</param>
/// <param name="Sequence">The application order within the highlight scope.</param>
/// <param name="Scope">Whether the match applies to a whole row or one cell.</param>
/// <param name="Column">The target cell column, or <see langword="null"/> for a row highlight.</param>
/// <param name="ProjectionName">The private SQL projection that carries the database-evaluated match marker.</param>
internal sealed record HighlightEffect(
    string Id,
    string Name,
    int Sequence,
    HighlightScope Scope,
    ColumnModel? Column,
    string ProjectionName) : RuleEffect;

/// <summary>Specifies whether a highlight decorates an entire row or one cell.</summary>
internal enum HighlightScope
{
    /// <summary>Decorates the complete row.</summary>
    Row,
    /// <summary>Decorates one cell in the row.</summary>
    Cell,
}
