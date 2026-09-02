using System.Globalization;
using System.Text.Json;
using InteractiveReport.Core.Formatting;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Client.FileDownload;

/// <summary>A visible, CSV-oriented projection of an ordinary report query result.</summary>
public sealed record CsvReportTable(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>
/// Applies report-document presentation choices to the tabular CSV projection. This is
/// deliberately owned by the file client rather than the query engine: Core returns raw
/// values and presentation metadata, while the client decides how those values fit a file.
/// </summary>
public static class CsvReportPresentation
{
    private static readonly string[] TotalFunctionOrder =
        ["sum", "avg", "median", "min", "max", "count", "countDistinct"];

    /// <summary>
    /// Relabels visible columns, applies display masks and file-appropriate renderer
    /// semantics, removes hidden renderer inputs, and materializes requested pivot totals.
    /// Links become their displayed text, images become their URL, and actions become their
    /// label. Browser HTML and CSS have no CSV representation and are never emitted.
    /// </summary>
    public static CsvReportTable Render(ReportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var presentation = ResolvePresentation(result.Document, result.ConfiguredLabels);
        var metadata = result.AvailableColumns
            .Concat(result.Columns)
            .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var columns = result.Columns
            .Select(column => presentation.Labels.TryGetValue(column.Name, out var label)
                ? column with { Label = label }
                : column)
            .ToArray();
        var sourceRows = AppendPivotTotals(result, columns, presentation.Pivot);
        var decimalColumns = columns
            .Where(column => IsNumber(column)
                && sourceRows.Any(row => row.TryGetValue(column.Name, out var value) && HasFraction(value)))
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = sourceRows.Select(row =>
        {
            var rendered = new Dictionary<string, object?>(columns.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                row.TryGetValue(column.Name, out var value);
                presentation.Formats.TryGetValue(column.Name, out var format);
                rendered[column.Name] = RenderValue(
                    metadata,
                    presentation.Formats,
                    row,
                    column,
                    value,
                    format,
                    decimalColumns.Contains(column.Name));
            }
            return (IReadOnlyDictionary<string, object?>)rendered;
        }).ToArray();

        return new CsvReportTable(columns, rows);
    }

    private static object? RenderValue(
        IReadOnlyDictionary<string, ColumnInfo> metadata,
        IReadOnlyDictionary<string, ColumnFormat> formats,
        IReadOnlyDictionary<string, object?> row,
        ColumnInfo column,
        object? value,
        ColumnFormat? format,
        bool decimalColumn)
    {
        var renderer = format?.DisplayAs?.Trim();
        if (string.Equals(renderer, "action", StringComparison.OrdinalIgnoreCase))
            return value;

        if (string.Equals(renderer, "image", StringComparison.OrdinalIgnoreCase))
        {
            var url = SourceValue(row, format!.UrlColumn, column.Name);
            var text = RawString(url).Trim();
            return IsAllowedUrl(text, image: true)
                ? text
                : Formatted(value, RenderText(value, column, decimalColumn, format.Mask));
        }

        if (string.Equals(renderer, "link", StringComparison.OrdinalIgnoreCase))
        {
            var url = RawString(SourceValue(row, format!.UrlColumn, column.Name)).Trim();
            var textName = string.IsNullOrWhiteSpace(format.TextColumn)
                ? column.Name
                : format.TextColumn.Trim();
            var textValue = SourceValue(row, textName, column.Name);
            var textColumn = metadata.TryGetValue(textName, out var known)
                ? known
                : new ColumnInfo(textName, textName, "other", false);
            formats.TryGetValue(textColumn.Name, out var textFormat);
            if (string.Equals(textColumn.Name, column.Name, StringComparison.OrdinalIgnoreCase))
                textFormat = format;
            var text = RenderText(
                textValue,
                textColumn,
                string.Equals(textColumn.Name, column.Name, StringComparison.OrdinalIgnoreCase)
                    ? decimalColumn
                    : HasFraction(textValue),
                textFormat?.Mask);
            if (string.IsNullOrEmpty(text))
                return IsAllowedUrl(url, image: false)
                    ? url
                    : text;
            return Formatted(textValue, text);
        }

        return format?.Mask is null
            ? value
            : Formatted(value, RenderText(value, column, decimalColumn, format.Mask));
    }

    /// <summary>
    /// Marks text rendered from a typed source so the writer exempts it from the formula guard;
    /// text rendered from text stays text.
    /// </summary>
    private static object? Formatted(object? source, string? text)
        => text is null || source is string or char
            ? text
            : new CsvFormattedValue(text);

    private static object? SourceValue(
        IReadOnlyDictionary<string, object?> row,
        string? requested,
        string fallback)
    {
        var name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        return row.TryGetValue(name, out var value) ? value : null;
    }

    private static string? RenderText(
        object? value,
        ColumnInfo column,
        bool decimalColumn,
        string? mask)
    {
        if (value is null) return null;

        if (IsNumber(column) && TryDecimal(value, out var number))
        {
            var masked = FormatCodes.FormatNumber(number, mask);
            if (masked is not null)
                return masked;
            var unmasked = !decimalColumn && decimal.Truncate(number) == number
                ? number.ToString("0", CultureInfo.InvariantCulture)
                : number.ToString("N2", CultureInfo.InvariantCulture);
            return unmasked;
        }

        if (IsDate(column) && TryDate(value, out var date))
        {
            return FormatCodes.FormatDate(date, mask)
                ?? (date.TimeOfDay == TimeSpan.Zero
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        return value switch
        {
            bool boolean => boolean ? "true" : "false",
            string text => text,
            char character => character.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? "",
        };
    }

    private static Presentation ResolvePresentation(
        ReportState? document,
        IReadOnlyDictionary<string, string> configuredLabels)
    {
        var chain = TableChain(document);
        var labels = new Dictionary<string, string>(configuredLabels, StringComparer.OrdinalIgnoreCase);
        var formats = new Dictionary<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase);
        TableComposable? lastShape = null;

        for (var index = 0; index < chain.Count; index++)
        {
            var table = chain[index];
            labels = ProjectLabels(labels, table);
            if (index > 0)
            {
                formats = ProjectFormats(formats, table);
            }

            foreach (var composable in table.Composables ?? [])
            {
                var kind = composable.Kind.Trim();
                if (kind.Equals("group", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("pivot", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("chart", StringComparison.OrdinalIgnoreCase))
                    lastShape = composable;

                if (kind.Equals("labels", StringComparison.OrdinalIgnoreCase)
                    && composable.Labels is { } localLabels)
                {
                    if (localLabels.Count == 0) labels.Clear();
                    foreach (var (name, label) in localLabels)
                        labels[name] = label;
                }
                else if (kind.Equals("formats", StringComparison.OrdinalIgnoreCase)
                         && composable.Formats is { } localFormats)
                {
                    if (localFormats.Count == 0) formats.Clear();
                    foreach (var (name, format) in localFormats)
                        formats[name] = Copy(format);
                }
            }
        }

        return new Presentation(labels, formats, lastShape);
    }

    private static Dictionary<string, string> ProjectLabels(
        IReadOnlyDictionary<string, string> inherited,
        ReportTable table)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Schema ?? [])
        {
            if (inherited.TryGetValue(column.Name, out var same))
            {
                result[column.Name] = same;
                continue;
            }

            var source = LabelSource(table, column);
            if (source is null || !inherited.TryGetValue(source, out var sourceLabel))
                continue;
            result[column.Name] = ReplaceAggregateSource(column.Label, sourceLabel);
        }
        return result;
    }

    private static Dictionary<string, ColumnFormat> ProjectFormats(
        IReadOnlyDictionary<string, ColumnFormat> inherited,
        ReportTable table)
    {
        var result = new Dictionary<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Schema ?? [])
        {
            var source = column.FormatSource ?? column.Name;
            if (!inherited.TryGetValue(source, out var format) || string.IsNullOrWhiteSpace(format.Mask))
                continue;
            result[column.Name] = new ColumnFormat { Mask = format.Mask };
        }
        return result;
    }

    private static string? LabelSource(ReportTable table, ColumnInfo column)
    {
        if (column.FormatSource is not null) return column.FormatSource;
        var shape = (table.Composables ?? []).LastOrDefault(composable =>
        {
            var kind = composable.Kind.Trim();
            return kind.Equals("group", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("pivot", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("chart", StringComparison.OrdinalIgnoreCase);
        });
        if (shape is null) return column.Name;

        if (shape.Kind.Equals("group", StringComparison.OrdinalIgnoreCase))
            return shape.Values?.FirstOrDefault(metric =>
                metric.Id.Equals(column.Name, StringComparison.OrdinalIgnoreCase))?.Col
                ?? column.Name;
        if (shape.Kind.Equals("pivot", StringComparison.OrdinalIgnoreCase)
            && column.PivotMetricId is { } metricId)
            return shape.Values?.FirstOrDefault(metric =>
                metric.Id.Equals(metricId, StringComparison.OrdinalIgnoreCase))?.Col;
        if (shape.Kind.Equals("chart", StringComparison.OrdinalIgnoreCase)
            && table.Schema is { Count: > 1 }
            && column.Name.Equals(table.Schema[1].Name, StringComparison.OrdinalIgnoreCase))
            return shape.Value;
        return column.Name;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> AppendPivotTotals(
        ReportResult result,
        IReadOnlyList<ColumnInfo> columns,
        TableComposable? lastShape)
    {
        if (lastShape is null
            || !lastShape.Kind.Trim().Equals("pivot", StringComparison.OrdinalIgnoreCase)
            || lastShape.Totals is not true
            || result.Aggregates.Count == 0)
            return result.Rows;

        var visible = columns.Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dimensions = (lastShape.Rows ?? [])
            .Where(visible.Contains)
            .ToArray();
        var firstDimension = dimensions.FirstOrDefault();
        var metricFunctions = (lastShape.Values ?? [])
            .ToDictionary(
                metric => metric.Id,
                metric => JsonNamingPolicy.CamelCase.ConvertName(metric.Fn.ToString()),
                StringComparer.OrdinalIgnoreCase);

        var rows = result.Rows.ToList();
        foreach (var function in TotalFunctionOrder)
        {
            var total = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var dimension in dimensions)
                total[dimension] = dimension.Equals(firstDimension, StringComparison.OrdinalIgnoreCase)
                    ? $"{TotalLabel(function)}:"
                    : null;

            var hasValue = false;
            foreach (var column in columns)
            {
                var expected = column.PivotMetricId switch
                {
                    "__count" => "count",
                    { } metric when metricFunctions.TryGetValue(metric, out var name) => name,
                    _ => null,
                };
                if (!string.Equals(expected, function, StringComparison.OrdinalIgnoreCase)
                    || !result.Aggregates.TryGetValue(column.Name, out var byFunction)
                    || !byFunction.TryGetValue(function, out var value))
                    continue;
                total[column.Name] = value;
                hasValue = true;
            }
            if (hasValue) rows.Add(total);
        }
        return rows;
    }

    private static IReadOnlyList<ReportTable> TableChain(ReportState? document)
    {
        if (document?.Tables is not { Count: > 0 } tables
            || string.IsNullOrWhiteSpace(document.ActiveTable))
            return [];

        var lookup = tables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var chain = new List<ReportTable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = document.ActiveTable;
        while (!string.IsNullOrWhiteSpace(current)
               && seen.Add(current)
               && lookup.TryGetValue(current, out var table))
        {
            chain.Add(table);
            if (string.Equals(table.From, "definition", StringComparison.OrdinalIgnoreCase))
                break;
            current = table.From;
        }
        chain.Reverse();
        return chain;
    }

    private static string ReplaceAggregateSource(string label, string sourceLabel)
    {
        var open = label.LastIndexOf('(');
        var close = open < 0 ? -1 : label.IndexOf(')', open + 1);
        return close > open
            ? $"{label[..(open + 1)]}{sourceLabel}{label[close..]}"
            : label;
    }

    private static string TotalLabel(string function) => function switch
    {
        "avg" => "Average",
        "countDistinct" => "Count Distinct",
        _ => char.ToUpperInvariant(function[0]) + function[1..],
    };

    private static ColumnFormat Copy(ColumnFormat source) => new()
    {
        Mask = source.Mask,
        Align = source.Align,
        Bold = source.Bold,
        Italic = source.Italic,
        Fg = source.Fg,
        Bg = source.Bg,
        Classes = source.Classes?.ToList(),
        DisplayAs = source.DisplayAs,
        UrlColumn = source.UrlColumn,
        TextColumn = source.TextColumn,
        Command = source.Command,
        KeyColumn = source.KeyColumn,
    };

    private static bool IsAllowedUrl(string value, bool image)
    {
        if (value.Length == 0 || value.Any(char.IsControl)) return false;
        var colon = value.IndexOf(':');
        var delimiter = value.IndexOfAny(['/', '?', '#']);
        if (colon > 0 && (delimiter < 0 || colon < delimiter))
        {
            var scheme = value[..colon];
            if (!char.IsAsciiLetter(scheme[0])
                || scheme.Skip(1).Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '+' and not '-' and not '.'))
                return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out _)) return false;
            return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || (!image && (scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
                    || scheme.Equals("tel", StringComparison.OrdinalIgnoreCase)));
        }
        return Uri.TryCreate(value, UriKind.Relative, out _);
    }

    private static string RawString(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? "",
    };

    private static bool IsNumber(ColumnInfo column)
        => column.Type.Equals("number", StringComparison.OrdinalIgnoreCase);

    private static bool IsDate(ColumnInfo column)
        => column.Type.Equals("date", StringComparison.OrdinalIgnoreCase);

    private static bool HasFraction(object? value)
        => TryDecimal(value, out var number) && decimal.Truncate(number) != number;

    private static bool TryDecimal(object? value, out decimal number)
    {
        try
        {
            number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
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

    private sealed record Presentation(
        IReadOnlyDictionary<string, string> Labels,
        IReadOnlyDictionary<string, ColumnFormat> Formats,
        TableComposable? Pivot);
}
