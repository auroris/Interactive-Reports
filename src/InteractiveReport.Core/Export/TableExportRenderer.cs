using System.Globalization;
using System.Net;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Export;

/// <summary>
/// Shapes any terminal table's export like its browser cells. The renderer depends on
/// the terminal schema and ordinary presentation composables, never on the shape that
/// produced the table. Renderer-only source columns never cross into the returned row
/// shape.
/// </summary>
internal static class TableExportRenderer
{
    /// <summary>
    /// Shapes a terminal result into ordered export columns and display-formatted rows.
    /// </summary>
    /// <param name="availableColumns">All projected columns, including hidden renderer dependencies.</param>
    /// <param name="columns">Visible export columns in final order.</param>
    /// <param name="rows">Raw projected provider rows.</param>
    /// <param name="schema">The terminal schema used to recover kinds and source columns.</param>
    /// <param name="formats">Formats owned by the terminal table.</param>
    /// <param name="labels">Effective labels keyed by logical column name.</param>
    /// <param name="inheritedFormats">Optional parent masks keyed by their source logical ids.</param>
    /// <returns>Relabeled visible columns and rows containing raw scalars or browser-equivalent link/image HTML.</returns>
    public static RenderedExportTable Render(
        IReadOnlyList<ColumnInfo> availableColumns,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        ReportSchema schema,
        IReadOnlyDictionary<string, ColumnFormat> formats,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, ColumnFormat>? inheritedFormats = null)
    {
        inheritedFormats ??= NoFormats;
        var metadata = availableColumns
            .Concat(columns)
            .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var renderedColumns = columns
            .Select(column => labels.TryGetValue(column.Name, out var label)
                ? column with { Label = label }
                : column)
            .ToList();
        var decimalColumns = columns
            .Where(column => Model(schema, column).Kind == ColumnKind.Number
                && rows.Any(row => row.TryGetValue(column.Name, out var value) && HasFraction(value)))
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var rendered = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                row.TryGetValue(column.Name, out var value);
                var format = EffectiveFormat(column, formats, inheritedFormats);
                rendered[column.Name] = RenderValue(
                    schema,
                    metadata,
                    formats,
                    inheritedFormats,
                    row,
                    Model(schema, column),
                    value,
                    format,
                    decimalColumns.Contains(column.Name));
            }
            result.Add(rendered);
        }
        return new RenderedExportTable(renderedColumns, result);
    }

    /// <summary>
    /// Renders one link, image, or action cell; ordinary cells remain raw for the file serializer.
    /// </summary>
    /// <param name="schema">The terminal schema used to resolve renderer source columns.</param>
    /// <param name="metadata">Available result-column metadata keyed by logical name.</param>
    /// <param name="formats">Terminal-table formats.</param>
    /// <param name="inheritedFormats">Parent masks keyed by format-source logical id.</param>
    /// <param name="row">The raw projected row, including hidden dependencies.</param>
    /// <param name="column">The visible column being rendered.</param>
    /// <param name="value">The provider value to format for the exported column.</param>
    /// <param name="format">The effective display format for <paramref name="column"/>.</param>
    /// <param name="decimalColumn">Whether any row proves the visible numeric column has fractional values.</param>
    /// <returns>A raw scalar for ordinary/action cells, or safe HTML/text for link and image cells.</returns>
    private static object? RenderValue(
        ReportSchema schema,
        IReadOnlyDictionary<string, ColumnInfo> metadata,
        IReadOnlyDictionary<string, ColumnFormat> formats,
        IReadOnlyDictionary<string, ColumnFormat> inheritedFormats,
        IReadOnlyDictionary<string, object?> row,
        ColumnModel column,
        object? value,
        ColumnFormat? format,
        bool decimalColumn)
    {
        var renderer = format?.DisplayAs?.Trim();
        // A command button has no CSV shape: the label (the cell's own value) exports as plain
        // text, and a NULL label stays an empty field.
        if (string.Equals(renderer, "action", StringComparison.OrdinalIgnoreCase))
            return value;
        if (!string.Equals(renderer, "link", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(renderer, "image", StringComparison.OrdinalIgnoreCase))
            return value;

        var urlColumn = SourceColumn(schema, format!.UrlColumn, column);
        row.TryGetValue(urlColumn.Name, out var urlValue);
        var url = RawString(urlValue).Trim();

        if (string.Equals(renderer, "image", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAllowedUrl(url, image: true))
                return WebUtility.HtmlEncode(RenderText(value, column, decimalColumn, format.Mask));
            return $"<img class=\"ir-cell-image\" src=\"{WebUtility.HtmlEncode(url)}\" alt=\"\" loading=\"lazy\" decoding=\"async\">";
        }

        var textColumn = SourceColumn(schema, format.TextColumn, column);
        row.TryGetValue(textColumn.Name, out var textValue);
        var textInfo = metadata.TryGetValue(textColumn.Name, out var known)
            ? known
            : ToInfo(textColumn);
        var textFormat = EffectiveFormat(textInfo, formats, inheritedFormats);
        if (textColumn.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)) textFormat = format;
        var text = RenderText(
            textValue,
            textColumn,
            textColumn.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)
                ? decimalColumn
                : HasFraction(textValue),
            textFormat?.Mask);

        if (!IsAllowedUrl(url, image: false)) return WebUtility.HtmlEncode(text);
        if (text.Length == 0) text = url;
        return $"<a class=\"ir-cell-link\" href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(text)}</a>";
    }

    /// <summary>
    /// Resolves the final format inherited by an exported column.
    /// </summary>
    /// <param name="column">The result column carrying an optional inherited format source.</param>
    /// <param name="formats">Terminal-table formats, which take precedence.</param>
    /// <param name="inheritedFormats">Parent masks keyed by source logical id.</param>
    /// <returns>The terminal format, inherited format, or <see langword="null"/>.</returns>
    private static ColumnFormat? EffectiveFormat(
        ColumnInfo column,
        IReadOnlyDictionary<string, ColumnFormat> formats,
        IReadOnlyDictionary<string, ColumnFormat> inheritedFormats)
    {
        if (formats.TryGetValue(column.Name, out var format)) return format;
        var inheritedName = column.FormatSource ?? column.Name;
        return inheritedFormats.TryGetValue(inheritedName, out format) ? format : null;
    }

    /// <summary>
    /// Resolves a renderer's URL or text source column, preserving an absent authored name as a null-valued placeholder.
    /// </summary>
    /// <param name="schema">The terminal schema used for case-insensitive lookup.</param>
    /// <param name="requested">The optional authored source-column name.</param>
    /// <param name="fallback">The displayed column used when no source is configured.</param>
    /// <returns>The live source column, fallback, or an object-typed placeholder for a stale inherited source.</returns>
    private static ColumnModel SourceColumn(ReportSchema schema, string? requested, ColumnModel fallback)
    {
        if (string.IsNullOrWhiteSpace(requested)) return fallback;
        if (schema.TryGetValue(requested, out var source)) return source;

        // An inherited source-table renderer can name a column that does not exist in shaped
        // output. Browser rendering reads that absent row key and falls back to text;
        // substituting the displayed metric here would turn its numeric value into a spurious
        // relative URL.
        return new ColumnModel
        {
            Name = requested.Trim(),
            Label = requested.Trim(),
            ClrType = typeof(object),
        };
    }

    /// <summary>
    /// Resolves the schema model associated with an exported column.
    /// </summary>
    /// <param name="schema">The terminal schema used for case-insensitive lookup.</param>
    /// <param name="column">The exported result-column metadata.</param>
    /// <returns>The live model or a conservative object-typed fallback.</returns>
    private static ColumnModel Model(ReportSchema schema, ColumnInfo column)
        => schema.TryGetValue(column.Name, out var model)
            ? model
            : new ColumnModel
            {
                Name = column.Name,
                Label = column.Label,
                ClrType = typeof(object),
                IsComputed = column.Computed,
            };

    /// <summary>
    /// Converts discovered schema metadata into the result-column contract used by export rendering.
    /// </summary>
    /// <param name="column">The schema model to project.</param>
    /// <returns>Name, label, portable type name, and computed flag.</returns>
    private static ColumnInfo ToInfo(ColumnModel column)
        => new(column.Name, column.Label, column.KindName, column.IsComputed);

    /// <summary>Shared immutable-by-convention empty format lookup used when no inherited map is supplied.</summary>
    private static readonly IReadOnlyDictionary<string, ColumnFormat> NoFormats
        = new Dictionary<string, ColumnFormat>();

    /// <summary>
    /// Determines whether a URL uses a scheme that server-side export may emit.
    /// </summary>
    /// <param name="value">The raw absolute or relative URL.</param>
    /// <param name="image">Indicates whether the rendered value is an image URL.</param>
    /// <returns><see langword="true"/> for valid relative URLs, HTTP(S), and non-image mail/tel URLs; otherwise, <see langword="false"/>.</returns>
    private static bool IsAllowedUrl(string value, bool image)
    {
        if (value.Length == 0 || value.Any(char.IsControl)) return false;

        var colon = value.IndexOf(':');
        var delimiter = value.IndexOfAny(['/', '?', '#']);
        if (colon > 0 && (delimiter < 0 || colon < delimiter))
        {
            var scheme = value[..colon];
            if (!char.IsAsciiLetter(scheme[0])
                || scheme.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '+' and not '-' and not '.'))
                return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out _)) return false;
            return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || (!image && (scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
                    || scheme.Equals("tel", StringComparison.OrdinalIgnoreCase)));
        }

        return Uri.TryCreate(value, UriKind.Relative, out _);
    }

    /// <summary>
    /// Implements the server counterpart of the browser's base text renderer. CSV only invokes it when a
    /// Display As cell needs text inside HTML; ordinary CSV scalars remain raw.
    /// </summary>
    /// <param name="value">The provider value to render with the selected display mask.</param>
    /// <param name="column">The source column supplying kind information.</param>
    /// <param name="decimalColumn">Whether unmasked numeric output should retain two fractional places.</param>
    /// <param name="mask">The optional display mask to apply.</param>
    /// <returns>The rendered display text.</returns>
    private static string RenderText(
        object? value,
        ColumnModel column,
        bool decimalColumn,
        string? mask)
    {
        if (value is null) return "";

        if (column.Kind == ColumnKind.Number && TryDecimal(value, out var number))
        {
            var masked = FormatNumberMask(number, mask);
            if (masked is not null) return masked;
            if (!decimalColumn && decimal.Truncate(number) == number)
                return number.ToString("0", CultureInfo.InvariantCulture);
            return number.ToString("N2", CultureInfo.InvariantCulture);
        }

        if (column.Kind == ColumnKind.Date && TryDate(value, out var date))
        {
            return mask switch
            {
                "date" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "datetime" => date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "datetimeSeconds" => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "time" => date.ToString("h:mm tt", CultureInfo.InvariantCulture),
                "timeSeconds" => date.ToString("h:mm:ss tt", CultureInfo.InvariantCulture),
                "dateMedium" => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                "dateLong" => date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
                "dateTimeMedium" => date.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture),
                "dateTimeLong" => date.ToString("MMMM d, yyyy, h:mm:ss tt", CultureInfo.InvariantCulture),
                _ => date.TimeOfDay == TimeSpan.Zero
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            };
        }

        return value switch
        {
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>
    /// Applies one supported numeric display mask.
    /// </summary>
    /// <param name="number">The decimal value to round and format.</param>
    /// <param name="mask">An optional integer, decimal, plain, currency, or percent mask.</param>
    /// <returns>Invariant formatted text, or <see langword="null"/> when the mask is absent, unsupported, or overflows.</returns>
    private static string? FormatNumberMask(decimal number, string? mask)
    {
        var digits = mask switch
        {
            "integer" => 0,
            "decimal1" => 1,
            "decimal2" => 2,
            "decimal3" => 3,
            "decimal4" => 4,
            _ => -1,
        };
        if (digits >= 0)
            return decimal.Round(number, digits, MidpointRounding.AwayFromZero)
                .ToString($"N{digits}", CultureInfo.InvariantCulture);
        if (mask == "plain")
            return decimal.Round(number, 2, MidpointRounding.AwayFromZero)
                .ToString("F2", CultureInfo.InvariantCulture);

        if (mask is not null && mask.StartsWith("currency:", StringComparison.Ordinal))
        {
            var currency = mask[9..];
            var (symbol, currencyDigits) = currency switch
            {
                "CAD" => ("CA$", 2),
                "USD" => ("$", 2),
                "EUR" => ("€", 2),
                "GBP" => ("£", 2),
                "JPY" => ("¥", 0),
                _ => ((string?)null, 0),
            };
            if (symbol is not null)
            {
                var formatted = decimal.Round(number, currencyDigits, MidpointRounding.AwayFromZero)
                    .ToString($"N{currencyDigits}", CultureInfo.InvariantCulture);
                return formatted.StartsWith("-", StringComparison.Ordinal)
                    ? $"-{symbol}{formatted[1..]}"
                    : $"{symbol}{formatted}";
            }
        }

        if (mask is { Length: 8 } && mask.StartsWith("percent", StringComparison.Ordinal)
            && mask[7] is >= '0' and <= '2')
        {
            try
            {
                var percentDigits = mask[7] - '0';
                return decimal.Round(number * 100m, percentDigits, MidpointRounding.AwayFromZero)
                    .ToString($"N{percentDigits}", CultureInfo.InvariantCulture) + "%";
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a provider value to invariant raw text before display formatting.
    /// </summary>
    /// <param name="value">The provider value to convert to invariant raw text.</param>
    /// <returns>The decoded raw string literal.</returns>
    private static string RawString(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Determines whether a numeric value contains a fractional component.
    /// </summary>
    /// <param name="value">The numeric provider value to inspect.</param>
    /// <returns><see langword="true"/> when the numeric value contains a fractional component; otherwise, <see langword="false"/>.</returns>
    private static bool HasFraction(object? value)
        => TryDecimal(value, out var number) && decimal.Truncate(number) != number;

    /// <summary>
    /// Attempts to convert a provider value to a decimal without throwing for unsupported types.
    /// </summary>
    /// <param name="value">The provider value to convert to a decimal.</param>
    /// <param name="number">Receives the converted decimal on success, or zero on failure.</param>
    /// <returns><see langword="true"/> when the provider value can be converted to a decimal; otherwise, <see langword="false"/>.</returns>
    private static bool TryDecimal(object? value, out decimal number)
    {
        try
        {
            number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            number = 0;
            return false;
        }
    }

    /// <summary>
    /// Attempts to convert a provider value to a date and time without throwing for unsupported types.
    /// </summary>
    /// <param name="value">The provider value to convert to a date and time.</param>
    /// <param name="date">Receives the converted wall-clock date/time.</param>
    /// <returns><see langword="true"/> when the provider value can be converted to a date and time; otherwise, <see langword="false"/>.</returns>
    private static bool TryDate(object value, out DateTime date)
    {
        if (value is DateTime typed)
        {
            date = typed;
            return true;
        }
        if (value is DateTimeOffset offset)
        {
            // Match the browser formatter by displaying the session-local
            // wall-clock components carried in the value, not the server process timezone.
            date = offset.DateTime;
            return true;
        }
        if (value is DateOnly dateOnly)
        {
            date = dateOnly.ToDateTime(TimeOnly.MinValue);
            return true;
        }
        return DateTime.TryParse(
            value.ToString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out date);
    }
}

/// <summary>Contains visible export columns and fully projected/rendered rows.</summary>
internal sealed record RenderedExportTable(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
