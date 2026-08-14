// Tail-authoring dialogs: Group By, Pivot, and Chart. Each writes the pipeline's
// tail stages — [group], [group, spread], or [chart] — and swaps them in through
// the shelf so the toolbar can switch back to any configured mode without
// re-asking, including after a saved-report reload. Editing a spec preserves the
// existing stages' layers (per-view columns, labels, formats, computed, sorts,
// highlights); metrics keep their stable ids across edits when their column and
// function survive, so per-metric state never silently re-attaches elsewhere.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import {
    activateTail,
    configuredTail,
    nextFreeId,
    pruneRetiredMetrics,
    removeStageComputedColumn,
    sameColumn,
    stageOf,
} from "../state.js";
import { pickable, typeOf, chartFnsFor } from "../schema.js";
import {
    aggregateRowList,
    colOptions,
    DIR_OPTIONS,
    fieldGroup,
    rowField,
    rowList,
} from "./parts.js";
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
        row.append(rowField(addLabel, colSel));
        row._read = () => colSel.value || null;
    }, { addLabel, max });
    return { container, list };
}

function valueList(w, initial) {
    return aggregateRowList(w, initial, { addLabel: "Value" });
}

/// Assign stable metric ids to the dialog's (col, fn) rows. A row that matches a
/// previous value keeps that value's id — its formats, computed references, and
/// cell state stay attached; new rows get fresh ids never used by the old spec.
/// Returns the identified values plus the ids the edit retired.
function assignMetricIds(rows, previous) {
    const remaining = [...(previous ?? [])];
    const used = new Set(remaining.map(v => String(v.id).toLowerCase()));
    const fresh = () => {
        const id = nextFreeId(used, "m");
        used.add(id);
        return id;
    };
    const values = rows.map(row => {
        const index = remaining.findIndex(v => sameColumn(v.col, row.col) && v.fn === row.fn);
        if (index >= 0) {
            const [kept] = remaining.splice(index, 1);
            return { id: kept.id, col: row.col, fn: row.fn };
        }
        return { id: fresh(), col: row.col, fn: row.fn };
    });
    return { values, retired: remaining.map(v => v.id) };
}

/// Retired dims and metrics take their dependent stage-layer state with them —
/// the same coarse rule as deleting a computed column.
function pruneRetiredStageState(d, retiredMetricIds, retiredDims) {
    const stage = stageOf(d, "group");
    if (!stage) return;
    pruneRetiredMetrics(d, stage, retiredMetricIds);
    for (const dim of retiredDims) removeStageComputedColumn(d, stage, dim);
}

const groupShape = tail => tail?.find(s => (s.shape?.kind ?? "") === "group") ?? null;
const spreadShape = tail => tail?.find(s => (s.shape?.kind ?? "") === "spread") ?? null;

export function groupByDialog(w) {
    const existingTail = configuredTail(w.doc, "groupBy");
    const existingGroup = groupShape(existingTail);
    const shape = existingGroup?.shape ?? {};
    const dims = dimList(w, shape.by, { addLabel: "Group Column", max: 3 });
    const values = valueList(w, shape.values);

    openDialog({
        owner: w,
        title: "Group By",
        width: "30rem",
        build: body => body.append(
            fieldGroup("Group by", dims.container, dims.list.addButton),
            fieldGroup("Aggregate values", values.container, values.list.addButton),
            el("p", { class: "ir-dialog-note" }, "A row count per group is always included.")),
        onApply: () => {
            const by = [...new Set(dims.list.read())];
            if (!by.length) throw new Error("Pick at least one group column");
            const { values: withIds, retired } = assignMetricIds(values.list.read(), shape.values);
            const retiredDims = (shape.by ?? []).filter(old => !by.some(n => sameColumn(n, old)));
            return w.apply(d => {
                const stage = structuredClone(existingGroup) ?? {};
                stage.shape = { kind: "group", by, values: withIds };
                activateTail(d, "groupBy", [stage]);
                pruneRetiredStageState(d, retired, retiredDims);
            });
        },
    });
}

export function pivotDialog(w) {
    const existingTail = configuredTail(w.doc, "pivot");
    const existingGroup = groupShape(existingTail);
    const existingSpread = spreadShape(existingTail);
    const shape = existingGroup?.shape ?? {};
    const spreadCols = existingSpread?.shape?.cols ?? [];
    const rowDims = (shape.by ?? []).filter(n => !spreadCols.some(c => sameColumn(c, n)));

    const rows = dimList(w, rowDims, { addLabel: "Row Column", max: 2 });
    const cols = dimList(w, spreadCols, { addLabel: "Column", max: 2 });
    const values = valueList(w, shape.values);
    const totalsInp = el("input", { type: "checkbox", checked: existingSpread?.shape?.totals === true });

    openDialog({
        owner: w,
        title: "Pivot",
        width: "30rem",
        build: body => body.append(
            fieldGroup("Rows", rows.container, rows.list.addButton),
            fieldGroup("Columns (become headings)", cols.container, cols.list.addButton),
            fieldGroup("Values", values.container, values.list.addButton),
            el("label", { class: "ir-checkline ir-gap-above" }, totalsInp, "Show total rows"),
            el("p", { class: "ir-dialog-note" }, "No values = a count per cell.")),
        onApply: () => {
            const rowNames = [...new Set(rows.list.read())];
            const colNames = [...new Set(cols.list.read())].filter(c => !rowNames.some(n => sameColumn(n, c)));
            if (!rowNames.length || !colNames.length)
                throw new Error("Pick at least one row column and one distinct column heading");
            const { values: withIds, retired } = assignMetricIds(values.list.read(), shape.values);
            const by = [...rowNames, ...colNames];
            const retiredDims = (shape.by ?? []).filter(old => !by.some(n => sameColumn(n, old)));
            return w.apply(d => {
                const group = structuredClone(existingGroup) ?? {};
                group.shape = { kind: "group", by, values: withIds };
                const spread = structuredClone(existingSpread) ?? {};
                spread.shape = { kind: "spread", cols: colNames };
                if (totalsInp.checked) spread.shape.totals = true;
                activateTail(d, "pivot", [group, spread]);
                pruneRetiredStageState(d, retired, retiredDims);
            });
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
    const existingTail = configuredTail(w.doc, "chart");
    const active = existingTail?.find(s => (s.shape?.kind ?? "") === "chart")?.shape;
    const chartable = pickable(w).filter(c => c.type !== "other");

    const typeSel = sel(CHART_TYPES, active?.type ?? "bar");
    typeSel.classList.add("ir-chart-type");
    const labelSel = sel([
        { value: "", label: "— Select —" },
        ...chartable.map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label })),
    ], active?.label ?? "");
    labelSel.required = true;
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
    valueSel.addEventListener("change", () => refreshFns(fnSel.value));
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
    for (const field of [orientField, labelTitleField, valueTitleField])
        field.classList.add("ir-non-pie");

    openDialog({
        owner: w,
        title: "Chart",
        width: "30rem",
        build: body => body.append(
            labeled("Chart Type", typeSel),
            labeled("Label", labelSel),
            fieldGroup("Value",
                el("div", { class: "ir-dlgrow ir-chart-valuerow" },
                    rowField("Function", fnSel),
                    el("span", { class: "ir-row-of", "aria-hidden": "true" }, "of"),
                    rowField("Column", valueSel))),
            orientField,
            fieldGroup("Sort",
                el("div", { class: "ir-dlgrow" },
                    rowField("By", sortBySel), rowField("Direction", sortDirSel))),
            labelTitleField,
            valueTitleField,
            el("p", { class: "ir-dialog-note" },
                "The chart draws the whole filtered result — never just the visible page — up to the report's point limit.")),
        onApply: () => {
            const shape = {
                kind: "chart",
                type: typeSel.value,
                label: labelSel.value,
                sort: { by: sortBySel.value, dir: sortDirSel.value },
            };
            if (valueSel.value) shape.value = valueSel.value;
            if (fnSel.value) shape.fn = fnSel.value;
            if (typeSel.value !== "pie") {
                shape.orientation = orientSel.value;
                if (labelTitleInp.value.trim()) shape.labelAxisTitle = labelTitleInp.value.trim();
                if (valueTitleInp.value.trim()) shape.valueAxisTitle = valueTitleInp.value.trim();
            }
            return w.apply(d => activateTail(d, "chart", [{ shape }]));
        },
    });
}
