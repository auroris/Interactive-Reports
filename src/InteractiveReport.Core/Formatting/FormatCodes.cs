using System.Globalization;
using System.Text;

namespace InteractiveReport.Core.Formatting;

/// <summary>
/// Formats report scalars through Excel-style format codes for file outputs. The grammar is the
/// subset of Excel custom number and date formats that survives a round trip into a workbook
/// cell: digit placeholders, thousands grouping, scaling commas, percent, quoted or escaped
/// literals, positive;negative;zero sections, and the y/m/d/h/s date tokens. The browser client
/// applies the same grammar with locale-specific separators; this implementation is invariant
/// (period decimal, comma grouping, English month names) so a file reads the same everywhere,
/// and a future workbook export can write the stored code straight into a cell style.
/// </summary>
public static class FormatCodes
{
    /// <summary>The longest mask accepted from report state; longer masks fall through to default rendering.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Accepts a stored mask as a format code.
    /// </summary>
    /// <param name="mask">The mask text from report state.</param>
    /// <returns>The code, or <see langword="null"/> for a blank or over-long mask.</returns>
    private static string? Code(string? mask)
        => string.IsNullOrWhiteSpace(mask) || mask.Length > MaxLength ? null : mask;

    /// <summary>
    /// Formats a number through a mask.
    /// </summary>
    /// <param name="value">The exact value to format.</param>
    /// <param name="mask">An Excel number format code.</param>
    /// <returns>The formatted text, or <see langword="null"/> when the mask is blank, invalid, or the scaled value overflows.</returns>
    public static string? FormatNumber(decimal value, string? mask)
    {
        var code = Code(mask);
        if (code is null) return null;
        var sections = SplitSections(code).Select(ParseNumberSection).ToList();
        if (sections.Count > 3 || sections.Any(section => section is null) || !sections[0]!.Digits) return null;
        var positive = sections[0]!;
        var negative = sections.Count > 1 ? sections[1] : null;
        var zero = sections.Count > 2 ? sections[2] : null;
        try
        {
            if (value < 0 && negative is not null) return FormatNumberSection(Math.Abs(value), negative);
            if (value == 0 && zero is not null) return FormatNumberSection(value, zero);
            return FormatNumberSection(value, positive);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a date through a mask.
    /// </summary>
    /// <param name="value">The date to format.</param>
    /// <param name="mask">An Excel date format code.</param>
    /// <returns>The formatted text, or <see langword="null"/> when the mask is blank or invalid.</returns>
    public static string? FormatDate(DateTime value, string? mask)
    {
        var code = Code(mask);
        if (code is null) return null;
        var tokens = ParseDateCode(code);
        if (tokens is null) return null;
        var twelveHour = tokens.Any(token => token.Kind == DateTokenKind.Meridiem);
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        var result = new StringBuilder();
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case DateTokenKind.Literal:
                    result.Append(token.Text);
                    break;
                case DateTokenKind.Year:
                    result.Append(token.Width <= 2
                        ? (value.Year % 100).ToString("00", CultureInfo.InvariantCulture)
                        : value.Year.ToString("0000", CultureInfo.InvariantCulture));
                    break;
                case DateTokenKind.Month:
                    result.Append(token.Width switch
                    {
                        1 => value.Month.ToString(CultureInfo.InvariantCulture),
                        2 => value.Month.ToString("00", CultureInfo.InvariantCulture),
                        3 => names.GetAbbreviatedMonthName(value.Month),
                        4 => names.GetMonthName(value.Month),
                        _ => names.GetMonthName(value.Month)[..1],
                    });
                    break;
                case DateTokenKind.Day:
                    result.Append(token.Width switch
                    {
                        1 => value.Day.ToString(CultureInfo.InvariantCulture),
                        2 => value.Day.ToString("00", CultureInfo.InvariantCulture),
                        3 => names.GetAbbreviatedDayName(value.DayOfWeek),
                        _ => names.GetDayName(value.DayOfWeek),
                    });
                    break;
                case DateTokenKind.Hour:
                    var hour = twelveHour ? (value.Hour % 12 == 0 ? 12 : value.Hour % 12) : value.Hour;
                    result.Append(token.Width == 1
                        ? hour.ToString(CultureInfo.InvariantCulture)
                        : hour.ToString("00", CultureInfo.InvariantCulture));
                    break;
                case DateTokenKind.Minute:
                    result.Append(token.Width == 1
                        ? value.Minute.ToString(CultureInfo.InvariantCulture)
                        : value.Minute.ToString("00", CultureInfo.InvariantCulture));
                    break;
                case DateTokenKind.Second:
                    result.Append(token.Width == 1
                        ? value.Second.ToString(CultureInfo.InvariantCulture)
                        : value.Second.ToString("00", CultureInfo.InvariantCulture));
                    break;
                case DateTokenKind.Meridiem:
                    var marker = value.Hour < 12 ? "AM" : "PM";
                    var shown = token.Width == 3 ? marker[..1] : marker;
                    result.Append(token.Upper ? shown : shown.ToLowerInvariant());
                    break;
            }
        }
        return result.ToString();
    }

    private sealed class NumberSection
    {
        public string Prefix = "";
        public string Suffix = "";
        public int MinInteger;
        public int MinFraction;
        public int MaxFraction;
        public bool Grouping;
        public int Scale;
        public bool Digits;
    }

    private enum DateTokenKind { Literal, Year, Month, Day, Hour, Minute, Second, Meridiem }

    private sealed class DateToken
    {
        public DateTokenKind Kind;
        public int Width;
        public bool Upper;
        public string Text = "";
    }

    private static readonly HashSet<char> NumberLiteralChars =
        [' ', '$', '+', '-', '/', '(', ')', ':', '!', '^', '&', '\'', '~', '{', '}', '<', '>', '='];

    /// <summary>Splits a code at semicolons outside quotes, brackets, and escapes.</summary>
    private static List<string> SplitSections(string code)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (quoted) { current.Append(ch); if (ch == '"') quoted = false; continue; }
            if (bracket) { current.Append(ch); if (ch == ']') bracket = false; continue; }
            if (ch == '\\') { current.Append(ch); if (i + 1 < code.Length) current.Append(code[++i]); continue; }
            if (ch == '"') { quoted = true; current.Append(ch); continue; }
            if (ch == '[') { bracket = true; current.Append(ch); continue; }
            if (ch == ';') { sections.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        sections.Add(current.ToString());
        return sections;
    }

    /// <summary>Reads one literal construct shared by number and date codes.</summary>
    private static bool TryReadLiteral(string section, int index, out string text, out int next)
    {
        text = "";
        next = index;
        var ch = section[index];
        switch (ch)
        {
            case '"':
                var end = section.IndexOf('"', index + 1);
                if (end < 0) return false;
                text = section[(index + 1)..end];
                next = end + 1;
                return true;
            case '\\':
                if (index + 1 >= section.Length) return false;
                text = section[index + 1].ToString();
                next = index + 2;
                return true;
            case '_':
                if (index + 1 >= section.Length) return false;
                text = " ";
                next = index + 2;
                return true;
            case '*':
                if (index + 1 >= section.Length) return false;
                next = index + 2;
                return true;
            case '[':
                var close = section.IndexOf(']', index + 1);
                if (close < 0) return false;
                var body = section[(index + 1)..close];
                next = close + 1;
                if (body.StartsWith('$'))
                {
                    var dash = body.IndexOf('-');
                    text = dash < 0 ? body[1..] : body[1..dash];
                    return true;
                }
                // [Red] and [Color 10] are display colors with no text.
                var letters = body.TakeWhile(char.IsLetter).Count();
                return letters > 0 && body[letters..].Trim().All(char.IsDigit);
            default:
                return false;
        }
    }

    private static NumberSection? ParseNumberSection(string section)
    {
        var spec = new NumberSection();
        var literal = new StringBuilder();
        var inFraction = false;
        var started = false;
        var pendingCommas = 0;
        // Literals seen after the digit body become the suffix; another digit after them has
        // no sensible cell position.
        bool Numeric()
        {
            if (started && literal.Length > 0) return false;
            if (!started)
            {
                spec.Prefix += literal.ToString();
                literal.Clear();
                started = true;
            }
            return true;
        }
        for (var i = 0; i < section.Length;)
        {
            var ch = section[i];
            if (TryReadLiteral(section, i, out var text, out var next))
            {
                literal.Append(text);
                i = next;
                continue;
            }
            if (ch is '0' or '#' or '?')
            {
                if (!Numeric()) return null;
                if (inFraction)
                {
                    if (pendingCommas > 0) return null;
                    spec.MaxFraction++;
                    if (ch == '0') spec.MinFraction = spec.MaxFraction;
                }
                else
                {
                    if (pendingCommas > 0) { spec.Grouping = true; pendingCommas = 0; }
                    if (ch == '0') spec.MinInteger++;
                }
                spec.Digits = true;
                i++;
                continue;
            }
            if (ch == '.' && !inFraction)
            {
                if (!Numeric()) return null;
                inFraction = true;
                i++;
                continue;
            }
            if (ch == ',' && started && literal.Length == 0) { pendingCommas++; i++; continue; }
            if (ch == '%') { spec.Scale += 2; literal.Append('%'); i++; continue; }
            if (ch is '"' or '\\' or '_' or '*' or '[') return null;
            if (NumberLiteralChars.Contains(ch) || ch is '.' or ',' || ch is >= '1' and <= '9' || ch > 127)
            {
                literal.Append(ch);
                i++;
                continue;
            }
            return null;
        }
        // Commas after the last digit divide by a thousand each (#,##0, shows thousands).
        spec.Scale -= 3 * pendingCommas;
        if (started) spec.Suffix = literal.ToString();
        else spec.Prefix = literal.ToString();
        if (spec.MaxFraction > 28) return null;
        return spec;
    }

    private static string FormatNumberSection(decimal value, NumberSection section)
    {
        if (!section.Digits) return section.Prefix + section.Suffix;
        var scaled = value;
        for (var i = 0; i < section.Scale; i++) scaled *= 10m;
        for (var i = 0; i > section.Scale; i--) scaled /= 10m;
        var rounded = decimal.Round(scaled, section.MaxFraction, MidpointRounding.AwayFromZero);
        var negative = rounded < 0;
        var fixedText = Math.Abs(rounded).ToString("F" + section.MaxFraction, CultureInfo.InvariantCulture);
        var point = fixedText.IndexOf('.');
        var integer = point < 0 ? fixedText : fixedText[..point];
        var fraction = point < 0 ? "" : fixedText[(point + 1)..];
        while (fraction.Length > section.MinFraction && fraction.EndsWith('0'))
            fraction = fraction[..^1];
        if (integer == "0" && section.MinInteger == 0) integer = "";
        if (integer.Length < section.MinInteger) integer = integer.PadLeft(section.MinInteger, '0');
        if (section.Grouping && integer.Length > 3)
        {
            var grouped = new StringBuilder();
            for (var i = 0; i < integer.Length; i++)
            {
                if (i > 0 && (integer.Length - i) % 3 == 0) grouped.Append(',');
                grouped.Append(integer[i]);
            }
            integer = grouped.ToString();
        }
        var magnitude = fraction.Length > 0 ? integer + "." + fraction : integer;
        return (negative ? "-" : "") + section.Prefix + magnitude + section.Suffix;
    }

    private static List<DateToken>? ParseDateCode(string code)
    {
        var tokens = new List<DateToken>();
        void Literal(string text)
        {
            if (tokens.Count > 0 && tokens[^1].Kind == DateTokenKind.Literal) tokens[^1].Text += text;
            else tokens.Add(new DateToken { Kind = DateTokenKind.Literal, Text = text });
        }
        for (var i = 0; i < code.Length;)
        {
            var ch = code[i];
            // Elapsed-time brackets ([h]:mm) and colors have no place in a date cell.
            if (ch == '[') return null;
            if (TryReadLiteral(code, i, out var text, out var next))
            {
                Literal(text);
                i = next;
                continue;
            }
            var rest = code.AsSpan(i);
            var meridiem = rest.StartsWith("AM/PM") || rest.StartsWith("am/pm") ? 5
                : rest.StartsWith("A/P") || rest.StartsWith("a/p") ? 3
                : 0;
            if (meridiem > 0)
            {
                tokens.Add(new DateToken { Kind = DateTokenKind.Meridiem, Width = meridiem, Upper = ch == 'A' });
                i += meridiem;
                continue;
            }
            var lower = char.ToLowerInvariant(ch);
            if ("ymdhs".Contains(lower))
            {
                var width = 0;
                while (i + width < code.Length && char.ToLowerInvariant(code[i + width]) == lower) width++;
                tokens.Add(new DateToken
                {
                    Kind = lower switch
                    {
                        'y' => DateTokenKind.Year,
                        'm' => DateTokenKind.Month,
                        'd' => DateTokenKind.Day,
                        'h' => DateTokenKind.Hour,
                        _ => DateTokenKind.Second,
                    },
                    Width = width,
                });
                i += width;
                continue;
            }
            if (char.IsAsciiLetter(ch) || ch is '"' or '\\' or '_' or '*' or ']') return null;
            Literal(ch.ToString());
            i++;
        }
        var dateTokens = tokens.Where(token => token.Kind != DateTokenKind.Literal).ToList();
        if (dateTokens.Count == 0) return null;
        // m means minutes beside hours or seconds, and months everywhere else.
        for (var index = 0; index < dateTokens.Count; index++)
        {
            var token = dateTokens[index];
            if (token.Kind != DateTokenKind.Month || token.Width > 2) continue;
            var previous = index > 0 ? dateTokens[index - 1].Kind : DateTokenKind.Literal;
            var following = index + 1 < dateTokens.Count ? dateTokens[index + 1].Kind : DateTokenKind.Literal;
            if (previous == DateTokenKind.Hour || following == DateTokenKind.Second) token.Kind = DateTokenKind.Minute;
        }
        foreach (var token in dateTokens)
        {
            var limit = token.Kind switch
            {
                DateTokenKind.Year => 4,
                DateTokenKind.Month => 5,
                DateTokenKind.Day => 4,
                DateTokenKind.Meridiem => 5,
                _ => 2,
            };
            if (token.Width > limit) return null;
        }
        return tokens;
    }
}
