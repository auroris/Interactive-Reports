// Chart bundle entrypoint: exposes Chart.js rendering behind the small adapter consumed by the
// report element. This isolates the optional chart dependency from the base report bundle.

// Protocol contract: this chart renderer is built as its own self-contained bundle
// (dist/ir-chart.js) and loaded on demand the first time a report enters chart view, so
// grid-only pages never pay for Chart.js. The implementation lives under chart/ (theme token
// resolution, Chart.js assembly); everything in it is presentation and never enters protocol or
// saved state.

export { renderChart } from "./chart/render.js";
