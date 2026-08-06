import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";

const window = new Window({ url: "https://host.example/dashboard" });
function Option(text = "", value = "", defaultSelected = false, selected = false) {
    const option = window.document.createElement("option");
    option.text = text;
    option.value = value;
    option.defaultSelected = defaultSelected;
    option.selected = selected;
    return option;
}
const browserGlobals = {
    window,
    document: window.document,
    HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot,
    customElements: window.customElements,
    Option,
    Node: window.Node,
    requestAnimationFrame: callback => setTimeout(callback, 0),
};
Object.assign(globalThis, browserGlobals);

const requests = [];
let visibleReports = [
    { name: "orders", title: "Orders" },
    { name: "inventory", title: "Inventory" },
];
let unloadableReports = new Set();
const json = (value, init = {}) => new Response(JSON.stringify(value), {
    status: init.status ?? 200,
    headers: { "Content-Type": "application/json" },
});

globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET" });
    if (String(url) === "/custom-report-api") return json(visibleReports);
    if (String(url).endsWith("/schema")) {
        const name = /\/([^/]+)\/schema$/.exec(String(url))?.[1];
        if (unloadableReports.has(name))
            return json({ title: "Report unavailable" }, { status: 503 });
        return json({
            stateVersion: 2,
            defaultState: { page: { index: 1, size: 25 }, view: { mode: "grid" } },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [{ name: "ID", label: "ID", type: "number" }],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
        });
    }
    if (String(url).endsWith("/whoami")) return json({ identity: "test-user" });
    if (String(url).endsWith("/saved")) return json([]);
    if (String(url).endsWith("/query")) {
        return json({
            columns: [{ name: "ID", label: "ID", type: "number" }],
            rows: [{ ID: 1 }],
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

test("the report is style-isolated and uses its explicit API base", async () => {
    document.head.append(Object.assign(document.createElement("style"), {
        textContent: "button, table, .ir-toolbar { display: none !important; }",
    }));

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api/");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.ok(report.shadowRoot, "the component should render behind a shadow root");
    assert.ok(report.shadowRoot.querySelector("style[data-ir-styles]"), "styles should live in the shadow root");
    assert.ok(report.shadowRoot.querySelector(".ir-toolbar"), "the report UI should render in the shadow root");
    assert.equal(report.shadowRoot.querySelector(".ir-toolbar").getAttribute("part"), "toolbar");
    assert.equal(report.shadowRoot.querySelector(".ir-table").getAttribute("part"), "table");
    assert.equal(report.shadowRoot.querySelector(".ir-report-select").value, "orders");
    assert.equal(report.shadowRoot.querySelector(".ir-report-select").closest("label").hidden, false);
    assert.equal(document.querySelector("link[data-ir-css]"), null, "the bundle should not inject global CSS");
    assert.equal(document.querySelector(".ir-toolbar"), null, "internal elements should not leak into the host DOM");
    assert.equal(report.apiBase, "/custom-report-api/");
    assert.ok(requests.every(r => r.url === "/custom-report-api" || r.url.startsWith("/custom-report-api/")));
    assert.ok(requests.some(r => r.url === "/custom-report-api/orders/schema"));
    assert.ok(
        requests.some(r => r.url === "/custom-report-api/orders/query" && r.method === "POST"),
        `expected a query request; received ${JSON.stringify(requests)}`);

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    const menu = report.shadowRoot.querySelector(".ir-popup");
    assert.ok(menu, "menus should remain in the component shadow root");
    assert.equal(menu.getAttribute("part"), "menu");
    [...menu.querySelectorAll(".ir-menu-item")]
        .find(item => item.textContent.includes("Columns"))
        .click();
    assert.equal(report.shadowRoot.querySelector(".ir-popup"), null);
    assert.equal(report.shadowRoot.querySelector(".ir-dialog").getAttribute("part"), "dialog");

    report.remove();
    assert.equal(report.shadowRoot.querySelector(".ir-dialog"), null, "transient UI should be disposed on unmount");
});

test("an unavailable preferred report falls back without requesting it", async () => {
    requests.length = 0;
    visibleReports = [{ name: "allowed", title: "Allowed Report" }];

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "not-allowed");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/allowed/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.equal(report.reportName, "allowed");
    assert.equal(report.shadowRoot.querySelector(".ir-report-select").value, "allowed");
    assert.ok(requests.some(r => r.url === "/custom-report-api/allowed/schema"));
    assert.ok(requests.some(r => r.url === "/custom-report-api/allowed/query"));
    assert.ok(!requests.some(r => r.url.includes("not-allowed")));

    report.remove();
});

test("a visible preferred report that cannot initialize falls back to another visible report", async () => {
    requests.length = 0;
    visibleReports = [
        { name: "broken", title: "Broken Report" },
        { name: "allowed", title: "Allowed Report" },
    ];
    unloadableReports = new Set(["broken"]);

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "broken");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/allowed/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.equal(report.reportName, "allowed");
    assert.ok(requests.some(r => r.url === "/custom-report-api/broken/schema"));
    assert.ok(!requests.some(r => r.url === "/custom-report-api/broken/saved"));
    assert.ok(!requests.some(r => r.url === "/custom-report-api/broken/query"));
    assert.ok(requests.some(r => r.url === "/custom-report-api/allowed/query"));

    report.remove();
    unloadableReports = new Set();
});
