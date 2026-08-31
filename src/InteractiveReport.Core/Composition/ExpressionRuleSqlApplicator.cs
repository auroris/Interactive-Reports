using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Applies compiled expression values according to their typed effects. Relation lowering
/// controls phase ordering; this class owns the SQL representation of each effect.
/// </summary>
internal static class ExpressionRuleSqlApplicator
{
    /// <summary>
    /// Adds a compiled expression as a projected definition column.
    /// </summary>
    /// <param name="query">The mutable SqlKata query receiving the projection.</param>
    /// <param name="rule">The bound expression and computed-column effect.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="physicalColumns">Optional logical-to-physical column mapping for a lowered relation.</param>
    /// <param name="physicalAlias">Optional output alias overriding the effect column name.</param>
    /// <remarks>Adds one raw select expression and its bindings to <paramref name="query"/>.</remarks>
    public static void ApplyDefinition(
        Query query,
        CompiledRule<DefineColumnEffect> rule,
        ReportDialect dialect,
        DateTime evaluationUtcNow,
        IReadOnlyDictionary<string, string>? physicalColumns = null,
        string? physicalAlias = null)
    {
        var (sql, bindings) = ExprEmitter.Emit(
            rule.Expression.Ast,
            dialect,
            evaluationUtcNow,
            physicalColumns);
        query.SelectRaw(
            $"{sql} AS {SqlKataSyntax.Identifier(dialect, physicalAlias ?? rule.Effect.Column.Name)}",
            bindings.ToArray());
    }

    /// <summary>
    /// Adds a compiled expression as a row predicate.
    /// </summary>
    /// <param name="query">The mutable SqlKata query receiving the predicate.</param>
    /// <param name="rule">The bound boolean expression and row-inclusion effect.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="physicalColumns">Optional logical-to-physical column mapping for a lowered relation.</param>
    /// <remarks>Adds one raw where predicate and its bindings to <paramref name="query"/>.</remarks>
    public static void ApplyRowPredicate(
        Query query,
        CompiledRule<IncludeRowEffect> rule,
        ReportDialect dialect,
        DateTime evaluationUtcNow,
        IReadOnlyDictionary<string, string>? physicalColumns = null)
    {
        var (sql, bindings) = ExprEmitter.EmitCondition(
            rule.Expression.Ast,
            dialect,
            evaluationUtcNow,
            physicalColumns);
        query.WhereRaw(sql, bindings.ToArray());
    }

    /// <summary>
    /// Adds a compiled expression as a query-only decoration column.
    /// </summary>
    /// <param name="query">The mutable SqlKata query receiving the private marker projection.</param>
    /// <param name="rule">The bound boolean expression and highlight effect.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="physicalColumns">Optional logical-to-physical column mapping for a lowered relation.</param>
    /// <remarks>Adds a private integer CASE projection and its bindings to <paramref name="query"/>.</remarks>
    public static void ApplyDecoration(
        Query query,
        CompiledRule<HighlightEffect> rule,
        ReportDialect dialect,
        DateTime evaluationUtcNow,
        IReadOnlyDictionary<string, string>? physicalColumns = null)
    {
        var (sql, bindings) = ExprEmitter.EmitCondition(
            rule.Expression.Ast,
            dialect,
            evaluationUtcNow,
            physicalColumns);
        query.SelectRaw(
            $"CASE WHEN {sql} THEN 1 ELSE 0 END AS {SqlKataSyntax.Identifier(dialect, rule.Effect.ProjectionName)}",
            bindings.ToArray());
    }
}
