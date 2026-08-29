using InteractiveReport.Core.Model;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Legacy synchronous validator for the static, zero-or-one-shape query planner.
/// Production named-table documents use <see cref="ReportExecutor.ValidateDocument"/>
/// because recursive composition and dynamic Pivot schemas require an async database
/// scope. Unknown columns remain ignored for saved-report resilience; structurally or
/// semantically invalid rules produce precise validation errors.
/// </summary>
internal static class StateValidator
{
    /// <summary>
    /// Validates the legacy synchronous, statically shaped query plan. Named tables
    /// containing multiple shapes or a data-derived Pivot schema require
    /// <see cref="ReportExecutor.ValidateDocument"/>, which uses the recursive compiler
    /// and a database read scope for Pivot discovery.
    /// </summary>
    public static ValidatedState Validate(
        ReportDefinition def,
        ReportState state,
        IReadOnlyList<ColumnModel> schema)
        => Validate(def, state, ReportSchema.Create(def.Name, schema));

    public static ValidatedState Validate(ReportDefinition def, ReportState state, ReportSchema schema)
        => Validate(def, state, schema, DateTime.UtcNow);

    internal static ValidatedState Validate(
        ReportDefinition def,
        ReportState state,
        ReportSchema schema,
        DateTime evaluationUtcNow)
    {
        evaluationUtcNow = evaluationUtcNow.Kind switch
        {
            DateTimeKind.Utc => evaluationUtcNow,
            DateTimeKind.Local => evaluationUtcNow.ToUniversalTime(),
            _ => DateTime.SpecifyKind(evaluationUtcNow, DateTimeKind.Utc),
        };

        // Structural nulls crash the resolver's deep copy before any schema check runs,
        // so they gate here. A structurally broken caller document is the caller's 400;
        // a broken default state is server-side data and fails as a configuration error.
        var structural = StateStructureValidator.Collect(state);
        if (structural.Count > 0)
            throw new ReportValidationException(structural);
        if (def.DefaultState is not null
            && StateStructureValidator.Collect(def.DefaultState) is { Count: > 0 } defaultErrors)
            throw new InvalidOperationException(
                $"Report '{def.Name}': the default state document is structurally invalid — "
                + $"{defaultErrors[0].Path}: {defaultErrors[0].Message}.");

        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var resolved = ReportStateResolver.Resolve(def.DefaultState, state);
        var composition = TableCompositionValidator.Fold(resolved, errors);
        var policy = ColumnPolicy.From(def);
        var sourcePlan = TableLayerValidator.Validate(
            composition.Input,
            def.Name,
            schema,
            policy,
            errors,
            ignored);
        var view = TableCompositionValidator.ValidateTail(
            composition,
            def.Name,
            sourcePlan.Schema,
            errors,
            ignored,
            policy);

        var gridMode = view.Mode == ViewMode.Grid;
        var columns = sourcePlan.SelectColumns.ToList();
        var sorts = gridMode ? sourcePlan.Sorts : [];
        var aggregates = gridMode ? sourcePlan.Aggregates : [];
        var breaks = gridMode ? sourcePlan.Breaks : [];
        var highlights = gridMode ? sourcePlan.Decorations : [];
        var formats = sourcePlan.Formats;

        // Labels are opaque presentation data. An explicit labels composable, including
        // an empty map, replaces the definition defaults at the definition-input table.
        var hasSourceLabels = composition.Input.Any(item =>
            string.Equals(item.Value.Kind, "labels", StringComparison.OrdinalIgnoreCase));
        var labels = hasSourceLabels
            ? sourcePlan.Labels
            : ResolveLabels(def.GetEffectiveColumnLabels());

        // A renderer may read a different row column than the displayed slot. Keep
        // those dependencies out of Columns/result metadata, but bind every identifier
        // through the discovered schema before it reaches query composition. The
        // definition's edit link widens the projection the same way: its template
        // columns ride as hidden row data whether or not the view displays them.
        var projectionColumns = gridMode
            ? sourcePlan.ProjectionColumns.ToList()
            : columns.ToList();
        if (gridMode && def.EditLink is not null)
            AddEditLinkColumns(def.EditLink, projectionColumns, schema, ignored);

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
            Policy = policy,
            EvaluationUtcNow = evaluationUtcNow,
            Schema = sourcePlan.Schema,
            Operations = sourcePlan.Operations,
            Rules = new ExpressionRulePlan(sourcePlan.Computed, sourcePlan.RowPredicates, highlights),
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

    internal static IReadOnlyDictionary<string, string> ResolveLabels(
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

    internal static List<ColumnModel> ValidateBreaks(
        List<string>? requested,
        ReportSchema schema,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy = null)
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
            if (policy?.IsSortable(col) == false)
            {
                ignored.Add(new IgnoredItem(
                    "break",
                    $"column '{col.Name}' is not sortable (control breaks imply sorting)"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(col);
        }
        return result;
    }

    /// <summary>
    /// Drops filter rules that reference a non-filterable base column. Runs after
    /// compilation so malformed expressions still surface as precise errors, and
    /// reads the bound AST so references resolve to canonical columns (a filter on
    /// a computed column stays, even when the computation reads restricted inputs).
    /// </summary>
    internal static List<CompiledRule<IncludeRowEffect>> StripRestrictedFilters(
        List<CompiledRule<IncludeRowEffect>> filters,
        ColumnPolicy policy,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        if (filters.Count == 0 || !policy.HasFilterRestrictions) return filters;

        var kept = new List<CompiledRule<IncludeRowEffect>>(filters.Count);
        foreach (var rule in filters)
        {
            var blocked = ExprColumns.Collect(rule.Expression.Ast)
                .FirstOrDefault(name => schema.TryGetValue(name, out var col) && !policy.IsFilterable(col));
            if (blocked is null)
            {
                kept.Add(rule);
                continue;
            }
            ignored.Add(new IgnoredItem(
                "filter",
                $"filter references non-filterable column '{blocked}'"));
        }
        return kept;
    }

    /// <summary>
    /// Appends the edit link's template columns to the grid projection so their values
    /// ride as hidden row data — same mechanics as renderer source columns. Binding is
    /// against the definition schema: the template is definition-authored, and computed ids
    /// are document-scoped names the definition cannot know.
    /// </summary>
    internal static void AddEditLinkColumns(
        ReportEditLink editLink,
        List<ColumnModel> projection,
        ReportSchema baseSchema,
        List<IgnoredItem> ignored)
    {
        var placeholders = EditLinkTemplate.Parse(editLink.UrlTemplate, out var error);
        if (placeholders is null)
        {
            // Configuration validation rejects malformed templates; an in-code
            // definition can still carry one — degrade, never fail the query.
            ignored.Add(new IgnoredItem("editLink", $"invalid urlTemplate — {error}"));
            return;
        }

        var seen = new HashSet<string>(projection.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in placeholders)
        {
            if (!baseSchema.TryGetValue(name, out var col))
            {
                ignored.Add(new IgnoredItem("editLink", $"references unknown column '{name}'"));
                continue;
            }
            if (seen.Add(col.Name)) projection.Add(col);
        }
    }

    private static readonly IReadOnlyDictionary<string, ColumnFormat> NoFormats =
        new Dictionary<string, ColumnFormat>();

    internal static IReadOnlyDictionary<string, ColumnFormat> ResolveFormats(
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

    internal static List<ColumnModel> ResolveRendererColumns(
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
            if (string.Equals(renderer, "action", StringComparison.OrdinalIgnoreCase))
            {
                // The action event needs its key in row data. Unlike link/image,
                // a blank source binds nothing: the labeled action cell itself is
                // already displayed, so there is no fallback to add.
                if (!string.IsNullOrWhiteSpace(format.KeyColumn))
                    Add(format.KeyColumn, "key", column);
                continue;
            }
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

    internal static List<ValidSort> ValidateSorts(
        List<SortRule>? sorts,
        ReportSchema schema,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy = null)
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
            if (policy?.IsSortable(col) == false)
            {
                ignored.Add(new IgnoredItem("sort", $"column '{col.Name}' is not sortable"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(new ValidSort(col, s.Dir, s.Nulls));
        }
        return result;
    }

    internal static List<ColumnModel> ValidateColumns(
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
