using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Builds LIKE patterns whose user text is literal. Every emitted LIKE declares
/// <c>ESCAPE '\'</c>, so <c>%</c>, <c>_</c>, and <c>\</c> are escaped on every dialect; SQL Server
/// additionally treats <c>[</c> as a character-class opener even under an ESCAPE clause, so it is
/// escaped there too. Text predicates (CONTAINS, STARTS_WITH, ENDS_WITH, WILDCARD_MATCH), toolbar
/// search, and list-of-values search all go through here so one rule governs "the user typed a
/// metacharacter".
/// </summary>
internal static class SqlLikePattern
{
    /// <summary>The LIKE escape character.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>The escape clause every LIKE built from user text carries, with its leading space.</summary>
    public const string EscapeClause = " ESCAPE '\\'";

    /// <summary>Escapes one user string so LIKE matches it literally.</summary>
    /// <param name="value">The user text.</param>
    /// <param name="dialect">The dialect whose metacharacter set applies.</param>
    /// <returns>The escaped text without wildcards.</returns>
    public static string Escape(string value, ReportDialect dialect)
    {
        var pattern = new StringBuilder(value.Length + 8);
        foreach (var c in value) AppendLiteral(pattern, c, dialect);
        return pattern.ToString();
    }

    /// <summary>Builds a substring pattern: <c>%</c>, the escaped text, <c>%</c>.</summary>
    public static string Contains(string value, ReportDialect dialect)
        => "%" + Escape(value, dialect) + "%";

    /// <summary>Appends one character, escaping it when the dialect's LIKE would interpret it.</summary>
    public static void AppendLiteral(StringBuilder pattern, char value, ReportDialect dialect)
    {
        if (IsMetacharacter(value, dialect)) pattern.Append(EscapeCharacter);
        pattern.Append(value);
    }

    /// <summary>Whether LIKE on the dialect interprets the character under <see cref="EscapeClause"/>.</summary>
    public static bool IsMetacharacter(char value, ReportDialect dialect)
        => value is '%' or '_' or EscapeCharacter
           || (value == '[' && dialect == ReportDialect.SqlServer);

    /// <summary>
    /// The metacharacters to neutralize when the pattern is a SQL expression rather than a literal:
    /// each pair is applied as <c>REPLACE(expr, from, to)</c>, backslash first so later escapes are
    /// not doubled.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> Replacements(ReportDialect dialect)
        => dialect == ReportDialect.SqlServer ? SqlServerReplacements : PortableReplacements;

    private static readonly (string, string)[] PortableReplacements =
    [
        ("\\", "\\\\"),
        ("%", "\\%"),
        ("_", "\\_"),
    ];

    private static readonly (string, string)[] SqlServerReplacements =
    [
        ("\\", "\\\\"),
        ("%", "\\%"),
        ("_", "\\_"),
        ("[", "\\["),
    ];
}
