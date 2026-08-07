// Chart rendering for the report widget — the only module that knows Chart.js.
// Built as its own self-contained bundle (dist/ir-chart.js) and loaded on demand
// the first time a report enters chart view, so grid-only pages never pay for it.
// Chart.js is tree-shaken down to the four forms the state document allows;
// everything in here is presentation and never enters protocol or saved state.

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

// Fallbacks mirror the token defaults in ir.css; getComputedStyle resolves any
// host overrides of the documented --ir-* custom properties.
const FALLBACKS = {
    "--ir-chart-1": "#0572ce",
    "--ir-chart-2": "#eb6834",
    "--ir-chart-3": "#1baf7a",
    "--ir-chart-4": "#eda100",
    "--ir-chart-5": "#e87ba4",
    "--ir-chart-6": "#008300",
    "--ir-chart-7": "#4a3aa7",
    "--ir-chart-8": "#e34948",
    "--ir-chart-grid": "#e8ebee",
    "--ir-chart-text": "#5d6771",
    "--ir-bg": "#ffffff",
    "--ir-font": 'system-ui, -apple-system, "Segoe UI", sans-serif',
};

function readTheme(canvas) {
    const styles = getComputedStyle(canvas);
    const token = name => styles.getPropertyValue(name).trim() || FALLBACKS[name];
    return {
        palette: [1, 2, 3, 4, 5, 6, 7, 8].map(i => token(`--ir-chart-${i}`)),
        grid: token("--ir-chart-grid"),
        text: token("--ir-chart-text"),
        surface: token("--ir-bg"),
        font: { family: token("--ir-font"), size: 12 },
    };
}

/// Canvas normalizes any CSS color, so tokens may hold hex, rgb(), or names.
function withAlpha(color, alpha) {
    const probe = document.createElement("canvas").getContext("2d");
    probe.fillStyle = "#000";
    probe.fillStyle = color;
    const parsed = probe.fillStyle;
    if (parsed.startsWith("#")) {
        const r = parseInt(parsed.slice(1, 3), 16);
        const g = parseInt(parsed.slice(3, 5), 16);
        const b = parseInt(parsed.slice(5, 7), 16);
        return `rgba(${r}, ${g}, ${b}, ${alpha})`;
    }
    const inner = parsed.slice(parsed.indexOf("(") + 1, parsed.lastIndexOf(")")).split(",");
    return `rgba(${inner[0].trim()}, ${inner[1].trim()}, ${inner[2].trim()}, ${alpha})`;
}

const formatNumber = value =>
    typeof value === "number" ? value.toLocaleString(undefined, { maximumFractionDigits: 2 }) : String(value);

/// Fixed hue order, never re-cut per dataset; past the 8th slice the same hues
/// return as lighter tints (a step of the same hue, not an invented color).
function sliceColors(count, palette) {
    const tiers = [1, 0.72, 0.5];
    return Array.from({ length: count }, (_, i) => {
        const tier = tiers[Math.min(Math.floor(i / palette.length), tiers.length - 1)];
        const hue = palette[i % palette.length];
        return tier === 1 ? hue : withAlpha(hue, tier);
    });
}

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
            animation: { duration: 300 },
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
