using System.Globalization;
using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Export;

/// <summary>
/// Writes RFC 4180 output with CRLF row endings. Fields containing commas, quotes, CR, or LF are
/// quoted, with embedded quotes doubled. Output is UTF-8 with a BOM so Excel detects the encoding. Headers are column
/// labels (what the user sees), not internal names.
///
/// The default cell policy neutralizes spreadsheet formula injection: RFC 4180
/// quoting does not stop Excel from evaluating a cell that begins with =, +, -, @,
/// tab, or CR, and exported database text can be attacker-authored. Text-sourced
/// cells starting with those characters get the OWASP-recommended leading apostrophe
/// (Excel's text marker). Values of other CLR types (numbers, dates, booleans) format
/// to safe representations and are never altered, so negative numbers keep full
/// fidelity. Pass <see cref="CsvCellPolicy.Verbatim"/> for byte-exact text when the
/// consumer is not a spreadsheet.
/// </summary>
public static class CsvWriter
{
    /// <summary>
    /// Serializes protocol columns and rows as a UTF-8 CSV document with a byte-order mark.
    /// </summary>
    /// <param name="columns">The columns whose labels form the header and whose names select row values.</param>
    /// <param name="rows">The result rows to serialize in their existing order.</param>
    /// <param name="policy">The spreadsheet-injection policy for text cells; defaults to <c>CsvCellPolicy.SafeText</c>.</param>
    /// <returns>The complete UTF-8 CSV payload, including its byte-order mark.</returns>
    public static byte[] Write(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CsvCellPolicy policy = CsvCellPolicy.SafeText)
    {
        var sb = new StringBuilder();

        // Header labels are text by nature (definition- or document-authored).
        AppendRow(sb, columns.Select(c => Sanitize(c.Label, fromText: true, policy)));
        foreach (var row in rows)
        {
            AppendRow(sb, columns.Select(c =>
            {
                row.TryGetValue(c.Name, out var value);
                return Sanitize(Format(value), value is string or char, policy);
            }));
        }

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var bom = Encoding.UTF8.GetPreamble();
        var result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }

    /// <summary>
    /// Appends one RFC 4180 row and its CRLF terminator.
    /// </summary>
    /// <param name="sb">The text builder that receives generated output.</param>
    /// <param name="fields">The ordered field values written as one CSV row.</param>
    private static void AppendRow(StringBuilder sb, IEnumerable<string> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendField(sb, field);
        }
        sb.Append("\r\n");
    }

    /// <summary>
    /// Appends one field, quoting and escaping it when RFC 4180 requires it.
    /// </summary>
    /// <param name="sb">The text builder that receives generated output.</param>
    /// <param name="field">The already formatted field text.</param>
    private static void AppendField(StringBuilder sb, string field)
    {
        if (field.AsSpan().IndexOfAny(',', '"', '\r') < 0 && !field.Contains('\n'))
        {
            sb.Append(field);
            return;
        }
        sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
    }

    /// <summary>
    /// Converts a provider value to invariant text before CSV escaping.
    /// </summary>
    /// <param name="value">The provider value to format.</param>
    /// <returns>The invariant CSV field text.</returns>
    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Applies the configured spreadsheet-formula injection policy to a formatted field.
    /// </summary>
    /// <param name="field">The formatted field text.</param>
    /// <param name="fromText">Indicates whether the value originated as text and must preserve spreadsheet-safe quoting.</param>
    /// <param name="policy">The spreadsheet-injection policy to apply.</param>
    /// <returns>The value after applying the configured spreadsheet-injection policy.</returns>
    private static string Sanitize(string field, bool fromText, CsvCellPolicy policy)
        => policy == CsvCellPolicy.SafeText
           && fromText
           && field.Length > 0
           && field[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + field
            : field;
}

/// <summary>Specifies how <see cref="CsvWriter"/> treats text cells a spreadsheet would evaluate.</summary>
public enum CsvCellPolicy
{
    /// <summary>Prefixes formula-triggering text cells with an apostrophe; this is the default.</summary>
    SafeText,

    /// <summary>Emits text exactly as stored; use only for non-spreadsheet consumers.</summary>
    Verbatim,
}
