using InteractiveReport.Core.Model;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Builds the common terminal datasets over any completed composable relation. Public
/// names are kept out of SQL; the reader restores them by ordinal from <c>PagePublicNames</c>.
/// </summary>
internal static class ComposableTerminalQueryComposer
{
    /// <summary>
    /// Composes main rows, total count, optional footer aggregates, and optional break totals.
    /// </summary>
    /// <param name="definition">Supplies dialect and row/chart limits.</param>
    /// <param name="relation">The completed lowered relation to query without mutating.</param>
    /// <param name="terminal">The bound owner-local projection, sorts, decorations, breaks, and aggregates.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="pageIndex">The one-based page index passed to SQLKata.</param>
    /// <param name="pageSize">The positive page size when paging is active.</param>
    /// <param name="pageAll">Whether the request selected the explicit unpaged mode.</param>
    /// <param name="terminalShape">The optional group, pivot, or chart shape that supplies default ordering.</param>
    /// <param name="chartTerminal">Whether chart point limits replace ordinary paging.</param>
    /// <returns>Independent SQLKata queries plus public names for the main-row projection.</returns>
    public static MappedTerminalQueries Compose(
        ReportDefinition definition,
        ComposableSqlRelation relation,
        BoundLocalResult terminal,
        DateTime evaluationUtcNow,
        int pageIndex,
        int pageSize,
        bool pageAll,
        CompiledShape? terminalShape,
        bool chartTerminal = false)
    {
        var dialect = definition.GetEffectiveDialect();
        // ComposableSqlRelation still owns a mutable physical-name allocator.
        // Every terminal role receives an independent snapshot so adding a footer or break
        // query cannot rename aliases in an unrelated statement.
        var rowRelation = Isolate(relation);
        var core = Addressable(rowRelation);
        var count = core.Clone().AsCount();
        var effectiveSorts = EffectiveSorts(terminal, terminalShape, relation.Schema).ToList();
        var aggregates = terminal.Aggregates.Length == 0
            ? null
            : AggregateQuery(Isolate(relation), terminal.Aggregates, dialect);
        var breakTotals = terminal.Breaks.Length == 0
            ? null
            : BreakQuery(
                Isolate(relation),
                terminal.Breaks,
                terminal.Aggregates,
                effectiveSorts,
                dialect);

        var projection = terminal.ProjectionColumns.ToList();

        var page = core.Clone().Select(
            projection.Select(column => rowRelation.PhysicalColumns[column.Name]).ToArray());
        var publicNames = projection.Select(column => column.Name).ToList();
        foreach (var rule in terminal.Decorations)
        {
            ExpressionRuleSqlApplicator.ApplyDecoration(
                page,
                rule,
                dialect,
                evaluationUtcNow,
                rowRelation.PhysicalColumns);
            publicNames.Add(rule.Effect.ProjectionName);
        }

        foreach (var sort in effectiveSorts)
            ApplySort(page, sort, rowRelation.PhysicalColumns[sort.Column.Name], dialect);

        if (chartTerminal)
        {
            page.Limit(definition.MaxChartPoints + 1);
        }
        else if (!pageAll)
        {
            page.ForPage(pageIndex, pageSize);
            if (terminal.Breaks.Length > 0 && pageSize < int.MaxValue)
                page.Limit(pageSize + 1);
        }
        else if (definition.MaxRows > 0)
        {
            page.Limit(definition.MaxRows);
        }

        return new MappedTerminalQueries(
            new TerminalQueries(page, count, aggregates, breakTotals),
            publicNames);
    }

    /// <summary>
    /// Composes one unpaged export query with terminal projection and effective ordering.
    /// </summary>
    /// <param name="definition">Supplies the dialect used for null-placement SQL.</param>
    /// <param name="relation">The completed lowered relation to query without mutating.</param>
    /// <param name="terminal">The bound owner-local export projection and sorts.</param>
    /// <param name="terminalShape">The optional shape that supplies default ordering.</param>
    /// <param name="maxRows">The public row cap; a positive cap fetches one extra sentinel when possible.</param>
    /// <returns>An isolated SQLKata query and its public projection names.</returns>
    public static MappedQuery ComposeExport(
        ReportDefinition definition,
        ComposableSqlRelation relation,
        BoundLocalResult terminal,
        CompiledShape? terminalShape,
        int maxRows)
    {
        var exportRelation = Isolate(relation);
        var query = Addressable(exportRelation).Select(
            terminal.ProjectionColumns
                .Select(column => exportRelation.PhysicalColumns[column.Name])
                .ToArray());
        foreach (var sort in EffectiveSorts(terminal, terminalShape, relation.Schema))
            ApplySort(
                query,
                sort,
                exportRelation.PhysicalColumns[sort.Column.Name],
                definition.GetEffectiveDialect());
        if (maxRows > 0)
            query.Limit(maxRows == int.MaxValue ? int.MaxValue : maxRows + 1);
        return new MappedQuery(
            query,
            terminal.ProjectionColumns.Select(column => column.Name).ToList());
    }

    /// <summary>
    /// Composes the request-local aggregate query for the terminal relation.
    /// </summary>
    /// <param name="relation">An isolated completed relation.</param>
    /// <param name="aggregates">Validated terminal aggregates in result ordinal order.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>A one-row query projecting only generated aggregate aliases.</returns>
    private static Query AggregateQuery(
        ComposableSqlRelation relation,
        IReadOnlyList<ValidAggregate> aggregates,
        ReportDialect dialect)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__ir_terminal_count" };
        var metrics = aggregates
            .Select((aggregate, index) => new ValidMetric(
                Unique($"__ir_terminal_a{index}", used),
                aggregate.Column,
                aggregate.Fn))
            .ToList();
        var grouped = ComposableSqlPlanner.Group(
            relation,
            $"{relation.SchemaName}#terminal-aggregates",
            [],
            metrics,
            dialect,
            "__ir_terminal_count");
        return ComposableSqlPlanner.Project(
            grouped,
            metrics.Select(metric => grouped.Schema.Lookup[metric.Id]).ToList());
    }

    /// <summary>
    /// Composes the control-break query for the terminal relation.
    /// </summary>
    /// <param name="relation">An isolated completed relation.</param>
    /// <param name="breaks">The ordered break columns whose values define a group boundary.</param>
    /// <param name="aggregates">Validated terminal aggregates in result ordinal order.</param>
    /// <param name="effectiveSorts">Final sorts; the leading break-key sorts order subtotal rows.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>A grouped query projecting break keys, generated count, and generated aggregate aliases.</returns>
    private static Query BreakQuery(
        ComposableSqlRelation relation,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ValidAggregate> aggregates,
        IReadOnlyList<ValidSort> effectiveSorts,
        ReportDialect dialect)
    {
        var used = new HashSet<string>(
            breaks.Select(column => column.Name),
            StringComparer.OrdinalIgnoreCase);
        var countName = Unique("__ir_terminal_count", used);
        var metrics = aggregates
            .Select((aggregate, index) => new ValidMetric(
                Unique($"__ir_terminal_a{index}", used),
                aggregate.Column,
                aggregate.Fn))
            .ToList();
        var grouped = ComposableSqlPlanner.Group(
            relation,
            $"{relation.SchemaName}#terminal-breaks",
            breaks,
            metrics,
            dialect,
            countName);
        var projected = breaks
            .Select(column => grouped.Schema.Lookup[column.Name])
            .Concat([grouped.Schema.Lookup[countName]])
            .Concat(metrics.Select(metric => grouped.Schema.Lookup[metric.Id]))
            .ToList();
        var query = ComposableSqlPlanner.Project(grouped, projected);
        foreach (var sort in effectiveSorts.Take(breaks.Count))
            ApplySort(query, sort, grouped.PhysicalColumns[sort.Column.Name], dialect);
        return query;
    }

    /// <summary>
    /// Allocates an internal identifier by prepending underscores until it is unique.
    /// </summary>
    /// <param name="candidate">The preferred generated identifier.</param>
    /// <param name="used">The set of identifiers already allocated in the generated scope.</param>
    /// <returns>A unique name derived from the requested base value.</returns>
    private static string Unique(string candidate, HashSet<string> used)
    {
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

    /// <summary>
    /// Wraps a completed relation as an addressable derived table.
    /// </summary>
    /// <param name="relation">The relation whose query should be cloned and aliased.</param>
    /// <returns>A new outer query reading from the cloned relation.</returns>
    private static Query Addressable(ComposableSqlRelation relation)
        => new Query().From(relation.Query.Clone().As(relation.Names.Relation()));

    /// <summary>
    /// Clones and isolates a query before provider-neutral SQL composition mutates it.
    /// </summary>
    /// <param name="relation">The relation whose mutable SQLKata and allocator state must be isolated.</param>
    /// <returns>A copy with cloned query, copied physical map, and a fresh allocator seeded from all logical/physical names.</returns>
    private static ComposableSqlRelation Isolate(ComposableSqlRelation relation)
    {
        var reserved = relation.Schema.Columns
            .Select(column => column.Name)
            .Concat(relation.PhysicalColumns.Values);
        return relation with
        {
            Query = relation.Query.Clone(),
            PhysicalColumns = new Dictionary<string, string>(
                relation.PhysicalColumns,
                StringComparer.OrdinalIgnoreCase),
            Names = new SqlPhysicalNameAllocator(reserved),
        };
    }

    /// <summary>
    /// Combines request sorts with terminal-shape defaults in execution order.
    /// </summary>
    /// <param name="terminal">The bound explicit sorts and break columns.</param>
    /// <param name="shape">The optional terminal shape supplying stable defaults.</param>
    /// <param name="schema">The completed schema used to resolve chart role columns.</param>
    /// <returns>Break columns first, then explicit sorts, then non-conflicting shape defaults.</returns>
    private static IEnumerable<ValidSort> EffectiveSorts(
        BoundLocalResult terminal,
        CompiledShape? shape,
        ReportSchema schema)
    {
        var owned = ShapeSorts(shape, schema, terminal.Sorts.Length > 0).ToList();
        var declared = terminal.Sorts.Concat(owned.Where(candidate => !terminal.Sorts.Any(sort =>
            string.Equals(sort.Column.Name, candidate.Column.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (terminal.Breaks.Length == 0) return declared;
        var byName = declared.ToDictionary(
            sort => sort.Column.Name,
            StringComparer.OrdinalIgnoreCase);
        var breakNames = new HashSet<string>(
            terminal.Breaks.Select(column => column.Name),
            StringComparer.OrdinalIgnoreCase);
        return terminal.Breaks
            .Select(column => byName.TryGetValue(column.Name, out var sort)
                ? sort
                : new ValidSort(column, SortDir.Asc))
            .Concat(declared.Where(sort => !breakNames.Contains(sort.Column.Name)));
    }

    /// <summary>
    /// Derives stable default sorts from the terminal shape.
    /// </summary>
    /// <param name="shape">The optional terminal group, pivot, or chart shape.</param>
    /// <param name="schema">The shape output schema; chart label and value occupy its first two columns.</param>
    /// <param name="hasTerminalSort">Whether an explicit sort suppresses chart-owned default ordering.</param>
    /// <returns>Ascending dimensions for group/pivot, chart-owned ordering for an unsorted chart, or an empty sequence.</returns>
    private static IEnumerable<ValidSort> ShapeSorts(
        CompiledShape? shape,
        ReportSchema schema,
        bool hasTerminalSort)
    {
        if (shape is { Kind: ShapeKind.Group, Dimensions: { } groupDimensions })
            return groupDimensions.Select(column => new ValidSort(column, SortDir.Asc));
        if (shape is { Kind: ShapeKind.Pivot, Dimensions: { } pivotRows })
            return pivotRows.Select(column => new ValidSort(column, SortDir.Asc));
        if (shape is not { Kind: ShapeKind.Chart, Chart: { } chart } || hasTerminalSort)
            return [];

        var label = schema.Columns[0];
        var value = schema.Columns[1];
        return chart.SortBy == ChartSortBy.Value
            ? [new ValidSort(value, chart.SortDir), new ValidSort(label, SortDir.Asc)]
            : [new ValidSort(label, chart.SortDir)];
    }

    /// <summary>
    /// Applies validated sort terms to a terminal SQL query.
    /// </summary>
    /// <param name="query">The mutable terminal query to order.</param>
    /// <param name="sort">The validated direction and optional null placement.</param>
    /// <param name="physicalName">The allocated SQL projection name.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <remarks>Mutates <paramref name="query"/>. SQL Server emulates explicit null placement with a leading CASE rank.</remarks>
    private static void ApplySort(
        Query query,
        ValidSort sort,
        string physicalName,
        ReportDialect dialect)
    {
        if (sort.Nulls is null)
        {
            if (sort.Dir == SortDir.Asc) query.OrderBy(physicalName);
            else query.OrderByDesc(physicalName);
            return;
        }

        var direction = sort.Dir == SortDir.Asc ? "ASC" : "DESC";
        var placement = sort.Nulls == NullPlacement.First ? "FIRST" : "LAST";
        if (dialect != ReportDialect.SqlServer)
        {
            query.OrderByRaw(
                $"{SqlKataSyntax.Identifier(dialect, physicalName)} {direction} NULLS {placement}");
            return;
        }

        var nullRank = sort.Nulls == NullPlacement.First ? 0 : 1;
        query.OrderByRaw(
            $"CASE WHEN {SqlKataSyntax.Identifier(dialect, physicalName)} IS NULL THEN {nullRank} ELSE {1 - nullRank} END");
        if (sort.Dir == SortDir.Asc) query.OrderBy(physicalName);
        else query.OrderByDesc(physicalName);
    }
}

/// <summary>Contains the logical SQL result sets needed for a terminal table.</summary>
internal sealed record TerminalQueries(
    Query MainRows,
    Query Count,
    Query? FooterAggregates = null,
    Query? BreakTotals = null);

/// <summary>Pairs terminal SQL result sets with public main-row names in projection order.</summary>
internal sealed record MappedTerminalQueries(
    TerminalQueries Queries,
    IReadOnlyList<string> PagePublicNames);

/// <summary>Pairs one SQLKata query with public output names in projection order.</summary>
internal sealed record MappedQuery(Query Query, IReadOnlyList<string> PublicNames);
