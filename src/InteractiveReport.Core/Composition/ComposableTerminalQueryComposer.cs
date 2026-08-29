using InteractiveReport.Core.Model;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Builds the common terminal datasets over any completed composable relation. Public
/// names are kept out of SQL; the reader restores them by ordinal from PagePublicNames.
/// </summary>
internal static class ComposableTerminalQueryComposer
{
    public static MappedComposedQueries Compose(
        ReportDefinition definition,
        ComposableSqlRelation relation,
        ValidTableLayer terminal,
        DateTime evaluationUtcNow,
        int pageIndex,
        int pageSize,
        bool pageAll,
        CompiledShape? terminalShape,
        bool chartTerminal = false)
    {
        var dialect = definition.GetEffectiveDialect();
        var core = Addressable(relation);
        var count = core.Clone().AsCount();
        var effectiveSorts = EffectiveSorts(terminal, terminalShape, relation.Schema).ToList();
        var aggregates = terminal.Aggregates.Count == 0
            ? null
            : AggregateQuery(relation, terminal.Aggregates, dialect);
        var breakTotals = terminal.Breaks.Count == 0
            ? null
            : BreakQuery(relation, terminal.Breaks, terminal.Aggregates, effectiveSorts, dialect);

        var projection = terminal.ProjectionColumns.ToList();

        var page = core.Clone().Select(
            projection.Select(column => relation.PhysicalColumns[column.Name]).ToArray());
        var publicNames = projection.Select(column => column.Name).ToList();
        foreach (var rule in terminal.Decorations)
        {
            ExpressionRuleSqlApplicator.ApplyDecoration(
                page,
                rule,
                dialect,
                evaluationUtcNow,
                relation.PhysicalColumns);
            publicNames.Add(rule.Effect.ProjectionName);
        }

        foreach (var sort in effectiveSorts)
            ApplySort(page, sort, relation.PhysicalColumns[sort.Column.Name], dialect);

        if (chartTerminal)
        {
            page.Limit(definition.MaxChartPoints + 1);
        }
        else if (!pageAll)
        {
            page.ForPage(pageIndex, pageSize);
            if (terminal.Breaks.Count > 0 && pageSize < int.MaxValue)
                page.Limit(pageSize + 1);
        }
        else if (definition.MaxRows > 0)
        {
            page.Limit(definition.MaxRows);
        }

        return new MappedComposedQueries(
            new ComposedQueries(page, count, aggregates, breakTotals),
            publicNames);
    }

    public static MappedQuery ComposeExport(
        ReportDefinition definition,
        ComposableSqlRelation relation,
        ValidTableLayer terminal,
        CompiledShape? terminalShape,
        int maxRows)
    {
        var query = Addressable(relation).Select(
            terminal.ProjectionColumns
                .Select(column => relation.PhysicalColumns[column.Name])
                .ToArray());
        foreach (var sort in EffectiveSorts(terminal, terminalShape, relation.Schema))
            ApplySort(
                query,
                sort,
                relation.PhysicalColumns[sort.Column.Name],
                definition.GetEffectiveDialect());
        if (maxRows > 0)
            query.Limit(maxRows == int.MaxValue ? int.MaxValue : maxRows + 1);
        return new MappedQuery(
            query,
            terminal.ProjectionColumns.Select(column => column.Name).ToList());
    }

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

    private static string Unique(string candidate, HashSet<string> used)
    {
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

    private static Query Addressable(ComposableSqlRelation relation)
        => new Query().From(relation.Query.Clone().As(relation.Names.Relation()));

    private static IEnumerable<ValidSort> EffectiveSorts(
        ValidTableLayer terminal,
        CompiledShape? shape,
        ReportSchema schema)
    {
        var owned = ShapeSorts(shape, schema, terminal.Sorts.Count > 0).ToList();
        var declared = terminal.Sorts.Concat(owned.Where(candidate => !terminal.Sorts.Any(sort =>
            string.Equals(sort.Column.Name, candidate.Column.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (terminal.Breaks.Count == 0) return declared;
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

internal sealed record MappedComposedQueries(
    ComposedQueries Queries,
    IReadOnlyList<string> PagePublicNames);

internal sealed record MappedQuery(Query Query, IReadOnlyList<string> PublicNames);
