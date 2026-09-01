using System.Buffers;
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
    private static readonly UTF8Encoding BodyEncoding = new(encoderShouldEmitUTF8Identifier: false);

    // Rendered text reaches the stream in fixed chunks, sized to stay well under the large object
    // heap threshold so an unpaged export never allocates a buffer proportional to the result.
    private const int ChunkChars = 16 * 1024;

    /// <summary>
    /// Writes the report to a destination stream in bounded chunks.
    /// </summary>
    /// <param name="destination">The stream that receives the BOM and CSV body.</param>
    /// <param name="columns">The visible columns in wire order; their labels become the header row.</param>
    /// <param name="rows">The result rows, keyed by column name.</param>
    /// <param name="policy">Whether formula-like text is neutralized.</param>
    /// <param name="ct">Cancels writing between chunks.</param>
    /// <remarks>
    /// Asynchronous throughout: an ASP.NET Core response body rejects synchronous writes unless the
    /// host opts back in, and an unpaged export is the last place worth doing that.
    /// </remarks>
    public static async Task WriteToAsync(
        Stream destination,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CsvCellPolicy policy = CsvCellPolicy.SafeText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        await destination.WriteAsync(Encoding.UTF8.GetPreamble(), ct);

        // One encoder spans every chunk, so a surrogate pair split across a boundary stays intact.
        var encoder = BodyEncoding.GetEncoder();
        var capacity = ChunkChars + 1024;
        var text = new StringBuilder(capacity);
        var chars = ArrayPool<char>.Shared.Rent(capacity);
        var bytes = ArrayPool<byte>.Shared.Rent(BodyEncoding.GetMaxByteCount(capacity));
        try
        {
            AppendRecord(text, columns, policy, row: null);
            foreach (var row in rows)
            {
                AppendRecord(text, columns, policy, row);
                if (text.Length >= ChunkChars)
                    await FlushAsync(destination, encoder, text, chars, bytes, flush: false, ct);
            }
            await FlushAsync(destination, encoder, text, chars, bytes, flush: true, ct);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// Renders the report to a complete byte array. Prefer <see cref="WriteToAsync"/> for exports
    /// large enough that holding the whole payload matters.
    /// </summary>
    /// <param name="columns">The visible columns in wire order; their labels become the header row.</param>
    /// <param name="rows">The result rows, keyed by column name.</param>
    /// <param name="policy">Whether formula-like text is neutralized.</param>
    /// <returns>The complete file, including the UTF-8 BOM.</returns>
    public static byte[] Write(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CsvCellPolicy policy = CsvCellPolicy.SafeText)
    {
        using var buffer = new MemoryStream();
        // A MemoryStream completes every write synchronously, so this never blocks a thread.
        WriteToAsync(buffer, columns, rows, policy).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    /// <summary>Appends the header record when the row is null, otherwise one data record.</summary>
    private static void AppendRecord(
        StringBuilder text,
        IReadOnlyList<ColumnInfo> columns,
        CsvCellPolicy policy,
        IReadOnlyDictionary<string, object?>? row)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) text.Append(',');
            string field;
            if (row is null)
            {
                field = Sanitize(columns[i].Label, true, policy);
            }
            else
            {
                row.TryGetValue(columns[i].Name, out var value);
                var (formatted, fromText) = Format(value);
                field = Sanitize(formatted, fromText, policy);
            }
            AppendField(text, field);
        }
        text.Append("\r\n");
    }

    /// <summary>Encodes the pending text into the pooled buffer, writes it, and clears it.</summary>
    private static async Task FlushAsync(
        Stream destination,
        Encoder encoder,
        StringBuilder text,
        char[] chars,
        byte[] bytes,
        bool flush,
        CancellationToken ct)
    {
        if (text.Length > 0)
        {
            text.CopyTo(0, chars, text.Length);
            var count = encoder.GetBytes(chars.AsSpan(0, text.Length), bytes.AsSpan(), flush);
            text.Clear();
            await destination.WriteAsync(bytes.AsMemory(0, count), ct);
        }

        if (flush) await destination.FlushAsync(ct);
    }

    /// <summary>Appends one field, quoting and doubling embedded quotes only when RFC 4180 requires it.</summary>
    private static void AppendField(StringBuilder text, string field)
    {
        if (field.AsSpan().IndexOfAny(',', '"', '\r') < 0 && !field.Contains('\n'))
        {
            text.Append(field);
            return;
        }

        text.Append('"');
        foreach (var character in field)
        {
            if (character == '"') text.Append('"');
            text.Append(character);
        }
        text.Append('"');
    }

    private static (string Text, bool FromText) Format(object? value) => value switch
    {
        null => ("", false),
        string text => (text, true),
        char character => (character.ToString(), true),
        DateTime date => (date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), false),
        bool boolean => (boolean ? "true" : "false", false),
        IFormattable formattable => (formattable.ToString(null, CultureInfo.InvariantCulture) ?? "", false),
        _ => (value.ToString() ?? "", true),
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
