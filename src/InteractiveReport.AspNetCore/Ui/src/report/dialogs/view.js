// View-mode dialogs: Group By, Pivot, and Chart. Each applies a full view spec
// to the doc and records it in the widget's viewMemory so the toolbar can
// switch back to the mode without re-asking.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { pickable, typeOf, chartFnsFor } from "../schema.js";
import { rowList, colOptions, fnSelectFor, DIR_OPTIONS } from "./parts.js";
import { FN_LABELS } from "../render/format.js";

/// Open the configuration dialog for a non-grid view mode. Toolbar switches and
/// the view chip both land here.
export function openViewDialog(w, mode) {
    if (mode === "groupBy") groupByDialog(w);
    else if (mode === "pivot") pivotDialog(w);
    else chartDialog(w);
}

// --- Group By / Pivot --------------------------------------------------------

function dimList(w, initial, { addLabel, max }) {
    const container = el("div", {});
    const list = rowList(container, (initial ?? []).map(c => ({ col: c })), (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        row.append(colSel);
        row._read = () => colSel.value || null;
    }, { addLabel, max });
    return { container, list };
}

function valueList(w, initial) {
    const container = el("div", {});
    const list = rowList(container, initial ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        const fnSel = fnSelectFor(w, colSel, item?.fn);
        row.append(fnSel, el("span", { class: "ir-row-of" }, "of"), colSel);
        row._read = () => colSel.value && fnSel.value ? { col: colSel.value, fn: fnSel.value } : null;
    }, { addLabel: "Value" });
    return { container, list };
}

export function groupByDialog(w) {
    const active = w.doc.view?.mode === "groupBy" ? w.doc.view : w.viewMemory.groupBy;
    const dims = dimList(w, active?.groupBy, { addLabel: "Group Column", max: 3 });
    const values = valueList(w, active?.values);

    openDialog({
        owner: w,
        title: "Group By",
        width: "30rem",
        build: body => body.append(
            el("div", { class: "ir-field-label" }, "Group by"),
            dims.container, dims.list.addButton,
            el("div", { class: "ir-field-label ir-gap-above" }, "Aggregate values"),
            values.container, values.list.addButton,
            el("p", { class: "ir-dialog-note" }, "A row count per group is always included.")),
        onApply: () => {
            const groupBy = [...new Set(dims.list.read())];
            if (!groupBy.length) throw new Error("Pick at least one group column");
            const spec = { mode: "groupBy", groupBy, values: values.list.read() };
            return w.apply(d => { d.view = spec; }).then(() => { w.viewMemory.groupBy = spec; });
        },
    });
}

export function pivotDialog(w) {
    const active = w.doc.view?.mode === "pivot" ? w.doc.view : w.viewMemory.pivot;
    const rows = dimList(w, active?.rows, { addLabel: "Row Column", max: 2 });
    const cols = dimList(w, active?.cols, { addLabel: "Column", max: 2 });
    const values = valueList(w, active?.values);

    openDialog({
        owner: w,
        title: "Pivot",
        width: "30rem",
        build: body => body.append(
            el("div", { class: "ir-field-label" }, "Rows"),
            rows.container, rows.list.addButton,
            el("div", { class: "ir-field-label ir-gap-above" }, "Columns (become headings)"),
            cols.container, cols.list.addButton,
            el("div", { class: "ir-field-label ir-gap-above" }, "Values"),
            values.container, values.list.addButton,
            el("p", { class: "ir-dialog-note" }, "No values = a count per cell.")),
        onApply: () => {
            const rowDims = [...new Set(rows.list.read())];
            const colDims = [...new Set(cols.list.read())].filter(c => !rowDims.includes(c));
            if (!rowDims.length || !colDims.length) throw new Error("Pick at least one row column and one distinct column heading");
            const spec = { mode: "pivot", rows: rowDims, cols: colDims, values: values.list.read() };
            return w.apply(d => { d.view = spec; }).then(() => { w.viewMemory.pivot = spec; });
        },
    });
}

// --- Chart -------------------------------------------------------------------

const CHART_TYPES = [
    { value: "bar", label: "Bar" },
    { value: "line", label: "Line" },
    { value: "area", label: "Line with Area" },
    { value: "pie", label: "Pie" },
];

export function chartDialog(w) {
    const active = w.doc.view?.mode === "chart" ? w.doc.view : w.viewMemory.chart;
    const chartable = pickable(w).filter(c => c.type !== "other");

    const typeSel = sel(CHART_TYPES, active?.type ?? "bar");
    const labelSel = sel([
        { value: "", label: "— Select —" },
        ...chartable.map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label })),
    ], active?.label ?? "");
    const valueSel = sel([
        { value: "", label: "— Row Count —" },
        ...pickable(w).map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label })),
    ], active?.value ?? "");

    // The chart's function select is not fnSelectFor: count-alone is legal (no
    // value column), the catalog is the stricter chartAggregateFunctions, and
    // numeric columns offer "Each Row" (no aggregation at all).
    const fnSel = el("select", { class: "ir-select" });
    const refreshFns = keep => {
        const options = [];
        if (!valueSel.value) {
            options.push({ value: "count", label: FN_LABELS.count });
        } else {
            const type = typeOf(w, valueSel.value);
            options.push(...chartFnsFor(w, type).map(f => ({ value: f, label: FN_LABELS[f] ?? f })));
            if (type === "number") options.push({ value: "", label: "— Each Row —" });
        }
        fnSel.replaceChildren(...options.map(o => new Option(o.label, o.value)));
        if (keep !== undefined && [...fnSel.options].some(o => o.value === keep)) fnSel.value = keep;
    };
    valueSel.onchange = () => refreshFns(fnSel.value);
    refreshFns(active ? (active.fn ?? "") : undefined);

    const orientSel = sel([
        { value: "vertical", label: "Vertical" },
        { value: "horizontal", label: "Horizontal" },
    ], active?.orientation ?? "vertical");
    const sortBySel = sel([{ value: "label", label: "Label" }, { value: "value", label: "Value" }], active?.sort?.by ?? "label");
    const sortDirSel = sel(DIR_OPTIONS, active?.sort?.dir ?? "asc");

    const labelTitleInp = el("input", { class: "ir-input", type: "text", value: active?.labelAxisTitle ?? "", placeholder: "Optional" });
    const valueTitleInp = el("input", { class: "ir-input", type: "text", value: active?.valueAxisTitle ?? "", placeholder: "Optional" });

    const orientField = labeled("Orientation", orientSel);
    const labelTitleField = labeled("Label Axis Title", labelTitleInp);
    const valueTitleField = labeled("Value Axis Title", valueTitleInp);
    const syncType = () => {
        const pie = typeSel.value === "pie";
        orientField.hidden = pie;
        labelTitleField.hidden = pie;
        valueTitleField.hidden = pie;
    };
    typeSel.onchange = syncType;
    syncType();

    openDialog({
        owner: w,
        title: "Chart",
        width: "30rem",
        build: body => body.append(
            labeled("Chart Type", typeSel),
            labeled("Label", labelSel),
            el("div", { class: "ir-field" },
                el("span", { class: "ir-field-label" }, "Value"),
                el("div", { class: "ir-dlgrow ir-chart-valuerow" },
                    fnSel, el("span", { class: "ir-row-of" }, "of"), valueSel)),
            orientField,
            el("div", { class: "ir-field" },
                el("span", { class: "ir-field-label" }, "Sort"),
                el("div", { class: "ir-dlgrow" }, sortBySel, sortDirSel)),
            labelTitleField,
            valueTitleField,
            el("p", { class: "ir-dialog-note" },
                "The chart draws the whole filtered result — never just the visible page — up to the report's point limit.")),
        onApply: () => {
            if (!labelSel.value) throw new Error("Pick a label column");
            const spec = {
                mode: "chart",
                type: typeSel.value,
                label: labelSel.value,
                sort: { by: sortBySel.value, dir: sortDirSel.value },
            };
            if (valueSel.value) spec.value = valueSel.value;
            if (fnSel.value) spec.fn = fnSel.value;
            if (typeSel.value !== "pie") {
                spec.orientation = orientSel.value;
                if (labelTitleInp.value.trim()) spec.labelAxisTitle = labelTitleInp.value.trim();
                if (valueTitleInp.value.trim()) spec.valueAxisTitle = valueTitleInp.value.trim();
            }
            return w.apply(d => { d.view = spec; }).then(() => { w.viewMemory.chart = spec; });
        },
    });
}
