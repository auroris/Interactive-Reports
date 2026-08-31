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
    /// <summary>
    /// Binds terminal selection, ordering, highlights, breaks, and aggregates against a completed relation schema.
    /// </summary>
    /// <param name="local">The immutable owner-local specification.</param>
    /// <param name="schema">The completed relation schema used to bind all column references.</param>
    /// <param name="policy">The definition-level sort and filter restrictions.</param>
    /// <param name="errors">Receives fatal expression, aggregate, and highlight errors.</param>
    /// <param name="ignored">Receives stale, duplicate, or policy-restricted rules that can be dropped safely.</param>
    /// <param name="sharedContext">Optional document-wide aggregate/highlight budgets and identities; a fresh context is used when omitted.</param>
    /// <returns>The immutable terminal execution contract. Labels and formats remain empty because metadata is bound separately.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
    /// <remarks>Appends diagnostics to <paramref name="errors"/> and <paramref name="ignored"/> and advances counters in <paramref name="sharedContext"/>.</remarks>
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

    /// <summary>
    /// Binds terminal visible columns, dropping unknown and duplicate names.
    /// </summary>
    /// <param name="selection">The optional canonical select declaration.</param>
    /// <param name="schema">The completed schema that supplies canonical names and default order.</param>
    /// <param name="ignored">Receives unknown-column diagnostics.</param>
    /// <returns>Selected columns in authored order, or every schema column when selection is absent, select-all, empty, or entirely stale.</returns>
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

        // Treat an entirely stale selection as
        // select-all so a saved report cannot accidentally render a zero-column grid.
        return result.Count > 0 ? result : schema.Columns.ToList();
    }

    /// <summary>
    /// Binds terminal ordering, dropping unknown, restricted, and duplicate columns.
    /// </summary>
    /// <param name="ordering">The optional canonical sort declaration.</param>
    /// <param name="schema">The completed schema used to canonicalize names.</param>
    /// <param name="policy">Determines which schema columns remain sortable.</param>
    /// <param name="ignored">Receives dropped sort diagnostics.</param>
    /// <returns>Valid distinct sorts in authored order.</returns>
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

    /// <summary>
    /// Reserves document-wide highlight identities and precedence, then binds enabled effects and predicates.
    /// </summary>
    /// <param name="rules">Canonical highlight nodes in deterministic order.</param>
    /// <param name="population">Authored-rule count and source paths, including disabled rules.</param>
    /// <param name="columns">The completed schema lookup used by targets and expressions.</param>
    /// <param name="errors">Receives identity, precedence, style, and expression errors.</param>
    /// <param name="ignored">Receives non-fatal target diagnostics.</param>
    /// <param name="context">Document-wide highlight counters and reserved identities.</param>
    /// <returns>Compiled enabled highlight rules in canonical order.</returns>
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
            // Disabled highlights reserve identity, precedence, and projection
            // slots, but their expression and presentation effect are never bound or run.
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

    /// <summary>
    /// Binds control-break columns, dropping unknown, restricted, and duplicate names.
    /// </summary>
    /// <param name="breaks">The optional ordered break declaration.</param>
    /// <param name="schema">The completed schema used to canonicalize names.</param>
    /// <param name="policy">Determines which columns may participate because breaks imply sorting.</param>
    /// <param name="ignored">Receives dropped break diagnostics.</param>
    /// <returns>Valid distinct break columns in authored order.</returns>
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

    /// <summary>
    /// Binds terminal aggregates against the completed schema and document-wide aggregate budget.
    /// </summary>
    /// <param name="rules">Canonical aggregate declarations in deterministic order.</param>
    /// <param name="columns">The completed schema lookup used to bind input columns.</param>
    /// <param name="errors">Receives invalid aggregate errors.</param>
    /// <param name="ignored">Receives stale or duplicate aggregate diagnostics.</param>
    /// <param name="context">Document-wide aggregate count and identity state.</param>
    /// <returns>Valid aggregates in canonical order.</returns>
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
/// Local result binding context: carries document-wide identity and complexity counters across local-result binds.
/// It contains no relation or presentation state of its own.
/// </summary>
internal sealed class LocalResultBindingContext
{
    /// <summary>Gets document-wide aggregate validation state shared by all table binds.</summary>
    internal AggregateRuleValidator.Context Aggregates { get; } = new();
    /// <summary>Gets document-wide highlight validation state shared by all table binds.</summary>
    internal HighlightRuleValidator.Context Highlights { get; } = new();
}
