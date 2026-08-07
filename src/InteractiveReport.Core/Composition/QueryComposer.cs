using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Turns (definition, validated state) into SqlKata queries: the developer's base SELECT
/// wrapped as a derived table (the APEX trick) with user operations layered on top.
/// The page and count queries are cloned from one filtered core so they cannot disagree.
/// Identifiers come exclusively from schema-validated ColumnModel instances; values are
/// always bindings, never inlined.
/// </summary>
public static class QueryComposer
{
    public const string BaseAlias = "ir_base";
    public const string CalcAlias = "ir_calc";

    public static ComposedQueries Compose(ReportDefinition def, ValidatedState state)
    {
        var core = BuildFilteredCore(def, state);

        var count = core.Clone().AsCount();

        // Aggregates and break totals compute over the whole filtered set — they derive
        // from the pre-select, pre-order, pre-paging core, same as count.
        var aggregates = state.Aggregates.Count > 0 ? BuildAggregates(core, state, def.Dialect) : null;
        var breakTotals = state.Breaks.Count > 0 ? BuildBreakTotals(core, state, def.Dialect) : null;

        var page = core.Clone()
            .Select(state.ProjectionColumns.Select(c => c.Name).ToArray());

        // Highlights are predicates over the same filtered row source. Project
        // their truth values as private markers so every expression function and
        // dialect rule is evaluated by the database, not reimplemented in C#.
        foreach (var rule in state.Rules.Decorations)
            ExpressionRuleSqlApplicator.ApplyDecoration(page, rule, def.Dialect);

        foreach (var sort in EffectiveSorts(state))
        {
            if (sort.Dir == SortDir.Asc) page.OrderBy(sort.Column.Name);
            else page.OrderByDesc(sort.Column.Name);
        }

        if (!state.PageAll)
            page.ForPage(state.PageIndex, state.PageSize);

        return new ComposedQueries(page, count, aggregates, breakTotals);
    }

    /// <summary>
    /// The filtered core every derived query clones: base wrap (+ ir_calc second wrap
    /// when computed columns exist) with filters and search applied.
    /// </summary>
    public static Query BuildFilteredCore(ReportDefinition def, ValidatedState state)
    {
        // Alias without AS: Oracle rejects AS in table aliases (ORA-00933); the bare
        // form is valid on all three dialects.
        var inner = new Query().FromRaw($"({def.Sql}) {BaseAlias}");

        Query core;
        if (state.Rules.Definitions.Count > 0)
        {
            // Second wrap: no dialect reliably allows referencing a SELECT alias in
            // WHERE, so computed columns become real columns of ir_calc — after this,
            // filters, sorts, search, aggregates, breaks, and view dimensions treat
            // them uniformly.
            // Bare, matching the unquoted alias above: a quoted "ir_base" would not
            // resolve against the case-folded IR_BASE on Oracle (ORA-00904).
            inner.SelectRaw($"{BaseAlias}.*");
            foreach (var rule in state.Rules.Definitions)
                ExpressionRuleSqlApplicator.ApplyDefinition(inner, rule, def.Dialect);
            core = new Query().From(inner.As(CalcAlias));
        }
        else
        {
            core = inner;
        }

        foreach (var rule in state.Rules.RowPredicates)
            ExpressionRuleSqlApplicator.ApplyRowPredicate(core, rule, def.Dialect);

        if (state.Search is not null)
            ApplySearch(core, state);

        return core;
    }

    /// <summary>GroupBy view: paginated groups plus the group-count query.</summary>
    public static (Query Page, Query Count) ComposeGroupByView(ReportDefinition def, ValidatedState state)
    {
        var core = BuildFilteredCore(def, state);
        var dims = state.View.GroupBy;

        var page = BuildGrouped(core, dims, state.View.Values, def.Dialect, DimSorts(dims, state.Sorts));
        if (!state.PageAll)
            page.ForPage(state.PageIndex, state.PageSize);

        var groups = core.Clone().Select(dims.Select(d => d.Name).ToArray()).GroupBy(dims.Select(d => d.Name).ToArray());
        var count = new Query().From(groups.As("ir_groups")).AsCount();

        return (page, count);
    }

    /// <summary>GroupBy view for export: all groups, capped for truncation detection.</summary>
    public static Query ComposeGroupByExport(ReportDefinition def, ValidatedState state, int maxRows)
    {
        var core = BuildFilteredCore(def, state);
        var dims = state.View.GroupBy;
        return BuildGrouped(core, dims, state.View.Values, def.Dialect, DimSorts(dims, state.Sorts))
            .Limit(maxRows + 1);
    }

    /// <summary>
    /// Pivot source: one grouped query over rows+cols dimensions, deterministically
    /// ordered, capped at maxGroups+1 so the executor can detect overflow. The pivot
    /// itself happens in memory — native PIVOT syntax never enters the picture.
    /// </summary>
    public static Query ComposePivotSource(ReportDefinition def, ValidatedState state, int maxGroups)
    {
        var core = BuildFilteredCore(def, state);
        var dims = state.View.PivotRows.Concat(state.View.PivotCols).ToList();
        return BuildGrouped(core, dims, state.View.Values, def.Dialect,
                dims.Select(d => new ValidSort(d, SortDir.Asc)))
            .Limit(maxGroups + 1);
    }

    /// <summary>
    /// Chart view: the whole filtered set collapsed to (label, metric) points — through
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
            IReadOnlyList<ValidAggregate> values = chart.Value is null
                ? []
                : [new ValidAggregate(chart.Value, fn)];
            q = BuildGrouped(core, [chart.Label], values, def.Dialect, []);
            metricAlias = chart.Value is null ? "__rows" : "a0";
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
        {
            if (sort.Dir == SortDir.Asc) q.OrderBy(sort.Column.Name);
            else q.OrderByDesc(sort.Column.Name);
        }
        return q.Limit(maxRows + 1);
    }

    /// <summary>
    /// The shared grouped shape: dims, COUNT(*) AS __rows, value aggregates a0..aN,
    /// GROUP BY dims, ordered. Break totals, the groupBy view, and the pivot source all
    /// read through this one layout.
    /// </summary>
    private static Query BuildGrouped(
        Query core,
        IReadOnlyList<ColumnModel> dims,
        IReadOnlyList<ValidAggregate> values,
        ReportDialect dialect,
        IEnumerable<ValidSort> order)
    {
        var dimNames = dims.Select(d => d.Name).ToArray();
        var q = core.Clone().Select(dimNames);
        q.SelectRaw("COUNT(*) AS [__rows]");
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            q.SelectRaw($"{DialectSupport.AggregateExpression(dialect, v.Fn, $"[{v.Column.Name}]")} AS [a{i}]");
        }
        q.GroupBy(dimNames);

        foreach (var sort in order)
        {
            if (sort.Dir == SortDir.Asc) q.OrderBy(sort.Column.Name);
            else q.OrderByDesc(sort.Column.Name);
        }
        return q;
    }

    /// <summary>Dimension ordering, honoring a user sort's direction on that dimension.</summary>
    private static IEnumerable<ValidSort> DimSorts(IReadOnlyList<ColumnModel> dims, IReadOnlyList<ValidSort> sorts)
    {
        var byName = sorts.ToDictionary(s => s.Column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var dim in dims)
            yield return byName.TryGetValue(dim.Name, out var s) ? s : new ValidSort(dim, SortDir.Asc);
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
        var q = core.Clone();
        for (var i = 0; i < state.Aggregates.Count; i++)
        {
            var agg = state.Aggregates[i];
            q.SelectRaw($"{DialectSupport.AggregateExpression(dialect, agg.Fn, $"[{agg.Column.Name}]")} AS [a{i}]");
        }
        return q;
    }

    private static Query BuildBreakTotals(Query core, ValidatedState state, ReportDialect dialect)
    {
        // Group ordering mirrors the page's break ordering so renderers walk both in step.
        return BuildGrouped(core, state.Breaks, state.Aggregates, dialect,
            EffectiveSorts(state).Take(state.Breaks.Count));
    }

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

public sealed record ComposedQueries(Query Page, Query Count, Query? Aggregates = null, Query? BreakTotals = null);
