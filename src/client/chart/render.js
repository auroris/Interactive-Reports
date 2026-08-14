// Chart assembly — the only module that knows Chart.js. Chart.js is tree-shaken
// down to the four forms the state document allows (bar, line, area, pie).

import {
    ArcElement,
    BarController,
    BarElement,
    CategoryScale,
    Chart,
    Filler,
    Legend,
    LineController,
    LineElement,
    LinearScale,
    PieController,
    PointElement,
    Tooltip,
} from "chart.js";
import { readTheme, withAlpha, sliceColors } from "./theme.js";

Chart.register(
    ArcElement,
    BarController,
    BarElement,
    CategoryScale,
    Filler,
    Legend,
    LineController,
    LineElement,
    LinearScale,
    PieController,
    PointElement,
    Tooltip);

const formatNumber = value =>
    typeof value === "number" ? value.toLocaleString(undefined, { maximumFractionDigits: 2 }) : String(value);

/**
 * Render one chart. spec:
 *   { type: "bar"|"line"|"area"|"pie", horizontal, labels[], values[],
 *     metricLabel, labelAxisTitle, valueAxisTitle }
 * Returns the Chart.js instance; the caller owns destroy().
 */
export function renderChart(canvas, spec) {
    const theme = readTheme(canvas);
    const font = theme.font;
    const pie = spec.type === "pie";
    const horizontal = !pie && spec.horizontal;
    const series = theme.palette[0];

    const dataset = { data: spec.values };
    if (pie) {
        // 2px surface ring between slices so adjacent fills never touch.
        dataset.backgroundColor = sliceColors(spec.values.length, theme.palette);
        dataset.borderColor = theme.surface;
        dataset.borderWidth = 2;
    } else if (spec.type === "bar") {
        dataset.backgroundColor = series;
        dataset.borderRadius = 4;                                    // rounded data end, flat baseline
        dataset.maxBarThickness = 40;
    } else {
        dataset.borderColor = series;
        dataset.borderWidth = 2;
        dataset.pointRadius = 2;
        dataset.pointHoverRadius = 4;
        dataset.pointHitRadius = 8;
        dataset.pointBackgroundColor = series;
        if (spec.type === "area") {
            dataset.fill = "origin";
            dataset.backgroundColor = withAlpha(series, 0.18);
        }
    }

    const axisTitle = text => ({ display: !!text, text: text ?? "", color: theme.text, font });
    const categoryAxis = {
        grid: { display: false },
        border: { color: theme.grid },
        ticks: { color: theme.text, font, autoSkip: true, maxRotation: 50 },
        title: axisTitle(spec.labelAxisTitle),
    };
    const valueAxis = {
        // Bars and filled areas encode magnitude from zero; a cut axis would lie.
        beginAtZero: spec.type !== "line",
        grid: { color: theme.grid, drawTicks: false },
        border: { display: false },
        ticks: { color: theme.text, font, callback: value => formatNumber(value), padding: 6 },
        title: axisTitle(spec.valueAxisTitle),
    };

    const tooltipValue = ctx => pie
        ? ctx.parsed
        : horizontal ? ctx.parsed.x : ctx.parsed.y;

    return new Chart(canvas, {
        type: pie ? "pie" : spec.type === "bar" ? "bar" : "line",
        data: { labels: spec.labels, datasets: [dataset] },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: window.matchMedia?.("(prefers-reduced-motion: reduce)").matches
                ? false
                : { duration: 300 },
            indexAxis: horizontal ? "y" : "x",
            interaction: pie || spec.type === "bar"
                ? { mode: "nearest", intersect: true }
                : { mode: "index", intersect: false },
            scales: pie ? {} : horizontal
                ? { x: valueAxis, y: categoryAxis }
                : { x: categoryAxis, y: valueAxis },
            plugins: {
                // Single series: the chip/summary names the metric, so no legend
                // box outside pie, where the legend carries slice identity.
                legend: pie
                    ? { position: "right", labels: { color: theme.text, font, boxWidth: 10, boxHeight: 10 } }
                    : { display: false },
                tooltip: {
                    callbacks: {
                        label: ctx => {
                            const value = tooltipValue(ctx);
                            if (!pie) return `${spec.metricLabel}: ${formatNumber(value)}`;
                            const total = spec.values.reduce((sum, v) => sum + (v ?? 0), 0);
                            const pct = total ? ` (${(value / total * 100).toLocaleString(undefined, { maximumFractionDigits: 1 })}%)` : "";
                            return `${spec.metricLabel}: ${formatNumber(value)}${pct}`;
                        },
                    },
                },
            },
        },
    });
}
