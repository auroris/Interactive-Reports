using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// The expression-rule language frontend, staged (ARCHITECTURE §8):
///
///   text ── ExprSyntaxParser ──► untyped syntax tree (shape + positions)
///        ── ExprBinder ────────► typed AST (schema + function registry)
///        ── ExprEmitter ───────► dialect SQL fragment + bindings
///
/// This facade exposes the two valid top-level expression roles: computed values
/// and row conditions. Every failure is a message referencing only the client's
/// own input; parse errors are validation errors, never SQL errors.
/// </summary>
public static class ExprParser
{
    /// <summary>
    /// Parse and bind an expression, then enforce the result contract required by
    /// the rule effect that will consume it.
    /// </summary>
    public static (ExprNode? Ast, string? Error) Parse(
        string expression,
        IReadOnlyDictionary<string, ColumnModel> schema,
        ExpressionRequirement requirement)
    {
        var (ast, error) = ParseAndBind(expression, schema);
        if (error is not null) return (null, error);

        return requirement switch
        {
            ExpressionRequirement.Predicate when ast!.Kind != ColumnKind.Bool
                => (null, "the expression must produce a true/false condition"),
            ExpressionRequirement.Value when ast!.Kind == ColumnKind.Bool
                => (null, "the expression is a condition — wrap it in CASE WHEN <condition> THEN 1 ELSE 0 END to compute a value"),
            ExpressionRequirement.Value when ast is NullLit
                => (null, "the expression cannot be just NULL"),
            _ => (ast, null),
        };
    }

    /// <summary>Schema keys are base-schema column names (case-insensitive dictionary).</summary>
    public static (ExprNode? Ast, string? Error) Parse(string expression, IReadOnlyDictionary<string, ColumnModel> schema)
        => Parse(expression, schema, ExpressionRequirement.Value);

    /// <summary>A filter/highlight expression must produce a boolean condition.</summary>
    public static (ExprNode? Ast, string? Error) ParseCondition(
        string expression,
        IReadOnlyDictionary<string, ColumnModel> schema)
        => Parse(expression, schema, ExpressionRequirement.Predicate);

    private static (ExprNode? Ast, string? Error) ParseAndBind(
        string expression,
        IReadOnlyDictionary<string, ColumnModel> schema)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (null, "expression is empty");
        if (expression.Length > 2000)
            return (null, "expression exceeds 2000 characters");

        try
        {
            var syntax = ExprSyntaxParser.Parse(expression);
            var ast = ExprBinder.Bind(syntax, schema);
            return (ast, null);
        }
        catch (ExprError ex)
        {
            return (null, ex.Message);
        }
    }
}

/// <summary>The result contract imposed by an expression rule's effect.</summary>
public enum ExpressionRequirement
{
    Value,
    Predicate,
}
