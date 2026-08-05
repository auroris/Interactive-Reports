using System.Globalization;
using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Export;

/// <summary>
/// RFC 4180 CSV: CRLF row endings, fields quoted when they contain comma/quote/CR/LF,
/// quotes doubled. UTF-8 with BOM so Excel detects the encoding. Headers are column
/// labels (what the user sees), not internal names.
/// </summary>
public static class CsvWriter
{
    public static byte[] Write(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();

        AppendRow(sb, columns.Select(c => c.Label));
        foreach (var row in rows)
        {
            AppendRow(sb, columns.Select(c =>
            {
                row.TryGetValue(c.Name, out var value);
                return Format(value);
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
}
