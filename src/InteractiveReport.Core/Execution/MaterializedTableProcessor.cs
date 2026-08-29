using System.Globalization;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Legacy compatibility path for applying ordinary composables to an already
/// materialized table. Named-table Pivot now discovers its data-dependent columns and
/// emits a wide SQL relation; the recursive pipeline does not use this processor.
/// The shared ValidTableLayer behavior remains covered by conformance tests.
/// </summary>
internal static class MaterializedTableProcessor
{
    public static ProcessedTable Apply(
        IReadOnlyList<ColumnInfo> shapeColumns,
        IEnumerable<IReadOnlyDictionary<string, object?>> sourceRows,
        ValidTableLayer layer,
        ValidatedState state,
        ReportDialect dialect,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? shapeTotals = null,
        bool unpaged = false,
        int maxRows = 0)
    {
        var evaluator = new ExpressionEvaluator(state.EvaluationUtcNow);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var sourceRow in sourceRows)
        {
            var row = new Dictionary<string, object?>(sourceRow, StringComparer.OrdinalIgnoreCase);
            if (ApplyRelationalOperations(row, layer, evaluator)) rows.Add(row);
        }

        var effectiveSorts = EffectiveSorts(layer).ToList();
        if (effectiveSorts.Count > 0)
            rows = rows.OrderBy(row => row, new RowComparer(effectiveSorts, dialect)).ToList();

        var totalRows = rows.Count;
        var aggregates = MergeTotals(shapeTotals, AggregateRows(rows, layer.Aggregates));
        var breakTotals = BreakTotals(rows, layer.Breaks, layer.Aggregates);
        var truncated = false;
        var breakContinues = false;
        if (unpaged || state.PageAll)
        {
            if (maxRows > 0 && rows.Count > maxRows)
            {
                rows = rows.Take(maxRows).ToList();
                truncated = true;
            }
        }
        else
        {
            var offset = ((long)state.PageIndex - 1L) * state.PageSize;
            if (offset >= rows.Count)
            {
                rows = [];
            }
            else
            {
                var pageWithBoundary = rows
                    .Skip((int)offset)
                    .Take(state.PageSize == int.MaxValue ? int.MaxValue : state.PageSize + 1)
                    .ToList();
                if (pageWithBoundary.Count > state.PageSize)
                {
                    var boundary = pageWithBoundary[^1];
                    pageWithBoundary.RemoveAt(pageWithBoundary.Count - 1);
                    breakContinues = layer.Breaks.Count > 0
                        && pageWithBoundary.Count > 0
                        && SameBreakKey(pageWithBoundary[^1], boundary, layer.Breaks);
                }
                rows = pageWithBoundary;
            }
        }

        var highlights = EvaluateHighlights(layer.Decorations, rows, evaluator);
        var projected = ReportRowProjector.Columns(
            rows.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            layer.ProjectionColumns);
        var available = ReportResultColumns.ForMaterializedTable(
            layer.Schema,
            shapeColumns);
        var visible = ReportResultColumns.Select(available, layer.SelectColumns);

        return new ProcessedTable(
            available,
            visible,
            projected,
            aggregates,
            breakTotals,
            breakContinues,
            highlights,
            totalRows,
            truncated);
    }

    /// <summary>
    /// Applies the ordered row-local portion of a table layer. Chart execution uses
    /// this while streaming its shaped SQL result so downstream filters run before the
    /// terminal point cap without buffering an unbounded pre-filter table.
    /// </summary>
    internal static bool ApplyRelationalOperations(
        Dictionary<string, object?> row,
        ValidTableLayer layer,
        ExpressionEvaluator evaluator)
    {
        foreach (var operation in layer.Operations)
        {
            foreach (var rule in operation.Definitions)
                row[rule.Effect.Column.Name] = evaluator.Evaluate(rule.Expression.Ast, row);

            if (operation.Predicates.Any(rule =>
                    !evaluator.IsTrue(rule.Expression.Ast, row)))
                return false;
        }
        return true;
    }

    private static IEnumerable<ValidSort> EffectiveSorts(ValidTableLayer layer)
    {
        if (layer.Breaks.Count == 0) return layer.Sorts;
        var byName = layer.Sorts.ToDictionary(sort => sort.Column.Name, StringComparer.OrdinalIgnoreCase);
        var breakNames = new HashSet<string>(
            layer.Breaks.Select(column => column.Name),
            StringComparer.OrdinalIgnoreCase);
        return layer.Breaks
            .Select(column => byName.TryGetValue(column.Name, out var sort)
                ? sort
                : new ValidSort(column, SortDir.Asc))
            .Concat(layer.Sorts.Where(sort => !breakNames.Contains(sort.Column.Name)));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> AggregateRows(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<ValidAggregate> aggregates)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var aggregate in aggregates)
        {
            if (!result.TryGetValue(aggregate.Column.Name, out var values))
                result[aggregate.Column.Name] = values = new Dictionary<string, object?>();
            values[ReportResultColumns.AggregateName(aggregate.Fn)] = Aggregate(rows, aggregate);
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, object?>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? Aggregate(
        IReadOnlyList<Dictionary<string, object?>> rows,
        ValidAggregate aggregate)
    {
        var values = rows
            .Select(row => row.TryGetValue(aggregate.Column.Name, out var value) ? value : null)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
        if (aggregate.Fn == AggregateFn.Count) return (long)values.Count;
        if (aggregate.Fn == AggregateFn.CountDistinct)
            return (long)values.Distinct().Count();
        if (values.Count == 0) return null;

        if (aggregate.Fn is AggregateFn.Sum or AggregateFn.Avg or AggregateFn.Median)
        {
            var numbers = values
                .Select(value => Convert.ToDecimal(value, CultureInfo.InvariantCulture))
                .OrderBy(value => value)
                .ToList();
            if (aggregate.Fn == AggregateFn.Sum) return numbers.Sum();
            if (aggregate.Fn == AggregateFn.Avg) return numbers.Average();
            var middle = numbers.Count / 2;
            return numbers.Count % 2 == 1
                ? numbers[middle]
                : (numbers[middle - 1] + numbers[middle]) / 2m;
        }

        var best = values[0];
        foreach (var value in values.Skip(1))
        {
            var comparison = ExpressionEvaluator.Compare(value, best, aggregate.Column.Kind);
            if ((aggregate.Fn == AggregateFn.Min && comparison < 0)
                || (aggregate.Fn == AggregateFn.Max && comparison > 0))
                best = value;
        }
        return best;
    }

    private static IReadOnlyList<BreakTotal> BreakTotals(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ValidAggregate> aggregates)
    {
        if (breaks.Count == 0) return [];
        return rows
            .GroupBy(row => new BreakKey(row, breaks), BreakKeyComparer.Instance)
            .Select(group => new BreakTotal(
                breaks.ToDictionary(
                    column => column.Name,
                    column => group.First().TryGetValue(column.Name, out var value) ? value : null,
                    StringComparer.OrdinalIgnoreCase),
                group.LongCount(),
                AggregateRows(group.ToList(), aggregates)))
            .ToList();
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> MergeTotals(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? shape,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> ordinary)
    {
        var result = (shape ?? new Dictionary<string, IReadOnlyDictionary<string, object?>>())
            .ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<string, object?>(pair.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        foreach (var (column, values) in ordinary)
        {
            if (!result.TryGetValue(column, out var target))
                result[column] = target = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (function, value) in values) target[function] = value;
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, object?>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<HighlightHit> EvaluateHighlights(
        IReadOnlyList<CompiledRule<HighlightEffect>> rules,
        IReadOnlyList<Dictionary<string, object?>> rows,
        ExpressionEvaluator evaluator)
    {
        if (rules.Count == 0) return [];
        var result = new List<HighlightHit>();
        var ordered = rules
            .OrderBy(rule => rule.Effect.Scope == HighlightScope.Cell ? 1 : 0)
            .ThenBy(rule => rule.Effect.Sequence);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            foreach (var rule in ordered)
                if (evaluator.IsTrue(rule.Expression.Ast, rows[rowIndex]))
                    result.Add(new HighlightHit(
                        rowIndex,
                        rule.Effect.Id,
                        rule.Effect.Scope == HighlightScope.Cell ? rule.Effect.Column!.Name : null));
        return result;
    }

    private static bool SameBreakKey(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right,
        IReadOnlyList<ColumnModel> breaks)
        => breaks.All(column => Equals(
            left.TryGetValue(column.Name, out var leftValue) ? leftValue : null,
            right.TryGetValue(column.Name, out var rightValue) ? rightValue : null));

    private sealed class RowComparer(
        IReadOnlyList<ValidSort> sorts,
        ReportDialect dialect) : IComparer<Dictionary<string, object?>>
    {
        public int Compare(Dictionary<string, object?>? left, Dictionary<string, object?>? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null || right is null) return left is null ? -1 : 1;
            foreach (var sort in sorts)
            {
                left.TryGetValue(sort.Column.Name, out var leftValue);
                right.TryGetValue(sort.Column.Name, out var rightValue);
                var comparison = CompareValues(leftValue, rightValue, sort);
                if (comparison != 0) return comparison;
            }
            return 0;
        }

        private int CompareValues(object? left, object? right, ValidSort sort)
        {
            if (left is null || right is null)
            {
                if (left is null && right is null) return 0;
                var nullsFirst = sort.Nulls switch
                {
                    NullPlacement.First => true,
                    NullPlacement.Last => false,
                    _ => DefaultNullsFirst(sort.Dir, dialect),
                };
                return left is null == nullsFirst ? -1 : 1;
            }
            var result = ExpressionEvaluator.Compare(left, right, sort.Column.Kind);
            return sort.Dir == SortDir.Desc ? -result : result;
        }

        private static bool DefaultNullsFirst(SortDir direction, ReportDialect dialect)
        {
            var ascendingFirst = dialect is ReportDialect.SqlServer or ReportDialect.Sqlite;
            return direction == SortDir.Asc ? ascendingFirst : !ascendingFirst;
        }
    }

    private sealed record BreakKey(
        IReadOnlyDictionary<string, object?> Row,
        IReadOnlyList<ColumnModel> Columns);

    private sealed class BreakKeyComparer : IEqualityComparer<BreakKey>
    {
        public static readonly BreakKeyComparer Instance = new();
        public bool Equals(BreakKey? left, BreakKey? right)
            => left is not null && right is not null && left.Columns.All(column => Equals(
                left.Row.TryGetValue(column.Name, out var leftValue) ? leftValue : null,
                right.Row.TryGetValue(column.Name, out var rightValue) ? rightValue : null));

        public int GetHashCode(BreakKey key)
        {
            var hash = new HashCode();
            foreach (var column in key.Columns)
                hash.Add(key.Row.TryGetValue(column.Name, out var value) ? value : null);
            return hash.ToHashCode();
        }
    }
}

internal sealed record ProcessedTable(
    IReadOnlyList<ColumnInfo> AvailableColumns,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Totals,
    IReadOnlyList<BreakTotal> BreakTotals,
    bool BreakContinues,
    IReadOnlyList<HighlightHit> Highlights,
    long TotalRows,
    bool Truncated);
