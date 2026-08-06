using InteractiveReport.Core.Model;
using InteractiveReport.Core.Expressions;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates highlight identity, scope, target, style, and row condition.</summary>
internal static class HighlightRuleValidator
{
    public static List<CompiledRule<HighlightEffect>> Validate(
        List<HighlightRule>? rules,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ExpressionRuleCompiler.Compile<HighlightRule, HighlightEffect>(
            rules,
            maxRules: 50,
            collectionPath: "highlights",
            columns,
            ExpressionRequirement.Predicate,
            prepareEffect: (rule, index) => PrepareEffect(
                rule,
                index,
                columns,
                seenIds,
                errors,
                ignored),
            errors);
    }

    private static Func<BoundExpression, HighlightEffect>? PrepareEffect(
        HighlightRule rule,
        int index,
        IReadOnlyDictionary<string, ColumnModel> columns,
        HashSet<string> seenIds,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var path = $"highlights[{index}]";
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            errors.Add(new ValidationError(path, "highlight id is required"));
            return null;
        }
        if (!seenIds.Add(rule.Id))
        {
            errors.Add(new ValidationError(path, $"duplicate highlight id '{rule.Id}'"));
            return null;
        }

        var scope = ParseScope(rule, path, errors);
        if (scope is null) return null;

        ColumnModel? cellColumn = null;
        if (scope == HighlightScope.Cell
            && (rule.Col is null || !columns.TryGetValue(rule.Col, out cellColumn)))
        {
            ignored.Add(new IgnoredItem(
                "highlight",
                $"'{rule.Id}': unknown cell column '{rule.Col}'"));
            return null;
        }

        if (string.IsNullOrWhiteSpace(rule.Style?.Bg)
            && string.IsNullOrWhiteSpace(rule.Style?.Fg))
        {
            errors.Add(new ValidationError($"{path}.style", "pick a background or text color"));
            return null;
        }

        return _ => new HighlightEffect(
            rule.Id,
            scope.Value,
            cellColumn,
            ProjectionName(index, columns));
    }

    private static HighlightScope? ParseScope(
        HighlightRule rule,
        string path,
        List<ValidationError> errors)
    {
        if (string.Equals(rule.Scope, "row", StringComparison.OrdinalIgnoreCase))
            return HighlightScope.Row;
        if (string.Equals(rule.Scope, "cell", StringComparison.OrdinalIgnoreCase))
            return HighlightScope.Cell;

        errors.Add(new ValidationError(
            path,
            $"scope must be 'row' or 'cell', got '{rule.Scope}'"));
        return null;
    }

    private static string ProjectionName(
        int index,
        IReadOnlyDictionary<string, ColumnModel> columns)
    {
        var name = $"__ir_highlight_{index}";
        while (columns.ContainsKey(name)) name = $"_{name}";
        return name;
    }
}
