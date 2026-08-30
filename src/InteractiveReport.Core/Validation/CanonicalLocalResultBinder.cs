using System.Collections.Immutable;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Binds the owner-local portion of a canonical table directly against its completed
/// relation schema. This keeps terminal presentation out of the mutable
/// <see cref="TableComposable"/> validation boundary while preserving the canonical
/// drop, deduplication, policy, and expression rules.
/// </summary>
internal static class CanonicalLocalResultBinder
{
    public static BoundLocalResult Bind(
        CanonicalLocalResult local,
        ReportSchema schema,
        ColumnPolicy policy,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        LocalResultBindingContext? sharedContext = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(ignored);

        sharedContext ??= new LocalResultBindingContext();

        var selected = BindSelection(local.Selection, schema, ignored);
        var sorts = BindOrdering(local.Ordering, schema, policy, ignored);
        var highlights = BindHighlights(
            local.Highlights,
            local.HighlightPopulation,
            schema.Lookup,
            errors,
            ignored,
            sharedContext.Highlights);
        var breaks = BindBreaks(local.Breaks, schema, policy, ignored);
        var aggregates = BindAggregates(
            local.Aggregates,
            schema.Lookup,
            errors,
            ignored,
            sharedContext.Aggregates);

        var projection = selected.ToList();
        var projectedNames = new HashSet<string>(
            projection.Select(column => column.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var column in breaks)
            if (projectedNames.Add(column.Name)) projection.Add(column);

        return new BoundLocalResult(
            schema,
            Decorations: highlights.ToImmutableArray(),
            Sorts: sorts.ToImmutableArray(),
            SelectColumns: selected.ToImmutableArray(),
            ProjectionColumns: projection.ToImmutableArray(),
            Aggregates: aggregates.ToImmutableArray(),
            Breaks: breaks.ToImmutableArray(),
            Labels: ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
            Formats: ImmutableDictionary.Create<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase));
    }

    private static List<ColumnModel> BindSelection(
        CanonicalSelection? selection,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        if (selection is null || selection.SelectAll || selection.Columns.IsDefaultOrEmpty)
            return schema.Columns.ToList();

        var result = new List<ColumnModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in selection.Columns)
        {
            if (!schema.TryGetValue(name, out var column))
            {
                ignored.Add(new IgnoredItem("column", $"unknown column '{name}'"));
                continue;
            }
            if (seen.Add(column.Name)) result.Add(column);
        }

        // The existing terminal validator treats an entirely stale selection as
        // select-all so a saved report cannot accidentally render a zero-column grid.
        return result.Count > 0 ? result : schema.Columns.ToList();
    }

    private static List<ValidSort> BindOrdering(
        CanonicalOrdering? ordering,
        ReportSchema schema,
        ColumnPolicy policy,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidSort>();
        if (ordering is null || ordering.Sorts.IsDefaultOrEmpty) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sort in ordering.Sorts)
        {
            if (!schema.TryGetValue(sort.Column, out var column))
            {
                ignored.Add(new IgnoredItem("sort", $"unknown column '{sort.Column}'"));
                continue;
            }
            if (!policy.IsSortable(column))
            {
                ignored.Add(new IgnoredItem("sort", $"column '{column.Name}' is not sortable"));
                continue;
            }
            if (seen.Add(column.Name))
                result.Add(new ValidSort(column, sort.Direction, sort.Nulls));
        }
        return result;
    }

    private static List<CompiledRule<HighlightEffect>> BindHighlights(
        ImmutableArray<CanonicalHighlight> rules,
        CanonicalRulePopulation population,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        HighlightRuleValidator.Context context)
    {
        var result = new List<CompiledRule<HighlightEffect>>();
        if (!HighlightRuleValidator.TryBeginBatch(
                population.AuthoredCount,
                population.BudgetPath("highlights"),
                context,
                errors,
                out var offset)) return result;
        if (rules.IsDefaultOrEmpty) return result;

        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            var path = rule.SourcePath;
            var globalIndex = offset + index;
            if (!HighlightRuleValidator.TryReserveOrder(
                    rule.Id,
                    rule.Sequence,
                    globalIndex,
                    context,
                    errors,
                    path,
                    out var sequence)) continue;
            // Disabled highlights reserve identity, precedence, and projection slots,
            // but their expression and presentation effect are never bound or run.
            if (!rule.Enabled) continue;
            var effect = HighlightRuleValidator.PrepareEffect(
                rule.Id,
                rule.Name,
                sequence,
                rule.Scope,
                rule.Column,
                rule.Style?.Background,
                rule.Style?.Foreground,
                globalIndex,
                columns,
                errors,
                ignored,
                path);
            if (effect is null) continue;

            var expression = ExpressionRuleCompiler.Bind(
                rule.Expression,
                columns,
                ExpressionRequirement.Predicate,
                $"{path}.expr",
                errors);
            if (expression is null) continue;

            result.Add(new CompiledRule<HighlightEffect>(
                expression,
                effect(expression)));
        }

        return result;
    }

    private static List<ColumnModel> BindBreaks(
        CanonicalBreaks? breaks,
        ReportSchema schema,
        ColumnPolicy policy,
        List<IgnoredItem> ignored)
    {
        var result = new List<ColumnModel>();
        if (breaks is null || breaks.Columns.IsDefaultOrEmpty) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in breaks.Columns)
        {
            if (!schema.TryGetValue(name, out var column))
            {
                ignored.Add(new IgnoredItem("break", $"unknown column '{name}'"));
                continue;
            }
            if (!policy.IsSortable(column))
            {
                ignored.Add(new IgnoredItem(
                    "break",
                    $"column '{column.Name}' is not sortable (control breaks imply sorting)"));
                continue;
            }
            if (seen.Add(column.Name)) result.Add(column);
        }
        return result;
    }

    private static List<ValidAggregate> BindAggregates(
        ImmutableArray<CanonicalAggregate> rules,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        AggregateRuleValidator.Context context)
    {
        var result = new List<ValidAggregate>();
        if (rules.IsDefaultOrEmpty) return result;

        foreach (var rule in rules)
        {
            var aggregate = AggregateRuleValidator.Bind(
                rule.Column,
                rule.Function,
                rule.SourcePath,
                columns,
                errors,
                ignored,
                context);
            if (aggregate is not null) result.Add(aggregate);
        }
        return result;
    }

}

/// <summary>
/// Carries document-wide identity and complexity counters across local-result binds.
/// It contains no relation or presentation state of its own.
/// </summary>
internal sealed class LocalResultBindingContext
{
    internal AggregateRuleValidator.Context Aggregates { get; } = new();
    internal HighlightRuleValidator.Context Highlights { get; } = new();
}
