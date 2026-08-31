using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Parses and binds canonical expressions while retaining their exact source paths.
/// </summary>
internal static class ExpressionRuleCompiler
{
    /// <summary>
    /// Parses and binds one canonical expression without requiring a mutable
    /// <see cref="ExpressionRule"/> DTO. Callers retain the exact source path owned by the canonical node.
    /// </summary>
    /// <param name="source">The expression source text; <see langword="null"/> is treated as an empty expression.</param>
    /// <param name="schema">The column schema used to bind names, types, and capabilities.</param>
    /// <param name="requirement">The result contract that the bound expression must satisfy.</param>
    /// <param name="expressionPath">The exact expression property path to attach to parse or binding errors.</param>
    /// <param name="errors">The collection to which a path-specific parse or binding error is appended.</param>
    /// <returns>The bound expression, or <see langword="null"/> when parsing or binding fails.</returns>
    /// <remarks>Appends at most one error to <paramref name="errors"/>.</remarks>
    internal static BoundExpression? Bind(
        string? source,
        IReadOnlyDictionary<string, ColumnModel> schema,
        ExpressionRequirement requirement,
        string expressionPath,
        List<ValidationError> errors)
    {
        var (ast, error) = ExprParser.Parse(source ?? "", schema, requirement);
        if (error is not null)
        {
            errors.Add(new ValidationError(expressionPath, error));
            return null;
        }

        return new BoundExpression(ast!);
    }
}
