namespace InteractiveReport.AspNetCore;

/// <summary>
/// A minimal SQL text scanner for configuration lint: it tracks comments (line and
/// nested block), string literals, quoted identifiers ("..." and [...]), and
/// parenthesis depth without parsing SQL. Block comments nest per the most permissive
/// dialects (Postgres and T-SQL nest; Oracle and SQLite do not) — the permissive
/// reading only ever accepts more, and a genuinely malformed query still fails
/// loudly at schema discovery.
/// </summary>
internal static class SqlTopLevelScanner
{
    /// <summary>
    /// True when an ORDER BY clause exists at parenthesis depth 0 — the position that
    /// breaks the derived-table wrap. ORDER BY inside strings, comments, quoted
    /// identifiers, or subqueries never matches, and a comment between ORDER and BY
    /// does not split the clause.
    /// </summary>
    public static bool HasTopLevelOrderBy(string sql)
    {
        var depth = 0;
        var pendingOrder = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                i = SkipLineComment(sql, i + 2);
                continue;
            }
            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                i = SkipBlockComment(sql, i + 2);
                continue;
            }
            if (c is '\'' or '"')
            {
                i = SkipQuoted(sql, i + 1, c);
                pendingOrder = false;
                continue;
            }
            if (c == '[')
            {
                i = SkipQuoted(sql, i + 1, ']');
                pendingOrder = false;
                continue;
            }
            if (c == '(')
            {
                depth++;
                pendingOrder = false;
                i++;
                continue;
            }
            if (c == ')')
            {
                if (depth > 0) depth--;
                pendingOrder = false;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#')) i++;
                var word = sql.AsSpan(start, i - start);
                if (depth == 0 && pendingOrder && word.Equals("BY", StringComparison.OrdinalIgnoreCase))
                    return true;
                pendingOrder = depth == 0 && word.Equals("ORDER", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            // Any other punctuation (commas, operators, semicolons) breaks ORDER/BY adjacency.
            pendingOrder = false;
            i++;
        }
        return false;
    }

    private static char Peek(string sql, int index) => index < sql.Length ? sql[index] : '\0';

    private static int SkipLineComment(string sql, int i)
    {
        while (i < sql.Length && sql[i] != '\n') i++;
        return i;
    }

    private static int SkipBlockComment(string sql, int i)
    {
        var nesting = 1;
        while (i < sql.Length && nesting > 0)
        {
            if (sql[i] == '*' && Peek(sql, i + 1) == '/')
            {
                nesting--;
                i += 2;
                continue;
            }
            if (sql[i] == '/' && Peek(sql, i + 1) == '*')
            {
                nesting++;
                i += 2;
                continue;
            }
            i++;
        }
        return i;
    }

    /// <summary>Doubled closers ('' "" ]]) are escapes on every supported dialect.</summary>
    private static int SkipQuoted(string sql, int i, char closer)
    {
        while (i < sql.Length)
        {
            if (sql[i] == closer)
            {
                if (Peek(sql, i + 1) == closer)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return i;
    }
}
