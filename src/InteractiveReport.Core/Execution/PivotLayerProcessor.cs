using System.Globalization;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Binds and executes a Pivot stage's layer after the data-dependent wide schema is
/// known. Shape runs in SQL as a portable grouped query; the wide table then behaves
/// like any other report table for compute, filter, sort, highlight, projection, and
/// paging.
/// </summary>
internal static class PivotLayerProcessor
{
    public static ProcessedPivot Apply(
        PivotTable pivot,
        ValidatedState state,
        ReportDialect dialect,
        bool unpaged = false,
        int maxRows = 0)
    {
        var raw = state.View.PivotLayer ?? new StageLayer();
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var schema = RuntimeSchema(pivot.Columns, state);

        var computed = ComputedColumnValidator.Validate(
            raw.Computed,
            schema.Lookup,
            errors,
            "pipeline[1].layer.computed");
        var extended = schema.Extend(
            $"{state.Schema.Columns[0].Name}#pivot",
            computed.Select(rule => rule.Effect.Column));
        var filters = ExpressionRuleCompiler.Compile<FilterRule, IncludeRowEffect>(
            raw.Filters,
            50,
            "pipeline[1].layer.filters",
            extended.Lookup,
            ExpressionRequirement.Predicate,
            static (_, _) => _ => new IncludeRowEffect(),
            errors);
        var sorts = StateValidator.ValidateSorts(raw.Sorts, extended, ignored);
        var highlights = HighlightRuleValidator.Validate(
            raw.Highlights,
            extended.Lookup,
            errors,
            ignored,
            "pipeline[1].layer.highlights");
        var selected = StateValidator.ValidateColumns(raw.Columns, extended, ignored);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);

        var rows = pivot.Rows
            .Select(row => new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var row in rows)
            foreach (var rule in computed)
                row[rule.Effect.Column.Name] = ExpressionEvaluator.Evaluate(rule.Expression.Ast, row);

        if (filters.Count > 0)
            rows = rows
                .Where(row => filters.All(rule => ExpressionEvaluator.IsTrue(rule.Expression.Ast, row)))
                .ToList();

        if (sorts.Count > 0)
            rows = rows.OrderBy(row => row, new RowComparer(sorts, dialect)).ToList();

        var totalRows = rows.Count;
        var truncated = false;
        if (unpaged || state.PageAll)
        {
            if (maxRows > 0 && rows.Count > maxRows)
            {
                rows = rows.Take(maxRows).ToList();
                truncated = true;
            }
        }
        else if (!state.PageAll)
        {
            rows = rows
                .Skip((state.PageIndex - 1) * state.PageSize)
                .Take(state.PageSize)
                .ToList();
        }

        var hits = EvaluateHighlights(highlights, rows);
        var projected = ReportRowProjector.Columns(
            rows.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            selected);
        var labels = StateValidator.ResolveLabels(raw.Labels);
        var columns = selected.Select(column => new ColumnInfo(
            column.Name,
            labels.TryGetValue(column.Name, out var label) ? label : column.Label,
            column.KindName,
            column.IsComputed)
        {
            FormatSource = pivot.Columns
                .FirstOrDefault(candidate => string.Equals(candidate.Name, column.Name, StringComparison.OrdinalIgnoreCase))
                ?.FormatSource,
        }).ToList();
        var selectedNames = new HashSet<string>(selected.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        var totals = pivot.Totals
            .Where(pair => selectedNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return new ProcessedPivot(
            columns,
            projected,
            totals,
            hits,
            totalRows,
            truncated,
            state.Ignored.Concat(ignored).ToList());
    }

    private static ReportSchema RuntimeSchema(
        IReadOnlyList<ColumnInfo> columns,
        ValidatedState state)
    {
        var rowDimensions = state.View.PivotRows.ToDictionary(
            column => column.Name,
            StringComparer.OrdinalIgnoreCase);
        return ReportSchema.Create(
            "pivot",
            columns.Select(column => rowDimensions.TryGetValue(column.Name, out var dimension)
                ? new ColumnModel
                {
                    Name = dimension.Name,
                    Label = column.Label,
                    ClrType = dimension.ClrType,
                    IsNullable = dimension.IsNullable,
                    IsComputed = dimension.IsComputed,
                }
                : new ColumnModel
                {
                    Name = column.Name,
                    Label = column.Label,
                    ClrType = column.Type switch
                    {
                        "number" => typeof(decimal),
                        "date" => typeof(DateTime),
                        "bool" => typeof(bool),
                        "text" => typeof(string),
                        _ => typeof(object),
                    },
                    IsComputed = column.Computed,
                }));
    }

    private static IReadOnlyList<HighlightHit> EvaluateHighlights(
        IReadOnlyList<CompiledRule<HighlightEffect>> rules,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        if (rules.Count == 0) return [];
        var result = new List<HighlightHit>();
        var ordered = rules
            .OrderBy(rule => rule.Effect.Scope == HighlightScope.Cell ? 1 : 0)
            .ThenBy(rule => rule.Effect.Sequence);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            foreach (var rule in ordered)
            {
                if (!ExpressionEvaluator.IsTrue(rule.Expression.Ast, rows[rowIndex])) continue;
                result.Add(new HighlightHit(
                    rowIndex,
                    rule.Effect.Id,
                    rule.Effect.Scope == HighlightScope.Cell ? rule.Effect.Column!.Name : null));
            }
        }
        return result;
    }

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
}

internal sealed record ProcessedPivot(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Totals,
    IReadOnlyList<HighlightHit> Highlights,
    long TotalRows,
    bool Truncated,
    IReadOnlyList<IgnoredItem> Ignored);
