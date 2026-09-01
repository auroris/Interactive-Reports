using System.Globalization;
using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Client.FileDownload;

/// <summary>
/// Writes RFC 4180 output with CRLF row endings and a UTF-8 BOM. Headers use display
/// labels. Text that could trigger spreadsheet formula evaluation is prefixed with an
/// apostrophe by default; typed numbers, dates, and booleans retain their values.
/// </summary>
public static class CsvWriter
{
    public static byte[] Write(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CsvCellPolicy policy = CsvCellPolicy.SafeText)
    {
        var buffer = new StringBuilder();
        AppendRow(buffer, columns.Select(column => Sanitize(column.Label, true, policy)));
        foreach (var row in rows)
        {
            AppendRow(buffer, columns.Select(column =>
            {
                row.TryGetValue(column.Name, out var value);
                return Sanitize(Format(value), value is string or char, policy);
            }));
        }

        var body = Encoding.UTF8.GetBytes(buffer.ToString());
        var preamble = Encoding.UTF8.GetPreamble();
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private static void AppendRow(StringBuilder buffer, IEnumerable<string> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first) buffer.Append(',');
            first = false;
            AppendField(buffer, field);
        }
        buffer.Append("\r\n");
    }

    private static void AppendField(StringBuilder buffer, string field)
    {
        if (field.AsSpan().IndexOfAny(',', '"', '\r') < 0 && !field.Contains('\n'))
        {
            buffer.Append(field);
            return;
        }
        buffer.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
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

/// <summary>Specifies how <see cref="CsvWriter"/> treats formula-like text.</summary>
public enum CsvCellPolicy
{
    SafeText,
    Verbatim,
}
