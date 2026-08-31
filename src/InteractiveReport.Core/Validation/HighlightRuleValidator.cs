using InteractiveReport.Core.Model;
using InteractiveReport.Core.Expressions;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates highlight identity, order, scope, target, style, and row condition.</summary>
internal static class HighlightRuleValidator
{
    /// <summary>Tracks report-wide highlight ids, sequences, and resource usage across table binds.</summary>
    internal sealed class Context
    {
        /// <summary>Gets the case-insensitive ids already reserved.</summary>
        public HashSet<string> SeenIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Gets the explicit or derived sequence numbers already reserved.</summary>
        public HashSet<int> SeenSequences { get; } = [];
        /// <summary>Gets or sets the total highlight rules encountered across the report.</summary>
        public int RuleCount { get; set; }
    }

    /// <summary>
    /// Attempts to reserve resource budget for a batch of highlight rules.
    /// </summary>
    /// <param name="ruleCount">The number of rules in the next collection.</param>
    /// <param name="collectionPath">The highlight collection path to use when the report-wide budget is exceeded.</param>
    /// <param name="context">The report-wide highlight context to advance.</param>
    /// <param name="errors">The validation list that receives a report-wide budget error.</param>
    /// <param name="offset">Receives the report-wide index of the collection's first rule.</param>
    /// <returns><see langword="true"/> when the highlight batch fits within the resource budget; otherwise, <see langword="false"/>.</returns>
    /// <remarks>Advances <paramref name="context"/>'s rule count even when the budget is exceeded and appends an error on failure.</remarks>
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

    /// <summary>
    /// Attempts to reserve a unique evaluation sequence for a highlight rule.
    /// </summary>
    /// <param name="id">The authored highlight id to reserve.</param>
    /// <param name="requestedSequence">The caller-supplied highlight sequence to reserve.</param>
    /// <param name="globalIndex">The report-wide zero-based rule index used to derive a default sequence.</param>
    /// <param name="context">The report-wide identity and sequence sets.</param>
    /// <param name="errors">The validation list that receives missing id, invalid sequence, and duplicate errors.</param>
    /// <param name="rulePath">The highlight rule path used for order diagnostics.</param>
    /// <param name="sequence">Receives the requested or derived positive sequence.</param>
    /// <returns><see langword="true"/> when the requested highlight order is valid and available; otherwise, <see langword="false"/>.</returns>
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

    /// <summary>
    /// Validates and prepares a highlight effect for execution.
    /// </summary>
    /// <param name="id">The reserved highlight id.</param>
    /// <param name="name">The optional display name; blank values fall back to <paramref name="id"/>.</param>
    /// <param name="sequence">The reserved evaluation sequence.</param>
    /// <param name="scopeName">The authored <c>row</c> or <c>cell</c> token.</param>
    /// <param name="columnName">The target column required by cell scope.</param>
    /// <param name="background">The optional CSS background color carried by a highlight effect.</param>
    /// <param name="foreground">The optional CSS foreground color carried by a highlight effect.</param>
    /// <param name="globalIndex">The report-wide rule index used to derive a private marker name.</param>
    /// <param name="columns">The current schema used to resolve cell targets and avoid marker collisions.</param>
    /// <param name="errors">The validation list that receives invalid scope and missing-style errors.</param>
    /// <param name="ignored">The diagnostics list that receives an unknown cell target.</param>
    /// <param name="rulePath">The highlight rule path used for property diagnostics.</param>
    /// <returns>A callback that combines the subsequently bound condition with the validated effect, or <see langword="null"/> on failure.</returns>
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

    /// <summary>
    /// Parses and validates a highlight evaluation scope.
    /// </summary>
    /// <param name="scopeName">The authored scope token.</param>
    /// <param name="path">The exact scope property path.</param>
    /// <param name="errors">The validation list that receives an unsupported-scope error.</param>
    /// <returns>The normalized scope, or <see langword="null"/> after appending an error.</returns>
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

    /// <summary>
    /// Builds a private marker projection name that does not collide with the current schema.
    /// </summary>
    /// <param name="index">The report-wide zero-based highlight index.</param>
    /// <param name="columns">The current columns whose names must be avoided.</param>
    /// <returns>The physical projection name for the requested logical column.</returns>
    private static string ProjectionName(
        int index,
        IReadOnlyDictionary<string, ColumnModel> columns)
    {
        var name = $"__ir_highlight_{index}";
        while (columns.ContainsKey(name)) name = $"_{name}";
        return name;
    }
}
