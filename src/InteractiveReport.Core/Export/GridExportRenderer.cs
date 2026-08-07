using System.Globalization;
using System.Net;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Export;

/// <summary>
/// Shapes grid export rows like their browser cells. Ordinary columns remain scalar;
/// link and image columns become encoded HTML fragments matching the client renderer.
/// Renderer-only source columns never cross into the returned row shape.
/// </summary>
internal static class GridExportRenderer
{
    public static List<IReadOnlyDictionary<string, object?>> Render(
        ValidatedState state,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var decimalColumns = state.SelectColumns
            .Where(column => column.Kind == ColumnKind.Number
                && rows.Any(row => row.TryGetValue(column.Name, out var value) && HasFraction(value)))
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var rendered = new Dictionary<string, object?>(state.SelectColumns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var column in state.SelectColumns)
            {
                row.TryGetValue(column.Name, out var value);
                state.Formats.TryGetValue(column.Name, out var format);
                rendered[column.Name] = RenderValue(
                    state,
                    row,
                    column,
                    value,
                    format,
                    decimalColumns.Contains(column.Name));
            }
            result.Add(rendered);
        }
        return result;
    }

    private static object? RenderValue(
        ValidatedState state,
        IReadOnlyDictionary<string, object?> row,
        ColumnModel column,
        object? value,
        ColumnFormat? format,
        bool decimalColumn)
    {
        var renderer = format?.DisplayAs?.Trim();
        if (!string.Equals(renderer, "link", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(renderer, "image", StringComparison.OrdinalIgnoreCase))
            return value;

        var urlColumn = SourceColumn(state, format!.UrlColumn, column);
        row.TryGetValue(urlColumn.Name, out var urlValue);
        var url = RawString(urlValue).Trim();

        if (string.Equals(renderer, "image", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAllowedUrl(url, image: true))
                return WebUtility.HtmlEncode(RenderText(value, column, decimalColumn, format.Mask));
            return $"<img class=\"ir-cell-image\" src=\"{WebUtility.HtmlEncode(url)}\" alt=\"\" loading=\"lazy\" decoding=\"async\">";
        }

        var textColumn = SourceColumn(state, format.TextColumn, column);
        row.TryGetValue(textColumn.Name, out var textValue);
        state.Formats.TryGetValue(textColumn.Name, out var textFormat);
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

    private static ColumnModel SourceColumn(ValidatedState state, string? requested, ColumnModel fallback)
        => !string.IsNullOrWhiteSpace(requested) && state.Schema.TryGetValue(requested, out var source)
            ? source
            : fallback;

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
    /// Server counterpart of the browser's base text renderer. CSV only invokes it
    /// when a Display As cell needs text inside HTML; ordinary CSV scalars remain raw.
    /// </summary>
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

    private static string RawString(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? "",
    };

    private static bool HasFraction(object? value)
        => TryDecimal(value, out var number) && decimal.Truncate(number) != number;

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

    private static bool TryDate(object value, out DateTime date)
    {
        if (value is DateTime typed)
        {
            date = typed;
            return true;
        }
        if (value is DateTimeOffset offset)
        {
            // Match the browser formatter: display the session-local wall-clock
            // components carried in the value, not the server process timezone.
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
