using System.Globalization;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Defines stage 1 of the expression pipeline: source text to an untyped syntax tree. There is no schema and no
/// types — just shape, with source positions on every node so the binder can point
/// at exactly what it rejects. Stage 2 (ExprBinder) turns this into the typed AST.
///
/// The language is a documented portable subset (ARCHITECTURE §8), not "whatever
/// the target database accepts": keywords and operators below are the whole story.
/// </summary>
internal abstract record SyntaxNode(int Pos);

/// <summary>Represents a decimal numeric literal.</summary>
internal sealed record NumberSyntax(int Pos, decimal Value) : SyntaxNode(Pos);
/// <summary>Represents a decoded single-quoted string literal.</summary>
internal sealed record StringSyntax(int Pos, string Value) : SyntaxNode(Pos);
/// <summary>Represents the <c>NULL</c> literal.</summary>
internal sealed record NullSyntax(int Pos) : SyntaxNode(Pos);
/// <summary>Represents an unbound quoted or unquoted column/function name.</summary>
internal sealed record NameSyntax(int Pos, string Name) : SyntaxNode(Pos);
/// <summary>Represents a function call before function discovery, arity checks, or type binding.</summary>
internal sealed record CallSyntax(int Pos, string Name, IReadOnlyList<SyntaxNode> Args) : SyntaxNode(Pos);

/// <summary>Represents unary numeric negation or logical <c>NOT</c>.</summary>
internal sealed record UnarySyntax(int Pos, string Op, SyntaxNode Operand) : SyntaxNode(Pos);

/// <summary>Represents arithmetic, concatenation, comparison, or logical binary syntax.</summary>
internal sealed record BinarySyntax(int Pos, string Op, SyntaxNode Left, SyntaxNode Right) : SyntaxNode(Pos);

/// <summary>Represents postfix <c>IS NULL</c> or <c>IS NOT NULL</c>.</summary>
internal sealed record NullTestSyntax(int Pos, SyntaxNode Operand, bool Negated) : SyntaxNode(Pos);

/// <summary>Represents an inclusive <c>BETWEEN</c> predicate.</summary>
internal sealed record BetweenSyntax(int Pos, SyntaxNode Operand, SyntaxNode Lower, SyntaxNode Upper) : SyntaxNode(Pos);

/// <summary>Contains one CASE branch before condition and result types are bound.</summary>
internal sealed record WhenClauseSyntax(SyntaxNode When, SyntaxNode Then);

/// <summary>Represents a searched CASE when operand is null, or a simple CASE when an operand is present.</summary>
internal sealed record CaseSyntax(
    int Pos,
    SyntaxNode? Operand,
    IReadOnlyList<WhenClauseSyntax> Whens,
    SyntaxNode? Else) : SyntaxNode(Pos);

/// <summary>Reports a caller-safe validation failure anywhere in the portable expression pipeline.</summary>
internal sealed class ExprError(string message) : Exception(message);

/// <summary>
/// Implements the lexer and parser. Recursive descent handles primaries and CASE; the binary-operator
/// portion is a Pratt loop over a precedence table (SQL order: OR &lt; AND &lt; NOT &lt;
/// comparison/IS &lt; additive/concat &lt; multiplicative &lt; unary minus).
/// </summary>
internal sealed class ExprSyntaxParser
{
    private const int MaxDepth = 64;

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASE", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "NOT", "IS", "NULL", "BETWEEN",
    };

    // Binary precedence. NOT sits between AND and comparison via ParsePrefix.
    private static readonly Dictionary<string, int> Precedence = new()
    {
        ["OR"] = 1,
        ["AND"] = 2,
        ["="] = 4,
        ["<>"] = 4,
        ["<"] = 4,
        ["<="] = 4,
        [">"] = 4,
        [">="] = 4,
        ["+"] = 5,
        ["-"] = 5,
        ["||"] = 5,
        ["*"] = 6,
        ["/"] = 6,
    };
    private const int ComparisonPrec = 4;

    private readonly string _input;
    private readonly List<Token> _tokens = [];
    private int _pos;
    private int _depth;

    /// <summary>
    /// Initializes a stateful parser over one expression source string.
    /// </summary>
    /// <param name="input">The complete portable expression source.</param>
    private ExprSyntaxParser(string input) => _input = input;

    /// <summary>
    /// Tokenizes and parses portable expression source into an untyped syntax tree.
    /// </summary>
    /// <param name="input">The complete portable expression source.</param>
    /// <returns>The root untyped syntax node with zero-based source positions.</returns>
    /// <exception cref="ExprError">Thrown for invalid characters, literals, grammar, trailing tokens, or excessive nesting.</exception>
    public static SyntaxNode Parse(string input)
    {
        var parser = new ExprSyntaxParser(input);
        parser.Tokenize();
        var node = parser.ParseExpr();
        if (parser._pos < parser._tokens.Count)
            throw new ExprError($"unexpected '{parser.Current.Text}' at position {parser.Current.Position + 1}");
        return node;
    }

    // Lexical representation and scanner.

    private enum TokKind { Number, String, Ident, QuotedIdent, Op }

    private readonly record struct Token(TokKind Kind, string Text, int Position);

    /// <summary>
    /// Scans the complete source into decoded tokens with source positions.
    /// </summary>
    /// <exception cref="ExprError">Thrown for invalid characters or unterminated string/identifier literals.</exception>
    /// <remarks>Appends tokens to this parser's token list; string and quoted-identifier escape sequences are decoded.</remarks>
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

            // Backticks quote a column name in the portable expression language. This is needed
            // for data-derived Pivot columns, whose stable names contain separators and values
            // that are not ordinary identifiers. A doubled backtick represents one literal
            // backtick.
            if (c == '`')
            {
                var start = i;
                i++;
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    if (i >= _input.Length)
                        throw new ExprError($"unterminated quoted identifier starting at position {start + 1}");
                    if (_input[i] == '`')
                    {
                        if (i + 1 < _input.Length && _input[i + 1] == '`') { sb.Append('`'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(_input[i]);
                    i++;
                }
                if (sb.Length == 0)
                    throw new ExprError($"quoted identifier at position {start + 1} cannot be empty");
                _tokens.Add(new Token(TokKind.QuotedIdent, sb.ToString(), start));
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

    // Stateful token reader and recursive-descent parser.

    /// <summary>Gets the current token or throws a caller-safe end-of-expression error.</summary>
    private Token Current => _pos < _tokens.Count
        ? _tokens[_pos]
        : throw new ExprError("unexpected end of expression");

    /// <summary>Gets whether every scanned token has been consumed.</summary>
    private bool AtEnd => _pos >= _tokens.Count;

    /// <summary>
    /// Determines whether the current token is the requested operator without consuming it.
    /// </summary>
    /// <param name="op">The exact punctuation/operator token to test.</param>
    /// <returns><see langword="true"/> when the current token matches <paramref name="op"/>; otherwise, <see langword="false"/>.</returns>
    private bool AtOp(string op) => !AtEnd
        && _tokens[_pos].Kind == TokKind.Op && _tokens[_pos].Text == op;

    /// <summary>
    /// Determines whether the current token is the requested keyword without consuming it.
    /// </summary>
    /// <param name="keyword">The keyword spelling to recognize in a case-insensitive expression parse.</param>
    /// <returns><see langword="true"/> when the current token matches <paramref name="keyword"/>; otherwise, <see langword="false"/>.</returns>
    private bool AtKeyword(string keyword) => !AtEnd
        && _tokens[_pos].Kind == TokKind.Ident
        && string.Equals(_tokens[_pos].Text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether the token is a reserved keyword for the portable expression language.
    /// </summary>
    /// <param name="token">The identifier token to classify without consuming it.</param>
    /// <returns><see langword="true"/> when the token is a reserved keyword; otherwise, <see langword="false"/>.</returns>
    private bool IsKeyword(in Token token) => token.Kind == TokKind.Ident && Keywords.Contains(token.Text);

    /// <summary>
    /// Consumes the required operator or raises a positioned syntax error.
    /// </summary>
    /// <param name="op">The exact punctuation/operator token required at the current position.</param>
    /// <exception cref="ExprError">Thrown when another token or end of expression appears.</exception>
    /// <remarks>Advances the token position on success.</remarks>
    private void ExpectOp(string op)
    {
        if (!AtOp(op))
            throw new ExprError(!AtEnd
                ? $"expected '{op}' but found '{Current.Text}' at position {Current.Position + 1}"
                : $"expected '{op}' but the expression ended");
        _pos++;
    }

    /// <summary>
    /// Consumes the required keyword or raises a positioned syntax error.
    /// </summary>
    /// <param name="keyword">The keyword spelling to recognize in a case-insensitive expression parse.</param>
    /// <exception cref="ExprError">Thrown when another token or end of expression appears.</exception>
    /// <remarks>Advances the token position on success.</remarks>
    private void ExpectKeyword(string keyword)
    {
        if (!AtKeyword(keyword))
            throw new ExprError(!AtEnd
                ? $"expected {keyword} but found '{Current.Text}' at position {Current.Position + 1}"
                : $"expected {keyword} but the expression ended");
        _pos++;
    }

    /// <summary>
    /// Parses a complete expression from the current token position.
    /// </summary>
    /// <returns>The root node parsed with the lowest binary precedence.</returns>
    private SyntaxNode ParseExpr() => ParseBinary(1);

    /// <summary>
    /// Parses binary operators with precedence climbing.
    /// </summary>
    /// <param name="minPrec">The minimum operator precedence accepted by the recursive expression parse.</param>
    /// <returns>The expression rooted at the current token, stopping before an operator below <paramref name="minPrec"/>.</returns>
    /// <exception cref="ExprError">Thrown for malformed syntax or more than <see cref="MaxDepth"/> recursive levels.</exception>
    private SyntaxNode ParseBinary(int minPrec)
    {
        // Hostile deeply nested input must be a clean validation
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

                // BETWEEN binds at comparison precedence; its bounds parse one level above AND
                // so the connecting AND stays BETWEEN's own, and a trailing AND falls out of
                // the loop as a logical operator (SQL's grammar).
                if (AtKeyword("BETWEEN"))
                {
                    if (ComparisonPrec < minPrec) break;
                    var pos = Current.Position;
                    _pos++;
                    var lower = ParseBinary(ComparisonPrec + 1);
                    ExpectKeyword("AND");
                    var upper = ParseBinary(ComparisonPrec + 1);
                    left = new BetweenSyntax(pos, left, lower, upper);
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

    /// <summary>
    /// Views the current token as a binary operator: symbol operators verbatim and AND/OR keywords
    /// uppercased.
    /// </summary>
    /// <returns>The normalized operator, or <see langword="null"/> when the current token is not binary.</returns>
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

    /// <summary>
    /// Parses prefix operators and their operand.
    /// </summary>
    /// <returns>The unary node or next primary expression.</returns>
    /// <exception cref="ExprError">Thrown for malformed syntax or excessive unary nesting.</exception>
    private SyntaxNode ParsePrefix()
    {
        if (AtOp("-"))
        {
            // ParsePrefix recurses on itself here, bypassing ParseBinary — count the depth or a
            // hostile '-----…-1' walks straight past the nesting guard.
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

    /// <summary>
    /// Parses a literal, identifier, call, parenthesized expression, or CASE expression.
    /// </summary>
    /// <returns>The parsed primary node.</returns>
    /// <remarks>Consumes the primary and all nested tokens from this parser.</remarks>
    /// <exception cref="ExprError">Thrown for an unexpected token, invalid decimal, unknown reserved-keyword position, or malformed call.</exception>
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
            return new StringSyntax(tok.Position, tok.Text); // Text holds the decoded value.
        }

        if (tok.Kind is TokKind.Ident or TokKind.QuotedIdent)
        {
            if (tok.Kind == TokKind.Ident && AtKeyword("CASE")) return ParseCase();
            if (tok.Kind == TokKind.Ident && AtKeyword("NULL")) { _pos++; return new NullSyntax(tok.Position); }
            if (tok.Kind == TokKind.Ident && IsKeyword(tok))
                throw new ExprError($"unexpected {tok.Text.ToUpperInvariant()} at position {tok.Position + 1}");

            // An identifier followed by '(' is a call; the binder decides whether the
            // name is a known function. Otherwise it names a column.
            if (tok.Kind == TokKind.Ident
                && _pos + 1 < _tokens.Count && _tokens[_pos + 1] is { Kind: TokKind.Op, Text: "(" })
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

    /// <summary>
    /// Parses a CASE expression and its WHEN, ELSE, and END clauses.
    /// </summary>
    /// <returns>A simple or searched CASE syntax node.</returns>
    /// <exception cref="ExprError">Thrown when CASE has no WHEN branch or any clause is malformed.</exception>
    /// <remarks>Consumes through the matching <c>END</c> token.</remarks>
    private SyntaxNode ParseCase()
    {
        var pos = Current.Position;
        _pos++; // CASE.

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
