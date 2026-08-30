using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Encodes literal SQL and identifiers passed through SqlKata raw clauses. SqlKata
/// treats square and curly brackets as portable identifier markers and every question
/// mark as a positional binding slot, including when those characters occur inside an
/// otherwise opaque raw fragment. Literal identifier markers use SqlKata's escape
/// character. Literal question marks use a reversible sentinel which the compilers
/// returned by <see cref="DialectSupport.GetCompiler"/> remove after binding expansion.
/// </summary>
internal static class SqlKataSyntax
{
    /// <summary>Portable alias for the opaque configured SQL derived table.</summary>
    public const string BaseRelationAlias = "ir_base";

    // Private-use characters keep literal question marks out of SqlKata's binding
    // scanner. Doubling Sentinel escapes an actual occurrence from configured SQL,
    // so the encoding remains reversible for every input string.
    private const char Sentinel = '\uE000';
    private const char QuestionMarkTag = '\uE001';

    public static string PreserveRaw(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(RequiresRawEncoding)) return text;

        var escaped = new StringBuilder(text.Length + 8);
        foreach (var character in text)
        {
            if (character == Sentinel)
            {
                escaped.Append(Sentinel).Append(Sentinel);
                continue;
            }
            if (character == '?')
            {
                escaped.Append(Sentinel).Append(QuestionMarkTag);
                continue;
            }
            if (IsIdentifierMarker(character)) escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    public static string Identifier(ReportDialect dialect, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var requiresDirectQuote = name.Any(character => IsIdentifierMarker(character) || character == '?')
            || (dialect != ReportDialect.SqlServer && name.Contains('"'));
        if (!requiresDirectQuote) return ProtectQuestionMarks($"[{name}]");

        var quoted = dialect == ReportDialect.SqlServer
            ? $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]"
            : $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        // This is already dialect-quoted. Keep SqlKata's raw-fragment marker pass
        // from interpreting any bracket or brace in the quoted identifier.
        return PreserveRaw(quoted);
    }

    internal static string ProtectQuestionMarks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(character => character is Sentinel or '?')) return text;

        var protectedText = new StringBuilder(text.Length + 4);
        foreach (var character in text)
        {
            if (character == Sentinel)
                protectedText.Append(Sentinel).Append(Sentinel);
            else if (character == '?')
                protectedText.Append(Sentinel).Append(QuestionMarkTag);
            else
                protectedText.Append(character);
        }
        return protectedText.ToString();
    }

    internal static string RestoreCompiled(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains(Sentinel, StringComparison.Ordinal)) return text;

        var restored = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character != Sentinel || index + 1 >= text.Length)
            {
                restored.Append(character);
                continue;
            }

            var tag = text[++index];
            if (tag == Sentinel)
                restored.Append(Sentinel);
            else if (tag == QuestionMarkTag)
                restored.Append('?');
            else
                restored.Append(Sentinel).Append(tag);
        }
        return restored.ToString();
    }

    private static bool RequiresRawEncoding(char character)
        => character == Sentinel || character == '?' || IsIdentifierMarker(character);

    private static bool IsIdentifierMarker(char character)
        => character is '[' or ']' or '{' or '}';
}
