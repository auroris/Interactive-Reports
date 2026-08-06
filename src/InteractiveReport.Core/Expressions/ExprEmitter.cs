using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 3: emits a typed AST as a dialect-specific SQL fragment. Identifiers are
/// emitted in SqlKata bracket form ("[COL]") so raw fragments still get dialect
/// quoting; literals become positional '?' bindings, never inlined text (the one
/// exception is the NULL keyword, which is ours, not client data). Every binary
/// operation is parenthesized — verbose SQL beats precedence surprises.
///
/// Function SQL comes from the registry (ExprFunctions): the emitter knows shapes,
/// the registry knows names and dialect idioms. CASE/comparisons/IS NULL emit
/// identically on all three dialects — that is the portable core of the subset.
/// </summary>
public static class ExprEmitter
{
    public static (string Sql, IReadOnlyList<object> Bindings) Emit(ExprNode ast, ReportDialect dialect)
    {
        var ctx = new EmitContext(dialect);
        ctx.Visit(ast);
        return (ctx.Sql, ctx.Bindings);
    }
}

internal sealed class EmitContext(ReportDialect dialect)
{
    private readonly StringBuilder _sb = new();
    private readonly List<object> _bindings = [];

    public ReportDialect Dialect { get; } = dialect;
    public string Sql => _sb.ToString();
    public IReadOnlyList<object> Bindings => _bindings;

    public EmitContext Append(string text) { _sb.Append(text); return this; }
    public EmitContext Append(char c) { _sb.Append(c); return this; }

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
                _sb.Append('[').Append(c.Column.Name).Append(']');
                break;

            case UnaryMinus u:
                _sb.Append("(-");
                Visit(u.Operand);
                _sb.Append(')');
                break;

            case NotOp n:
                _sb.Append("(NOT ");
                Visit(n.Operand);
                _sb.Append(')');
                break;

            case BinaryOp { Op: "||" } b:
                ExprFunctions.EmitConcat(this, [b.Left, b.Right]);
                break;

            case BinaryOp b:
                Infix(b.Op, b.Left, b.Right);
                break;

            case Comparison c:
                Infix(c.Op, c.Left, c.Right);
                break;

            case LogicalOp l:
                Infix(l.Op, l.Left, l.Right);
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
        _sb.Append('(');
        Visit(left);
        _sb.Append(' ').Append(op).Append(' ');
        Visit(right);
        _sb.Append(')');
    }

    private void EmitCase(CaseWhen c)
    {
        _sb.Append("CASE");
        if (c.Operand is not null)
        {
            _sb.Append(' ');
            Visit(c.Operand);
        }
        foreach (var branch in c.Branches)
        {
            _sb.Append(" WHEN ");
            Visit(branch.When);
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
