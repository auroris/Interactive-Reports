using System.Globalization;
using System.Net;
using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Client.FileDownload;

/// <summary>Renders a self-contained, script-free HTML document from an authorized report result.</summary>
public static class HtmlReportPresentation
{
    public const int MaximumCharacters = 4 * 1024 * 1024;

    // Keep the embedded styles in sync with src/browser-server/html.js.
    private const string Styles = """
        :root { color-scheme: light; }
        * { box-sizing: border-box; }
        body {
            margin: 0; padding: 32px; background: #f1f5f9; color: #243449;
            font: 13px/1.5 "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
        }
        .report {
            max-width: 1440px; margin: 0 auto; padding: 32px;
            background: #fff; border: 1px solid #dce3eb; border-radius: 12px;
            box-shadow: 0 8px 32px #2434490a;
        }
        .report-header { border-top: 3px solid #35658d; padding-top: 20px; margin-bottom: 24px; }
        h1 { margin: 0; color: #17324d; font-size: 28px; line-height: 1.2; letter-spacing: -.025em; overflow-wrap: anywhere; }
        .report-meta { margin: 10px 0 0; color: #526479; font-size: 12px; }
        .notice { margin: 16px 0; padding: 12px 16px; border-left: 3px solid #b07b29; background: #fff9ed; color: #6b4b19; }
        .table-wrap { overflow-x: auto; }
        table { width: 100%; min-width: calc(var(--column-count) * 7rem); border-collapse: separate; border-spacing: 0; table-layout: auto; }
        caption { padding: 0 0 12px; text-align: left; font-size: 14px; font-weight: 600; color: #17324d; }
        /* Bound intrinsic cell widths so one long value cannot squeeze every other column. */
        th, td { max-width: 28ch; padding: 10px 12px; border-bottom: 1px solid #dce3eb; text-align: left; vertical-align: top; overflow-wrap: anywhere; }
        thead th { background: #eaf0f6; color: #17324d; font-size: 12px; font-weight: 650; border-top: 1px solid #c6d3e0; border-bottom: 2px solid #a7bbce; }
        td { white-space: pre-wrap; }
        .detail:nth-child(even) > td { background: #f7f9fc; }
        .number { text-align: right; font-variant-numeric: tabular-nums lining-nums; }
        .row-label { width: 10em; color: #526479; font-size: .9em; }
        .break th { padding-top: 14px; background: #edf3f8; color: #17324d; border-top: 1px solid #a7bbce; font-weight: 650; }
        .total > * { background: #f1f5f9; font-weight: 600; border-top: 1px solid #c6d3e0; }
        .grand-total > * { background: #eaf0f6; color: #17324d; border-top: 3px double #8ca5bc; font-weight: 700; }
        .empty { padding: 32px 12px; text-align: center; color: #526479; font-style: italic; }
        .visually-hidden { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; border: 0; }
        @media screen and (max-width: 640px) {
            body { padding: 12px; }
            .report { padding: 20px 16px; }
            h1 { font-size: 24px; }
            th, td { padding: 8px; }
        }
        @media print {
            body { padding: 0; background: #fff; color: #17212b; font-size: 9pt; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
            .report { max-width: none; margin: 0; padding: 0; border: 0; border-radius: 0; box-shadow: none; }
            .report-header { padding-top: 12px; margin-bottom: 16px; }
            h1 { font-size: 20pt; }
            .report-meta { font-size: 9pt; }
            /* Scroll containers prevent table fragmentation in some PDF renderers. */
            .table-wrap { overflow: visible; }
            table { min-width: 0; font-size: 8.5pt; }
            th, td { max-width: 24ch; padding: 6px 8px; }
            thead th { font-size: 8.5pt; }
            /* Keeping the header group together enables repetition in Chromium. */
            thead { display: table-header-group; break-inside: avoid; page-break-inside: avoid; }
            tbody { break-inside: auto; page-break-inside: auto; }
            tr { break-inside: avoid; page-break-inside: avoid; }
            .report-header, caption, .break { break-after: avoid; page-break-after: avoid; }
            .total { break-before: avoid; page-break-before: avoid; }
            .notice { break-inside: avoid; page-break-inside: avoid; }
        }
        """;

    /// <summary>Applies labels, masks, visible columns, breaks and totals. Charts are rendered as data tables.</summary>
    public static string Render(string title, ReportResult result, string scope = "all", string pageSize = "A4", bool landscape = false)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(result);
        if (scope is not ("all" or "page")) throw new ArgumentOutOfRangeException(nameof(scope));
        if (pageSize is not ("A4" or "Letter" or "Legal")) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var table = CsvReportPresentation.Render(result);
        var hasTotals = result.Aggregates.Values.Any(v => v.Count > 0)
            || result.BreakTotals.Any(b => b.Aggregates.Values.Any(v => v.Count > 0));
        var columnCount = Math.Max(1, table.Columns.Count + (hasTotals ? 1 : 0));
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(E(title)).Append("</title><style>")
            .Append("@page{size:").Append(pageSize).Append(landscape ? " landscape" : " portrait")
            .Append(";margin:20mm 15mm}").Append(Styles)
            .Append("</style></head><body><main class=\"report\"><header class=\"report-header\"><h1 id=\"report-title\">").Append(E(title)).Append("</h1><p class=\"report-meta\">")
            .Append(scope == "all" ? "All matching rows" : $"Current page ({result.Page.Index})")
            .Append(" · ").Append(result.Rows.Count).Append(" of ").Append(result.TotalRows).Append(" rows</p></header>");
        if (result.Ignored.Count > 0)
            html.Append("<p class=\"notice\">Some saved settings no longer apply: ").Append(E(string.Join("; ", result.Ignored.Select(x => x.Detail)))).Append("</p>");
        var isChart = false;
        var active = result.Document?.ActiveTable;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (active is not null && visited.Add(active) && result.Document?.Tables?.TryGetValue(active, out var activeTable) == true)
        {
            isChart |= activeTable.Composables?.Any(c => c.Kind.Equals("chart", StringComparison.OrdinalIgnoreCase)) == true;
            active = activeTable.From;
        }
        html.Append("<div class=\"table-wrap\"><table aria-labelledby=\"report-title\" style=\"--column-count:").Append(columnCount).Append("\">");
        if (isChart) html.Append("<caption>Chart data</caption>");
        html.Append("<thead><tr>");
        if (hasTotals) html.Append("<th scope=\"col\" class=\"row-label\"><span class=\"visually-hidden\">Summary</span></th>");
        foreach (var column in table.Columns)
            html.Append(column.Type == "number" ? "<th scope=\"col\" class=\"number\">" : "<th scope=\"col\">").Append(E(column.Label)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        BreakTotal? previous = null;
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var source = index < result.Rows.Count ? result.Rows[index] : null;
            var group = source is null ? null : result.BreakTotals.FirstOrDefault(b => b.Key.All(k => source.TryGetValue(k.Key, out var value) && Text(value) == Text(k.Value)));
            if (group != previous)
            {
                if (previous is not null) Totals(previous.Aggregates, "Subtotal");
                if (group is not null) html.Append("<tr class=\"break\"><th colspan=\"").Append(columnCount).Append("\">")
                    .Append(E(string.Join(" · ", group.Key.Select(k => $"{table.Columns.FirstOrDefault(c => c.Name.Equals(k.Key, StringComparison.OrdinalIgnoreCase))?.Label ?? k.Key}: {Text(k.Value)}"))))
                    .Append("</th></tr>");
                previous = group;
            }
            Row(table.Rows[index]);
        }
        if (previous is not null && !result.BreakContinues) Totals(previous.Aggregates, "Subtotal");
        if (table.Rows.Count == 0) html.Append("<tr><td class=\"empty\" colspan=\"").Append(columnCount).Append("\">No matching rows.</td></tr>");
        if (table.Rows.Count == result.Rows.Count) Totals(result.Aggregates, "All matching rows", grand: true);
        html.Append("</tbody></table></div></main></body></html>");
        CheckSize();
        return html.ToString();

        void CheckSize()
        {
            if (html.Length > MaximumCharacters) throw new HtmlReportSizeLimitException();
        }

        void Row(IReadOnlyDictionary<string, object?> values, string label = "", bool total = false, bool grand = false)
        {
            html.Append(grand ? "<tr class=\"total grand-total\">" : total ? "<tr class=\"total\">" : "<tr class=\"detail\">");
            if (hasTotals)
                html.Append(total ? "<th scope=\"row\" class=\"row-label\">" : "<td class=\"row-label\">")
                    .Append(E(label)).Append(total ? "</th>" : "</td>");
            foreach (var column in table.Columns)
            {
                values.TryGetValue(column.Name, out var value);
                html.Append(column.Type == "number" ? "<td class=\"number\">" : "<td>").Append(E(Text(value))).Append("</td>");
            }
            html.Append("</tr>");
            CheckSize();
        }

        void Totals(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> values, string label, bool grand = false)
        {
            foreach (var fn in values.Values.SelectMany(v => v.Keys).Distinct())
            {
                var row = values.ToDictionary(v => v.Key, v => v.Value.GetValueOrDefault(fn), StringComparer.OrdinalIgnoreCase);
                // Use the same masks and labels as detail cells, without manufacturing pivot totals again.
                var projected = CsvReportPresentation.Render(new ReportResult {
                    Columns = result.Columns, AvailableColumns = result.AvailableColumns, ConfiguredLabels = result.ConfiguredLabels,
                    Document = result.Document, Rows = [row], Page = result.Page, TotalRows = 1, Ignored = [],
                });
                Row(projected.Rows[0], $"{label} — {fn}", total: true, grand: grand);
            }
        }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
    private static string Text(object? value) => value switch {
        null => "", CsvFormattedValue formatted => formatted.Text,
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "",
    };
}

/// <summary>The complete HTML document exceeds the supported export size.</summary>
public sealed class HtmlReportSizeLimitException()
    : Exception("This HTML document is too large. Narrow the filters and try again.");
