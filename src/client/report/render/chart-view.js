// The chart view container: an accessibly described canvas region plus a
// "View chart data" table of the same dataset. The Chart.js pixels themselves
// come from the lazily loaded chart bundle the widget hands in.

import { el } from "../../core/dom.js";
import { resolveLocale, translate } from "../../core/localization.js";
import { labelOf } from "../schema.js";
import { stageOf } from "../state.js";
import { fnLabel, hasFraction } from "./format.js";
import { formatForColumn, renderTextValue } from "./column-renderers.js";

/// "Sum of Amount by Status" — the chart's human name, shared by the view chip
/// and the accessible description.
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
 * Render the chart view into its container: a canvas region (described for
 * assistive tech — canvas pixels are invisible to it) plus a "View chart data"
 * disclosure holding the same label/value dataset as a real table. Returns the
 * Chart.js instance via the lazily loaded chartModule, or null with no data.
 */
export function renderChartView(w, container, chartModule) {
    const result = w.lastResult;
    const view = stageOf(w.doc, "chart")?.shape ?? {};
    if (!result?.rows.length) {
        container.replaceChildren(el("div", { class: "ir-chart-empty" }, translate(w, "grid.noData")));
        return null;
    }

    const [labelCol, valueCol] = result.columns;
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
        // Chart.js ultimately needs an IEEE-754 coordinate. Conversion happens only
        // at that pixel boundary; the response and accessible data table stay exact.
        const coordinate = Number(value);
        return Number.isFinite(coordinate) ? coordinate : null;
    });
    const decimal = result.rows.some(r => hasFraction(r[valueCol.name]));
    // One canonical display string per point, shared by the data table and the
    // chart's tooltips — exact values, masks and all, never the lossy coordinate.
    const displayValues = result.rows.map(r => {
        const value = r[valueCol.name];
        if (value === null || value === undefined) return null;
        return renderTextValue(w, r, valueCol, decimal, valueFormat);
    });

    // The metric column is synthetic (v0/__count) when aggregated, so its server
    // label embeds the raw column label — rebuild it from the chart spec instead.
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

    // No 2d context (headless/print environments): the description and data
    // table still stand on their own; only the pixels are skipped.
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
