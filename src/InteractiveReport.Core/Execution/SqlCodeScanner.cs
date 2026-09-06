namespace InteractiveReport.Core.Execution;

/// <summary>Locates executable SQL text while preserving literals, comments, and quoted identifiers.</summary>
internal static class SqlCodeScanner
{
    /// <summary>Yields executable text, excluding SQL comments, strings, and quoted identifiers.</summary>
    internal static IEnumerable<(int Start, int Length)> CodeSpans(string sql)
    {
        var start = 0;
        for (var index = 0; index < sql.Length;)
        {
            var next = SkipLiteral(sql, index);
            if (next == index)
            {
                index++;
                continue;
            }
            if (index > start) yield return (start, index - start);
            index = next;
            start = index;
        }
        if (start < sql.Length) yield return (start, sql.Length - start);
    }

    private static int SkipLiteral(string sql, int index)
    {
        var c = sql[index];
        var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
        if (c == '-' && next == '-')
        {
            var end = sql.IndexOfAny(['\r', '\n'], index + 2);
            return end < 0 ? sql.Length : end;
        }
        if (c == '/' && next == '*')
        {
            var depth = 1;
            var end = index + 2;
            while (end + 1 < sql.Length)
            {
                if (sql[end] == '/' && sql[end + 1] == '*') { depth++; end += 2; }
                else if (sql[end] == '*' && sql[end + 1] == '/')
                {
                    end += 2;
                    if (--depth == 0) return end;
                }
                else end++;
            }
            throw new InvalidOperationException("Unterminated block comment in report SQL or row restriction.");
        }
        // Oracle alternative quoting, including the national-character NQ prefix.
        var quoteIndex = (c is 'n' or 'N') && (next is 'q' or 'Q') ? index + 1 : index;
        if ((sql[quoteIndex] is 'q' or 'Q') && quoteIndex + 2 < sql.Length && sql[quoteIndex + 1] == '\''
            && (index == 0 || !IsIdentifier(sql[index - 1])))
        {
            var close = sql[quoteIndex + 2] switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', var value => value };
            var end = sql.IndexOf($"{close}'", quoteIndex + 3, StringComparison.Ordinal);
            return end < 0 ? throw new InvalidOperationException("Unterminated Oracle quoted string.") : end + 2;
        }
        // PostgreSQL dollar-quoted strings, including $$...$$.
        if (c == '$' && (index == 0 || !IsIdentifier(sql[index - 1])))
        {
            var tagEnd = index + 1;
            while (tagEnd < sql.Length && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_')) tagEnd++;
            if (tagEnd < sql.Length && sql[tagEnd] == '$'
                && (tagEnd == index + 1 || !char.IsDigit(sql[index + 1])))
            {
                var tag = sql[index..(tagEnd + 1)];
                var end = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                return end < 0 ? throw new InvalidOperationException("Unterminated PostgreSQL dollar-quoted string.") : end + tag.Length;
            }
        }
        if (c is not ('\'' or '"' or '[' or '`')) return index;
        var closer = c == '[' ? ']' : c;
        var escapes = c == '\'' && index > 0 && (sql[index - 1] is 'e' or 'E')
            && (index < 2 || !IsIdentifier(sql[index - 2]));
        for (var end = index + 1; end < sql.Length; end++)
        {
            if (escapes && sql[end] == '\\') { end++; continue; }
            if (sql[end] != closer) continue;
            if (end + 1 < sql.Length && sql[end + 1] == closer) { end++; continue; }
            return end + 1;
        }
        throw new InvalidOperationException("Unterminated quoted text in report SQL or row restriction.");
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
