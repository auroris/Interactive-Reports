// Rendering for the report widget: settings chips, the grid (control breaks,
// aggregate rows, highlights), and the pagination bar. Pure DOM out of the
// widget's state — no fetching in here.

import { el, icon } from "./ir-ui.js";

// --- value formatting --------------------------------------------------------

/// decimal: the column is known to carry fractional values, so whole numbers in it
/// still format as decimals (14474 → 14,474.00) instead of looking like ids.
export function formatValue(v, type, decimal = false) {
    if (v === null || v === undefined) return "";
    if (typeof v === "boolean") return v ? "true" : "false";
    if (type === "number" && typeof v === "number") {
        if (!decimal && Number.isInteger(v)) return String(v);
        return v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }
    if (type === "date") {
        const s = String(v);
        return s.endsWith("T00:00:00") ? s.slice(0, 10) : s.replace("T", " ");
    }
    return String(v);
}

function formatAgg(v) {
    if (v === null || v === undefined) return "—";
    return typeof v === "number" ? v.toLocaleString(undefined, { maximumFractionDigits: 2 }) : String(v);
}

export const FN_LABELS = {
    sum: "Sum", avg: "Avg", min: "Min", max: "Max",
    count: "Count", countDistinct: "Count Distinct",
};
const FN_ORDER = ["sum", "avg", "min", "max", "count", "countDistinct"];

// --- settings chips ----------------------------------------------------------

function chip({ w, kind, index, text, colLabel, off, toggleable = true, removable = true, swatch }) {
    const node = el("span", { class: "ir-chip" + (off ? " ir-chip-off" : ""), dataset: { kind } });
    if (toggleable) {
        node.append(el("input", {
            type: "checkbox", class: "ir-chip-check", checked: !off,
            title: off ? "Enable" : "Disable",
            onchange: e => w.chipToggle(kind, index, e.target.checked),
        }));
    }
    if (swatch) node.append(el("span", { class: "ir-chip-swatch", style: { background: swatch } }));
    const label = el("button", {
        type: "button", class: "ir-chip-label", title: "Edit",
        onclick: () => w.chipEdit(kind, index),
    });
    if (colLabel) label.append(el("b", {}, colLabel), " ");
    label.append(text);
    node.append(label);
    if (removable) {
        node.append(el("button", {
            type: "button", class: "ir-chip-x", "aria-label": "Remove", title: "Remove",
            onclick: () => w.chipRemove(kind, index),
        }, icon("close")));
    }
    return node;
}

export function renderChips(w, container) {
    const d = w.doc;
    const chips = [];

    if (d.search) {
        chips.push(chip({ w, kind: "search", index: 0, toggleable: false, colLabel: "Search", text: `'${d.search}'` }));
    }
    (d.filters ?? []).forEach((f, i) =>
        chips.push(chip({ w, kind: "filter", index: i, off: f.enabled === false, colLabel: "Filter", text: f.expr })));
    (d.breaks ?? []).forEach((b, i) =>
        chips.push(chip({ w, kind: "break", index: i, toggleable: false, colLabel: "Break", text: w.labelOf(b) })));
    (d.aggregates ?? []).forEach((a, i) =>
        chips.push(chip({ w, kind: "aggregate", index: i, toggleable: false, colLabel: "Σ", text: `${FN_LABELS[a.fn] ?? a.fn} of ${w.labelOf(a.col)}` })));
    (d.computed ?? []).forEach((c, i) =>
        chips.push(chip({ w, kind: "computed", index: i, off: c.enabled === false, colLabel: "ƒ", text: c.label ?? c.id })));
    (d.highlights ?? []).forEach((h, i) =>
        chips.push(chip({
            w, kind: "highlight", index: i, off: h.enabled === false,
            swatch: h.style?.bg ?? "#fff3a0",
            colLabel: "Highlight",
            text: h.expr + (h.scope === "cell" ? ` (${w.labelOf(h.col)} cell)` : " (row)"),
        })));
    if (d.view?.mode === "groupBy") {
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false, colLabel: "Group by",
            text: (d.view.groupBy ?? []).map(c => w.labelOf(c)).join(", "),
        }));
    } else if (d.view?.mode === "pivot") {
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false, colLabel: "Pivot",
            text: `${(d.view.rows ?? []).map(c => w.labelOf(c)).join(", ")} × ${(d.view.cols ?? []).map(c => w.labelOf(c)).join(", ")}`,
        }));
    }

    container.replaceChildren(...chips);
    container.hidden = chips.length === 0;
}

// --- grid --------------------------------------------------------------------

export function renderGrid(w, table) {
    const result = w.lastResult;
    if (!result) { table.replaceChildren(); return; }
    const mode = w.doc.view?.mode ?? "grid";
    const columns = result.columns;

    // Header. Sort indicators come from the state doc; menus depend on the view mode.
    const sortOrd = new Map((w.doc.sorts ?? []).map((s, i) => [s.col, { dir: s.dir ?? "asc", ord: i + 1 }]));
    const dims = mode === "groupBy" ? new Set(w.doc.view?.groupBy ?? []) : null;
    const headRow = el("tr", {});
    for (const col of columns) {
        const interactive = mode === "grid" || (mode === "groupBy" && dims.has(col.name));
        const s = sortOrd.get(col.name);
        const inner = el("span", { class: "ir-th-inner" }, col.label);
        if (s) {
            inner.append(el("span", { class: "ir-sort-dir", "aria-hidden": "true" }, s.dir === "desc" ? "▼" : "▲"));
            if ((w.doc.sorts ?? []).length > 1) inner.append(el("span", { class: "ir-sort-ord" }, String(s.ord)));
        }
        const th = el("th", {
            class: (col.type === "number" ? "ir-num " : "") + (interactive ? "ir-th-menu" : ""),
            scope: "col",
            "aria-sort": s ? (s.dir === "desc" ? "descending" : "ascending") : undefined,
        }, inner);
        if (interactive) th.onclick = () => w.openHeaderMenu(col.name, th);
        headRow.append(th);
    }

    // Body: data rows with break groups, per-group aggregate rows, highlights.
    const breaks = mode === "grid" ? (w.doc.breaks ?? []) : [];
    const keyOf = source => breaks.map(b => String(source[b] ?? "")).join("");
    const totalsByKey = new Map((result.breakTotals ?? []).map(bt => [keyOf(bt.key), bt]));

    // Columns whose page values include fractions format uniformly as decimals.
    const decimalCols = new Set(columns
        .filter(c => c.type === "number"
            && result.rows.some(r => typeof r[c.name] === "number" && !Number.isInteger(r[c.name])))
        .map(c => c.name));

    const styleById = new Map((w.doc.highlights ?? []).map(h => [h.id, h.style ?? {}]));
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
                const td = el("td", { class: col.type === "number" ? "ir-num" : "" });
                if (idx === 0) {
                    td.append(el("span", { class: "ir-agg-fn" }, `${FN_LABELS[fn] ?? fn}:`));
                    if (has) td.append(" ", formatAgg(aggregates[col.name][fn]));
                } else if (has) {
                    td.textContent = formatAgg(aggregates[col.name][fn]);
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
                const label = breaks.map(b => `${w.labelOf(b)}: ${row[b] ?? "(blank)"}`).join("  ·  ");
                bodyRows.push(el("tr", { class: "ir-break-header" },
                    el("td", { colSpan: columns.length },
                        el("span", {}, label),
                        bt ? el("span", { class: "ir-break-count" }, `${Number(bt.rows).toLocaleString()} rows`) : null)));
                currentKey = key;
            }
        }
        const tr = el("tr", { class: "ir-row" });
        for (const col of columns) {
            const cls = [col.type === "number" ? "ir-num" : "", col.type === "date" ? "ir-date" : ""].join(" ").trim();
            tr.append(el("td", { class: cls || undefined }, formatValue(row[col.name], col.type, decimalCols.has(col.name))));
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
        bodyRows.push(tr);
    }
    closeGroup();

    // Report-level aggregate rows (whole filtered set, never just the page).
    if (Object.keys(result.aggregates ?? {}).length)
        bodyRows.push(...aggRows(result.aggregates, "ir-grand-total"));

    if (!result.rows.length)
        bodyRows.push(el("tr", { class: "ir-empty" },
            el("td", { colSpan: Math.max(columns.length, 1) }, "No data found.")));

    table.replaceChildren(el("thead", {}, headRow), el("tbody", {}, ...bodyRows));
}

// --- pagination --------------------------------------------------------------

export function renderPager(w, container) {
    const result = w.lastResult;
    if (!result) { container.replaceChildren(); return; }

    const { index, size } = result.page;
    const total = result.totalRows;
    const unit = (w.doc.view?.mode ?? "grid") === "groupBy" ? "groups" : "rows";
    const start = total === 0 ? 0 : (index - 1) * size + 1;
    const end = total === 0 ? 0 : start + result.rows.length - 1;
    const pages = Math.max(1, Math.ceil(total / size));

    const sizes = [...new Set([15, 25, 50, 100, size])]
        .filter(s => s <= (w.schema?.limits?.maxPageSize ?? Infinity))
        .sort((a, b) => a - b);
    const sizeSel = el("select", { class: "ir-select ir-pagesize", title: "Rows per page" },
        ...sizes.map(s => new Option(String(s), String(s))));
    sizeSel.value = String(size);
    sizeSel.onchange = () => w.setPageSize(Number(sizeSel.value));

    container.replaceChildren(
        el("div", { class: "ir-pager-left" },
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: index <= 1,
                "aria-label": "Previous page", onclick: () => w.gotoPage(index - 1),
            }, "‹"),
            el("span", { class: "ir-page-info" },
                total === 0 ? `0 ${unit}`
                    : `${start.toLocaleString()} – ${end.toLocaleString()} of ${Number(total).toLocaleString()} ${unit}`),
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: index >= pages,
                "aria-label": "Next page", onclick: () => w.gotoPage(index + 1),
            }, "›"),
            el("span", { class: "ir-pagesize-wrap" }, "Rows ", sizeSel)),
        el("div", { class: "ir-pager-right" }, `${result.elapsedMs} ms`));
}
