using System.Text;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 3: emits a typed AST as a dialect-specific SQL fragment. Identifiers use
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
    public static (string Sql, IReadOnlyList<object> Bindings) Emit(ExprNode ast, ReportDialect dialect)
        => Emit(ast, dialect, DateTime.UtcNow);

    public static (string Sql, IReadOnlyList<object> Bindings) Emit(
        ExprNode ast,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
        => Emit(ast, dialect, evaluationUtcNow, physicalColumns: null);

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
    /// Emits an AST where SQL requires a predicate. This matters for bare boolean
    /// columns: SQL Server needs an explicit = 1 while PostgreSQL must use the
    /// boolean value directly.
    /// </summary>
    public static (string Sql, IReadOnlyList<object> Bindings) EmitCondition(
        ExprNode ast,
        ReportDialect dialect)
        => EmitCondition(ast, dialect, DateTime.UtcNow);

    public static (string Sql, IReadOnlyList<object> Bindings) EmitCondition(
        ExprNode ast,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
        => EmitCondition(ast, dialect, evaluationUtcNow, physicalColumns: null);

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

internal sealed class EmitContext(
    ReportDialect dialect,
    DateTime evaluationUtcNow,
    IReadOnlyDictionary<string, string>? physicalColumns = null)
{
    private readonly StringBuilder _sb = new();
    private readonly List<object> _bindings = [];

    public ReportDialect Dialect { get; } = dialect;
    public DateTime EvaluationUtcNow { get; } = evaluationUtcNow.Kind switch
    {
        DateTimeKind.Utc => evaluationUtcNow,
        DateTimeKind.Local => evaluationUtcNow.ToUniversalTime(),
        _ => DateTime.SpecifyKind(evaluationUtcNow, DateTimeKind.Utc),
    };
    public string Sql => _sb.ToString();
    public IReadOnlyList<object> Bindings => _bindings;

    public EmitContext Append(string text) { _sb.Append(text); return this; }
    public EmitContext Append(char c) { _sb.Append(c); return this; }

    /// <summary>A '?' placeholder bound to a value we computed (e.g. a translated format mask).</summary>
    public EmitContext AppendBinding(object value)
    {
        _sb.Append('?');
        _bindings.Add(value);
        return this;
    }

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

    private void Infix(string op, ExprNode left, ExprNode right)
    {
        if (op == "/")
        {
            // INTEGER / INTEGER truncates on SQLite and SQL Server. The portable
            // expression contract is decimal division on every supported dialect, so
            // promote the numerator with a server-owned decimal literal.
            // 1.0 participates as NUMBER/numeric/decimal/REAL on the four dialects.
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
    /// Emit a node where SQL demands a predicate. Condition nodes pass through.
    /// A boolean-*valued* expression (a BIT column, say) is lowered to an explicit
    /// "= 1" test — T-SQL has no boolean expressions, so "WHEN [FLAG]" is invalid
    /// there even though the type checker rightly accepted the column as a
    /// condition. The 1 is our literal, not client data. Postgres is the inverse:
    /// its booleans are real conditions, and "= 1" would be a boolean/integer type
    /// error — there the value emits bare.
    /// </summary>
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
    /// Emit a node in comparison position (comparisons, BETWEEN, simple-CASE
    /// matching). On SQLite, Date values are ISO text and date-only text sorts
    /// before its own midnight timestamp, so date operands normalize through
    /// datetime() — except values from producers that already emit the canonical
    /// full form (NOW/TO_DATE/DATE_TRUNC and date arithmetic). Everywhere else
    /// this is a plain Visit.
    /// </summary>
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

    private static bool IsCanonicalSqliteDate(ExprNode node)
        => node is DateAdd or FuncCall { Name: "NOW" or "TO_DATE" or "DATE_TRUNC" };

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
