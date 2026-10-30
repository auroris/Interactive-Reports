// Self-contained HTML export for the demo server. Presentation semantics and the size
// limit match HtmlReportPresentation in the .NET file-download client.
import { renderCsvTable } from "./presentation.js";

export const maximumHtmlCharacters = 4 * 1024 * 1024;
export class HtmlExportLimitError extends Error {}
const text = value => value == null ? "" : String(value);
const escape = value => text(value).replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]);
const lookup = (row, name) => row?.[Object.keys(row).find(key => key.toLowerCase() === name.toLowerCase())];

// Keep the embedded styles in sync with HtmlReportPresentation.cs.
const styles = `
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
`;

export function renderHtmlReport(title, result) {
    const table = renderCsvTable(result);
    const hasAggregates = values => Object.values(values ?? {}).some(functions => Object.keys(functions).length > 0);
    const hasTotals = hasAggregates(result.aggregates) || (result.breakTotals ?? []).some(group => hasAggregates(group.aggregates));
    const columnCount = Math.max(1, table.columns.length + (hasTotals ? 1 : 0));
    let html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${escape(title)}</title><style>
@page{size:A4 portrait;margin:20mm 15mm}${styles}
</style></head><body><main class="report"><header class="report-header"><h1 id="report-title">${escape(title)}</h1><p class="report-meta">All matching rows · ${result.rows.length} of ${result.totalRows} rows</p></header>`;
    if (result.ignored?.length) html += `<p class="notice">Some saved settings no longer apply: ${escape(result.ignored.map(item => item.detail).join("; "))}</p>`;
    let active = result.document?.activeTable, chart = false;
    const visited = new Set();
    while (active && !visited.has(active.toLowerCase())) {
        visited.add(active.toLowerCase());
        const current = lookup(result.document?.tables, active);
        if (!current) break;
        chart ||= current.composables?.some(item => item.kind?.toLowerCase() === "chart");
        active = current.from;
    }
    html += `<div class="table-wrap"><table aria-labelledby="report-title" style="--column-count:${columnCount}">${chart ? "<caption>Chart data</caption>" : ""}<thead><tr>`;
    if (hasTotals) html += '<th scope="col" class="row-label"><span class="visually-hidden">Summary</span></th>';
    for (const column of table.columns) html += `<th scope="col"${column.type === "number" ? ' class="number"' : ""}>${escape(column.label)}</th>`;
    html += "</tr></thead><tbody>";
    const checkSize = () => { if (html.length > maximumHtmlCharacters) throw new HtmlExportLimitError("This HTML document is too large. Narrow the filters and try again."); };
    function row(values, label = "", total = false, grand = false) {
        html += `<tr class="${grand ? "total grand-total" : total ? "total" : "detail"}">`;
        if (hasTotals) html += total ? `<th scope="row" class="row-label">${escape(label)}</th>` : '<td class="row-label"></td>';
        for (const column of table.columns) html += `<td${column.type === "number" ? ' class="number"' : ""}>${escape(lookup(values, column.name))}</td>`;
        html += "</tr>"; checkSize();
    }
    function totals(values, label, grand = false) {
        for (const fn of new Set(Object.values(values ?? {}).flatMap(Object.keys))) {
            const cells = Object.fromEntries(Object.entries(values).map(([column, functions]) => [column, functions[fn] ?? null]));
            row(renderCsvTable({ ...result, rows: [cells], aggregates: {}, breakTotals: [] }).rows[0], `${label} — ${fn}`, true, grand);
        }
    }
    let previous = null;
    for (let index = 0; index < table.rows.length; index++) {
        const group = (result.breakTotals ?? []).find(item => Object.entries(item.key).every(([key, value]) => text(lookup(result.rows[index], key)) === text(value))) ?? null;
        if (group !== previous) {
            if (previous) totals(previous.aggregates, "Subtotal");
            if (group) html += `<tr class="break"><th colspan="${columnCount}">${escape(Object.entries(group.key).map(([key, value]) => `${table.columns.find(column => column.name.toLowerCase() === key.toLowerCase())?.label ?? key}: ${text(value)}`).join(" · "))}</th></tr>`;
            previous = group;
        }
        row(table.rows[index]);
    }
    if (previous && !result.breakContinues) totals(previous.aggregates, "Subtotal");
    if (!table.rows.length) html += `<tr><td class="empty" colspan="${columnCount}">No matching rows.</td></tr>`;
    totals(result.aggregates, "All matching rows", true);
    html += "</tbody></table></div></main></body></html>"; checkSize(); return html;
}
