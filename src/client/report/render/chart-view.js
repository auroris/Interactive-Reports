// The chart view container: an accessibly described canvas region plus a
// "View chart data" table of the same dataset. The Chart.js pixels themselves
// come from the lazily loaded chart bundle the widget hands in.

import { el } from "../../core/dom.js";
import { labelOf } from "../schema.js";
import { hasFraction, FN_LABELS } from "./format.js";
import { formatForColumn, renderTextValue } from "./column-renderers.js";

const CHART_TYPE_LABELS = { bar: "Bar", line: "Line", area: "Line with Area", pie: "Pie" };

/// "Sum of Amount by Status" — the chart's human name, shared by the view chip
/// and the accessible description.
export function chartSummary(w, view) {
    const by = ` by ${labelOf(w, view.label)}`;
    if (!view.fn) return labelOf(w, view.value) + by;
    const fn = FN_LABELS[view.fn] ?? view.fn;
    return view.value ? `${fn} of ${labelOf(w, view.value)}${by}` : fn + by;
}

/**
 * Render the chart view into its container: a canvas region (described for
 * assistive tech — canvas pixels are invisible to it) plus a "View chart data"
 * disclosure holding the same label/value dataset as a real table. Returns the
 * Chart.js instance via the lazily loaded chartModule, or null with no data.
 */
export function renderChartView(w, container, chartModule) {
    const result = w.lastResult;
    const view = w.doc.view;
    if (!result?.rows.length) {
        container.replaceChildren(el("div", { class: "ir-chart-empty" }, "No data found."));
        return null;
    }

    const [labelCol, valueCol] = result.columns;
    const labelFormat = formatForColumn(w, labelCol);
    const valueFormat = formatForColumn(w, valueCol);
    const labels = result.rows.map(r => {
        const value = r[labelCol.name];
        return value === null || value === undefined
            ? "(blank)"
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

    // The metric column is synthetic (v0/__count) when aggregated, so its server
    // label embeds the raw column label — rebuild it from the chart spec instead.
    const metricLabel = view.fn
        ? (view.value ? `${view.fn}(${labelOf(w, view.value)})` : "Count")
        : labelOf(w, valueCol.formatSource ?? valueCol.name);

    const description =
        `${CHART_TYPE_LABELS[view.type] ?? "Chart"} chart of ${chartSummary(w, view)}. ${labels.length} data points.`;
    const canvas = el("canvas", { class: "ir-chart-canvas", role: "img", "aria-label": description });
    const table = el("table", { class: "ir-table ir-chart-table" },
        el("thead", {}, el("tr", {},
            el("th", { scope: "col" }, labelOf(w, labelCol.name)),
            el("th", { scope: "col", class: "ir-num" }, metricLabel))),
        el("tbody", {}, ...result.rows.map((r, i) => el("tr", {},
            el("td", {}, labels[i]),
            el("td", { class: "ir-num" }, renderTextValue(w, r, valueCol, decimal, valueFormat))))));

    container.replaceChildren(
        el("div", { class: "ir-chart-region" }, canvas),
        el("details", { class: "ir-chart-data" },
            el("summary", {}, "View chart data"),
            el("div", { class: "ir-tablewrap" }, table)));

    // No 2d context (headless/print environments): the description and data
    // table still stand on their own; only the pixels are skipped.
    if (!canvas.getContext?.("2d")) return null;

    return chartModule.renderChart(canvas, {
        type: view.type,
        horizontal: view.orientation === "horizontal",
        labels,
        values,
        metricLabel,
        labelAxisTitle: view.labelAxisTitle ?? null,
        valueAxisTitle: view.valueAxisTitle ?? null,
    });
}
