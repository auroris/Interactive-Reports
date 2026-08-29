using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Folds ordinary composables over the schema produced immediately before them.
/// Shape composables decide how a schema and rowset are produced; every operation here
/// is shape-agnostic and retains document order and provenance.
/// </summary>
internal static class TableLayerValidator
{
    public static ValidTableLayer Validate(
        IReadOnlyList<LocatedTableComposable> composables,
        string schemaName,
        ReportSchema initialSchema,
        ColumnPolicy policy,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        TableLayerValidationContext? sharedContext = null)
    {
        sharedContext ??= new TableLayerValidationContext();
        var schema = initialSchema;
        var operations = new List<ValidTableOperation>();
        var computed = new List<CompiledRule<DefineColumnEffect>>();
        var filters = new List<CompiledRule<IncludeRowEffect>>();
        var sorts = new List<ValidSort>();
        var decorations = new List<CompiledRule<HighlightEffect>>();
        var aggregates = new List<ValidAggregate>();
        var breaks = new List<ColumnModel>();
        var sortNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var breakNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var computedContext = sharedContext.Computed;
        var aggregateContext = sharedContext.Aggregates;
        var highlightContext = sharedContext.Highlights;
        List<ColumnModel>? selected = null;
        var selectAll = true;
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formats = new Dictionary<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase);

        foreach (var located in composables)
        {
            var value = located.Value;
            var kind = (value.Kind ?? "").Trim().ToLowerInvariant();
            switch (kind)
            {
                case "compute":
                {
                    var rules = ComputedColumnValidator.Validate(
                        value.Computed,
                        schema.Lookup,
                        errors,
                        $"{located.Path}.computed",
                        computedContext);
                    if (rules.Count == 0) break;
                    computed.AddRange(rules);
                    operations.Add(new ValidTableOperation("compute", rules, []));
                    schema = schema.Extend(
                        schemaName,
                        rules.Select(rule => rule.Effect.Column));
                    break;
                }
                case "filter":
                {
                    sharedContext.FilterRuleCount += value.Filters?.Count ?? 0;
                    if (sharedContext.FilterRuleCount > 50)
                    {
                        errors.Add(new ValidationError(
                            $"{located.Path}.filters",
                            "at most 50 filter rules per report state"));
                        break;
                    }
                    var rules = ExpressionRuleCompiler.Compile<FilterRule, IncludeRowEffect>(
                        value.Filters,
                        int.MaxValue,
                        $"{located.Path}.filters",
                        schema.Lookup,
                        ExpressionRequirement.Predicate,
                        static (_, _) => _ => new IncludeRowEffect(),
                        errors);
                    rules = StateValidator.StripRestrictedFilters(rules, policy, schema, ignored);
                    if (rules.Count == 0) break;
                    filters.AddRange(rules);
                    operations.Add(new ValidTableOperation("filter", [], rules));
                    break;
                }
                case "sort":
                    foreach (var sort in StateValidator.ValidateSorts(value.Sorts, schema, ignored, policy))
                        if (sortNames.Add(sort.Column.Name)) sorts.Add(sort);
                    break;
                case "highlight":
                    decorations.AddRange(HighlightRuleValidator.Validate(
                        value.Highlights,
                        schema.Lookup,
                        errors,
                        ignored,
                        $"{located.Path}.highlights",
                        highlightContext));
                    break;
                case "select":
                    selectAll = value.Columns is not { Count: > 0 };
                    selected = selectAll
                        ? null
                        : StateValidator.ValidateColumns(value.Columns, schema, ignored);
                    break;
                case "aggregate":
                    aggregates.AddRange(AggregateRuleValidator.Validate(
                        value.Aggregates,
                        $"{located.Path}.aggregates",
                        schema.Lookup,
                        errors,
                        ignored,
                        aggregateContext));
                    break;
                case "break":
                    foreach (var column in StateValidator.ValidateBreaks(value.Breaks, schema, ignored, policy))
                        if (breakNames.Add(column.Name)) breaks.Add(column);
                    break;
                case "labels":
                    Merge(labels, StateValidator.ResolveLabels(value.Labels), value.Labels is { Count: 0 });
                    break;
                case "formats":
                    Merge(formats, StateValidator.ResolveFormats(value.Formats), value.Formats is { Count: 0 });
                    break;
            }
        }

        if (selectAll || selected is null)
            selected = schema.Columns.ToList();

        var projection = StateValidator.ResolveRendererColumns(
            formats,
            selected,
            schema,
            ignored);

        // Break keys are required row data, not necessarily visible columns.
        foreach (var column in breaks)
            if (!projection.Any(projected => string.Equals(
                    projected.Name,
                    column.Name,
                    StringComparison.OrdinalIgnoreCase)))
                projection.Add(column);

        return new ValidTableLayer(
            schema,
            operations,
            computed,
            filters,
            decorations,
            sorts,
            selected,
            projection,
            aggregates,
            breaks,
            labels,
            formats);
    }

    private static void Merge<T>(
        Dictionary<string, T> target,
        IReadOnlyDictionary<string, T> next,
        bool clear)
    {
        if (clear) target.Clear();
        foreach (var (name, value) in next) target[name] = value;
    }
}

internal sealed class TableLayerValidationContext
{
    internal ComputedColumnValidator.Context Computed { get; } = new();
    internal AggregateRuleValidator.Context Aggregates { get; } = new();
    internal HighlightRuleValidator.Context Highlights { get; } = new();
    internal int FilterRuleCount { get; set; }
}
