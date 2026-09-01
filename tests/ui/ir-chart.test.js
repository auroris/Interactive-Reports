import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { reportState } from "./report-state-fixture.js";

const window = new Window({ url: "https://host.example/dashboard" });
function Option(text = "", value = "", defaultSelected = false, selected = false) {
    const option = window.document.createElement("option");
    option.text = text;
    option.value = value;
    option.defaultSelected = defaultSelected;
    option.selected = selected;
    return option;
}
Object.assign(globalThis, {
    window,
    document: window.document,
    HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot,
    customElements: window.customElements,
    Option,
    Node: window.Node,
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

const requests = [];
const json = value => new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
});

const chartStageOf = state =>
    Object.values(state.tables ?? {})
        .flatMap(table => table.composables ?? [])
        .find(composable => composable.kind === "chart") ?? null;

globalThis.fetch = async (url, options = {}) => {
    const request = { url: String(url), method: options.method ?? "GET", body: options.body ?? null };
    requests.push(request);
    if (request.url === "/api") return json([{ name: "orders", title: "Orders" }]);
    if (request.url.endsWith("/schema")) {
        return json({
            defaultState: {
                page: { index: 1, size: 25 },
                ...reportState(),
            },
            limits: { defaultPageSize: 25, maxPageSize: 100, maxRows: 1000, maxChartPoints: 1000 },
            columns: [
                { name: "STATUS", label: "Status", type: "text" },
                { name: "AMOUNT", label: "Amount", type: "number" },
            ],
            capabilities: {
                aggregateFunctions: {},
                expressionFunctions: [],
                chartAggregateFunctions: {
                    text: ["count", "countDistinct"],
                    number: ["count", "sum", "avg", "min", "max", "countDistinct"],
                },
            },
        });
    }
    if (request.url.endsWith("/whoami")) return json({ identity: "test-user" });
    if (request.url.endsWith("/saved")) return json([]);
    if (request.method === "GET" && /\/[^/?]+$/.test(request.url)) {
        const id = request.url.split("/").at(-1);
        return json({
            summary: { id, reportName: id, title: "Default", isDefault: true, isGlobal: true },
            state: {},
        });
    }
    if (request.url.endsWith("/query")) {
        const state = JSON.parse(request.body);
        if (chartStageOf(state)) {
            return json({
                columns: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "__count", label: "Count", type: "number" },
                ],
                rows: [
                    { STATUS: "PENDING", __count: 3 },
                    { STATUS: "SHIPPED", __count: 5 },
                    { STATUS: null, __count: 2 },
                ],
                page: { index: 1, size: 3 },
                totalRows: 3,
                aggregates: {},
                highlights: [],
                ignored: [],
            });
        }
        return json({
            columns: [
                { name: "STATUS", label: "Status", type: "text" },
                { name: "AMOUNT", label: "Amount", type: "number" },
            ],
            rows: [{ STATUS: "PENDING", AMOUNT: 100 }],
            page: { index: 1, size: 25 },
            totalRows: 1,
            aggregates: {},
            highlights: [],
            ignored: [],
        });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.AspNetCore/Ui/dist/ir.js");

const tick = () => new Promise(resolve => setTimeout(resolve, 2));
async function until(predicate, what) {
    for (let attempt = 0; attempt < 500 && !predicate(); attempt++) await tick();
    assert.ok(predicate(), `timed out waiting for ${what}`);
}

test("chart view renders behind the dialog with an accessible data table, and grid returns intact", async () => {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/api");
    document.body.append(report);
    await until(() => requests.some(r => r.url.endsWith("/query")), "the initial grid query");

    const root = report.shadowRoot;
    assert.ok(root.querySelector('.ir-viewbtn[data-mode="chart"]'), "the toolbar should offer a Chart view");

    // Open the chart dialog from the Actions menu.
    root.querySelector(".ir-actionsbtn").click();
    [...root.querySelectorAll(".ir-menu-item")]
        .find(item => item.textContent.includes("Chart"))
        .click();
    const dialog = root.querySelector(".ir-dialog");
    assert.ok(dialog, "the chart dialog should open");

    // Pie of row counts by Status. Value stays "— Row Count —", locking fn=count.
    const selects = [...dialog.querySelectorAll("select")];
    selects[0].value = "pie";                                        // chart type
    selects[1].value = "STATUS";                                     // label
    [...dialog.querySelectorAll("button")].find(b => b.textContent === "Apply").click();

    await until(() => {
        const body = requests.at(-1)?.body;
        return body && chartStageOf(JSON.parse(body)) && !root.querySelector(".ir-dialog");
    }, "the chart query to apply");
    const sent = JSON.parse(requests.at(-1).body);
    assert.deepEqual(
        chartStageOf(sent),
        { kind: "chart", type: "pie", label: "STATUS", fn: "count", sort: { by: "label", dir: "asc" } });
    assert.equal(Object.keys(sent.tables).length, 2, "the chart is a derived named table");

    // The chart region replaces the grid: canvas described for AT + the data table.
    await until(() => root.querySelector(".ir-chart-canvas"), "the chart region to render");
    const canvas = root.querySelector(".ir-chart-canvas");
    assert.equal(canvas.getAttribute("role"), "img");
    assert.match(canvas.getAttribute("aria-label"), /Pie chart of Count by Status/);
    assert.equal(root.querySelector(".ir-tablewrap:not(.ir-chart-data .ir-tablewrap)").hidden, true);

    const dataTable = root.querySelector(".ir-chart-table");
    assert.ok(dataTable, "the View chart data table should exist");
    assert.deepEqual(
        [...dataTable.querySelectorAll("th")].map(th => th.textContent),
        ["Status", "Count"]);
    const cells = [...dataTable.querySelectorAll("tbody tr")].map(tr => [...tr.children].map(td => td.textContent));
    assert.deepEqual(cells, [["PENDING", "3"], ["SHIPPED", "5"], ["(blank)", "2"]]);

    const chip = [...root.querySelectorAll(".ir-chip")].find(c => c.dataset.kind === "view");
    assert.ok(chip?.textContent.includes("Count by Status"), "the chart chip should describe the chart");
    assert.match(root.querySelector(".ir-pager").textContent, /points/);

    // Back to grid: the chart region empties and the table returns. The chart
    // table remains configured instead of being discarded.
    root.querySelector('.ir-viewbtn[data-mode="grid"]').click();
    await until(() => {
        const body = JSON.parse(requests.at(-1).body);
        return body.activeTable === "base";
    }, "the grid query");
    assert.ok(chartStageOf(JSON.parse(requests.at(-1).body)), "the chart table remains in the document");
    await until(() => root.querySelector(".ir-chartwrap").hidden, "the chart region to hide");
    assert.equal(root.querySelector(".ir-chartwrap").children.length, 0, "chart content should be disposed");
    assert.ok(root.querySelector(".ir-table tbody tr"), "grid rows should render again");

    // The chart config survives in report state: switching back skips the dialog.
    root.querySelector('.ir-viewbtn[data-mode="chart"]').click();
    await until(() => chartStageOf(JSON.parse(requests.at(-1).body)), "the remembered chart query");
    assert.equal(root.querySelector(".ir-dialog"), null, "no dialog when view memory holds a chart");

    report.remove();
});
