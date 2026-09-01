using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Builds every terminal statement over one completed relation in one deterministic
/// operation. The bundle is the hand-off between relation lowering and execution: each
/// query carries the ordinal layout needed to materialize its result without consulting
/// a parallel collection owned by the caller.
/// </summary>
internal static class TerminalExecutionBundleBuilder
{
    /// <summary>
    /// Composes the main, count, aggregate, break, and pivot-total queries needed by one terminal request.
    /// </summary>
    /// <param name="definition">The definition supplying dialect and delivery limits.</param>
    /// <param name="relation">The completed bound relation to lower.</param>
    /// <param name="terminal">The bound owner-local selection, sorting, aggregate, break, and decoration plan.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="request">The request-scoped paging, sorting, and search settings.</param>
    /// <param name="terminalShape">The terminal shape, when the relation ends in Group, Pivot, or Chart.</param>
    /// <param name="chartTerminal">Indicates whether the terminal represents a chart; defaults to <c>false</c>.</param>
    /// <returns>A self-describing bundle of main, count, footer, break, and pivot-total queries.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/>, <paramref name="relation"/>, <paramref name="terminal"/>, or <paramref name="request"/> is <see langword="null"/>.</exception>
    public static TerminalExecutionBundle Build(
        ReportDefinition definition,
        ComposableSqlRelation relation,
        BoundLocalResult terminal,
        DateTime evaluationUtcNow,
        BoundRequestOverlay request,
        CompiledShape? terminalShape,
        bool chartTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(request);

        var main = ComposableTerminalQueryComposer.Compose(
            definition,
            relation,
            terminal,
            evaluationUtcNow,
            request.PageIndex,
            request.PageSize,
            request.PageAll,
            terminalShape,
            chartTerminal);
        var footer = main.Queries.FooterAggregates is null
            ? null
            : new TerminalAggregateQuery(
                main.Queries.FooterAggregates,
                terminal.Aggregates.ToArray());
        var breakTotals = main.Queries.BreakTotals is null
            ? null
            : new TerminalBreakQuery(
                main.Queries.BreakTotals,
                terminal.Breaks.ToArray(),
                terminal.Aggregates.ToArray());

        return new TerminalExecutionBundle(
            MainRows: new TerminalRowQuery(
                main.Queries.MainRows,
                main.PagePublicNames.ToArray()),
            Count: main.Queries.Count,
            FooterAggregates: footer,
            BreakTotals: breakTotals,
            PivotTotals: PivotTotals(terminalShape));
    }

    /// <summary>
    /// Composes the additional query used to calculate pivot totals.
    /// </summary>
    /// <param name="shape">The compiled terminal shape, when one exists.</param>
    /// <returns>A cloned totals-discovery query and its metric/key layout when pivot totals are enabled; otherwise, <see langword="null"/>.</returns>
    private static PivotTotalsQuery? PivotTotals(CompiledShape? shape)
    {
        if (shape is not
            {
                Kind: ShapeKind.Pivot,
                PivotTotals: true,
                PivotTotalsRelation: { } relation,
                PivotColumns: { } columns,
                Metrics: { } metrics,
                PivotKeys: { } keys,
            })
            return null;

        return new PivotTotalsQuery(
            new PivotDiscoveryQuery(
                relation.Query.Clone(),
                RowDimensionCount: 0,
                ColumnDimensionCount: columns.Count,
                ValueCount: metrics.Count),
            metrics.ToArray(),
            keys.ToArray());
    }
}

/// <summary>Contains all executable statements for one completed active table.</summary>
/// <param name="MainRows">The paged row query and its result layout.</param>
/// <param name="Count">The whole-filtered-set row-count query.</param>
/// <param name="FooterAggregates">The optional whole-filtered-set footer aggregate query.</param>
/// <param name="BreakTotals">The optional control-break subtotal query.</param>
/// <param name="PivotTotals">The optional pivot totals query.</param>
internal sealed record TerminalExecutionBundle(
    TerminalRowQuery MainRows,
    Query Count,
    TerminalAggregateQuery? FooterAggregates,
    TerminalBreakQuery? BreakTotals,
    PivotTotalsQuery? PivotTotals);

/// <summary>Pairs a row-producing query with its public ordinal names.</summary>
/// <param name="Query">The executable SqlKata query.</param>
/// <param name="PublicNames">The public column name for each returned ordinal.</param>
internal sealed record TerminalRowQuery(
    Query Query,
    IReadOnlyList<string> PublicNames);

/// <summary>Pairs a footer query with the aggregate represented by each ordinal.</summary>
/// <param name="Query">The executable footer query.</param>
/// <param name="Aggregates">The aggregate binding for each returned ordinal.</param>
internal sealed record TerminalAggregateQuery(
    Query Query,
    IReadOnlyList<ValidAggregate> Aggregates);

/// <summary>
/// A break-total query laid out as break keys, row count, then aggregate ordinals.
/// </summary>
/// <param name="Query">The executable break-total query.</param>
/// <param name="BreakColumns">The leading break-key columns in ordinal order.</param>
/// <param name="Aggregates">The trailing aggregate bindings in ordinal order.</param>
internal sealed record TerminalBreakQuery(
    Query Query,
    IReadOnlyList<ColumnModel> BreakColumns,
    IReadOnlyList<ValidAggregate> Aggregates);

/// <summary>
/// A grouped pivot statement. Discovery is executed before the wide contract exists;
/// the same ordinal description also applies to the totals grouping after completion.
/// </summary>
/// <param name="Query">The executable grouped query.</param>
/// <param name="RowDimensionCount">The number of leading row-dimension ordinals.</param>
/// <param name="ColumnDimensionCount">The number of pivot-key ordinals after row dimensions.</param>
/// <param name="ValueCount">The number of metric ordinals after all dimensions.</param>
internal sealed record PivotDiscoveryQuery(
    Query Query,
    int RowDimensionCount,
    int ColumnDimensionCount,
    int ValueCount);

/// <summary>Pairs a pivot totals statement with the registered public-cell layout.</summary>
/// <param name="Query">The grouped totals-discovery query and ordinal counts.</param>
/// <param name="Metrics">The metrics represented by each value group.</param>
/// <param name="Keys">The registered typed pivot keys represented by each output cell.</param>
internal sealed record PivotTotalsQuery(
    PivotDiscoveryQuery Query,
    IReadOnlyList<ValidMetric> Metrics,
    IReadOnlyList<PivotColumnKey> Keys);
