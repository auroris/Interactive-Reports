using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Emits a typed AST as a dialect-specific SQL fragment. Identifiers are emitted in
/// SqlKata bracket form ("[COL]") so raw fragments still get dialect quoting; literals
/// become positional '?' bindings, never inlined text. Every binary operation is
/// parenthesized — verbose SQL beats precedence surprises.
///
/// Known semantic notes (documented, not hidden):
/// - Concatenation treats NULL as empty on all three dialects (CONCAT on
///   SqlServer/Sqlite; Oracle's native || already behaves that way).
/// - YEAR/MONTH/DAY accept ISO date text on SQLite (dates discover as text there).
/// </summary>
public static class ExprEmitter
{
    public static (string Sql, IReadOnlyList<object> Bindings) Emit(ExprNode ast, ReportDialect dialect)
    {
        var sb = new StringBuilder();
        var bindings = new List<object>();
        Visit(ast, dialect, sb, bindings);
        return (sb.ToString(), bindings);
    }

    private static void Visit(ExprNode node, ReportDialect d, StringBuilder sb, List<object> bindings)
    {
        switch (node)
        {
            case NumberLit n:
                sb.Append('?');
                bindings.Add(n.Value);
                break;

            case StringLit s:
                sb.Append('?');
                bindings.Add(s.Value);
                break;

            case ColumnRef c:
                sb.Append('[').Append(c.Column.Name).Append(']');
                break;

            case UnaryMinus u:
                sb.Append("(-");
                Visit(u.Operand, d, sb, bindings);
                sb.Append(')');
                break;

            case BinaryOp { Op: "||" } b:
                EmitConcat(d, sb, bindings, [b.Left, b.Right]);
                break;

            case BinaryOp b:
                sb.Append('(');
                Visit(b.Left, d, sb, bindings);
                sb.Append(' ').Append(b.Op).Append(' ');
                Visit(b.Right, d, sb, bindings);
                sb.Append(')');
                break;

            case FuncCall f:
                EmitFunc(f, d, sb, bindings);
                break;

            default:
                throw new InvalidOperationException($"unhandled AST node {node.GetType().Name}");
        }
    }

    private static void EmitFunc(FuncCall f, ReportDialect d, StringBuilder sb, List<object> bindings)
    {
        switch (f.Fn)
        {
            case ExprFn.Upper or ExprFn.Lower or ExprFn.Trim or ExprFn.Abs or ExprFn.Round or ExprFn.Coalesce:
                EmitPlain(f.Fn.ToString().ToUpperInvariant(), f.Args, d, sb, bindings);
                break;

            case ExprFn.Length:
                EmitPlain(d == ReportDialect.SqlServer ? "LEN" : "LENGTH", f.Args, d, sb, bindings);
                break;

            case ExprFn.Substr when d == ReportDialect.SqlServer:
                // SUBSTRING requires the length argument; "to end of string" is LEN(s).
                sb.Append("SUBSTRING(");
                Visit(f.Args[0], d, sb, bindings);
                sb.Append(", ");
                Visit(f.Args[1], d, sb, bindings);
                sb.Append(", ");
                if (f.Args.Count == 3)
                {
                    Visit(f.Args[2], d, sb, bindings);
                }
                else
                {
                    sb.Append("LEN(");
                    Visit(f.Args[0], d, sb, bindings);
                    sb.Append(')');
                }
                sb.Append(')');
                break;

            case ExprFn.Substr:
                EmitPlain("SUBSTR", f.Args, d, sb, bindings);
                break;

            case ExprFn.Concat:
                EmitConcat(d, sb, bindings, f.Args);
                break;

            case ExprFn.Year or ExprFn.Month or ExprFn.Day:
                EmitDatePart(f, d, sb, bindings);
                break;

            default:
                throw new InvalidOperationException($"unhandled function {f.Fn}");
        }
    }

    private static void EmitPlain(string name, IReadOnlyList<ExprNode> args, ReportDialect d, StringBuilder sb, List<object> bindings)
    {
        sb.Append(name).Append('(');
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Visit(args[i], d, sb, bindings);
        }
        sb.Append(')');
    }

    private static void EmitConcat(ReportDialect d, StringBuilder sb, List<object> bindings, IReadOnlyList<ExprNode> args)
    {
        if (d == ReportDialect.Oracle)
        {
            // Oracle CONCAT is two-arg only; native || already treats NULL as empty.
            sb.Append('(');
            for (var i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(" || ");
                Visit(args[i], d, sb, bindings);
            }
            sb.Append(')');
            return;
        }

        // Variadic CONCAT treats NULL as empty on SqlServer and SQLite (3.44+).
        EmitPlain("CONCAT", args, d, sb, bindings);
    }

    private static void EmitDatePart(FuncCall f, ReportDialect d, StringBuilder sb, List<object> bindings)
    {
        var arg = f.Args[0];
        switch (d)
        {
            case ReportDialect.SqlServer:
                EmitPlain(f.Fn.ToString().ToUpperInvariant(), f.Args, d, sb, bindings);
                break;

            case ReportDialect.Oracle:
                sb.Append("EXTRACT(").Append(f.Fn.ToString().ToUpperInvariant()).Append(" FROM ");
                Visit(arg, d, sb, bindings);
                sb.Append(')');
                break;

            case ReportDialect.Sqlite:
                var format = f.Fn switch
                {
                    ExprFn.Year => "%Y",
                    ExprFn.Month => "%m",
                    _ => "%d",
                };
                sb.Append("CAST(strftime('").Append(format).Append("', ");
                Visit(arg, d, sb, bindings);
                sb.Append(") AS INTEGER)");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(d), d, null);
        }
    }
}
