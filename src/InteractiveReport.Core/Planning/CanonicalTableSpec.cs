using System.Collections.Immutable;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Immutable, document-order-independent meaning of one named table. Its shape,
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
/// Resource accounting for an authored rule family. Active canonical nodes are kept
/// separately; disabled rules remain inert but still consume the document budget.
/// </summary>
internal sealed record CanonicalRulePopulation(
    int AuthoredCount,
    ImmutableArray<string> CollectionPaths)
{
    public static readonly CanonicalRulePopulation Empty = new(0, []);

    public string BudgetPath(string fallback)
        => CollectionPaths.IsDefaultOrEmpty
            ? fallback
            : CollectionPaths.OrderBy(path => path, StringComparer.Ordinal).First();
}

internal sealed record CanonicalOperationRef(
    ComposableKind Kind,
    ComposablePhase Phase,
    string SourcePath);

internal abstract record CanonicalShape(
    ComposableKind Kind,
    string SourcePath);

internal sealed record CanonicalGroupShape(
    ImmutableArray<string> By,
    ImmutableArray<CanonicalMetric> Values,
    string Path)
    : CanonicalShape(ComposableKind.Group, Path);

internal sealed record CanonicalPivotShape(
    ImmutableArray<string> Rows,
    ImmutableArray<string> Columns,
    ImmutableArray<CanonicalMetric> Values,
    bool Totals,
    string Path)
    : CanonicalShape(ComposableKind.Pivot, Path);

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

internal sealed record CanonicalMetric(
    string Id,
    string Column,
    AggregateFn Function,
    string SourcePath);

internal sealed record CanonicalChartSort(string By, SortDir Direction);

internal sealed record CanonicalComputedColumn(
    string Id,
    string? Label,
    string Expression,
    ImmutableArray<string> Dependencies,
    string SourcePath);

internal sealed record CanonicalFilter(string Expression, string SourcePath);

internal sealed record CanonicalMetadata(
    bool ClearsInheritedLabels,
    ImmutableDictionary<string, string> Labels,
    bool ClearsInheritedFormats,
    ImmutableDictionary<string, CanonicalColumnFormat> Formats);

/// <summary>A value snapshot of ColumnFormat; no mutable DTO collection enters a plan.</summary>
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

internal sealed record CanonicalLocalResult(
    CanonicalSelection? Selection,
    CanonicalOrdering? Ordering,
    ImmutableArray<CanonicalHighlight> Highlights,
    CanonicalRulePopulation HighlightPopulation,
    CanonicalBreaks? Breaks,
    ImmutableArray<CanonicalAggregate> Aggregates);

internal sealed record CanonicalSelection(
    bool SelectAll,
    ImmutableArray<string> Columns,
    string SourcePath);

internal sealed record CanonicalOrdering(
    ImmutableArray<CanonicalSort> Sorts,
    string SourcePath);

internal sealed record CanonicalSort(
    string Column,
    SortDir Direction,
    NullPlacement? Nulls,
    string SourcePath);

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

internal sealed record CanonicalHighlightStyle(string? Background, string? Foreground);

internal sealed record CanonicalBreaks(
    ImmutableArray<string> Columns,
    string SourcePath);

internal sealed record CanonicalAggregate(
    string Column,
    AggregateFn Function,
    string SourcePath);
