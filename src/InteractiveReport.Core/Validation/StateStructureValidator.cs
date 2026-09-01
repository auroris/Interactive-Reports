using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Performs the schema-independent first validation pass over a report-state document.
/// It rejects structural nulls and excessive collection sizes before resolution, canonicalization,
/// parsing, or schema binding can allocate work from untrusted input.
/// </summary>
internal static class StateStructureValidator
{
    // These are document-complexity ceilings, not UI conventions. They bound
    // ancestry validation and the number of null schema caches one submission can ask the
    // server to refresh while leaving ample room for externally authored alternatives.
    internal const int MaxTables = 64;
    internal const int MaxComposables = 512;
    internal const int MaxTableDepth = 64;

    // Nested collections must be bounded before canonicalization copies, sorts, or
    // parses their members. Keep the rule-specific ceilings aligned with their existing
    // semantic budgets. The generic ceiling matches the completed-relation width guard and
    // applies to collections without a tighter semantic budget.
    internal const int MaxComputedRules = 20;
    internal const int MaxFilterRules = 50;
    internal const int MaxHighlightRules = 50;
    internal const int MaxShapeMetrics = 256;
    internal const int MaxNestedCollectionEntries = 900;
    internal const int MaxSearchLength = 200;

    /// <summary>
    /// Collects structural errors for table identities, ancestry inputs, composables, and nested rule values.
    /// </summary>
    /// <param name="state">The deserialized report-state document to inspect without schema access.</param>
    /// <returns>All structural errors discovered in deterministic document order.</returns>
    public static List<ValidationError> Collect(ReportState state)
    {
        var errors = new List<ValidationError>();
        // The search overlay is bound once per text column of the active relation, so its length
        // multiplies; the same ceiling the list-of-values search uses keeps that product bounded.
        if (state.Search is { Length: > MaxSearchLength })
            errors.Add(new ValidationError(
                "search",
                $"search cannot exceed {MaxSearchLength} characters"));
        if (state.Tables is null) return errors;

        if (state.Tables.Count > MaxTables)
        {
            errors.Add(new ValidationError(
                "tables",
                $"at most {MaxTables} tables are allowed per report document"));
            return errors;
        }

        var composableCount = state.Tables.Values
            .Where(table => table is not null)
            .Sum(table => (long)(table.Composables?.Count ?? 0));
        if (composableCount > MaxComposables)
        {
            errors.Add(new ValidationError(
                "tables",
                $"at most {MaxComposables} composables are allowed per report document"));
            return errors;
        }

        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, table) in state.Tables)
        {
            var path = $"tables.{name}";
            if (string.IsNullOrWhiteSpace(name))
                errors.Add(new ValidationError("tables", "table identifiers cannot be blank"));
            else if (string.Equals(name, "definition", StringComparison.OrdinalIgnoreCase))
                errors.Add(new ValidationError(
                    path,
                    "'definition' is reserved for the configured SQL input and cannot be a table identifier"));
            else if (!tableNames.Add(name))
                errors.Add(new ValidationError(
                    path,
                    $"table identifier '{name}' differs from another identifier only by case"));
            if (table is null)
            {
                errors.Add(new ValidationError(path, "tables must not be null"));
                continue;
            }
            RequireValue(table.From, $"{path}.from", errors);
            CollectRules(table.Schema, $"{path}.schema", errors, (column, columnPath) =>
            {
                RequireValue(column.Name, $"{columnPath}.name", errors);
                RequireValue(column.Label, $"{columnPath}.label", errors);
                RequireValue(column.Type, $"{columnPath}.type", errors);
            }, MaxNestedCollectionEntries, GenericCollectionLimitMessage);
            if (table.Composables is null) continue;
            for (var index = 0; index < table.Composables.Count; index++)
            {
                var composablePath = $"{path}.composables[{index}]";
                if (table.Composables[index] is not { } composable)
                {
                    errors.Add(new ValidationError(composablePath, "composables must not be null"));
                    continue;
                }
                RequireValue(composable.Kind, $"{composablePath}.kind", errors);
                CollectStrings(composable.By, $"{composablePath}.by", errors);
                CollectStrings(composable.Rows, $"{composablePath}.rows", errors);
                CollectStrings(composable.Cols, $"{composablePath}.cols", errors);
                CollectStrings(composable.Columns, $"{composablePath}.columns", errors);
                CollectStrings(composable.Breaks, $"{composablePath}.breaks", errors);
                CollectRules(composable.Values, $"{composablePath}.values", errors, (metric, metricPath) =>
                {
                    RequireValue(metric.Id, $"{metricPath}.id", errors);
                    RequireValue(metric.Col, $"{metricPath}.col", errors);
                }, MaxShapeMetrics, $"a shape may contain at most {MaxShapeMetrics} metrics");
                CollectRules(composable.Computed, $"{composablePath}.computed", errors, (rule, rulePath) =>
                {
                    RequireValue(rule.Id, $"{rulePath}.id", errors);
                    RequireValue(rule.Expr, $"{rulePath}.expr", errors);
                }, MaxComputedRules, $"at most {MaxComputedRules} computed columns per report state");
                CollectRules(composable.Filters, $"{composablePath}.filters", errors, (rule, rulePath) =>
                    RequireValue(rule.Expr, $"{rulePath}.expr", errors),
                    MaxFilterRules,
                    $"at most {MaxFilterRules} filter rules per report state");
                CollectRules(composable.Sorts, $"{composablePath}.sorts", errors, (rule, rulePath) =>
                    RequireValue(rule.Col, $"{rulePath}.col", errors),
                    MaxNestedCollectionEntries,
                    GenericCollectionLimitMessage);
                CollectRules(composable.Highlights, $"{composablePath}.highlights", errors, (rule, rulePath) =>
                {
                    RequireValue(rule.Id, $"{rulePath}.id", errors);
                    RequireValue(rule.Scope, $"{rulePath}.scope", errors);
                    RequireValue(rule.Expr, $"{rulePath}.expr", errors);
                }, MaxHighlightRules, $"at most {MaxHighlightRules} highlight rules per report state");
                CollectRules(composable.Aggregates, $"{composablePath}.aggregates", errors, (rule, rulePath) =>
                    RequireValue(rule.Col, $"{rulePath}.col", errors),
                    MaxNestedCollectionEntries,
                    GenericCollectionLimitMessage);
                CheckCollection(composable.Labels, $"{composablePath}.labels", errors);
                CollectFormats(composable.Formats, $"{composablePath}.formats", errors);
            }
        }
        return errors;
    }

    /// <summary>
    /// Validates the size and non-null elements of one rule collection, then delegates rule-specific checks.
    /// </summary>
    /// <typeparam name="T">The reference type stored by the JSON collection.</typeparam>
    /// <param name="rules">The optional collection to validate.</param>
    /// <param name="path">The collection's report-state validation path.</param>
    /// <param name="errors">Receives collection and element errors.</param>
    /// <param name="check">Validates one non-null element with its indexed path.</param>
    /// <param name="maxCount">The largest accepted collection size.</param>
    /// <param name="limitMessage">The message emitted when <paramref name="maxCount"/> is exceeded.</param>
    /// <remarks>An oversized collection produces one error and its elements are not traversed.</remarks>
    private static void CollectRules<T>(
        List<T>? rules,
        string path,
        List<ValidationError> errors,
        Action<T, string> check,
        int maxCount,
        string limitMessage)
        where T : class
    {
        if (rules is null) return;
        if (rules.Count > maxCount)
        {
            errors.Add(new ValidationError(path, limitMessage));
            return;
        }
        for (var index = 0; index < rules.Count; index++)
        {
            var rulePath = $"{path}[{index}]";
            if (rules[index] is not { } rule)
            {
                errors.Add(new ValidationError(rulePath, "list elements must not be null"));
                continue;
            }
            check(rule, rulePath);
        }
    }

    /// <summary>
    /// Validates the generic size ceiling and non-null elements of an optional string list.
    /// </summary>
    /// <param name="values">The optional JSON string list.</param>
    /// <param name="path">The list's report-state validation path.</param>
    /// <param name="errors">Receives size and null-element errors.</param>
    private static void CollectStrings(List<string>? values, string path, List<ValidationError> errors)
    {
        if (values is null) return;
        if (values.Count > MaxNestedCollectionEntries)
        {
            errors.Add(new ValidationError(path, GenericCollectionLimitMessage));
            return;
        }
        for (var index = 0; index < values.Count; index++)
            if (values[index] is null)
                errors.Add(new ValidationError($"{path}[{index}]", "list elements must not be null"));
    }

    /// <summary>
    /// Applies the generic size ceiling to an optional dictionary.
    /// </summary>
    /// <typeparam name="TKey">The dictionary key type.</typeparam>
    /// <typeparam name="TValue">The dictionary value type.</typeparam>
    /// <param name="values">The optional dictionary to bound.</param>
    /// <param name="path">The dictionary's report-state validation path.</param>
    /// <param name="errors">Receives the size error, if any.</param>
    private static void CheckCollection<TKey, TValue>(
        Dictionary<TKey, TValue>? values,
        string path,
        List<ValidationError> errors)
        where TKey : notnull
    {
        if (values is { Count: > MaxNestedCollectionEntries })
            errors.Add(new ValidationError(path, GenericCollectionLimitMessage));
    }

    /// <summary>
    /// Bounds the format map and every nested CSS class-token list.
    /// </summary>
    /// <param name="formats">The optional column-format map to inspect.</param>
    /// <param name="path">The map's report-state validation path.</param>
    /// <param name="errors">Receives map and nested-list size errors.</param>
    private static void CollectFormats(
        Dictionary<string, ColumnFormat>? formats,
        string path,
        List<ValidationError> errors)
    {
        if (formats is null) return;
        if (formats.Count > MaxNestedCollectionEntries)
        {
            errors.Add(new ValidationError(path, GenericCollectionLimitMessage));
            return;
        }

        foreach (var (column, format) in formats)
            if (format is not null)
                CollectStrings(format.Classes, $"{path}.{column}.classes", errors);
    }

    /// <summary>Gets the common validation message for collections without a tighter semantic budget.</summary>
    private static string GenericCollectionLimitMessage =>
        $"a collection may contain at most {MaxNestedCollectionEntries} entries";

    /// <summary>
    /// Adds a required-value error when a deserialized JSON string property is null.
    /// </summary>
    /// <param name="value">The deserialized string value to test.</param>
    /// <param name="path">The property's report-state validation path.</param>
    /// <param name="errors">Receives the missing-value error.</param>
    private static void RequireValue(string? value, string path, List<ValidationError> errors)
    {
        if (value is null)
            errors.Add(new ValidationError(path, "a value is required (null is not accepted)"));
    }
}
