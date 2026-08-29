using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Turns (definition, validated state) into SqlKata queries: the developer's base SELECT
/// wrapped as a derived table (the APEX trick) with user operations layered on top.
/// The page and count queries are cloned from one filtered core so they cannot disagree.
/// A group composable extends the same pattern upward, and the shared ordinary-operation
/// plan continues over its output.
/// Identifiers come exclusively from schema-validated ColumnModel instances; values are
/// always bindings, never inlined.
/// </summary>
public static class QueryComposer
{
    public const string BaseAlias = "ir_base";
    public const string CalcAlias = "ir_calc";
    public const string StageAlias = "ir_stage";
    public const string StageCalcAlias = "ir_stage_calc";

    public static ComposedQueries Compose(ReportDefinition def, ValidatedState state)
    {
        var core = BuildFilteredCore(def, state);

        var count = core.Clone().AsCount();

        // Aggregates and break totals compute over the whole filtered set — they derive
        // from the pre-select, pre-order, pre-paging core, same as count.
        var effectiveSorts = EffectiveSorts(state).ToList();
        var aggregates = state.Aggregates.Count > 0
            ? BuildAggregates(core, state.Aggregates, def.GetEffectiveDialect())
            : null;
        var breakTotals = state.Breaks.Count > 0
            ? BuildBreakTotals(
                core,
                state.Breaks,
                state.Aggregates,
                effectiveSorts,
                def.GetEffectiveDialect())
            : null;

        var page = core.Clone()
            .Select(state.ProjectionColumns.Select(c => c.Name).ToArray());

        // Highlights are predicates over the same filtered row source. Project
        // their truth values as private markers so every expression function and
        // dialect rule is evaluated by the database, not reimplemented in C#.
        foreach (var rule in state.Rules.Decorations)
            ExpressionRuleSqlApplicator.ApplyDecoration(
                page,
                rule,
                def.GetEffectiveDialect(),
                state.EvaluationUtcNow);

        foreach (var sort in effectiveSorts)
            ApplySort(page, sort, def.GetEffectiveDialect());

        if (!state.PageAll)
        {
            page.ForPage(state.PageIndex, state.PageSize);
            // A single boundary row tells the executor whether the last visible
            // control-break group continues on the next page. ForPage establishes
            // the correct offset; replacing only its limit preserves that offset.
            if (state.Breaks.Count > 0 && state.PageSize < int.MaxValue)
                page.Limit(state.PageSize + 1);
        }
        else if (def.MaxRows > 0)
        {
            page.Limit(def.MaxRows);
        }

        return new ComposedQueries(page, count, aggregates, breakTotals);
    }

    /// <summary>
    /// The relational input every shape consumes. Each compute/filter composable is
    /// materialized in document order, so a later operation sees exactly the schema
    /// produced before it rather than a bucketed compute-before-filter rewrite.
    /// </summary>
    public static Query BuildFilteredCore(ReportDefinition def, ValidatedState state)
    {
        // Alias without AS: Oracle rejects AS in table aliases (ORA-00933); the bare
        // form is valid on every supported dialect.
        var relation = new Query().FromRaw(SqlKataSyntax.PreserveRaw(
            $"({def.Sql}) {BaseAlias}"));
        var core = ApplyOperations(
            relation,
            state.Operations,
            def.GetEffectiveDialect(),
            state.EvaluationUtcNow,
            $"{BaseAlias}.*",
            CalcAlias,
            "ir_source_calc");

        if (state.Search is not null)
            ApplySearch(core, state);

        return core;
    }

    /// <summary>
    /// Applies relational operations to an already-addressable relation. Filters can
    /// remain on the current relation. A compute step selects the current row plus its
    /// definitions, then materializes that result so every later operation can bind to
    /// the new columns. This preserves stack order without adding wrappers for ordinary
    /// filter-only documents.
    /// </summary>
    private static Query ApplyOperations(
        Query relation,
        IReadOnlyList<ValidTableOperation> operations,
        ReportDialect dialect,
        DateTime evaluationUtcNow,
        string initialStar,
        string firstComputeAlias,
        string laterComputeAliasPrefix)
    {
        var current = relation;
        var star = initialStar;
        var computeIndex = 0;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Definitions.Count > 0)
            {
                current.SelectRaw(star);
                foreach (var rule in operation.Definitions)
                    ExpressionRuleSqlApplicator.ApplyDefinition(
                        current,
                        rule,
                        dialect,
                        evaluationUtcNow);

                var alias = computeIndex++ == 0
                    ? firstComputeAlias
                    : $"{laterComputeAliasPrefix}_{index}";
                current = new Query().From(current.As(alias));
                star = $"{alias}.*";
            }

            foreach (var rule in operation.Predicates)
                ExpressionRuleSqlApplicator.ApplyRowPredicate(
                    current,
                    rule,
                    dialect,
                    evaluationUtcNow);
        }

        return current;
    }

    /// <summary>
    /// Backward-compatible page/count surface for callers that only compose the Group
    /// table itself. Execution uses ComposeGroupStageQueries so footer and break
    /// datasets share the same completed Group table.
    /// </summary>
    public static (Query Page, Query Count) ComposeGroupStage(ReportDefinition def, ValidatedState state)
    {
        var queries = ComposeGroupStageQueries(def, state);
        return (queries.Page, queries.Count);
    }

    /// <summary>
    /// Group stage as the terminal table: paginated rows, total group count, whole-table
    /// footer aggregates, and control-break subtotals. Every dataset derives from the
    /// same post-compute, post-filter Group table.
    /// </summary>
    public static ComposedQueries ComposeGroupStageQueries(ReportDefinition def, ValidatedState state)
    {
        var core = BuildFilteredCore(def, state);
        var dialect = def.GetEffectiveDialect();
        var layer = state.View.Output!;
        var page = BuildGroupStagePage(core, state, def.GetEffectiveDialect());
        if (!state.PageAll)
        {
            page.ForPage(state.PageIndex, state.PageSize);
            if (layer.Breaks.Count > 0 && state.PageSize < int.MaxValue)
                page.Limit(state.PageSize + 1);
        }
        else if (def.MaxRows > 0)
            page.Limit(def.MaxRows);

        var stageTable = BuildGroupStageTable(
            core.Clone(),
            state.View.GroupBy,
            MetricValues(state.View),
            layer with { Decorations = [], Sorts = [] },
            dialect,
            state.EvaluationUtcNow);
        // Footer and break datasets consume the completed terminal relation, not the
        // grouped shape beneath its ordinary operations.
        var stageCore = new Query().From(stageTable.As("ir_groups"));
        var count = stageCore.Clone().AsCount();
        var effectiveSorts = GroupStageSorts(layer.Sorts, layer.Breaks, state.View.GroupBy).ToList();
        var aggregates = layer.Aggregates.Count > 0
            ? BuildAggregates(stageCore, layer.Aggregates, dialect)
            : null;
        var breakTotals = layer.Breaks.Count > 0
            ? BuildBreakTotals(stageCore, layer.Breaks, layer.Aggregates, effectiveSorts, dialect)
            : null;

        return new ComposedQueries(page, count, aggregates, breakTotals);
    }

    /// <summary>Group-stage export, with a sentinel row when a positive cap applies.</summary>
    public static Query ComposeGroupStageExport(ReportDefinition def, ValidatedState state, int maxRows)
    {
        var core = BuildFilteredCore(def, state);
        return LimitForExport(
            BuildGroupStagePage(core, state, def.GetEffectiveDialect()), maxRows);
    }

    /// <summary>
    /// The complete ordered group-stage page. The shape produces the grouped relation;
    /// the shared operation fold transforms it; projection, decoration, and ordering
    /// are then ordinary terminal-table concerns.
    /// </summary>
    private static Query BuildGroupStagePage(Query core, ValidatedState state, ReportDialect dialect)
    {
        var view = state.View;
        var layer = view.Output!;
        var sorts = GroupStageSorts(layer.Sorts, layer.Breaks, view.GroupBy).ToList();
        var query = BuildGroupStageTable(
            core,
            view.GroupBy,
            MetricValues(view),
            layer,
            dialect,
            state.EvaluationUtcNow);
        query.Select(layer.ProjectionColumns.Select(column => column.Name).ToArray());
        foreach (var rule in layer.Decorations)
            ExpressionRuleSqlApplicator.ApplyDecoration(
                query,
                rule,
                dialect,
                state.EvaluationUtcNow);
        foreach (var sort in sorts)
            ApplySort(query, sort, dialect);
        return query;
    }

    /// <summary>
    /// Legacy no-table Pivot source: the input relation grouped over row + column
    /// dimensions, ordered rows first so groups arrive row-contiguous, and capped at
    /// maxGroups+1. Named tables instead compile a portable wide SQL relation through
    /// ComposableSqlPlanner; native provider PIVOT syntax is never required.
    /// </summary>
    public static Query ComposePivotSource(ReportDefinition def, ValidatedState state, int maxGroups)
    {
        var core = BuildFilteredCore(def, state);
        var view = state.View;
        var dims = view.PivotRows.Concat(view.PivotCols).ToList();

        // Row dims precede column dims so equal row keys stay adjacent for the builder.
        // Pivot-layer sorting happens after the wide table exists.
        var sorts = view.PivotRows
            .Concat(view.PivotCols)
            .Select(d => new ValidSort(d, SortDir.Asc))
            .ToList();

        var query = BuildGrouped(core, dims, MetricValues(view), def.GetEffectiveDialect());
        foreach (var sort in sorts)
            ApplySort(query, sort, def.GetEffectiveDialect());
        return query.Limit(maxGroups + 1);
    }

    /// <summary>
    /// Optional Pivot footer: re-aggregate the composed input relation by the Pivot's
    /// column dimensions alone. Deriving totals from the source, rather than adding
    /// rendered cells, keeps averages, medians, distinct counts, and null semantics
    /// correct.
    /// </summary>
    public static Query ComposePivotTotals(ReportDefinition def, ValidatedState state)
    {
        var core = BuildFilteredCore(def, state);
        var view = state.View;
        var sorts = view.PivotCols.Select(d => new ValidSort(d, SortDir.Asc)).ToList();
        var query = BuildGrouped(core, view.PivotCols, MetricValues(view), def.GetEffectiveDialect());
        foreach (var sort in sorts)
            ApplySort(query, sort, def.GetEffectiveDialect());
        return query;
    }

    /// <summary>
    /// The group composable's output relation followed by the same ordered operation
    /// fold used for a definition-backed table.
    /// </summary>
    private static Query BuildGroupStageTable(
        Query core,
        IReadOnlyList<ColumnModel> dims,
        IReadOnlyList<GroupedValue> values,
        ValidTableLayer layer,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
    {
        var grouped = BuildGrouped(core, dims, values, dialect);
        var relation = new Query().From(grouped.As(StageAlias));
        return ApplyOperations(
            relation,
            layer.Operations,
            dialect,
            evaluationUtcNow,
            $"{StageAlias}.*",
            StageCalcAlias,
            "ir_group_calc");
    }

    /// <summary>
    /// Break keys lead, preserving an explicit direction where one exists. Remaining
    /// layer sorts follow, then unsorted Group dimensions make paging deterministic.
    /// </summary>
    private static IEnumerable<ValidSort> GroupStageSorts(
        IReadOnlyList<ValidSort> sorts,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ColumnModel> dims)
    {
        var byName = sorts.ToDictionary(sort => sort.Column.Name, StringComparer.OrdinalIgnoreCase);
        var breakNames = new HashSet<string>(breaks.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        var ordered = breaks
            .Select(column => byName.TryGetValue(column.Name, out var sort)
                ? sort
                : new ValidSort(column, SortDir.Asc))
            .Concat(sorts.Where(sort => !breakNames.Contains(sort.Column.Name)))
            .ToList();
        var covered = new HashSet<string>(ordered.Select(s => s.Column.Name), StringComparer.OrdinalIgnoreCase);
        return ordered.Concat(dims
            .Where(d => !covered.Contains(d.Name))
            .Select(d => new ValidSort(d, SortDir.Asc)));
    }

    private static IReadOnlyList<GroupedValue> MetricValues(ValidView view)
        => view.Values.Select(m => new GroupedValue(m.Id, m.Column, m.Fn)).ToList();

    /// <summary>
    /// Chart stage: the whole filtered set collapsed to (label, metric) points — through
    /// the shared grouped shape when an aggregate is set, or raw label/value rows when
    /// charting one point per row. When no downstream row predicate exists, cap the SQL
    /// source at maxPoints+1 so the executor can reject overflow cheaply. A downstream
    /// filter must see the complete shaped table; that path streams until maxPoints+1
    /// surviving rows instead (a truncated pie lies about proportions).
    /// </summary>
    public static Query ComposeChartView(ReportDefinition def, ValidatedState state, int maxPoints)
    {
        var core = BuildFilteredCore(def, state);
        var chart = state.View.Chart!;

        Query q;
        string metricAlias;
        if (chart.Fn is { } fn)
        {
            IReadOnlyList<GroupedValue> values = chart.Value is null
                ? []
                : [new GroupedValue("m0", chart.Value, fn)];
            q = BuildGrouped(core, [chart.Label], values, def.GetEffectiveDialect());
            metricAlias = chart.Value is null ? "__count" : "m0";
        }
        else
        {
            q = core.Clone().Select(chart.Label.Name, chart.Value!.Name);
            metricAlias = chart.Value!.Name;
        }

        if (chart.SortBy == ChartSortBy.Value)
        {
            if (chart.SortDir == SortDir.Asc) q.OrderBy(metricAlias);
            else q.OrderByDesc(metricAlias);
            q.OrderBy(chart.Label.Name);   // deterministic ties
        }
        else
        {
            if (chart.SortDir == SortDir.Asc) q.OrderBy(chart.Label.Name);
            else q.OrderByDesc(chart.Label.Name);
        }

        return state.View.Output!.RowPredicates.Count == 0
            ? q.Limit(maxPoints + 1)
            : q;
    }

    /// <summary>Grid rows for export: display columns plus renderer sources, effective sorts, no paging, capped.</summary>
    public static Query ComposeGridExport(ReportDefinition def, ValidatedState state, int maxRows)
    {
        var core = BuildFilteredCore(def, state);
        var q = core.Clone().Select(state.ProjectionColumns.Select(c => c.Name).ToArray());
        foreach (var sort in EffectiveSorts(state))
            ApplySort(q, sort, def.GetEffectiveDialect());
        return LimitForExport(q, maxRows);
    }

    private static Query LimitForExport(Query query, int maxRows)
    {
        if (maxRows <= 0) return query;
        return query.Limit(maxRows == int.MaxValue ? int.MaxValue : maxRows + 1);
    }

    /// <summary>
    /// The shared grouped shape: dims, COUNT(*) AS __count, then each value under its
    /// stable alias (metric ids for stage values, a0..aN for footer aggregates and
    /// break totals). Break totals, the Group By stage, the Pivot source, and the chart
    /// metric all read through this one layout.
    /// </summary>
    private static Query BuildGrouped(
        Query core,
        IReadOnlyList<ColumnModel> dims,
        IReadOnlyList<GroupedValue> values,
        ReportDialect dialect)
    {
        if (values.Any(value => value.Fn == AggregateFn.Median))
            return BuildRankedAggregates(core, dims, values, dialect, includeRowCount: true);

        var dimNames = dims.Select(d => d.Name).ToArray();
        var q = core.Clone().Select(dimNames);
        q.SelectRaw("COUNT(*) AS [__count]");
        foreach (var v in values)
            q.SelectRaw(
                $"{DialectSupport.AggregateExpression(dialect, v.Fn, Identifier(dialect, v.Column.Name))} AS {Identifier(dialect, v.Alias)}");
        q.GroupBy(dimNames);
        return q;
    }

    /// <summary>
    /// Portable continuous median. The inner query ranks non-null values and counts
    /// them per dimension group; the outer grouped query averages the middle one or
    /// two positions. This window-plus-group shape works on every supported dialect
    /// and composes alongside ordinary aggregates without an optional SQLite extension.
    /// </summary>
    private static Query BuildRankedAggregates(
        Query core,
        IReadOnlyList<ColumnModel> dims,
        IReadOnlyList<GroupedValue> values,
        ReportDialect dialect,
        bool includeRowCount)
    {
        var dimNames = dims.Select(d => d.Name).ToArray();
        var selectedNames = dimNames
            .Concat(values.Select(value => value.Column.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usedNames = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        var ranked = core.Clone().Select(selectedNames);
        var partition = dimNames.Length == 0
            ? ""
            : $"PARTITION BY {string.Join(", ", dimNames.Select(name => Identifier(dialect, name)))} ";
        var medianAliases = new Dictionary<int, (string Rank, string Count)>();

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Fn != AggregateFn.Median) continue;
            var column = values[i].Column.Name;
            var rank = UniquePrivateName($"__ir_median_rank_{i}", usedNames);
            var count = UniquePrivateName($"__ir_median_count_{i}", usedNames);
            var quotedColumn = Identifier(dialect, column);
            ranked.SelectRaw(
                $"ROW_NUMBER() OVER ({partition}ORDER BY CASE WHEN {quotedColumn} IS NULL THEN 1 ELSE 0 END, {quotedColumn}) AS {Identifier(dialect, rank)}");
            ranked.SelectRaw(
                $"COUNT({quotedColumn}) OVER ({partition.TrimEnd()}) AS {Identifier(dialect, count)}");
            medianAliases[i] = (rank, count);
        }

        var query = new Query().From(ranked.As("ir_ranked_aggregates")).Select(dimNames);
        if (includeRowCount) query.SelectRaw("COUNT(*) AS [__count]");
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (value.Fn != AggregateFn.Median)
            {
                query.SelectRaw(
                    $"{DialectSupport.AggregateExpression(dialect, value.Fn, Identifier(dialect, value.Column.Name))} AS {Identifier(dialect, value.Alias)}");
                continue;
            }

            var aliases = medianAliases[i];
            var lower = HalfPosition(aliases.Count, 1, dialect);
            var upper = HalfPosition(aliases.Count, 2, dialect);
            var candidate =
                $"CASE WHEN {Identifier(dialect, aliases.Rank)} IN ({lower}, {upper}) THEN {Identifier(dialect, value.Column.Name)} END";
            var median = dialect == ReportDialect.SqlServer
                ? $"AVG(CAST({candidate} AS FLOAT))"
                : $"AVG({candidate})";
            query.SelectRaw($"{median} AS {Identifier(dialect, value.Alias)}");
        }

        if (dimNames.Length > 0) query.GroupBy(dimNames);
        return query;
    }

    private static string HalfPosition(string countAlias, int add, ReportDialect dialect)
        => dialect == ReportDialect.Oracle
            ? $"FLOOR(({Identifier(dialect, countAlias)} + {add}) / 2)"
            : $"(({Identifier(dialect, countAlias)} + {add}) / 2)";

    private static string UniquePrivateName(string candidate, HashSet<string> used)
    {
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

    private static string Identifier(ReportDialect dialect, string name)
        => SqlKataSyntax.Identifier(dialect, name);

    /// <summary>
    /// Apply one schema-bound sort. Oracle, Postgres, and supported SQLite versions
    /// have native NULLS FIRST/LAST syntax. SQL Server needs a leading null-rank key;
    /// the actual value key retains the requested direction in both cases. Text order
    /// follows the database collation. There is no portable collation name or syntax
    /// shared by all four providers; use a binary/ordinal report collation when exact
    /// parity with the legacy materialized Pivot/Chart path is required.
    /// </summary>
    private static void ApplySort(Query query, ValidSort sort, ReportDialect dialect)
    {
        if (sort.Nulls is null)
        {
            if (sort.Dir == SortDir.Asc) query.OrderBy(sort.Column.Name);
            else query.OrderByDesc(sort.Column.Name);
            return;
        }

        var direction = sort.Dir == SortDir.Asc ? "ASC" : "DESC";
        var placement = sort.Nulls == NullPlacement.First ? "FIRST" : "LAST";
        if (dialect != ReportDialect.SqlServer)
        {
            query.OrderByRaw(
                $"{Identifier(dialect, sort.Column.Name)} {direction} NULLS {placement}");
            return;
        }

        var nullRank = sort.Nulls == NullPlacement.First ? 0 : 1;
        query.OrderByRaw(
            $"CASE WHEN {Identifier(dialect, sort.Column.Name)} IS NULL THEN {nullRank} ELSE {1 - nullRank} END");
        if (sort.Dir == SortDir.Asc) query.OrderBy(sort.Column.Name);
        else query.OrderByDesc(sort.Column.Name);
    }

    /// <summary>
    /// Break columns must sort first so groups arrive contiguous. A user sort on a break
    /// column contributes its direction to the break position; remaining user sorts
    /// follow after all breaks.
    /// </summary>
    internal static IEnumerable<ValidSort> EffectiveSorts(ValidatedState state)
    {
        if (state.Breaks.Count == 0)
            return state.Sorts;

        var byName = state.Sorts.ToDictionary(s => s.Column.Name, StringComparer.OrdinalIgnoreCase);
        var breakNames = new HashSet<string>(state.Breaks.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

        return state.Breaks
            .Select(b => byName.TryGetValue(b.Name, out var s) ? s : new ValidSort(b, SortDir.Asc))
            .Concat(state.Sorts.Where(s => !breakNames.Contains(s.Column.Name)));
    }

    private static Query BuildAggregates(
        Query core,
        IReadOnlyList<ValidAggregate> aggregates,
        ReportDialect dialect)
    {
        var values = AggregateValues(aggregates);
        if (values.Any(value => value.Fn == AggregateFn.Median))
            return BuildRankedAggregates(core, [], values, dialect, includeRowCount: false);

        var q = core.Clone();
        foreach (var value in values)
            q.SelectRaw(
                $"{DialectSupport.AggregateExpression(dialect, value.Fn, Identifier(dialect, value.Column.Name))} AS {Identifier(dialect, value.Alias)}");
        return q;
    }

    private static Query BuildBreakTotals(
        Query core,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ValidAggregate> aggregates,
        IReadOnlyList<ValidSort> effectiveSorts,
        ReportDialect dialect)
    {
        // Group ordering mirrors the page's break ordering so renderers walk both in step.
        var query = BuildGrouped(core, breaks, AggregateValues(aggregates), dialect);
        foreach (var sort in effectiveSorts.Take(breaks.Count))
            ApplySort(query, sort, dialect);
        return query;
    }

    private static IReadOnlyList<GroupedValue> AggregateValues(IReadOnlyList<ValidAggregate> aggregates)
        => aggregates.Select((a, i) => new GroupedValue($"a{i}", a.Column, a.Fn)).ToList();

    private static void ApplySearch(Query q, ValidatedState state)
    {
        var textCols = state.SelectColumns.Where(c => c.Kind == ColumnKind.Text).ToList();
        q.Where(sub =>
        {
            foreach (var col in textCols)
                sub.OrWhereContains(col.Name, state.Search!, caseSensitive: false);
            return sub;
        });
    }
}

/// <summary>One aggregated value of the shared grouped shape, with its SQL alias.</summary>
internal readonly record struct GroupedValue(string Alias, ColumnModel Column, AggregateFn Fn);

public sealed record ComposedQueries(Query Page, Query Count, Query? Aggregates = null, Query? BreakTotals = null);
