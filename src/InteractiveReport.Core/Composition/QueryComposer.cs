using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Turns (definition, validated state) into SqlKata queries: the developer's base SELECT
/// wrapped as a derived table (the APEX trick) with user operations layered on top.
/// The page and count queries are cloned from one filtered core so they cannot disagree.
/// Stage layers extend the same pattern upward: a group stage wraps the filtered core in
/// GROUP BY, and its layer's computed columns/markers/sorts ride one more wrap above it.
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
        var aggregates = state.Aggregates.Count > 0 ? BuildAggregates(core, state, def.GetEffectiveDialect()) : null;
        var breakTotals = state.Breaks.Count > 0 ? BuildBreakTotals(core, state, def.GetEffectiveDialect()) : null;

        var page = core.Clone()
            .Select(state.ProjectionColumns.Select(c => c.Name).ToArray());

        // Highlights are predicates over the same filtered row source. Project
        // their truth values as private markers so every expression function and
        // dialect rule is evaluated by the database, not reimplemented in C#.
        foreach (var rule in state.Rules.Decorations)
            ExpressionRuleSqlApplicator.ApplyDecoration(page, rule, def.GetEffectiveDialect());

        foreach (var sort in EffectiveSorts(state))
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

        return new ComposedQueries(page, count, aggregates, breakTotals);
    }

    /// <summary>
    /// The filtered core every derived query clones: base wrap (+ ir_calc second wrap
    /// when computed columns exist) with filters and search applied.
    /// </summary>
    public static Query BuildFilteredCore(ReportDefinition def, ValidatedState state)
    {
        // Alias without AS: Oracle rejects AS in table aliases (ORA-00933); the bare
        // form is valid on every supported dialect.
        var inner = new Query().FromRaw($"({def.Sql}) {BaseAlias}");

        Query core;
        if (state.Rules.Definitions.Count > 0)
        {
            // Second wrap: no dialect reliably allows referencing a SELECT alias in
            // WHERE, so computed columns become real columns of ir_calc — after this,
            // filters, sorts, search, aggregates, breaks, and stage dimensions treat
            // them uniformly.
            // Bare, matching the unquoted alias above: a quoted "ir_base" would not
            // resolve against the case-folded IR_BASE on Oracle (ORA-00904).
            inner.SelectRaw($"{BaseAlias}.*");
            foreach (var rule in state.Rules.Definitions)
                ExpressionRuleSqlApplicator.ApplyDefinition(inner, rule, def.GetEffectiveDialect());
            core = new Query().From(inner.As(CalcAlias));
        }
        else
        {
            core = inner;
        }

        foreach (var rule in state.Rules.RowPredicates)
            ExpressionRuleSqlApplicator.ApplyRowPredicate(core, rule, def.GetEffectiveDialect());

        if (state.Search is not null)
            ApplySearch(core, state);

        return core;
    }

    /// <summary>Group stage as the terminal table: paginated groups plus the group-count query.</summary>
    public static (Query Page, Query Count) ComposeGroupStage(ReportDefinition def, ValidatedState state)
    {
        var core = BuildFilteredCore(def, state);
        var page = BuildGroupStagePage(core, state, def.GetEffectiveDialect());
        if (!state.PageAll)
            page.ForPage(state.PageIndex, state.PageSize);

        var dimNames = state.View.GroupBy.Select(d => d.Name).ToArray();
        var groups = core.Clone().Select(dimNames).GroupBy(dimNames);
        var count = new Query().From(groups.As("ir_groups")).AsCount();

        return (page, count);
    }

    /// <summary>Group stage for export: all groups, capped for truncation detection.</summary>
    public static Query ComposeGroupStageExport(ReportDefinition def, ValidatedState state, int maxRows)
    {
        var core = BuildFilteredCore(def, state);
        return BuildGroupStagePage(core, state, def.GetEffectiveDialect()).Limit(maxRows + 1);
    }

    /// <summary>
    /// The complete ordered group-stage table: grouped core, the layer's computed
    /// columns and highlight markers wrapped above it, layer sorts (explicit sorts
    /// first, remaining dims ascending so paging stays deterministic).
    /// </summary>
    private static Query BuildGroupStagePage(Query core, ValidatedState state, ReportDialect dialect)
    {
        var view = state.View;
        var layer = view.GroupLayer!;
        var sorts = GroupStageSorts(layer.Sorts, view.GroupBy).ToList();
        var query = BuildGroupStageTable(
            core,
            view.GroupBy,
            MetricValues(view),
            layer,
            dialect,
            includeDecorations: true,
            sorts);
        foreach (var sort in sorts)
            ApplySort(query, sort, dialect);
        return query;
    }

    /// <summary>
    /// Spread source: the group stage's table over row+column dimensions, ordered rows
    /// first so groups arrive row-contiguous, capped at maxGroups+1 so the executor can
    /// detect overflow. The spread itself happens in memory — native PIVOT syntax never
    /// enters the picture.
    /// </summary>
    public static Query ComposeSpreadSource(ReportDefinition def, ValidatedState state, int maxGroups)
    {
        var core = BuildFilteredCore(def, state);
        var view = state.View;
        var dims = view.PivotRows.Concat(view.PivotCols).ToList();

        // Layer sorts (row dims only, enforced at validation) choose the row order;
        // remaining row dims and the column dims follow ascending. Row dims always
        // precede column dims so equal row keys stay adjacent for the builder.
        var sorts = GroupStageSorts(view.GroupLayer!.Sorts, view.PivotRows)
            .Concat(view.PivotCols.Select(d => new ValidSort(d, SortDir.Asc)))
            .ToList();

        var query = BuildGroupStageTable(
            core,
            dims,
            MetricValues(view),
            view.GroupLayer!,
            def.GetEffectiveDialect(),
            includeDecorations: false,
            sorts);
        foreach (var sort in sorts)
            ApplySort(query, sort, def.GetEffectiveDialect());
        return query.Limit(maxGroups + 1);
    }

    /// <summary>
    /// Optional spread footer: re-aggregate the filtered source by the spread's column
    /// dimensions alone, through the same computed wrap so derived metrics total
    /// correctly. Deriving totals from the source, rather than adding rendered cells,
    /// keeps averages, medians, distinct counts, and null semantics correct. Only the
    /// totals-safe computed columns (see <see cref="SpreadTotalsComputed"/>) ride along —
    /// an expression referencing a row dimension has no meaning in a cols-only grouping.
    /// </summary>
    public static Query ComposeSpreadTotals(
        ReportDefinition def,
        ValidatedState state,
        IReadOnlyList<CompiledRule<DefineColumnEffect>> totalsComputed)
    {
        var core = BuildFilteredCore(def, state);
        var view = state.View;
        var sorts = view.PivotCols.Select(d => new ValidSort(d, SortDir.Asc)).ToList();
        var query = BuildGroupStageTable(
            core,
            view.PivotCols,
            MetricValues(view),
            view.GroupLayer! with { Computed = totalsComputed, Decorations = [], Sorts = [] },
            def.GetEffectiveDialect(),
            includeDecorations: false,
            sorts);
        foreach (var sort in sorts)
            ApplySort(query, sort, def.GetEffectiveDialect());
        return query;
    }

    /// <summary>
    /// Group-layer computed columns whose expressions reference only metrics, __count,
    /// and the spread's column dimensions — the inputs that still exist when the totals
    /// query re-groups by column dimensions alone. The rest keep their cells but show
    /// no total.
    /// </summary>
    public static IReadOnlyList<CompiledRule<DefineColumnEffect>> SpreadTotalsComputed(ValidView view)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__count" };
        foreach (var metric in view.Values) allowed.Add(metric.Id);
        foreach (var column in view.PivotCols) allowed.Add(column.Name);

        return view.GroupLayer!.Computed
            .Where(rule => Expressions.ExprColumns.Collect(rule.Expression.Ast).All(allowed.Contains))
            .ToList();
    }

    /// <summary>
    /// The group stage's table, unordered: grouped core plus — when the layer computes,
    /// decorates, or needs an alias materialized for a null-placement sort — the
    /// ir_stage wrap. Computed columns become real columns of ir_stage_calc before any
    /// marker or sort expression references them, mirroring the ir_base/ir_calc split.
    /// </summary>
    private static Query BuildGroupStageTable(
        Query core,
        IReadOnlyList<ColumnModel> dims,
        IReadOnlyList<GroupedValue> values,
        ValidStageLayer layer,
        ReportDialect dialect,
        bool includeDecorations,
        IReadOnlyList<ValidSort> sorts)
    {
        var grouped = BuildGrouped(core, dims, values, dialect);
        var decorations = includeDecorations ? layer.Decorations : [];

        var dimNames = new HashSet<string>(dims.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var computedNames = new HashSet<string>(
            layer.Computed.Select(rule => rule.Effect.Column.Name),
            StringComparer.OrdinalIgnoreCase);

        // ORDER BY tolerates a bare SELECT alias on every dialect, but not an alias
        // inside an expression (SQL Server's null-rank CASE) — those sorts need the
        // alias materialized by a wrap first.
        var nullsSortOnAlias = sorts.Any(s => s.Nulls is not null && !dimNames.Contains(s.Column.Name));
        var nullsSortOnComputed = sorts.Any(s => s.Nulls is not null && computedNames.Contains(s.Column.Name));

        if (layer.Computed.Count == 0 && decorations.Count == 0 && !nullsSortOnAlias)
            return grouped;

        var wrapped = new Query().From(grouped.As(StageAlias)).SelectRaw($"{StageAlias}.*");
        foreach (var rule in layer.Computed)
            ExpressionRuleSqlApplicator.ApplyDefinition(wrapped, rule, dialect);

        var query = wrapped;
        if (layer.Computed.Count > 0 && (decorations.Count > 0 || nullsSortOnComputed))
            query = new Query().From(wrapped.As(StageCalcAlias)).SelectRaw($"{StageCalcAlias}.*");

        foreach (var rule in decorations)
            ExpressionRuleSqlApplicator.ApplyDecoration(query, rule, dialect);
        return query;
    }

    /// <summary>Explicit layer sorts first; remaining dims ascending keep the order total.</summary>
    private static IEnumerable<ValidSort> GroupStageSorts(
        IReadOnlyList<ValidSort> sorts,
        IReadOnlyList<ColumnModel> dims)
    {
        var covered = new HashSet<string>(sorts.Select(s => s.Column.Name), StringComparer.OrdinalIgnoreCase);
        return sorts.Concat(dims
            .Where(d => !covered.Contains(d.Name))
            .Select(d => new ValidSort(d, SortDir.Asc)));
    }

    private static IReadOnlyList<GroupedValue> MetricValues(ValidView view)
        => view.Values.Select(m => new GroupedValue(m.Id, m.Column, m.Fn)).ToList();

    /// <summary>
    /// Chart stage: the whole filtered set collapsed to (label, metric) points — through
    /// the shared grouped shape when an aggregate is set, or raw label/value rows when
    /// charting one point per row. Capped at maxPoints+1 so the executor can reject
    /// overflow precisely instead of truncating (a truncated pie lies about proportions).
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

        return q.Limit(maxPoints + 1);
    }

    /// <summary>Grid rows for export: display columns plus renderer sources, effective sorts, no paging, capped.</summary>
    public static Query ComposeGridExport(ReportDefinition def, ValidatedState state, int maxRows)
    {
        var core = BuildFilteredCore(def, state);
        var q = core.Clone().Select(state.ProjectionColumns.Select(c => c.Name).ToArray());
        foreach (var sort in EffectiveSorts(state))
            ApplySort(q, sort, def.GetEffectiveDialect());
        return q.Limit(maxRows + 1);
    }

    /// <summary>
    /// The shared grouped shape: dims, COUNT(*) AS __count, then each value under its
    /// stable alias (metric ids for stage values, a0..aN for footer aggregates and
    /// break totals). Break totals, the group stage, the spread source, and the chart
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
            q.SelectRaw($"{DialectSupport.AggregateExpression(dialect, v.Fn, $"[{v.Column.Name}]")} AS [{v.Alias}]");
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
            : $"PARTITION BY {string.Join(", ", dimNames.Select(name => $"[{name}]"))} ";
        var medianAliases = new Dictionary<int, (string Rank, string Count)>();

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Fn != AggregateFn.Median) continue;
            var column = values[i].Column.Name;
            var rank = UniquePrivateName($"__ir_median_rank_{i}", usedNames);
            var count = UniquePrivateName($"__ir_median_count_{i}", usedNames);
            ranked.SelectRaw(
                $"ROW_NUMBER() OVER ({partition}ORDER BY CASE WHEN [{column}] IS NULL THEN 1 ELSE 0 END, [{column}]) AS [{rank}]");
            ranked.SelectRaw($"COUNT([{column}]) OVER ({partition.TrimEnd()}) AS [{count}]");
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
                    $"{DialectSupport.AggregateExpression(dialect, value.Fn, $"[{value.Column.Name}]")} AS [{value.Alias}]");
                continue;
            }

            var aliases = medianAliases[i];
            var lower = HalfPosition(aliases.Count, 1, dialect);
            var upper = HalfPosition(aliases.Count, 2, dialect);
            var candidate =
                $"CASE WHEN [{aliases.Rank}] IN ({lower}, {upper}) THEN [{value.Column.Name}] END";
            var median = dialect == ReportDialect.SqlServer
                ? $"AVG(CAST({candidate} AS FLOAT))"
                : $"AVG({candidate})";
            query.SelectRaw($"{median} AS [{value.Alias}]");
        }

        if (dimNames.Length > 0) query.GroupBy(dimNames);
        return query;
    }

    private static string HalfPosition(string countAlias, int add, ReportDialect dialect)
        => dialect == ReportDialect.Oracle
            ? $"FLOOR(([{countAlias}] + {add}) / 2)"
            : $"(([{countAlias}] + {add}) / 2)";

    private static string UniquePrivateName(string candidate, HashSet<string> used)
    {
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

    /// <summary>
    /// Apply one schema-bound sort. Oracle, Postgres, and supported SQLite versions
    /// have native NULLS FIRST/LAST syntax. SQL Server needs a leading null-rank key;
    /// the actual value key retains the requested direction in both cases.
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
            query.OrderByRaw($"[{sort.Column.Name}] {direction} NULLS {placement}");
            return;
        }

        var nullRank = sort.Nulls == NullPlacement.First ? 0 : 1;
        query.OrderByRaw(
            $"CASE WHEN [{sort.Column.Name}] IS NULL THEN {nullRank} ELSE {1 - nullRank} END");
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

    private static Query BuildAggregates(Query core, ValidatedState state, ReportDialect dialect)
    {
        var values = AggregateValues(state.Aggregates);
        if (values.Any(value => value.Fn == AggregateFn.Median))
            return BuildRankedAggregates(core, [], values, dialect, includeRowCount: false);

        var q = core.Clone();
        foreach (var value in values)
            q.SelectRaw($"{DialectSupport.AggregateExpression(dialect, value.Fn, $"[{value.Column.Name}]")} AS [{value.Alias}]");
        return q;
    }

    private static Query BuildBreakTotals(Query core, ValidatedState state, ReportDialect dialect)
    {
        // Group ordering mirrors the page's break ordering so renderers walk both in step.
        var query = BuildGrouped(core, state.Breaks, AggregateValues(state.Aggregates), dialect);
        foreach (var sort in EffectiveSorts(state).Take(state.Breaks.Count))
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
