// Table-shaping dialogs: pagination, sort order, control breaks, and aggregate
// rows. Every operation follows the same active terminal-table context.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { stageContext } from "../stage.js";
import {
    aggregateRowList,
    colOptions,
    dirOptions,
    nullsOptions,
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
        { value: "0", label: w.t("common.all") },
    ], String(current));
    limit.setAttribute("aria-label", w.t("pagination.limit"));

    openDialog({
        owner: w,
        title: w.t("pagination.title"),
        width: "20rem",
        build: body => body.append(
            labeled(w.t("pagination.limit"), limit),
            el("p", { class: "ir-dialog-note" }, w.t("pagination.allNote"))),
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
        const colSel = sel(colOptions(w, { none: w.t("common.select"), columns: ctx.sortColumns }), item?.col ?? "");
        const dirSel = sel(dirOptions(w), item?.dir ?? "asc");
        const nullsSel = sel(nullsOptions(w), item?.nulls ?? "");
        row.append(
            rowField(w.t("common.column"), colSel),
            rowField(w.t("common.direction"), dirSel),
            rowField(w.t("sort.nullSorting"), nullsSel));
        row._read = () => colSel.value ? {
            col: colSel.value,
            dir: dirSel.value,
            ...(nullsSel.value ? { nulls: nullsSel.value } : {}),
        } : null;
    }, { addLabel: w.t("sort.add"), max: 6, context: w });

    openDialog({
        owner: w,
        title: w.t("sort.title"),
        width: "38rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, w.t("sort.breakFirst"))),
        onApply: () => w.apply(d => { layerOf(d).sorts = list.read(); }),
    });
}

export function breakDialog(w) {
    const ctx = stageContext(w);
    const layerOf = ctx.columnsLayer;
    const container = el("div", {});
    const list = rowList(container, (layerOf(w.doc).breaks ?? []).map(b => ({ col: b })), (row, item) => {
        // Breaks force sorting, so a definition sort restriction removes the
        // column here too.
        const colSel = sel(colOptions(w, { none: w.t("common.select"), columns: ctx.sortColumns }), item?.col ?? "");
        row.append(rowField(w.t("common.column"), colSel));
        row._read = () => colSel.value || null;
    }, { addLabel: w.t("break.addColumn"), max: 3, context: w });

    openDialog({
        owner: w,
        title: w.t("break.title"),
        width: "24rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, w.t("break.note"))),
        onApply: () => w.apply(d => {
            layerOf(d).breaks = [...new Set(list.read())];
        }),
    });
}

export function aggregateDialog(w) {
    const ctx = stageContext(w);
    const layerOf = ctx.columnsLayer;
    const { container, list } = aggregateRowList(
        w,
        layerOf(w.doc).aggregates ?? [],
        { addLabel: w.t("aggregate.title"), columns: ctx.columns });

    openDialog({
        owner: w,
        title: w.t("aggregate.title"),
        width: "28rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, w.t("aggregate.note"))),
        onApply: () => w.apply(d => { layerOf(d).aggregates = list.read(); }),
    });
}
