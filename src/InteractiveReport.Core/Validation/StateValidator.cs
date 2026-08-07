using InteractiveReport.Core.Model;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Turns a raw state document into a ValidatedState against the discovered schema.
/// Policy: elements referencing unknown columns are dropped into ignored[] (saved-report
/// resilience); structurally wrong requests (bad arity, untypeable values, or expressions
/// that do not produce the required type) are precise validation errors.
/// </summary>
public static class StateValidator
{
    public static ValidatedState Validate(
        ReportDefinition def,
        ReportState state,
        IReadOnlyList<ColumnModel> schema)
        => Validate(def, state, ReportSchema.Create(def.Name, schema));

    public static ValidatedState Validate(ReportDefinition def, ReportState state, ReportSchema schema)
    {
        if (state.V != ReportState.CurrentVersion)
        {
            throw new ReportValidationException(
                [new ValidationError(
                    "v",
                    $"state version {state.V} is not supported; migrate filters and highlights to version {ReportState.CurrentVersion} expression rules")]);
        }

        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var resolved = ReportStateResolver.Resolve(def.DefaultState, state);

        // Computed columns validate first against the BASE schema, then join the
        // effective schema — everything after this line treats them as ordinary columns.
        var computed = ComputedColumnValidator.Validate(
            resolved.Computed,
            schema.Lookup,
            errors);
        var effectiveSchema = schema.Extend(def.Name, computed.Select(rule => rule.Effect.Column));

        var filters = ExpressionRuleCompiler.Compile<FilterRule, IncludeRowEffect>(
            resolved.Filters,
            maxRules: 50,
            collectionPath: "filters",
            effectiveSchema.Lookup,
            ExpressionRequirement.Predicate,
            prepareEffect: static (_, _) => _ => new IncludeRowEffect(),
            errors);
        var sorts = ValidateSorts(resolved.Sorts, effectiveSchema, ignored);
        var columns = ValidateColumns(resolved.Columns, effectiveSchema, ignored);
        var aggregates = AggregateRuleValidator.Validate(
            resolved.Aggregates,
            "aggregates",
            effectiveSchema.Lookup,
            errors,
            ignored);
        var breaks = ValidateBreaks(resolved.Breaks, effectiveSchema, ignored);
        var highlights = HighlightRuleValidator.Validate(
            resolved.Highlights,
            effectiveSchema.Lookup,
            errors,
            ignored);
        var view = ViewSpecValidator.Validate(
            resolved.View,
            effectiveSchema.Lookup,
            errors,
            ignored);

        if (view.Mode != ViewMode.Grid)
        {
            // Alternate views present aggregated rows; grid-only features are noted, not fatal.
            if (breaks.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "control breaks apply to the grid view only"));
                breaks = [];
            }
            if (highlights.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "highlights apply to the grid view only"));
                highlights = [];
            }
            if (aggregates.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "grid aggregates are ignored in alternate views (use view.values)"));
                aggregates = [];
            }

            if (view.Mode == ViewMode.GroupBy)
            {
                var dims = new HashSet<string>(view.GroupBy.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
                var kept = sorts.Where(s => dims.Contains(s.Column.Name)).ToList();
                if (kept.Count != sorts.Count)
                    ignored.Add(new IgnoredItem("view", "sorts on non-grouped columns are ignored in groupBy view"));
                sorts = kept;
            }
            else if (view.Mode == ViewMode.Chart && sorts.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "chart view orders by its own chart sort; sorts are ignored"));
                sorts = [];
            }
            else if (view.Mode == ViewMode.Pivot && sorts.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "pivot view orders by its dimensions; sorts are ignored"));
                sorts = [];
            }
        }

        // Break columns must be selected — renderers group page rows by their values.
        foreach (var b in breaks)
        {
            if (!columns.Any(c => string.Equals(c.Name, b.Name, StringComparison.OrdinalIgnoreCase)))
                columns.Add(b);
        }

        var search = string.IsNullOrWhiteSpace(resolved.Search) ? null : resolved.Search.Trim();
        if (search is not null && !columns.Any(c => c.Kind == ColumnKind.Text))
        {
            ignored.Add(new IgnoredItem("search", "no visible text columns to search"));
            search = null;
        }

        var (pageIndex, pageSize) = ClampPage(resolved.Page, def);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);

        return new ValidatedState
        {
            Schema = effectiveSchema,
            Rules = new ExpressionRulePlan(computed, filters, highlights),
            Search = search,
            Sorts = sorts,
            SelectColumns = columns,
            Aggregates = aggregates,
            Breaks = breaks,
            View = view,
            PageIndex = pageIndex,
            PageSize = pageSize,
            Ignored = ignored,
        };
    }

    private static List<ColumnModel> ValidateBreaks(
        List<string>? requested,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        var result = new List<ColumnModel>();
        if (requested is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in requested)
        {
            if (!schema.TryGetValue(name, out var col))
            {
                ignored.Add(new IgnoredItem("break", $"unknown column '{name}'"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(col);
        }
        return result;
    }

    private static (int Index, int Size) ClampPage(PageRequest? page, ReportDefinition def)
    {
        var size = page?.Size ?? def.DefaultPageSize;
        size = Math.Clamp(size, 1, def.MaxPageSize);
        var index = Math.Max(1, page?.Index ?? 1);
        return (index, size);
    }

    private static List<ValidSort> ValidateSorts(
        List<SortRule>? sorts,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidSort>();
        if (sorts is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sorts)
        {
            if (!schema.TryGetValue(s.Col, out var col))
            {
                ignored.Add(new IgnoredItem("sort", $"unknown column '{s.Col}'"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(new ValidSort(col, s.Dir));
        }
        return result;
    }

    private static List<ColumnModel> ValidateColumns(
        List<string>? requested,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        if (requested is null || requested.Count == 0)
            return schema.Columns.ToList();

        var result = new List<ColumnModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in requested)
        {
            if (!schema.TryGetValue(name, out var col))
            {
                ignored.Add(new IgnoredItem("column", $"unknown column '{name}'"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(col);
        }

        return result.Count > 0 ? result : schema.Columns.ToList();
    }
}
