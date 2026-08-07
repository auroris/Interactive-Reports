// Grid-shaping dialogs: sort order, control breaks, and aggregate rows.

import { el, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { rowList, colOptions, fnSelectFor, DIR_OPTIONS } from "./parts.js";

export function sortDialog(w) {
    const container = el("div", {});
    const list = rowList(container, w.doc.sorts ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        const dirSel = sel(DIR_OPTIONS, item?.dir ?? "asc");
        row.append(colSel, dirSel);
        row._read = () => colSel.value ? { col: colSel.value, dir: dirSel.value } : null;
    }, { addLabel: "Sort", max: 6 });

    openDialog({
        owner: w,
        title: "Sort",
        width: "26rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Control-break columns always sort first.")),
        onApply: () => w.apply(d => { d.sorts = list.read(); }),
    });
}

export function breakDialog(w) {
    const container = el("div", {});
    const list = rowList(container, (w.doc.breaks ?? []).map(b => ({ col: b })), (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        row.append(colSel);
        row._read = () => colSel.value || null;
    }, { addLabel: "Break Column", max: 3 });

    openDialog({
        owner: w,
        title: "Control Break",
        width: "24rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Rows group under a heading per break value; aggregates subtotal per group.")),
        onApply: () => w.apply(d => {
            d.breaks = [...new Set(list.read())];
        }),
    });
}

export function aggregateDialog(w) {
    const container = el("div", {});
    const list = rowList(container, w.doc.aggregates ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        const fnSel = fnSelectFor(w, colSel, item?.fn);
        row.append(fnSel, el("span", { class: "ir-row-of" }, "of"), colSel);
        row._read = () => colSel.value && fnSel.value ? { col: colSel.value, fn: fnSel.value } : null;
    }, { addLabel: "Aggregate" });

    openDialog({
        owner: w,
        title: "Aggregate",
        width: "28rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Computed over the whole filtered set — grand total and per-break subtotals.")),
        onApply: () => w.apply(d => { d.aggregates = list.read(); }),
    });
}
