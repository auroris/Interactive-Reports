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

    internal static bool TryBeginBatch(
        int ruleCount,
        string collectionPath,
        Context context,
        List<ValidationError> errors,
        out int offset)
    {
        offset = context.RuleCount;
        context.RuleCount += ruleCount;
        if (context.RuleCount <= 50) return true;

        errors.Add(new ValidationError(
            collectionPath,
            "at most 50 highlight rules per report state"));
        return false;
    }

    internal static bool TryReserveOrder(
        string id,
        int? requestedSequence,
        int globalIndex,
        Context context,
        List<ValidationError> errors,
        string rulePath,
        out int sequence)
    {
        sequence = requestedSequence ?? ((globalIndex + 1) * 10);
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new ValidationError(rulePath, "highlight id is required"));
            return false;
        }
        if (!context.SeenIds.Add(id))
        {
            errors.Add(new ValidationError(rulePath, $"duplicate highlight id '{id}'"));
            return false;
        }

        if (sequence <= 0)
        {
            errors.Add(new ValidationError(
                $"{rulePath}.sequence",
                "highlight sequence must be positive"));
            return false;
        }
        if (!context.SeenSequences.Add(sequence))
        {
            errors.Add(new ValidationError(
                $"{rulePath}.sequence",
                $"duplicate highlight sequence '{sequence}'"));
            return false;
        }
        return true;
    }

    internal static Func<BoundExpression, HighlightEffect>? PrepareEffect(
        string id,
        string? name,
        int sequence,
        string scopeName,
        string? columnName,
        string? background,
        string? foreground,
        int globalIndex,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        string rulePath)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? id : name.Trim();

        var scope = ParseScope(scopeName, rulePath, errors);
        if (scope is null) return null;

        ColumnModel? cellColumn = null;
        if (scope == HighlightScope.Cell
            && (columnName is null || !columns.TryGetValue(columnName, out cellColumn)))
        {
            ignored.Add(new IgnoredItem(
                "highlight",
                $"'{id}': unknown cell column '{columnName}'"));
            return null;
        }

        if (string.IsNullOrWhiteSpace(background)
            && string.IsNullOrWhiteSpace(foreground))
        {
            errors.Add(new ValidationError(
                $"{rulePath}.style",
                "pick a background or text color"));
            return null;
        }

        return _ => new HighlightEffect(
            id,
            normalizedName,
            sequence,
            scope.Value,
            cellColumn,
            ProjectionName(globalIndex, columns));
    }

    private static HighlightScope? ParseScope(
        string scopeName,
        string path,
        List<ValidationError> errors)
    {
        if (string.Equals(scopeName, "row", StringComparison.OrdinalIgnoreCase))
            return HighlightScope.Row;
        if (string.Equals(scopeName, "cell", StringComparison.OrdinalIgnoreCase))
            return HighlightScope.Cell;

        errors.Add(new ValidationError(
            path,
            $"scope must be 'row' or 'cell', got '{scopeName}'"));
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
