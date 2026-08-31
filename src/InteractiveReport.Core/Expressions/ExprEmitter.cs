using System.Text;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 3 of the portable expression pipeline: emits a typed AST as a dialect-specific SQL fragment. Identifiers use
/// SqlKata's portable bracket markers when possible and dialect quoting when their
/// names contain marker/delimiter characters. Literals become positional '?' bindings,
/// never inlined text (the one exception is the NULL keyword, which is ours, not client
/// data). Every binary
/// operation is parenthesized — verbose SQL beats precedence surprises.
///
/// Function SQL comes from the registry (ExprFunctions): the emitter knows shapes,
/// the registry knows names and dialect idioms. CASE/comparisons/BETWEEN/IS NULL
/// emit identically on every dialect — the portable core of the subset — except
/// that SQLite normalizes date comparands (see VisitComparand) and date arithmetic
/// is per-dialect by nature (see ExprFunctionEmitter.EmitDateAdd).
/// </summary>
public static class ExprEmitter
{
    /// <summary>
    /// Emits a bound value expression using the current UTC time for time-sensitive functions.
    /// </summary>
    /// <param name="ast">The bound expression tree to emit.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>The SQL fragment and positional binding values in placeholder order.</returns>
    public static (string Sql, IReadOnlyList<object> Bindings) Emit(ExprNode ast, ReportDialect dialect)
        => Emit(ast, dialect, DateTime.UtcNow);

    /// <summary>
    /// Emits a bound value expression using a fixed evaluation time.
    /// </summary>
    /// <param name="ast">The bound expression tree to emit.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <returns>The SQL fragment and positional binding values in placeholder order.</returns>
    public static (string Sql, IReadOnlyList<object> Bindings) Emit(
        ExprNode ast,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
        => Emit(ast, dialect, evaluationUtcNow, physicalColumns: null);

    /// <summary>
    /// Emits a bound value expression against an optional logical-to-physical column map.
    /// </summary>
    /// <param name="ast">The bound expression tree to emit.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="physicalColumns">Optional physical SQL identifiers keyed by logical column name.</param>
    /// <returns>The SQL fragment and positional binding values in placeholder order.</returns>
    internal static (string Sql, IReadOnlyList<object> Bindings) Emit(
        ExprNode ast,
        ReportDialect dialect,
        DateTime evaluationUtcNow,
        IReadOnlyDictionary<string, string>? physicalColumns)
    {
        var ctx = new EmitContext(dialect, evaluationUtcNow, physicalColumns);
        ctx.Visit(ast);
        return (ctx.Sql, ctx.Bindings);
    }

    /// <summary>
    /// Emits an AST where SQL requires a predicate. This matters for bare boolean columns: SQL Server needs
    /// an explicit = 1 while PostgreSQL must use the boolean value directly.
    /// </summary>
    /// <param name="ast">The bound expression tree to emit.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>The SQL predicate fragment and positional binding values in placeholder order.</returns>
    public static (string Sql, IReadOnlyList<object> Bindings) EmitCondition(
        ExprNode ast,
        ReportDialect dialect)
        => EmitCondition(ast, dialect, DateTime.UtcNow);

    /// <summary>
    /// Emits an expression as a SQL predicate with portable boolean semantics.
    /// </summary>
    /// <param name="ast">The bound expression tree to emit.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <returns>The SQL predicate fragment and positional binding values in placeholder order.</returns>
    public static (string Sql, IReadOnlyList<object> Bindings) EmitCondition(
        ExprNode ast,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
        => EmitCondition(ast, dialect, evaluationUtcNow, physicalColumns: null);

    /// <summary>
    /// Emits an expression as a SQL predicate with portable boolean semantics.
    /// </summary>
    /// <param name="ast">The bound expression tree to emit.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="physicalColumns">Optional physical SQL identifiers keyed by logical column name.</param>
    /// <returns>The SQL predicate fragment and positional binding values in placeholder order.</returns>
    internal static (string Sql, IReadOnlyList<object> Bindings) EmitCondition(
        ExprNode ast,
        ReportDialect dialect,
        DateTime evaluationUtcNow,
        IReadOnlyDictionary<string, string>? physicalColumns)
    {
        var ctx = new EmitContext(dialect, evaluationUtcNow, physicalColumns);
        ctx.VisitCondition(ast);
        return (ctx.Sql, ctx.Bindings);
    }
}

/// <summary>Accumulates one SQL fragment and its positional bindings while visiting a bound AST.</summary>
/// <param name="dialect">Controls identifier quoting, boolean predicates, functions, and date normalization.</param>
/// <param name="evaluationUtcNow">The request-stable instant used by time-sensitive functions.</param>
/// <param name="physicalColumns">Optional logical-to-physical identifier mapping for relation lowering.</param>
internal sealed class EmitContext(
    ReportDialect dialect,
    DateTime evaluationUtcNow,
    IReadOnlyDictionary<string, string>? physicalColumns = null)
{
    private readonly StringBuilder _sb = new();
    private readonly List<object> _bindings = [];

    /// <summary>Gets the SQL dialect used by this emission.</summary>
    public ReportDialect Dialect { get; } = dialect;
    /// <summary>Gets the evaluation time normalized to a UTC <see cref="DateTime"/>.</summary>
    public DateTime EvaluationUtcNow { get; } = evaluationUtcNow.Kind switch
    {
        DateTimeKind.Utc => evaluationUtcNow,
        DateTimeKind.Local => evaluationUtcNow.ToUniversalTime(),
        _ => DateTime.SpecifyKind(evaluationUtcNow, DateTimeKind.Utc),
    };
    /// <summary>Gets the SQL accumulated so far.</summary>
    public string Sql => _sb.ToString();
    /// <summary>Gets positional values in the same order as emitted <c>?</c> placeholders.</summary>
    public IReadOnlyList<object> Bindings => _bindings;

    /// <summary>
    /// Appends raw emitter-owned SQL text.
    /// </summary>
    /// <param name="text">Trusted SQL syntax, never client-authored literal data.</param>
    /// <returns>This context for fluent function emitters.</returns>
    public EmitContext Append(string text) { _sb.Append(text); return this; }
    /// <summary>
    /// Appends one emitter-owned SQL syntax character.
    /// </summary>
    /// <param name="c">The trusted syntax character.</param>
    /// <returns>This context for fluent function emitters.</returns>
    public EmitContext Append(char c) { _sb.Append(c); return this; }

    /// <summary>
    /// Appends a <c>?</c> placeholder and records its binding value.
    /// </summary>
    /// <param name="value">The provider value to add to the generated binding list.</param>
    /// <returns>This context for fluent function emitters.</returns>
    public EmitContext AppendBinding(object value)
    {
        _sb.Append('?');
        _bindings.Add(value);
        return this;
    }

    /// <summary>
    /// Emits one bound value or predicate node recursively.
    /// </summary>
    /// <param name="node">The typed AST node to emit.</param>
    /// <exception cref="InvalidOperationException">Thrown when no emission visitor exists for the node type.</exception>
    /// <remarks>Appends SQL and positional bindings to this context.</remarks>
    public void Visit(ExprNode node)
    {
        switch (node)
        {
            case NumberLit n:
                _sb.Append('?');
                _bindings.Add(n.Value);
                break;

            case StringLit s:
                _sb.Append('?');
                _bindings.Add(s.Value);
                break;

            case NullLit:
                _sb.Append("NULL");
                break;

            case ColumnRef c:
                _sb.Append(SqlKataSyntax.Identifier(Dialect,
                    physicalColumns is not null
                    && physicalColumns.TryGetValue(c.Column.Name, out var physical)
                        ? physical
                        : c.Column.Name));
                break;

            case UnaryMinus u:
                _sb.Append("(-");
                Visit(u.Operand);
                _sb.Append(')');
                break;

            case NotOp n:
                _sb.Append("(NOT ");
                VisitCondition(n.Operand);
                _sb.Append(')');
                break;

            case BinaryOp { Op: "||" } b:
                ExprFunctionEmitter.EmitConcat(this, [b.Left, b.Right]);
                break;

            case BinaryOp b:
                Infix(b.Op, b.Left, b.Right);
                break;

            case DateAdd d:
                ExprFunctionEmitter.EmitDateAdd(this, d);
                break;

            case Comparison c:
                _sb.Append('(');
                VisitComparand(c.Left);
                _sb.Append(' ').Append(c.Op).Append(' ');
                VisitComparand(c.Right);
                _sb.Append(')');
                break;

            case Between b:
                _sb.Append('(');
                VisitComparand(b.Operand);
                _sb.Append(" BETWEEN ");
                VisitComparand(b.Lower);
                _sb.Append(" AND ");
                VisitComparand(b.Upper);
                _sb.Append(')');
                break;

            case LogicalOp l:
                _sb.Append('(');
                VisitCondition(l.Left);
                _sb.Append(' ').Append(l.Op).Append(' ');
                VisitCondition(l.Right);
                _sb.Append(')');
                break;

            case NullTest t:
                _sb.Append('(');
                Visit(t.Operand);
                _sb.Append(t.Negated ? " IS NOT NULL)" : " IS NULL)");
                break;

            case CaseWhen c:
                EmitCase(c);
                break;

            case FuncCall f:
                ExprFunctions.Get(f.Name).Emit(this, f.Args);
                break;

            default:
                throw new InvalidOperationException($"unhandled AST node {node.GetType().Name}");
        }
    }

    /// <summary>
    /// Writes an infix expression with the precedence required by the portable expression language.
    /// </summary>
    /// <param name="op">The bound arithmetic operator.</param>
    /// <param name="left">The left bound operand.</param>
    /// <param name="right">The right bound operand.</param>
    private void Infix(string op, ExprNode left, ExprNode right)
    {
        if (op == "/")
        {
            // Integer division truncates on SQLite and SQL Server. The
            // portable expression contract is decimal division on every supported dialect, so
            // promote the numerator with a server-owned decimal literal. 1.0 participates as
            // NUMBER/numeric/decimal/REAL on the four dialects.
            _sb.Append("((1.0 * ");
            Visit(left);
            _sb.Append(") / ");
            Visit(right);
            _sb.Append(')');
            return;
        }

        _sb.Append('(');
        Visit(left);
        _sb.Append(' ').Append(op).Append(' ');
        Visit(right);
        _sb.Append(')');
    }

    /// <summary>
    /// Emits a node where SQL demands a predicate. Condition nodes pass through. A
    /// boolean-*valued* expression (a BIT column, say) is lowered to an explicit "= 1" test — T-SQL has no
    /// boolean expressions, so "WHEN [FLAG]" is invalid there even though the type checker rightly accepted
    /// the column as a condition. The 1 is our literal, not client data. PostgreSQL is the inverse: its
    /// booleans are real conditions, and "= 1" would be a boolean/integer type error — there the value emits
    /// bare.
    /// </summary>
    /// <param name="node">The bound Boolean or Boolean-valued node.</param>
    /// <remarks>Appends a dialect-appropriate predicate and any nested bindings.</remarks>
    internal void VisitCondition(ExprNode node)
    {
        if (node is Comparison or Between or LogicalOp or NotOp or NullTest
            or FuncCall { Kind: ColumnKind.Bool }
            || Dialect == ReportDialect.Postgres)
        {
            Visit(node);
            return;
        }
        _sb.Append('(');
        Visit(node);
        _sb.Append(" = 1)");
    }

    /// <summary>
    /// Emits a node in comparison position for comparisons, BETWEEN, or simple-CASE matching. On
    /// SQLite, Date values are ISO text and date-only text sorts before its own midnight timestamp, so date
    /// operands normalize through datetime() — except values from producers that already emit the canonical
    /// full form (NOW/TO_DATE/DATE_TRUNC and date arithmetic). Everywhere else this is a plain Visit.
    /// </summary>
    /// <param name="node">The bound value node used as a comparand.</param>
    private void VisitComparand(ExprNode node)
    {
        if (Dialect != ReportDialect.Sqlite || node.Kind != ColumnKind.Date || IsCanonicalSqliteDate(node))
        {
            Visit(node);
            return;
        }
        _sb.Append("datetime(");
        Visit(node);
        _sb.Append(')');
    }

    /// <summary>
    /// Determines whether a date-producing node already emits SQLite's canonical timestamp form.
    /// </summary>
    /// <param name="node">The bound date-valued node to classify.</param>
    /// <returns><see langword="true"/> for date arithmetic and canonical date functions; otherwise, <see langword="false"/>.</returns>
    private static bool IsCanonicalSqliteDate(ExprNode node)
        => node is DateAdd or FuncCall { Name: "NOW" or "TO_DATE" or "DATE_TRUNC" };

    /// <summary>
    /// Emits a bound CASE expression and all branch bindings.
    /// </summary>
    /// <param name="c">The bound simple or searched CASE node.</param>
    /// <remarks>Appends every branch and its positional bindings to this context.</remarks>
    private void EmitCase(CaseWhen c)
    {
        _sb.Append("CASE");
        if (c.Operand is not null)
        {
            _sb.Append(' ');
            VisitComparand(c.Operand);
        }
        foreach (var branch in c.Branches)
        {
            _sb.Append(" WHEN ");
            if (c.Operand is null) VisitCondition(branch.When);
            else VisitComparand(branch.When);
            _sb.Append(" THEN ");
            Visit(branch.Then);
        }
        if (c.Else is not null)
        {
            _sb.Append(" ELSE ");
            Visit(c.Else);
        }
        _sb.Append(" END");
    }
}
