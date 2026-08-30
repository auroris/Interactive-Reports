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
        var export = ComposableTerminalQueryComposer.ComposeExport(
            definition,
            relation,
            terminal,
            terminalShape,
            chartTerminal ? definition.MaxChartPoints : definition.MaxRows);

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
            PivotTotals: PivotTotals(terminalShape),
            Export: new TerminalRowQuery(
                export.Query,
                export.PublicNames.ToArray()));
    }

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

/// <summary>All executable statements for one completed active table.</summary>
internal sealed record TerminalExecutionBundle(
    TerminalRowQuery MainRows,
    Query Count,
    TerminalAggregateQuery? FooterAggregates,
    TerminalBreakQuery? BreakTotals,
    PivotTotalsQuery? PivotTotals,
    TerminalRowQuery Export);

/// <summary>A row-producing query and its public/private ordinal names.</summary>
internal sealed record TerminalRowQuery(
    Query Query,
    IReadOnlyList<string> PublicNames);

/// <summary>A footer query whose ordinals correspond exactly to <see cref="Aggregates"/>.</summary>
internal sealed record TerminalAggregateQuery(
    Query Query,
    IReadOnlyList<ValidAggregate> Aggregates);

/// <summary>
/// A break-total query laid out as break keys, row count, then the aggregate ordinals.
/// </summary>
internal sealed record TerminalBreakQuery(
    Query Query,
    IReadOnlyList<ColumnModel> BreakColumns,
    IReadOnlyList<ValidAggregate> Aggregates);

/// <summary>
/// A grouped Pivot statement. Discovery is executed before the wide contract exists;
/// the same ordinal description also applies to the totals grouping after completion.
/// </summary>
internal sealed record PivotDiscoveryQuery(
    Query Query,
    int RowDimensionCount,
    int ColumnDimensionCount,
    int ValueCount);

/// <summary>A Pivot totals statement plus the registered public-cell layout.</summary>
internal sealed record PivotTotalsQuery(
    PivotDiscoveryQuery Query,
    IReadOnlyList<ValidMetric> Metrics,
    IReadOnlyList<PivotColumnKey> Keys);
