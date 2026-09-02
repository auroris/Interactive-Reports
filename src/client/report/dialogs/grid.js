// Table-shaping dialogs: sort order, control breaks, and aggregate rows. Every operation follows
// the same active terminal-table context. (Page size is a submenu, not a dialog; see page-size.js.)

import { el, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { tableContext } from "../table.js";
import {
    aggregateRowList,
    colOptions,
    dirOptions,
    nullsOptions,
    rowField,
    rowList,
} from "./parts.js";

/**
 * Opens the active table's ordered multi-column sort editor.
 *
 * @param {object} w - The report controller whose active table supplies sort columns and existing terms.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; applying it replaces the active table's sort terms and runs the report.
 */
export function sortDialog(w) {
    const ctx = tableContext(w);
    const container = el("div", {});
    const list = rowList(container, ctx.node(w.doc, "sort")?.sorts ?? [], (row, item) => {
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
        onApply: () => w.apply(d => ctx.edit(d, "sort", node => { node.sorts = list.read(); })),
    });
}

/**
 * Opens the active table's ordered control-break column editor.
 *
 * @param {object} w - The report controller whose active table supplies sortable columns and existing breaks.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; applying it stores unique break columns and runs the report.
 */
export function breakDialog(w) {
    const ctx = tableContext(w);
    const container = el("div", {});
    const list = rowList(container, (ctx.node(w.doc, "break")?.breaks ?? []).map(b => ({ col: b })), (row, item) => {
        // Breaks force sorting, so a definition sort restriction removes the column here too.
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
        onApply: () => w.apply(d => ctx.edit(d, "break", node => {
            node.breaks = [...new Set(list.read())];
        })),
    });
}

/**
 * Opens the active table's footer-aggregate editor.
 *
 * @param {object} w - The report controller whose active table supplies result columns and existing aggregate rules.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; applying it replaces footer aggregates and runs the report.
 */
export function aggregateDialog(w) {
    const ctx = tableContext(w);
    const { container, list } = aggregateRowList(
        w,
        ctx.node(w.doc, "aggregate")?.aggregates ?? [],
        { addLabel: w.t("aggregate.title"), columns: ctx.columns });

    openDialog({
        owner: w,
        title: w.t("aggregate.title"),
        width: "28rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, w.t("aggregate.note"))),
        onApply: () => w.apply(d => ctx.edit(d, "aggregate", node => { node.aggregates = list.read(); })),
    });
}
