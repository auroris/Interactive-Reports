using InteractiveReport.Core.Execution;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// A minimal SQL text scanner for configuration lint. It walks only the executable spans that
/// <see cref="SqlCodeScanner"/> yields, so comments, string literals of every supported
/// dialect, and quoted identifiers never count, and tracks parenthesis depth without parsing
/// SQL. A genuinely malformed query still fails loudly at schema discovery.
/// </summary>
internal static class SqlTopLevelScanner
{
    /// <summary>
    /// Determines whether an ORDER BY clause exists at parenthesis depth zero, the position
    /// that breaks the derived-table wrap. ORDER BY inside strings, comments, quoted identifiers, or
    /// subqueries never matches, and a comment between ORDER and BY does not split the clause.
    /// </summary>
    /// <param name="sql">The configured SQL statement to inspect.</param>
    /// <returns><see langword="true"/> when the SQL contains a top-level ORDER BY clause; otherwise, <see langword="false"/>.</returns>
    public static bool HasTopLevelOrderBy(string sql)
    {
        var depth = 0;
        var pendingOrder = false;
        var previousEnd = 0;
        foreach (var (start, length) in SqlCodeScanner.CodeSpans(sql))
        {
            // The text skipped between two code spans is a comment, a string, or a quoted
            // identifier. A comment between ORDER and BY does not split the clause; anything
            // quoted does.
            if (start > previousEnd && !IsComment(sql, previousEnd)) pendingOrder = false;
            var end = start + length;
            var i = start;
            while (i < end)
            {
                var c = sql[i];
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
                    var wordStart = i;
                    while (i < end && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#')) i++;
                    var word = sql.AsSpan(wordStart, i - wordStart);
                    if (depth == 0 && pendingOrder && word.Equals("BY", StringComparison.OrdinalIgnoreCase))
                        return true;
                    pendingOrder = depth == 0 && word.Equals("ORDER", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                // Any other punctuation (commas, operators, semicolons) breaks ORDER/BY adjacency.
                pendingOrder = false;
                i++;
            }
            previousEnd = end;
        }
        return false;
    }

    /// <summary>Whether the skipped text starting at <paramref name="index"/> is a line or block comment.</summary>
    private static bool IsComment(string sql, int index)
        => index + 1 < sql.Length
            && ((sql[index] == '-' && sql[index + 1] == '-') || (sql[index] == '/' && sql[index + 1] == '*'));
}
