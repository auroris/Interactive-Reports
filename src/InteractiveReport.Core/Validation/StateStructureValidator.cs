using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Rejects structural nulls before document resolution and schema binding.</summary>
internal static class StateStructureValidator
{
    // These are document-complexity ceilings, not UI conventions. They bound ancestry
    // validation and the number of null schema caches one submission can ask the server
    // to refresh while leaving ample room for externally authored alternatives.
    internal const int MaxTables = 64;
    internal const int MaxComposables = 512;

    public static List<ValidationError> Collect(ReportState state)
    {
        var errors = new List<ValidationError>();
        if (state.Tables is null) return errors;

        if (state.Tables.Count > MaxTables)
            errors.Add(new ValidationError(
                "tables",
                $"at most {MaxTables} tables are allowed per report document"));

        var composableCount = state.Tables.Values
            .Where(table => table is not null)
            .Sum(table => table.Composables?.Count ?? 0);
        if (composableCount > MaxComposables)
            errors.Add(new ValidationError(
                "tables",
                $"at most {MaxComposables} composables are allowed per report document"));

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
            });
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
                });
                CollectRules(composable.Computed, $"{composablePath}.computed", errors, (rule, rulePath) =>
                {
                    RequireValue(rule.Id, $"{rulePath}.id", errors);
                    RequireValue(rule.Expr, $"{rulePath}.expr", errors);
                });
                CollectRules(composable.Filters, $"{composablePath}.filters", errors, (rule, rulePath) =>
                    RequireValue(rule.Expr, $"{rulePath}.expr", errors));
                CollectRules(composable.Sorts, $"{composablePath}.sorts", errors, (rule, rulePath) =>
                    RequireValue(rule.Col, $"{rulePath}.col", errors));
                CollectRules(composable.Highlights, $"{composablePath}.highlights", errors, (rule, rulePath) =>
                {
                    RequireValue(rule.Id, $"{rulePath}.id", errors);
                    RequireValue(rule.Scope, $"{rulePath}.scope", errors);
                    RequireValue(rule.Expr, $"{rulePath}.expr", errors);
                });
                CollectRules(composable.Aggregates, $"{composablePath}.aggregates", errors, (rule, rulePath) =>
                    RequireValue(rule.Col, $"{rulePath}.col", errors));
            }
        }
        return errors;
    }

    private static void CollectRules<T>(
        List<T>? rules,
        string path,
        List<ValidationError> errors,
        Action<T, string> check)
        where T : class
    {
        if (rules is null) return;
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

    private static void CollectStrings(List<string>? values, string path, List<ValidationError> errors)
    {
        if (values is null) return;
        for (var index = 0; index < values.Count; index++)
            if (values[index] is null)
                errors.Add(new ValidationError($"{path}[{index}]", "list elements must not be null"));
    }

    private static void RequireValue(string? value, string path, List<ValidationError> errors)
    {
        if (value is null)
            errors.Add(new ValidationError(path, "a value is required (null is not accepted)"));
    }
}
