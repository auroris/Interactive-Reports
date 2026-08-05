using System.Globalization;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Recursive-descent parser + type checker for the computed-column grammar:
///
///   expr   := term (('+'|'-'|'||') term)*
///   term   := factor (('*'|'/') factor)*
///   factor := number | 'string' | column | func '(' args ')' | '(' expr ')' | '-' factor
///
/// Types check bottom-up during the parse. Every failure is a message referencing only
/// the client's own input — parse errors are validation errors, never SQL errors.
/// </summary>
public sealed class ExprParser
{
    private const int MaxDepth = 64;

    private readonly string _input;
    private readonly IReadOnlyDictionary<string, ColumnModel> _schema;
    private readonly List<Token> _tokens = [];
    private int _pos;
    private int _depth;

    private ExprParser(string input, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        _input = input;
        _schema = schema;
    }

    /// <summary>Schema keys are base-schema column names (case-insensitive dictionary).</summary>
    public static (ExprNode? Ast, string? Error) Parse(string expression, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (null, "expression is empty");
        if (expression.Length > 2000)
            return (null, "expression exceeds 2000 characters");

        var parser = new ExprParser(expression, schema);
        try
        {
            parser.Tokenize();
            var ast = parser.ParseExpr();
            if (parser._pos < parser._tokens.Count)
                throw new ExprError($"unexpected '{parser.Current.Text}' at position {parser.Current.Position + 1}");
            return (ast, null);
        }
        catch (ExprError ex)
        {
            return (null, ex.Message);
        }
    }

    // --- lexer ---------------------------------------------------------------

    private enum TokKind { Number, String, Ident, Op }

    private readonly record struct Token(TokKind Kind, string Text, int Position);

    private void Tokenize()
    {
        var i = 0;
        while (i < _input.Length)
        {
            var c = _input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < _input.Length && (char.IsDigit(_input[i]) || _input[i] == '.')) i++;
                _tokens.Add(new Token(TokKind.Number, _input[start..i], start));
                continue;
            }

            if (c == '\'')
            {
                var start = i;
                i++;
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    if (i >= _input.Length)
                        throw new ExprError($"unterminated string starting at position {start + 1}");
                    if (_input[i] == '\'')
                    {
                        if (i + 1 < _input.Length && _input[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(_input[i]);
                    i++;
                }
                _tokens.Add(new Token(TokKind.String, sb.ToString(), start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < _input.Length && (char.IsLetterOrDigit(_input[i]) || _input[i] is '_' or '$' or '#')) i++;
                _tokens.Add(new Token(TokKind.Ident, _input[start..i], start));
                continue;
            }

            if (c == '|')
            {
                if (i + 1 < _input.Length && _input[i + 1] == '|')
                {
                    _tokens.Add(new Token(TokKind.Op, "||", i));
                    i += 2;
                    continue;
                }
                throw new ExprError($"single '|' at position {i + 1} (use '||' for concatenation)");
            }

            if (c is '+' or '-' or '*' or '/' or '(' or ')' or ',')
            {
                _tokens.Add(new Token(TokKind.Op, c.ToString(), i));
                i++;
                continue;
            }

            throw new ExprError($"unexpected character '{c}' at position {i + 1}");
        }
    }

    // --- parser --------------------------------------------------------------

    private Token Current => _pos < _tokens.Count
        ? _tokens[_pos]
        : throw new ExprError("unexpected end of expression");

    private bool AtOp(string op) => _pos < _tokens.Count
        && _tokens[_pos].Kind == TokKind.Op && _tokens[_pos].Text == op;

    private void Expect(string op)
    {
        if (!AtOp(op))
            throw new ExprError(_pos < _tokens.Count
                ? $"expected '{op}' but found '{Current.Text}' at position {Current.Position + 1}"
                : $"expected '{op}' but the expression ended");
        _pos++;
    }

    private ExprNode ParseExpr()
    {
        // Recursion guard: hostile deeply-nested input must be a clean validation
        // error, never a stack overflow.
        if (++_depth > MaxDepth)
            throw new ExprError($"expression nesting exceeds {MaxDepth} levels");
        try
        {
            var left = ParseTerm();
            while (AtOp("+") || AtOp("-") || AtOp("||"))
            {
                var op = Current.Text;
                _pos++;
                var right = ParseTerm();
                left = MakeBinary(op, left, right);
            }
            return left;
        }
        finally
        {
            _depth--;
        }
    }

    private ExprNode ParseTerm()
    {
        var left = ParseFactor();
        while (AtOp("*") || AtOp("/"))
        {
            var op = Current.Text;
            _pos++;
            var right = ParseFactor();
            left = MakeBinary(op, left, right);
        }
        return left;
    }

    private ExprNode ParseFactor()
    {
        var tok = Current;

        if (tok.Kind == TokKind.Op && tok.Text == "-")
        {
            _pos++;
            var operand = ParseFactor();
            if (operand.Kind != ColumnKind.Number)
                throw new ExprError("unary '-' requires a number operand");
            return new UnaryMinus(operand);
        }

        if (tok.Kind == TokKind.Op && tok.Text == "(")
        {
            _pos++;
            var inner = ParseExpr();
            Expect(")");
            return inner;
        }

        if (tok.Kind == TokKind.Number)
        {
            _pos++;
            if (!decimal.TryParse(tok.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new ExprError($"'{tok.Text}' is not a valid number");
            return new NumberLit(value);
        }

        if (tok.Kind == TokKind.String)
        {
            _pos++;
            return new StringLit(tok.Text);
        }

        if (tok.Kind == TokKind.Ident)
        {
            // Function when the ident is a known function name followed by '('.
            if (Enum.TryParse<ExprFn>(tok.Text, ignoreCase: true, out var fn)
                && _pos + 1 < _tokens.Count
                && _tokens[_pos + 1] is { Kind: TokKind.Op, Text: "(" })
            {
                _pos += 2;
                var args = new List<ExprNode>();
                if (!AtOp(")"))
                {
                    args.Add(ParseExpr());
                    while (AtOp(","))
                    {
                        _pos++;
                        args.Add(ParseExpr());
                    }
                }
                Expect(")");
                return MakeFunc(fn, args);
            }

            _pos++;
            if (_schema.TryGetValue(tok.Text, out var column))
                return new ColumnRef(column);
            throw new ExprError($"unknown column '{tok.Text}' (computed columns cannot reference other computed columns)");
        }

        throw new ExprError($"unexpected '{tok.Text}' at position {tok.Position + 1}");
    }

    // --- type checking -------------------------------------------------------

    private static ExprNode MakeBinary(string op, ExprNode left, ExprNode right)
    {
        if (op == "||")
        {
            RequireConcatable(left, "left of '||'");
            RequireConcatable(right, "right of '||'");
            return new BinaryOp("||", left, right);
        }

        if (left.Kind != ColumnKind.Number || right.Kind != ColumnKind.Number)
            throw new ExprError($"operator '{op}' requires number operands (got {left.Kind.ToString().ToLowerInvariant()} and {right.Kind.ToString().ToLowerInvariant()})");
        return new BinaryOp(op, left, right);
    }

    private static void RequireConcatable(ExprNode node, string where)
    {
        if (node.Kind is not (ColumnKind.Text or ColumnKind.Number or ColumnKind.Date))
            throw new ExprError($"{where}: cannot concatenate a {node.Kind.ToString().ToLowerInvariant()} value");
    }

    private static FuncCall MakeFunc(ExprFn fn, List<ExprNode> args)
    {
        void Arity(int min, int max)
        {
            if (args.Count < min || args.Count > max)
                throw new ExprError(min == max
                    ? $"{fn.ToString().ToUpperInvariant()} takes {min} argument{(min == 1 ? "" : "s")}, got {args.Count}"
                    : $"{fn.ToString().ToUpperInvariant()} takes {min}–{max} arguments, got {args.Count}");
        }

        void Require(int index, string what, params ColumnKind[] kinds)
        {
            if (!kinds.Contains(args[index].Kind))
                throw new ExprError($"{fn.ToString().ToUpperInvariant()} argument {index + 1} must be {what}");
        }

        switch (fn)
        {
            case ExprFn.Upper or ExprFn.Lower or ExprFn.Trim:
                Arity(1, 1);
                Require(0, "text", ColumnKind.Text);
                return new FuncCall(fn, args, ColumnKind.Text);

            case ExprFn.Length:
                Arity(1, 1);
                Require(0, "text", ColumnKind.Text);
                return new FuncCall(fn, args, ColumnKind.Number);

            case ExprFn.Substr:
                Arity(2, 3);
                Require(0, "text", ColumnKind.Text);
                Require(1, "a number", ColumnKind.Number);
                if (args.Count == 3) Require(2, "a number", ColumnKind.Number);
                return new FuncCall(fn, args, ColumnKind.Text);

            case ExprFn.Concat:
                Arity(2, 8);
                for (var i = 0; i < args.Count; i++)
                    Require(i, "text, number, or date", ColumnKind.Text, ColumnKind.Number, ColumnKind.Date);
                return new FuncCall(fn, args, ColumnKind.Text);

            case ExprFn.Round:
                Arity(1, 2);
                Require(0, "a number", ColumnKind.Number);
                if (args.Count == 2) Require(1, "a number", ColumnKind.Number);
                return new FuncCall(fn, args, ColumnKind.Number);

            case ExprFn.Abs:
                Arity(1, 1);
                Require(0, "a number", ColumnKind.Number);
                return new FuncCall(fn, args, ColumnKind.Number);

            case ExprFn.Coalesce:
            {
                Arity(2, 8);
                var kind = args[0].Kind;
                for (var i = 1; i < args.Count; i++)
                {
                    if (args[i].Kind != kind)
                        throw new ExprError($"COALESCE arguments must all be the same type (argument {i + 1} is {args[i].Kind.ToString().ToLowerInvariant()}, expected {kind.ToString().ToLowerInvariant()})");
                }
                return new FuncCall(fn, args, kind);
            }

            case ExprFn.Year or ExprFn.Month or ExprFn.Day:
                Arity(1, 1);
                // Text allowed: SQLite date columns discover as text (ISO strings).
                Require(0, "a date (or ISO date text)", ColumnKind.Date, ColumnKind.Text);
                return new FuncCall(fn, args, ColumnKind.Number);

            default:
                throw new ExprError($"unknown function '{fn}'");
        }
    }

    private sealed class ExprError(string message) : Exception(message);
}
