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
    /// <see cref="ExpressionRule"/> DTO. Callers retain the exact source path owned by
    /// the canonical node.
    /// </summary>
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
