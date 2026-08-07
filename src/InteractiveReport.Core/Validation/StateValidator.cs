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

        // Display labels resolve here — one ingestion path for every consumer — but
        // they are presentation, not a program: never validated against the schema
        // (unknown keys are unused display data) and never applied to query surfaces.
        // The definition's columnLabels are the bottom default layer, mirroring the
        // default report the schema endpoint delivers.
        var labels = ResolveLabels(resolved.Labels ?? def.ColumnLabels);

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
                sorts = [];
            }
        }

        // Break columns must be selected — renderers group page rows by their values.
        foreach (var b in breaks)
        {
            if (!columns.Any(c => string.Equals(c.Name, b.Name, StringComparison.OrdinalIgnoreCase)))
                columns.Add(b);
        }

        var formats = ResolveFormats(resolved.Formats);

        // A renderer may read a different row column than the displayed slot. Keep
        // those dependencies out of Columns/result metadata, but bind every identifier
        // through the discovered schema before it reaches query composition.
        var projectionColumns = view.Mode == ViewMode.Grid
            ? ResolveRendererColumns(formats, columns, effectiveSchema, ignored)
            : columns.ToList();

        var search = string.IsNullOrWhiteSpace(resolved.Search) ? null : resolved.Search.Trim();
        if (search is not null && !columns.Any(c => c.Kind == ColumnKind.Text))
        {
            ignored.Add(new IgnoredItem("search", "no visible text columns to search"));
            search = null;
        }

        var (pageIndex, pageSize, pageAll) = ClampPage(resolved.Page, def);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);

        return new ValidatedState
        {
            Schema = effectiveSchema,
            Rules = new ExpressionRulePlan(computed, filters, highlights),
            Search = search,
            Sorts = sorts,
            SelectColumns = columns,
            ProjectionColumns = projectionColumns,
            Formats = formats,
            Aggregates = aggregates,
            Breaks = breaks,
            View = view,
            PageIndex = pageIndex,
            PageSize = pageSize,
            PageAll = pageAll,
            Ignored = ignored,
            Labels = labels,
        };
    }

    private static readonly IReadOnlyDictionary<string, string> NoLabels =
        new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> ResolveLabels(
        Dictionary<string, string>? labels)
    {
        if (labels is not { Count: > 0 }) return NoLabels;

        var resolved = new Dictionary<string, string>(labels.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, label) in labels)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(label))
                resolved[name] = label.Trim();
        }
        return resolved;
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

    private static readonly IReadOnlyDictionary<string, ColumnFormat> NoFormats =
        new Dictionary<string, ColumnFormat>();

    private static IReadOnlyDictionary<string, ColumnFormat> ResolveFormats(
        Dictionary<string, ColumnFormat>? formats)
    {
        if (formats is not { Count: > 0 }) return NoFormats;

        var result = new Dictionary<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, format) in formats)
        {
            if (!string.IsNullOrWhiteSpace(name) && format is not null)
                result[name] = format;
        }
        return result;
    }

    private static List<ColumnModel> ResolveRendererColumns(
        IReadOnlyDictionary<string, ColumnFormat> formats,
        IReadOnlyList<ColumnModel> displayed,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        var result = displayed.ToList();
        if (formats.Count == 0) return result;
        var seen = new HashSet<string>(result.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var column in displayed)
        {
            if (!formats.TryGetValue(column.Name, out var format)) continue;

            var renderer = format.DisplayAs?.Trim();
            if (!string.Equals(renderer, "link", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(renderer, "image", StringComparison.OrdinalIgnoreCase))
                continue;

            Add(format.UrlColumn, "URL", column);
            if (string.Equals(renderer, "link", StringComparison.OrdinalIgnoreCase))
                Add(format.TextColumn, "text", column);
        }

        return result;

        void Add(string? requested, string role, ColumnModel fallback)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                if (seen.Add(fallback.Name)) result.Add(fallback);
                return;
            }

            if (!schema.TryGetValue(requested, out var source))
            {
                ignored.Add(new IgnoredItem(
                    "format",
                    $"renderer for '{fallback.Name}' references unknown {role} column '{requested}'"));
                return;
            }

            if (seen.Add(source.Name)) result.Add(source);
        }
    }

    private static (int Index, int Size, bool All) ClampPage(PageRequest? page, ReportDefinition def)
    {
        var size = page?.Size ?? def.DefaultPageSize;
        if (size == 0)
            return (1, 0, true);

        size = Math.Clamp(size, 1, def.MaxPageSize);
        var index = Math.Max(1, page?.Index ?? 1);
        return (index, size, false);
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
                result.Add(new ValidSort(col, s.Dir, s.Nulls));
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
