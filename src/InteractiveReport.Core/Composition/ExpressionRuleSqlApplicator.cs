using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Applies compiled expression values according to their typed effects. QueryComposer
/// controls phase ordering; this class owns the SQL representation of each effect.
/// </summary>
internal static class ExpressionRuleSqlApplicator
{
    public static void ApplyDefinition(
        Query query,
        CompiledRule<DefineColumnEffect> rule,
        ReportDialect dialect)
    {
        var (sql, bindings) = ExprEmitter.Emit(rule.Expression.Ast, dialect);
        query.SelectRaw($"{sql} AS [{rule.Effect.Column.Name}]", bindings.ToArray());
    }

    public static void ApplyRowPredicate(
        Query query,
        CompiledRule<IncludeRowEffect> rule,
        ReportDialect dialect)
    {
        var (sql, bindings) = ExprEmitter.EmitCondition(rule.Expression.Ast, dialect);
        query.WhereRaw(sql, bindings.ToArray());
    }

    public static void ApplyDecoration(
        Query query,
        CompiledRule<HighlightEffect> rule,
        ReportDialect dialect)
    {
        var (sql, bindings) = ExprEmitter.EmitCondition(rule.Expression.Ast, dialect);
        query.SelectRaw(
            $"CASE WHEN {sql} THEN 1 ELSE 0 END AS [{rule.Effect.ProjectionName}]",
            bindings.ToArray());
    }
}
