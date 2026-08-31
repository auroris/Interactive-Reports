// Accessible chart-view composition: a described canvas region plus a "View chart data"
// table of the same dataset. The Chart.js pixels themselves come from the lazily loaded chart
// bundle the widget hands in.

import { el } from "../../core/dom.js";
import { resolveLocale, translate } from "../../core/localization.js";
import { labelOf } from "../schema.js";
import { activeShapeLocation } from "../state.js";
import { fnLabel, hasFraction } from "./format.js";
import { formatForColumn, renderTextValue } from "./column-renderers.js";

/**
 * Resolves the active chart's label and metric roles against the returned result columns.
 *
 * @param {object} w - The report controller containing active state and the latest result contract.
 * @returns {[object, object]|null} The distinct label and metric columns, or `null` when either role is missing.
 */
export function chartResultColumns(w) {
    const view = activeShapeLocation(w.doc, "chart")?.composable ?? {};
    const columns = w.lastResult?.columns ?? [];
    const find = name => columns.find(column =>
        column.name.toLowerCase() === String(name ?? "").toLowerCase());
    const label = find(view.label);
    const metricBaseName = !view.value ? "__count" : view.fn ? "v0" : view.value;
    const metricName = label?.name.toLowerCase() === String(metricBaseName).toLowerCase()
        ? `${metricBaseName}_metric`
        : metricBaseName;
    const metric = find(metricName);
    return label && metric && label !== metric ? [label, metric] : null;
}

/**
 * Determines whether the active result and schema can render the selected chart view.
 *
 * @param {object} w - The report controller to inspect.
 * @returns {boolean} Whether both required chart result columns are available.
 */
export const canRenderChart = w => chartResultColumns(w) !== null;

/**
 * Builds the chart's human name, such as "Sum of Amount by Status," for the view chip and accessible
 * description.
 *
 * @param {object} w - The report controller providing schema labels and localization.
 * @param {object} view - The active chart view definition to summarize.
 * @returns {string} The chart summary.
 */
export function chartSummary(w, view) {
    const value = !view.fn
        ? labelOf(w, view.value)
        : view.value
            ? translate(w, "aggregate.ofColumn", {
                function: fnLabel(w, view.fn),
                column: labelOf(w, view.value),
            })
            : fnLabel(w, view.fn);
    return translate(w, "chart.by", { value, label: labelOf(w, view.label) });
}

/**
 * Renders a described canvas plus a "View chart data" disclosure holding the
 * same label/value dataset as a real table. Returns the Chart.js instance via the lazily loaded
 * chart module, or returns null when pixels cannot or should not be drawn.
 *
 * @param {object} w - The report controller containing state, schema, localization, and the latest result.
 * @param {Element} container - The chart-view region whose children will be replaced.
 * @param {{renderChart: Function}} chartModule - The lazily loaded Chart.js adapter.
 * @returns {object|null} The live chart instance, or `null` for empty, invalid, or canvas-less results.
 *
 * Side effects: replaces the chart region and may initialize Chart.js on a new canvas.
 */
export function renderChartView(w, container, chartModule) {
    const result = w.lastResult;
    const view = activeShapeLocation(w.doc, "chart")?.composable ?? {};
    if (!result?.rows.length) {
        container.replaceChildren(el("div", { class: "ir-chart-empty" }, translate(w, "grid.noData")));
        return null;
    }

    const chartColumns = chartResultColumns(w);
    if (!chartColumns) {
        container.replaceChildren();
        return null;
    }
    const [labelCol, valueCol] = chartColumns;
    const labelFormat = formatForColumn(w, labelCol);
    const valueFormat = formatForColumn(w, valueCol);
    const labels = result.rows.map(r => {
        const value = r[labelCol.name];
        return value === null || value === undefined
            ? translate(w, "chart.blank")
            : renderTextValue(w, r, labelCol, hasFraction(value), labelFormat);
    });
    const values = result.rows.map(r => {
        const value = r[valueCol.name];
        if (value === null || value === undefined) return null;
        // Protocol contract: chart.js ultimately needs an IEEE-754 coordinate. Conversion
        // happens only at that pixel boundary; the response and accessible data table stay
        // exact.
        const coordinate = Number(value);
        return Number.isFinite(coordinate) ? coordinate : null;
    });
    const decimal = result.rows.some(r => hasFraction(r[valueCol.name]));
    // Invariant: one canonical display string per point, shared by the data table and the
    // chart's tooltips. Exact values and masks never pass through the lossy coordinate.
    const displayValues = result.rows.map(r => {
        const value = r[valueCol.name];
        if (value === null || value === undefined) return null;
        return renderTextValue(w, r, valueCol, decimal, valueFormat);
    });

    // Protocol contract: the metric column is synthetic (v0/__count) when aggregated, so its
    // server label embeds the raw column label, so rebuild it from the chart spec instead.
    const metricLabel = view.fn
        ? (view.value ? `${view.fn}(${labelOf(w, view.value)})` : fnLabel(w, "count"))
        : labelOf(w, valueCol.formatSource ?? valueCol.name);

    const description = translate(w, "chart.description", {
        type: ["bar", "line", "area", "pie"].includes(view.type) ? view.type : "other",
        summary: chartSummary(w, view),
        count: labels.length,
    });
    const canvas = el("canvas", { class: "ir-chart-canvas", role: "img", "aria-label": description });
    const table = el("table", { class: "ir-table ir-chart-table" },
        el("thead", {}, el("tr", {},
            el("th", { scope: "col" }, labelOf(w, labelCol.name)),
            el("th", { scope: "col", class: "ir-num" }, metricLabel))),
        el("tbody", {}, ...result.rows.map((r, i) => el("tr", {},
            el("td", {}, labels[i]),
            el("td", { class: "ir-num" }, displayValues[i] ?? "")))));

    container.replaceChildren(
        el("div", { class: "ir-chart-region" }, canvas),
        el("details", { class: "ir-chart-data" },
            el("summary", {}, translate(w, "chart.viewData")),
            el("div", { class: "ir-tablewrap" }, table)));

    // Invariant: no 2d context (headless/print environments): the description and data table
    // still stand on their own; only the pixels are skipped.
    if (!canvas.getContext?.("2d")) return null;

    return chartModule.renderChart(canvas, {
        type: view.type,
        horizontal: view.orientation === "horizontal",
        labels,
        values,
        displayValues,
        metricLabel,
        labelAxisTitle: view.labelAxisTitle ?? null,
        valueAxisTitle: view.valueAxisTitle ?? null,
        locale: resolveLocale(w),
    });
}
