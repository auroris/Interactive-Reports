// The grid: header with sort indicators and per-column menus, data rows with
// control-break groups and per-group aggregate rows, highlights, grand totals.
// Pure DOM out of the widget's state — no fetching in here.

import { el } from "../../core/dom.js";
import { labelOf } from "../schema.js";
import { stageContext } from "../stage.js";
import { modeOf, sourceLayer } from "../state.js";
import { formatAgg, formatInteger, hasFraction, parseReportNumber, FN_LABELS, FN_ORDER } from "./format.js";
import { formatForColumn, renderColumnValue } from "./column-renderers.js";
import { headerMenuAvailable, openHeaderMenu } from "../menus.js";
import { columnClasses } from "../classes.js";

export function renderGrid(w, table) {
    const result = w.lastResult;
    if (!result) { table.replaceChildren(); return; }
    const ctx = stageContext(w);
    const mode = ctx.mode;
    const requestedBreaks = mode === "grid" ? (sourceLayer(w.doc).breaks ?? []) : [];
    const breaks = requestedBreaks.map(name =>
        result.columns.find(column => column.name.toLowerCase() === name.toLowerCase())?.name ?? name);
    const breakNames = new Set(breaks);
    // A control-break dimension lives in the heading, not in every detail row.
    const columns = mode === "grid"
        ? result.columns.filter(column => !breakNames.has(column.name))
        : result.columns;

    // Per-column display settings from the doc's formats map. Styles go inline on
    // the cells; highlights are applied later and deliberately win over them.
    const formatFor = col => formatForColumn(w, col);
    const classesFor = (col, ...builtIn) => [...builtIn, ...columnClasses(formatFor(col)?.classes)]
        .filter(Boolean).join(" ") || undefined;
    const alignStyle = col => formatFor(col)?.align ? { textAlign: formatFor(col).align } : undefined;
    const formatStyle = fmt => {
        if (!fmt) return undefined;
        const style = {};
        if (fmt.align) style.textAlign = fmt.align;
        if (fmt.bold) style.fontWeight = "600";
        if (fmt.italic) style.fontStyle = "italic";
        if (fmt.fg) style.color = fmt.fg;
        if (fmt.bg) style.background = fmt.bg;
        return Object.keys(style).length ? style : undefined;
    };

    // Labels resolve client-side: the current stage's universe already layered
    // its own labels over source labels and rebuilt synthetic metric captions,
    // so the response's neutral label is only the last resort.
    const stageColumnByName = new Map(ctx.columns.map(c => [c.name.toLowerCase(), c]));
    const displayLabel = col =>
        stageColumnByName.get(col.name.toLowerCase())?.label ?? col.label;

    // Header. Sort indicators come from the stage that owns ordering: the source
    // layer in grid, the group layer under a group or spread tail.
    const activeSorts = ctx.sortLayer ? (ctx.sortLayer(w.doc).sorts ?? []) : [];
    const sortOrd = new Map(activeSorts.map((s, i) => [s.col.toLowerCase(), { dir: s.dir ?? "asc", ord: i + 1 }]));
    const menuAvailable = headerMenuAvailable(w, mode);
    const headRow = el("tr", {});
    for (const col of columns) {
        const interactive = menuAvailable && mode !== "chart";
        const s = sortOrd.get(col.name.toLowerCase());
        const inner = el("span", { class: "ir-th-inner" }, displayLabel(col));
        if (s) {
            inner.append(el("span", { class: "ir-sort-dir", "aria-hidden": "true" }, s.dir === "desc" ? "▼" : "▲"));
            if (activeSorts.length > 1) inner.append(el("span", { class: "ir-sort-ord" }, String(s.ord)));
        }
        const th = el("th", {
            class: classesFor(col, col.type === "number" ? "ir-num" : "", interactive ? "ir-th-menu" : ""),
            scope: "col",
            style: alignStyle(col),
            "aria-sort": s ? (s.dir === "desc" ? "descending" : "ascending") : undefined,
        }, inner);
        if (interactive) th.onclick = () => openHeaderMenu(w, col.name, th);
        headRow.append(th);
    }

    // Body: data rows with break groups, per-group aggregate rows, highlights.
    const keyOf = source => JSON.stringify(breaks.map(b => source[b] ?? null));
    const totalsByKey = new Map((result.breakTotals ?? []).map(bt => [keyOf(bt.key), bt]));

    // Columns whose page values include fractions format uniformly as decimals.
    const decimalCols = new Set(columns
        .filter(c => c.type === "number"
            && result.rows.some(r => hasFraction(r[c.name])))
        .map(c => c.name));

    // Highlight styles belong to whichever layer decorated this table: the
    // source layer in grid, the group layer when the group stage is terminal.
    const activeHighlights = ctx.highlightLayer ? (ctx.highlightLayer(w.doc).highlights ?? []) : [];
    const styleById = new Map(activeHighlights.map(h => [h.id, h.style ?? {}]));
    const hitsByRow = new Map();
    for (const h of result.highlights ?? []) {
        if (!hitsByRow.has(h.row)) hitsByRow.set(h.row, []);
        hitsByRow.get(h.row).push(h);
    }

    const aggRows = (aggregates, cls) => {
        const rows = [];
        const fns = FN_ORDER.filter(fn => Object.values(aggregates ?? {}).some(byFn => fn in byFn));
        for (const fn of fns) {
            const tr = el("tr", { class: cls });
            columns.forEach((col, idx) => {
                const has = aggregates[col.name] && fn in aggregates[col.name];
                const fmt = formatFor(col);
                const isCount = fn === "count" || fn === "countDistinct";
                const aggregateType = isCount || fn === "sum" || fn === "avg" || fn === "median" ? "number" : col.type;
                const aggregateMask = isCount ? null : fmt?.mask;
                const td = el("td", {
                    class: classesFor(col, col.type === "number" ? "ir-num" : ""),
                    style: alignStyle(col),
                });
                if (idx === 0) {
                    td.append(el("span", { class: "ir-agg-fn" }, `${FN_LABELS[fn] ?? fn}:`));
                    if (has) td.append(" ", formatAgg(aggregates[col.name][fn], aggregateType, aggregateMask));
                } else if (has) {
                    td.textContent = formatAgg(aggregates[col.name][fn], aggregateType, aggregateMask);
                }
                tr.append(td);
            });
            rows.push(tr);
        }
        return rows;
    };

    const bodyRows = [];
    let currentKey = null;
    const closeGroup = () => {
        if (currentKey === null) return;
        const bt = totalsByKey.get(currentKey);
        if (bt && Object.keys(bt.aggregates ?? {}).length) bodyRows.push(...aggRows(bt.aggregates, "ir-break-total"));
    };

    for (const [r, row] of result.rows.entries()) {
        if (breaks.length) {
            const key = keyOf(row);
            if (key !== currentKey) {
                closeGroup();
                const bt = totalsByKey.get(key);
                const label = breaks.map(b => `${labelOf(w, b)}: ${row[b] ?? "(blank)"}`).join("  ·  ");
                bodyRows.push(el("tr", { class: "ir-break-header" },
                    el("td", { colSpan: Math.max(columns.length, 1) },
                        el("span", {}, label),
                        bt ? el("span", { class: "ir-break-count" }, `${formatInteger(bt.rows)} rows`) : null)));
                currentKey = key;
            }
        }
        const tr = el("tr", { class: "ir-row" });
        for (const col of columns) {
            const fmt = formatFor(col);
            const cls = classesFor(
                col,
                col.type === "number" ? "ir-num" : "",
                col.type === "date" ? "ir-date" : "");
            tr.append(el("td", { class: cls, style: formatStyle(fmt) },
                renderColumnValue(w, row, col, decimalCols.has(col.name), fmt, mode === "grid")));
        }
        const rowHits = (hitsByRow.get(r) ?? []).filter(hit => !hit.col);
        const cellHits = (hitsByRow.get(r) ?? []).filter(hit => !!hit.col);
        for (const hit of [...rowHits, ...cellHits]) {
            const style = styleById.get(hit.id) ?? {};
            if (!hit.col) {
                if (style.bg) tr.style.background = style.bg;
                if (style.fg) tr.style.color = style.fg;
            } else {
                const idx = columns.findIndex(c => c.name === hit.col);
                if (idx >= 0) {
                    if (style.bg) tr.children[idx].style.background = style.bg;
                    if (style.fg) tr.children[idx].style.color = style.fg;
                }
            }
        }
        if (columns.length) bodyRows.push(tr);
    }
    if (!result.breakContinues) closeGroup();

    // Report-level aggregates describe the whole filtered set, so render them only
    // where that set logically ends, never at the end of an intermediate page.
    const total = parseReportNumber(result.totalRows) ?? parseReportNumber(0);
    const { index = 1, size = 0 } = result.page ?? {};
    const atLogicalEnd = total.eq(0)
        || size === 0
        || (result.rows.length > 0 && parseReportNumber(index).times(size).gte(total));
    if (atLogicalEnd && Object.keys(result.aggregates ?? {}).length)
        bodyRows.push(...aggRows(result.aggregates, "ir-grand-total"));

    if (!result.rows.length)
        bodyRows.push(el("tr", { class: "ir-empty" },
            el("td", { colSpan: Math.max(columns.length, 1) }, "No data found.")));

    table.replaceChildren(el("thead", {}, headRow), el("tbody", {}, ...bodyRows));
}
