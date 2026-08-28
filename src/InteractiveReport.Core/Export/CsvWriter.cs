using System.Globalization;
using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Export;

/// <summary>
/// RFC 4180 CSV: CRLF row endings, fields quoted when they contain comma/quote/CR/LF,
/// quotes doubled. UTF-8 with BOM so Excel detects the encoding. Headers are column
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

    private static void AppendField(StringBuilder sb, string field)
    {
        if (field.AsSpan().IndexOfAny(',', '"', '\r') < 0 && !field.Contains('\n'))
        {
            sb.Append(field);
            return;
        }
        sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? "",
    };

    private static string Sanitize(string field, bool fromText, CsvCellPolicy policy)
        => policy == CsvCellPolicy.SafeText
           && fromText
           && field.Length > 0
           && field[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + field
            : field;
}

/// <summary>How <see cref="CsvWriter"/> treats text cells a spreadsheet would evaluate.</summary>
public enum CsvCellPolicy
{
    /// <summary>Prefix formula-triggering text cells with an apostrophe (the default).</summary>
    SafeText,

    /// <summary>Emit text exactly as stored; only choose this for non-spreadsheet consumers.</summary>
    Verbatim,
}
