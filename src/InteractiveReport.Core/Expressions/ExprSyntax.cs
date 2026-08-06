using System.Globalization;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 1 of the expression pipeline: text → untyped syntax tree. No schema, no
/// types — just shape, with source positions on every node so the binder can point
/// at exactly what it rejects. Stage 2 (ExprBinder) turns this into the typed AST.
///
/// The language is a documented portable subset (ARCHITECTURE §8), not "whatever
/// the target database accepts": keywords and operators below are the whole story.
/// </summary>
internal abstract record SyntaxNode(int Pos);

internal sealed record NumberSyntax(int Pos, decimal Value) : SyntaxNode(Pos);
internal sealed record StringSyntax(int Pos, string Value) : SyntaxNode(Pos);
internal sealed record NullSyntax(int Pos) : SyntaxNode(Pos);
internal sealed record NameSyntax(int Pos, string Name) : SyntaxNode(Pos);
internal sealed record CallSyntax(int Pos, string Name, IReadOnlyList<SyntaxNode> Args) : SyntaxNode(Pos);

/// <summary>Op is "-" or "NOT".</summary>
internal sealed record UnarySyntax(int Pos, string Op, SyntaxNode Operand) : SyntaxNode(Pos);

/// <summary>Op: arithmetic + - * /, concat ||, comparison = <> < <= > >=, logical AND OR.</summary>
internal sealed record BinarySyntax(int Pos, string Op, SyntaxNode Left, SyntaxNode Right) : SyntaxNode(Pos);

internal sealed record NullTestSyntax(int Pos, SyntaxNode Operand, bool Negated) : SyntaxNode(Pos);

internal sealed record WhenClauseSyntax(SyntaxNode When, SyntaxNode Then);

/// <summary>Operand null = searched CASE (WHEN are conditions); non-null = simple CASE (WHEN are values).</summary>
internal sealed record CaseSyntax(
    int Pos,
    SyntaxNode? Operand,
    IReadOnlyList<WhenClauseSyntax> Whens,
    SyntaxNode? Else) : SyntaxNode(Pos);

/// <summary>Validation failure anywhere in the pipeline — always a message about the client's own input.</summary>
internal sealed class ExprError(string message) : Exception(message);

/// <summary>
/// Lexer + parser. Recursive descent for primaries and CASE; the binary-operator
/// portion is a Pratt loop over a precedence table (SQL order: OR &lt; AND &lt; NOT &lt;
/// comparison/IS &lt; additive/concat &lt; multiplicative &lt; unary minus).
/// </summary>
internal sealed class ExprSyntaxParser
{
    private const int MaxDepth = 64;

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASE", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "NOT", "IS", "NULL",
    };

    // Binary precedence. NOT sits between AND and comparison via ParsePrefix.
    private static readonly Dictionary<string, int> Precedence = new()
    {
        ["OR"] = 1,
        ["AND"] = 2,
        ["="] = 4, ["<>"] = 4, ["<"] = 4, ["<="] = 4, [">"] = 4, [">="] = 4,
        ["+"] = 5, ["-"] = 5, ["||"] = 5,
        ["*"] = 6, ["/"] = 6,
    };
    private const int ComparisonPrec = 4;

    private readonly string _input;
    private readonly List<Token> _tokens = [];
    private int _pos;
    private int _depth;

    private ExprSyntaxParser(string input) => _input = input;

    public static SyntaxNode Parse(string input)
    {
        var parser = new ExprSyntaxParser(input);
        parser.Tokenize();
        var node = parser.ParseExpr();
        if (parser._pos < parser._tokens.Count)
            throw new ExprError($"unexpected '{parser.Current.Text}' at position {parser.Current.Position + 1}");
        return node;
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

            if (c is '<' or '>' or '!')
            {
                if (i + 1 < _input.Length && (_input[i + 1] == '=' || (c == '<' && _input[i + 1] == '>')))
                {
                    // != is accepted and normalized to <> at this stage.
                    var text = _input.Substring(i, 2) switch { "!=" => "<>", var t => t };
                    _tokens.Add(new Token(TokKind.Op, text, i));
                    i += 2;
                    continue;
                }
                if (c == '!')
                    throw new ExprError($"unexpected character '!' at position {i + 1} (use '<>' or '!=' for not-equal)");
                _tokens.Add(new Token(TokKind.Op, c.ToString(), i));
                i++;
                continue;
            }

            if (c is '+' or '-' or '*' or '/' or '(' or ')' or ',' or '=')
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

    private bool AtEnd => _pos >= _tokens.Count;

    private bool AtOp(string op) => !AtEnd
        && _tokens[_pos].Kind == TokKind.Op && _tokens[_pos].Text == op;

    private bool AtKeyword(string keyword) => !AtEnd
        && _tokens[_pos].Kind == TokKind.Ident
        && string.Equals(_tokens[_pos].Text, keyword, StringComparison.OrdinalIgnoreCase);

    private bool IsKeyword(in Token token) => token.Kind == TokKind.Ident && Keywords.Contains(token.Text);

    private void ExpectOp(string op)
    {
        if (!AtOp(op))
            throw new ExprError(!AtEnd
                ? $"expected '{op}' but found '{Current.Text}' at position {Current.Position + 1}"
                : $"expected '{op}' but the expression ended");
        _pos++;
    }

    private void ExpectKeyword(string keyword)
    {
        if (!AtKeyword(keyword))
            throw new ExprError(!AtEnd
                ? $"expected {keyword} but found '{Current.Text}' at position {Current.Position + 1}"
                : $"expected {keyword} but the expression ended");
        _pos++;
    }

    private SyntaxNode ParseExpr() => ParseBinary(1);

    private SyntaxNode ParseBinary(int minPrec)
    {
        // Recursion guard: hostile deeply-nested input must be a clean validation
        // error, never a stack overflow. All recursion funnels through here.
        if (++_depth > MaxDepth)
            throw new ExprError($"expression nesting exceeds {MaxDepth} levels");
        try
        {
            var left = ParsePrefix();
            while (!AtEnd)
            {
                // Postfix IS [NOT] NULL binds at comparison precedence.
                if (AtKeyword("IS"))
                {
                    if (ComparisonPrec < minPrec) break;
                    var pos = Current.Position;
                    _pos++;
                    var negated = false;
                    if (AtKeyword("NOT")) { negated = true; _pos++; }
                    ExpectKeyword("NULL");
                    left = new NullTestSyntax(pos, left, negated);
                    continue;
                }

                var opText = OperatorText();
                if (opText is null || !Precedence.TryGetValue(opText, out var prec) || prec < minPrec)
                    break;

                var opPos = Current.Position;
                _pos++;
                var right = ParseBinary(prec + 1);
                left = new BinarySyntax(opPos, opText, left, right);
            }
            return left;
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>The current token viewed as a binary operator: symbol ops verbatim, AND/OR keywords uppercased.</summary>
    private string? OperatorText()
    {
        if (AtEnd) return null;
        var tok = _tokens[_pos];
        if (tok.Kind == TokKind.Op) return tok.Text;
        if (tok.Kind == TokKind.Ident
            && (string.Equals(tok.Text, "AND", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tok.Text, "OR", StringComparison.OrdinalIgnoreCase)))
            return tok.Text.ToUpperInvariant();
        return null;
    }

    private SyntaxNode ParsePrefix()
    {
        if (AtOp("-"))
        {
            // ParsePrefix recurses on itself here, bypassing ParseBinary — count the
            // depth or a hostile '-----…-1' walks straight past the nesting guard.
            if (++_depth > MaxDepth)
                throw new ExprError($"expression nesting exceeds {MaxDepth} levels");
            try
            {
                var pos = Current.Position;
                _pos++;
                return new UnarySyntax(pos, "-", ParsePrefix());
            }
            finally
            {
                _depth--;
            }
        }

        if (AtKeyword("NOT"))
        {
            // NOT binds looser than comparisons, tighter than AND: NOT a = b ≡ NOT (a = b).
            var pos = Current.Position;
            _pos++;
            return new UnarySyntax(pos, "NOT", ParseBinary(3));
        }

        return ParsePrimary();
    }

    private SyntaxNode ParsePrimary()
    {
        var tok = Current;

        if (tok.Kind == TokKind.Op && tok.Text == "(")
        {
            _pos++;
            var inner = ParseExpr();
            ExpectOp(")");
            return inner;
        }

        if (tok.Kind == TokKind.Number)
        {
            _pos++;
            if (!decimal.TryParse(tok.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new ExprError($"'{tok.Text}' is not a valid number");
            return new NumberSyntax(tok.Position, value);
        }

        if (tok.Kind == TokKind.String)
        {
            _pos++;
            return new StringSyntax(tok.Position, tok.Text); // Text holds the decoded value
        }

        if (tok.Kind == TokKind.Ident)
        {
            if (AtKeyword("CASE")) return ParseCase();
            if (AtKeyword("NULL")) { _pos++; return new NullSyntax(tok.Position); }
            if (IsKeyword(tok))
                throw new ExprError($"unexpected {tok.Text.ToUpperInvariant()} at position {tok.Position + 1}");

            // Identifier followed by '(' is a call; the binder decides whether the
            // name is a known function. Otherwise it names a column.
            if (_pos + 1 < _tokens.Count && _tokens[_pos + 1] is { Kind: TokKind.Op, Text: "(" })
            {
                var name = tok.Text;
                _pos += 2;
                var args = new List<SyntaxNode>();
                if (!AtOp(")"))
                {
                    args.Add(ParseExpr());
                    while (AtOp(","))
                    {
                        _pos++;
                        args.Add(ParseExpr());
                    }
                }
                ExpectOp(")");
                return new CallSyntax(tok.Position, name, args);
            }

            _pos++;
            return new NameSyntax(tok.Position, tok.Text);
        }

        throw new ExprError($"unexpected '{tok.Text}' at position {tok.Position + 1}");
    }

    private SyntaxNode ParseCase()
    {
        var pos = Current.Position;
        _pos++; // CASE

        // Simple CASE has an operand before the first WHEN; searched CASE does not.
        SyntaxNode? operand = null;
        if (!AtKeyword("WHEN"))
            operand = ParseExpr();

        var whens = new List<WhenClauseSyntax>();
        while (AtKeyword("WHEN"))
        {
            _pos++;
            var when = ParseExpr();
            ExpectKeyword("THEN");
            var then = ParseExpr();
            whens.Add(new WhenClauseSyntax(when, then));
        }
        if (whens.Count == 0)
            throw new ExprError($"CASE at position {pos + 1} needs at least one WHEN … THEN branch");

        SyntaxNode? elseNode = null;
        if (AtKeyword("ELSE"))
        {
            _pos++;
            elseNode = ParseExpr();
        }
        ExpectKeyword("END");

        return new CaseSyntax(pos, operand, whens, elseNode);
    }
}
