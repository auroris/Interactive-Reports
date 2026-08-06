using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// The computed-column expression pipeline, staged (ARCHITECTURE §8):
///
///   text ── ExprSyntaxParser ──► untyped syntax tree (shape + positions)
///        ── ExprBinder ────────► typed AST (schema + function registry)
///        ── ExprEmitter ───────► dialect SQL fragment + bindings
///
/// This facade is the single entry point: parse + bind, plus the two rules that
/// only make sense at the top of a computed column — the result must be a value
/// (not a condition, which SQL Server cannot select; not bare NULL, which has no
/// type). Every failure is a message referencing only the client's own input —
/// parse errors are validation errors, never SQL errors.
/// </summary>
public static class ExprParser
{
    /// <summary>Schema keys are base-schema column names (case-insensitive dictionary).</summary>
    public static (ExprNode? Ast, string? Error) Parse(string expression, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (null, "expression is empty");
        if (expression.Length > 2000)
            return (null, "expression exceeds 2000 characters");

        try
        {
            var syntax = ExprSyntaxParser.Parse(expression);
            var ast = ExprBinder.Bind(syntax, schema);

            if (ast.Kind == ColumnKind.Bool)
                return (null, "the expression is a condition — wrap it in CASE WHEN <condition> THEN 1 ELSE 0 END to compute a value");
            if (ast is NullLit)
                return (null, "the expression cannot be just NULL");

            return (ast, null);
        }
        catch (ExprError ex)
        {
            return (null, ex.Message);
        }
    }
}
