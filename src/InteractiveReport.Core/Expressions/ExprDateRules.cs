using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Owns the portable DATE_TRUNC units and TO_STRING format vocabulary. Binding validates
/// this vocabulary; emission translates the same parsed representation per dialect.
/// </summary>
internal static class ExprDateRules
{
    private static readonly string[] FormatTokens = ["HH24", "YYYY", "MM", "DD", "MI", "SS"];

    /// <summary>Gets the default portable date-only format tokens.</summary>
    public static IReadOnlyList<string> DefaultFormat { get; } = ["YYYY", "-", "MM", "-", "DD"];

    /// <summary>
    /// Validates and returns a DATE_TRUNC unit literal.
    /// </summary>
    /// <param name="argument">The bound argument value supplied to the expression function.</param>
    /// <returns>The validated uppercase DATE_TRUNC unit.</returns>
    /// <exception cref="ExprError">Thrown when <paramref name="argument"/> is not a DAY, MONTH, or YEAR string literal.</exception>
    public static string TruncUnit(ExprNode argument)
    {
        if (argument is StringLit literal)
        {
            var unit = literal.Value.ToUpperInvariant();
            if (unit is "DAY" or "MONTH" or "YEAR") return unit;
        }
        throw new ExprError("DATE_TRUNC unit must be the literal 'DAY', 'MONTH', or 'YEAR'");
    }

    /// <summary>
    /// Reads the required string literal from a TO_STRING format argument.
    /// </summary>
    /// <param name="argument">The bound argument value supplied to the expression function.</param>
    /// <returns>The string literal carried by the expression argument.</returns>
    /// <exception cref="ExprError">Thrown when <paramref name="argument"/> is not a string literal.</exception>
    public static string FormatLiteral(ExprNode argument)
        => argument is StringLit literal
            ? literal.Value
            : throw new ExprError("TO_STRING format must be a string literal like 'YYYY-MM-DD'");

    /// <summary>
    /// Validates a TO_STRING format into tokens and single-character separators. Masks
    /// use the engine's portable vocabulary and never pass through as native SQL syntax.
    /// </summary>
    /// <param name="format">The requested output format.</param>
    /// <returns>The validated uppercase tokens and literal separators in source order.</returns>
    /// <exception cref="ExprError">Thrown when the format is empty or contains an unsupported token or separator.</exception>
    public static IReadOnlyList<string> ParseDateFormat(string format)
    {
        if (format.Length == 0)
            throw new ExprError("TO_STRING format cannot be empty");

        var upper = format.ToUpperInvariant();
        var parts = new List<string>();
        var index = 0;
        while (index < upper.Length)
        {
            var token = FormatTokens.FirstOrDefault(candidate =>
                index + candidate.Length <= upper.Length
                && string.CompareOrdinal(upper, index, candidate, 0, candidate.Length) == 0);
            if (token is not null)
            {
                parts.Add(token);
                index += token.Length;
                continue;
            }
            if (upper[index] is ' ' or '-' or '/' or ':' or 'T')
            {
                parts.Add(upper[index].ToString());
                index++;
                continue;
            }
            throw new ExprError(
                $"TO_STRING format is invalid at character {index + 1} — tokens are YYYY, MM, DD, HH24, MI, SS, separated by space, '-', '/', ':', or 'T'");
        }
        return parts;
    }

    /// <summary>
    /// Translates validated portable format parts into the target dialect's formatting vocabulary.
    /// </summary>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="parts">The tokens returned by <see cref="ParseDateFormat"/>.</param>
    /// <returns>The equivalent .NET, Oracle/PostgreSQL, or SQLite date-format string.</returns>
    public static string TranslateFormat(ReportDialect dialect, IReadOnlyList<string> parts)
    {
        var result = new StringBuilder();
        foreach (var part in parts)
        {
            switch (dialect)
            {
                case ReportDialect.SqlServer:
                    // Rationale: .NET custom format. Quoting separators prevents
                    // culture-specific meanings for '/' and ':'.
                    result.Append(part switch
                    {
                        "YYYY" => "yyyy",
                        "MM" => "MM",
                        "DD" => "dd",
                        "HH24" => "HH",
                        "MI" => "mm",
                        "SS" => "ss",
                        _ => $"'{part}'",
                    });
                    break;

                case ReportDialect.Oracle or ReportDialect.Postgres:
                    result.Append(part == "T" ? "\"T\"" : part);
                    break;

                default:
                    result.Append(part switch
                    {
                        "YYYY" => "%Y",
                        "MM" => "%m",
                        "DD" => "%d",
                        "HH24" => "%H",
                        "MI" => "%M",
                        "SS" => "%S",
                        _ => part,
                    });
                    break;
            }
        }
        return result.ToString();
    }
}
