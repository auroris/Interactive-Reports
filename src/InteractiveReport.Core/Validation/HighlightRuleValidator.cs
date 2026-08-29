using InteractiveReport.Core.Model;
using InteractiveReport.Core.Expressions;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates highlight identity, scope, target, style, and row condition.</summary>
internal static class HighlightRuleValidator
{
    internal sealed class Context
    {
        public HashSet<string> SeenIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> SeenSequences { get; } = [];
        public int RuleCount { get; set; }
    }

    public static List<CompiledRule<HighlightEffect>> Validate(
        List<HighlightRule>? rules,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        string collectionPath = "highlights",
        Context? context = null)
    {
        context ??= new Context();
        var offset = context.RuleCount;
        context.RuleCount += rules?.Count ?? 0;
        if (context.RuleCount > 50)
        {
            errors.Add(new ValidationError(
                collectionPath,
                "at most 50 highlight rules per report state"));
            return [];
        }

        return ExpressionRuleCompiler.Compile<HighlightRule, HighlightEffect>(
            rules,
            maxRules: int.MaxValue,
            collectionPath,
            columns,
            ExpressionRequirement.Predicate,
            prepareEffect: (rule, index) => PrepareEffect(
                rule,
                localIndex: index,
                globalIndex: offset + index,
                columns,
                context.SeenIds,
                context.SeenSequences,
                errors,
                ignored,
                collectionPath),
            errors);
    }

    private static Func<BoundExpression, HighlightEffect>? PrepareEffect(
        HighlightRule rule,
        int localIndex,
        int globalIndex,
        IReadOnlyDictionary<string, ColumnModel> columns,
        HashSet<string> seenIds,
        HashSet<int> seenSequences,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        string collectionPath)
    {
        var path = $"{collectionPath}[{localIndex}]";
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

        var sequence = rule.Sequence ?? ((globalIndex + 1) * 10);
        if (sequence <= 0)
        {
            errors.Add(new ValidationError($"{path}.sequence", "highlight sequence must be positive"));
            return null;
        }
        if (!seenSequences.Add(sequence))
        {
            errors.Add(new ValidationError($"{path}.sequence", $"duplicate highlight sequence '{sequence}'"));
            return null;
        }
        var name = string.IsNullOrWhiteSpace(rule.Name) ? rule.Id : rule.Name.Trim();

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
            name,
            sequence,
            scope.Value,
            cellColumn,
            ProjectionName(globalIndex, columns));
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
