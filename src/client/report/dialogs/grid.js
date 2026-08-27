// Table-shaping dialogs: pagination, sort order, control breaks, and aggregate
// rows. Sort follows the stage context — the source table in grid, the group
// stage under a group/spread tail (pivot restricted to row dimensions). Breaks
// and aggregates are source-stage features (grid only at T0).

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { sortableColumns } from "../schema.js";
import { stageContext } from "../stage.js";
import { sourceLayer } from "../state.js";
import {
    aggregateRowList,
    colOptions,
    DIR_OPTIONS,
    NULLS_OPTIONS,
    rowField,
    rowList,
} from "./parts.js";

const PAGE_LIMITS = [10, 50, 100, 500, 1000];

export function paginationDialog(w) {
    const current = w.lastResult?.page?.size ?? w.doc.page?.size ?? w.schema?.limits?.defaultPageSize ?? 50;
    const max = w.schema?.limits?.maxPageSize ?? 1000;
    const numeric = PAGE_LIMITS.filter(size => size <= max);
    // Preserve a developer-defined/default size that is outside the APEX choices.
    // It remains selectable until the user deliberately replaces it.
    if (current > 0 && current <= max && !numeric.includes(current)) numeric.push(current);
    numeric.sort((a, b) => a - b);
    const limit = sel([
        ...numeric.map(size => ({ value: String(size), label: String(size) })),
        { value: "0", label: "All" },
    ], String(current));
    limit.setAttribute("aria-label", "Limit");

    openDialog({
        owner: w,
        title: "Pagination",
        width: "20rem",
        build: body => body.append(
            labeled("Limit", limit),
            el("p", { class: "ir-dialog-note" }, "All returns every matching row in one page.")),
        onApply: () => w.apply(d => {
            d.page ??= { index: 1, size: current };
            d.page.size = Number(limit.value);
        }),
    });
}

export function sortDialog(w) {
    const ctx = stageContext(w);
    const layerOf = ctx.sortLayer;
    const container = el("div", {});
    const list = rowList(container, layerOf(w.doc).sorts ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —", columns: ctx.sortColumns }), item?.col ?? "");
        const dirSel = sel(DIR_OPTIONS, item?.dir ?? "asc");
        const nullsSel = sel(NULLS_OPTIONS, item?.nulls ?? "");
        row.append(
            rowField("Column", colSel),
            rowField("Direction", dirSel),
            rowField("Null Sorting", nullsSel));
        row._read = () => colSel.value ? {
            col: colSel.value,
            dir: dirSel.value,
            ...(nullsSel.value ? { nulls: nullsSel.value } : {}),
        } : null;
    }, { addLabel: "Sort", max: 6 });

    const note = ctx.mode === "grid"
        ? "Control-break columns always sort first."
        : ctx.mode === "pivot"
            ? "A pivot orders by its row dimensions."
            : "Remaining group columns keep the order deterministic.";

    openDialog({
        owner: w,
        title: "Sort",
        width: "38rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, note)),
        onApply: () => w.apply(d => { layerOf(d).sorts = list.read(); }),
    });
}

export function breakDialog(w) {
    const container = el("div", {});
    const list = rowList(container, (sourceLayer(w.doc).breaks ?? []).map(b => ({ col: b })), (row, item) => {
        // Breaks force sorting, so a definition sort restriction removes the
        // column here too.
        const colSel = sel(colOptions(w, { none: "— Select —", columns: sortableColumns(w) }), item?.col ?? "");
        row.append(rowField("Column", colSel));
        row._read = () => colSel.value || null;
    }, { addLabel: "Break Column", max: 3 });

    openDialog({
        owner: w,
        title: "Control Break",
        width: "24rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Rows group under a heading per break value; aggregates subtotal per group.")),
        onApply: () => w.apply(d => {
            sourceLayer(d).breaks = [...new Set(list.read())];
        }),
    });
}

export function aggregateDialog(w) {
    const { container, list } = aggregateRowList(
        w, sourceLayer(w.doc).aggregates ?? [], { addLabel: "Aggregate" });

    openDialog({
        owner: w,
        title: "Aggregate",
        width: "28rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Computed over the whole filtered set — grand total and per-break subtotals.")),
        onApply: () => w.apply(d => { sourceLayer(d).aggregates = list.read(); }),
    });
}
