// Table-authoring dialogs: Group By, Pivot, and Chart. Each authors a named
// table containing the corresponding shape composable, while view switching
// changes only activeTable. Editing a spec preserves the table's later
// composables (per-view columns, labels, formats, computed, sorts,
// highlights); metrics keep their stable ids across edits when their column and
// function survive, so per-metric state never silently re-attaches elsewhere.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import {
    assignShapeMetricIds,
    createView,
    pruneRetiredChartOutputs,
    pruneRetiredMetrics,
    pruneRetiredPivotOutputs,
    removeTerminalComputedColumn,
    replaceComposable,
    resolveCreationBase,
    resolveView,
    sameColumn,
} from "../state.js";
import { chartFnsFor } from "../schema.js";
import { shapeEditable, shapeInputColumns } from "../table.js";
import {
    aggregateRowList,
    colOptions,
    dirOptions,
    fieldGroup,
    rowField,
    rowList,
} from "./parts.js";
import { fnLabel } from "../render/format.js";

/// Open the configuration dialog for a non-grid view mode. Toolbar switches and
/// the view chip both land here.
export function openViewDialog(w, mode) {
    if (mode === "groupBy") groupByDialog(w);
    else if (mode === "pivot") pivotDialog(w);
    else chartDialog(w);
}

const modeName = (w, mode) => w.t(mode === "groupBy" ? "group.label" : `toolbar.${mode}`);

function shapeTarget(w, mode) {
    const resolution = resolveView(w.doc, mode);
    if (resolution.status === "ambiguous") {
        w.showError(new Error(w.t("view.ambiguous", {
            mode: modeName(w, mode),
            tables: resolution.candidates.map(candidate => candidate.tableId).join(", "),
        })));
        return null;
    }
    if (resolution.candidate) {
        if (!shapeEditable(resolution.candidate.shapeLocation)) {
            w.showError(new Error(w.t("view.shapeReadOnly", {
                mode: modeName(w, mode),
                table: resolution.candidate.tableId,
            })));
            return null;
        }
        return {
            location: resolution.candidate.shapeLocation,
            baseTableId: null,
        };
    }

    const base = resolveCreationBase(w.doc);
    if (base.status === "ambiguous") {
        w.showError(new Error(w.t("view.ambiguousBase", {
            tables: base.candidates.map(candidate => candidate.tableId).join(", "),
        })));
        return null;
    }
    if (!base.candidate) {
        w.showError(new Error(w.t("view.baseUnavailable")));
        return null;
    }
    return {
        location: null,
        baseTableId: base.candidate.tableId,
    };
}

// --- Group By / Pivot --------------------------------------------------------

function dimList(w, initial, { addLabel, max, columns }) {
    const container = el("div", {});
    const list = rowList(container, (initial ?? []).map(c => ({ col: c })), (row, item) => {
        const colSel = sel(colOptions(w, { none: w.t("common.select"), columns }), item?.col ?? "");
        row.append(rowField(addLabel, colSel));
        row._read = () => colSel.value || null;
    }, { addLabel, max, context: w });
    return { container, list };
}

function valueList(w, initial, columns) {
    return aggregateRowList(w, initial, { addLabel: w.t("common.value"), columns });
}

/// Retired dims and metrics take dependent terminal-table state with them —
/// the same coarse rule as deleting a computed column.
function pruneRetiredTableState(d, tableId, retiredMetricIds, retiredDims) {
    pruneRetiredMetrics(d, tableId, retiredMetricIds);
    for (const dim of retiredDims) removeTerminalComputedColumn(d, dim, tableId);
}

export function groupByDialog(w) {
    const target = shapeTarget(w, "groupBy");
    if (!target) return;
    const shape = target.location?.composable ?? {};
    const inputColumns = shapeInputColumns(w, target.location, target.baseTableId);
    const dims = dimList(w, shape.by, { addLabel: w.t("group.addColumn"), max: 3, columns: inputColumns });
    const values = valueList(w, shape.values, inputColumns);

    openDialog({
        owner: w,
        title: w.t("group.title"),
        width: "30rem",
        build: body => body.append(
            fieldGroup(w.t("group.by"), dims.container, dims.list.addButton),
            fieldGroup(w.t("aggregate.values"), values.container, values.list.addButton),
            el("p", { class: "ir-dialog-note" }, w.t("group.countNote"))),
        onApply: () => {
            const by = [...new Set(dims.list.read())];
            if (!by.length) throw new Error(w.t("group.pickColumn"));
            const { values: withIds, retired } = assignShapeMetricIds(
                w.doc,
                values.list.read(),
                shape.values,
                inputColumns);
            const retiredDims = (shape.by ?? []).filter(old => !by.some(n => sameColumn(n, old)));
            return w.apply(d => {
                const replacement = { kind: "group", by, values: withIds };
                const location = target.location
                    ? replaceComposable(d, target.location, replacement)
                    : createView(d, "groupBy", replacement, target.baseTableId);
                d.activeTable = location.tableId;
                pruneRetiredTableState(d, location.tableId, retired, retiredDims);
            });
        },
    });
}

export function pivotDialog(w) {
    const target = shapeTarget(w, "pivot");
    if (!target) return;
    const shape = target.location?.composable ?? {};
    const inputColumns = shapeInputColumns(w, target.location, target.baseTableId);

    const rows = dimList(w, shape.rows, { addLabel: w.t("pivot.rowColumn"), max: 2, columns: inputColumns });
    const cols = dimList(w, shape.cols, { addLabel: w.t("common.column"), max: 2, columns: inputColumns });
    const values = valueList(w, shape.values, inputColumns);
    const totalsInp = el("input", { type: "checkbox", checked: shape.totals === true });

    openDialog({
        owner: w,
        title: w.t("pivot.title"),
        width: "30rem",
        build: body => body.append(
            fieldGroup(w.t("pivot.rows"), rows.container, rows.list.addButton),
            fieldGroup(w.t("pivot.columns"), cols.container, cols.list.addButton),
            fieldGroup(w.t("pivot.values"), values.container, values.list.addButton),
            el("label", { class: "ir-checkline ir-gap-above" }, totalsInp, w.t("pivot.showTotals")),
            el("p", { class: "ir-dialog-note" }, w.t("pivot.countNote"))),
        onApply: () => {
            const rowNames = [...new Set(rows.list.read())];
            const colNames = [...new Set(cols.list.read())].filter(c => !rowNames.some(n => sameColumn(n, c)));
            if (!rowNames.length || !colNames.length)
                throw new Error(w.t("pivot.pickDimensions"));
            const { values: withIds, retired } = assignShapeMetricIds(
                w.doc,
                values.list.read(),
                shape.values,
                inputColumns);
            return w.apply(d => {
                const replacement = { kind: "pivot", rows: rowNames, cols: colNames, values: withIds };
                if (totalsInp.checked) replacement.totals = true;
                const location = target.location
                    ? replaceComposable(d, target.location, replacement)
                    : createView(d, "pivot", replacement, target.baseTableId);
                d.activeTable = location.tableId;
                pruneRetiredPivotOutputs(d, location.tableId, shape, replacement, retired);
            });
        },
    });
}

// --- Chart -------------------------------------------------------------------

export function chartDialog(w) {
    const target = shapeTarget(w, "chart");
    if (!target) return;
    const active = target.location?.composable;
    const inputColumns = shapeInputColumns(w, target.location, target.baseTableId);
    const chartable = inputColumns.filter(c => c.type !== "other");
    const inputType = name => inputColumns.find(column => sameColumn(column.name, name))?.type ?? "other";

    const typeSel = sel([
        { value: "bar", label: w.t("chart.bar") },
        { value: "line", label: w.t("chart.line") },
        { value: "area", label: w.t("chart.area") },
        { value: "pie", label: w.t("chart.pie") },
    ], active?.type ?? "bar");
    typeSel.classList.add("ir-chart-type");
    const labelSel = sel([
        { value: "", label: w.t("common.select") },
        ...chartable.map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label })),
    ], active?.label ?? "");
    labelSel.required = true;
    const valueSel = sel([
        { value: "", label: w.t("chart.rowCount") },
        ...inputColumns.map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label })),
    ], active?.value ?? "");

    // The chart's function select is not fnSelectFor: count-alone is legal (no
    // value column), the catalog is the stricter chartAggregateFunctions, and
    // numeric columns offer "Each Row" (no aggregation at all).
    const fnSel = el("select", { class: "ir-select" });
    const refreshFns = keep => {
        const options = [];
        if (!valueSel.value) {
            options.push({ value: "count", label: fnLabel(w, "count") });
        } else {
            const type = inputType(valueSel.value);
            options.push(...chartFnsFor(w, type).map(f => ({ value: f, label: fnLabel(w, f) })));
            if (type === "number") options.push({ value: "", label: w.t("chart.eachRow") });
        }
        fnSel.replaceChildren(...options.map(o => new Option(o.label, o.value)));
        if (keep !== undefined && [...fnSel.options].some(o => o.value === keep)) fnSel.value = keep;
    };
    valueSel.addEventListener("change", () => refreshFns(fnSel.value));
    refreshFns(active ? (active.fn ?? "") : undefined);

    const orientSel = sel([
        { value: "vertical", label: w.t("chart.vertical") },
        { value: "horizontal", label: w.t("chart.horizontal") },
    ], active?.orientation ?? "vertical");
    const sortBySel = sel([
        { value: "label", label: w.t("chart.sortByLabel") },
        { value: "value", label: w.t("chart.sortByValue") },
    ], active?.sort?.by ?? "label");
    const sortDirSel = sel(dirOptions(w), active?.sort?.dir ?? "asc");

    const labelTitleInp = el("input", { class: "ir-input", type: "text", value: active?.labelAxisTitle ?? "", placeholder: w.t("common.optional") });
    const valueTitleInp = el("input", { class: "ir-input", type: "text", value: active?.valueAxisTitle ?? "", placeholder: w.t("common.optional") });

    const orientField = labeled(w.t("chart.orientation"), orientSel);
    const labelTitleField = labeled(w.t("chart.labelAxisTitle"), labelTitleInp);
    const valueTitleField = labeled(w.t("chart.valueAxisTitle"), valueTitleInp);
    for (const field of [orientField, labelTitleField, valueTitleField])
        field.classList.add("ir-non-pie");

    openDialog({
        owner: w,
        title: w.t("chart.title"),
        width: "30rem",
        build: body => body.append(
            labeled(w.t("chart.type"), typeSel),
            labeled(w.t("common.label"), labelSel),
            fieldGroup(w.t("common.value"),
                el("div", { class: "ir-dlgrow ir-chart-valuerow" },
                    rowField(w.t("common.function"), fnSel),
                    el("span", { class: "ir-row-of", "aria-hidden": "true" }, w.t("common.of")),
                    rowField(w.t("common.column"), valueSel))),
            orientField,
            fieldGroup(w.t("chart.sort"),
                el("div", { class: "ir-dlgrow" },
                    rowField(w.t("common.by"), sortBySel), rowField(w.t("common.direction"), sortDirSel))),
            labelTitleField,
            valueTitleField,
            el("p", { class: "ir-dialog-note" },
                w.t("chart.note"))),
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
            return w.apply(d => {
                const location = target.location
                    ? replaceComposable(d, target.location, shape)
                    : createView(d, "chart", shape, target.baseTableId);
                d.activeTable = location.tableId;
                if (target.location)
                    pruneRetiredChartOutputs(d, location.tableId, active, shape);
            });
        },
    });
}
