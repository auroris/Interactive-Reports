using System.Collections.Immutable;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Represents the immutable, document-order-independent meaning of one named table. Its shape,
/// exported transformations, metadata, and owner-local result are deliberately
/// separate so a child planner can consume only the inherited channels.
/// </summary>
internal sealed record CanonicalTableSpec(
    CanonicalShape? Shape,
    ImmutableArray<CanonicalComputedColumn> Computed,
    CanonicalRulePopulation ComputedPopulation,
    ImmutableArray<CanonicalFilter> Filters,
    CanonicalRulePopulation FilterPopulation,
    CanonicalMetadata Metadata,
    CanonicalLocalResult Local,
    ImmutableArray<CanonicalOperationRef> NaturalOrder);

/// <summary>
/// Records resource usage and source paths for one authored rule family. Active canonical nodes are kept
/// separately; disabled rules remain inert but still consume the document budget.
/// </summary>
internal sealed record CanonicalRulePopulation(
    int AuthoredCount,
    ImmutableArray<string> CollectionPaths)
{
    /// <summary>Represents a rule family with no authored entries or source paths.</summary>
    public static readonly CanonicalRulePopulation Empty = new(0, []);

    /// <summary>
    /// Returns the diagnostic path associated with a composable resource budget.
    /// </summary>
    /// <param name="fallback">The path to use when the family has no recorded collection path.</param>
    /// <returns>The ordinally first recorded collection path, or <paramref name="fallback"/> when empty.</returns>
    public string BudgetPath(string fallback)
        => CollectionPaths.IsDefaultOrEmpty
            ? fallback
            : CollectionPaths.OrderBy(path => path, StringComparer.Ordinal).First();
}

/// <summary>Locates one canonical operation in semantic phase order while retaining its authored path.</summary>
internal sealed record CanonicalOperationRef(
    ComposableKind Kind,
    ComposablePhase Phase,
    string SourcePath);

/// <summary>Describes the single relation-shaping operation owned by a table.</summary>
internal abstract record CanonicalShape(
    ComposableKind Kind,
    string SourcePath);

/// <summary>Describes grouping dimensions and aggregate metrics for a group relation.</summary>
internal sealed record CanonicalGroupShape(
    ImmutableArray<string> By,
    ImmutableArray<CanonicalMetric> Values,
    string Path)
    : CanonicalShape(ComposableKind.Group, Path);

/// <summary>Describes pivot row dimensions, dynamic column dimensions, metrics, and totals.</summary>
internal sealed record CanonicalPivotShape(
    ImmutableArray<string> Rows,
    ImmutableArray<string> Columns,
    ImmutableArray<CanonicalMetric> Values,
    bool Totals,
    string Path)
    : CanonicalShape(ComposableKind.Pivot, Path);

/// <summary>Describes the normalized category, metric, aggregation, ordering, and presentation of a chart relation.</summary>
internal sealed record CanonicalChartShape(
    string? Type,
    string? Label,
    string? Value,
    AggregateFn? Function,
    string? Orientation,
    CanonicalChartSort? Sort,
    string? LabelAxisTitle,
    string? ValueAxisTitle,
    string Path)
    : CanonicalShape(ComposableKind.Chart, Path);

/// <summary>Describes one stable synthetic aggregate column and its authored source path.</summary>
internal sealed record CanonicalMetric(
    string Id,
    string Column,
    AggregateFn Function,
    string SourcePath);

/// <summary>Describes the chart field and direction used for default point ordering.</summary>
internal sealed record CanonicalChartSort(string By, SortDir Direction);

/// <summary>Describes an enabled computed column, its dependencies, and authored source path.</summary>
internal sealed record CanonicalComputedColumn(
    string Id,
    string? Label,
    string Expression,
    ImmutableArray<string> Dependencies,
    string SourcePath);

/// <summary>Describes an enabled filter expression and its authored source path.</summary>
internal sealed record CanonicalFilter(string Expression, string SourcePath);

/// <summary>Contains table metadata deltas, including explicit clears of inherited labels or formats.</summary>
internal sealed record CanonicalMetadata(
    bool ClearsInheritedLabels,
    ImmutableDictionary<string, string> Labels,
    bool ClearsInheritedFormats,
    ImmutableDictionary<string, CanonicalColumnFormat> Formats);

/// <summary>Contains an immutable value snapshot of <see cref="ColumnFormat"/> so mutable DTO collections never enter a plan.</summary>
internal sealed record CanonicalColumnFormat(
    string? Mask,
    string? Align,
    bool? Bold,
    bool? Italic,
    string? Foreground,
    string? Background,
    ImmutableArray<string> Classes,
    string? DisplayAs,
    string? UrlColumn,
    string? TextColumn,
    string? Command,
    string? KeyColumn);

/// <summary>Contains operations whose results remain local to the owning table rather than flowing into child relations.</summary>
internal sealed record CanonicalLocalResult(
    CanonicalSelection? Selection,
    CanonicalOrdering? Ordering,
    ImmutableArray<CanonicalHighlight> Highlights,
    CanonicalRulePopulation HighlightPopulation,
    CanonicalBreaks? Breaks,
    ImmutableArray<CanonicalAggregate> Aggregates);

/// <summary>Describes the terminal visible-column selection for a table.</summary>
internal sealed record CanonicalSelection(
    bool SelectAll,
    ImmutableArray<string> Columns,
    string SourcePath);

/// <summary>Contains the terminal sort list and the composable path that authored it.</summary>
internal sealed record CanonicalOrdering(
    ImmutableArray<CanonicalSort> Sorts,
    string SourcePath);

/// <summary>Describes one canonical terminal sort, including explicit null placement.</summary>
internal sealed record CanonicalSort(
    string Column,
    SortDir Direction,
    NullPlacement? Nulls,
    string SourcePath);

/// <summary>Describes one highlight rule after normalization while retaining precedence and source location.</summary>
internal sealed record CanonicalHighlight(
    string Id,
    string? Name,
    int? Sequence,
    string Scope,
    string? Column,
    string Expression,
    CanonicalHighlightStyle? Style,
    string SourcePath,
    bool Enabled = true);

/// <summary>Contains the optional background and foreground colors applied by a highlight.</summary>
internal sealed record CanonicalHighlightStyle(string? Background, string? Foreground);

/// <summary>Contains control-break columns and the composable path that authored them.</summary>
internal sealed record CanonicalBreaks(
    ImmutableArray<string> Columns,
    string SourcePath);

/// <summary>Describes one terminal aggregate and its authored source path.</summary>
internal sealed record CanonicalAggregate(
    string Column,
    AggregateFn Function,
    string SourcePath);
