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
    requests.push({ url: String(url), method: options.method ?? "GET", body: options.body });
    if (String(url) === "/custom-report-api") return json(visibleReports);
    if (String(url).endsWith("/schema")) {
        const name = /\/([^/]+)\/schema$/.exec(String(url))?.[1];
        if (unloadableReports.has(name))
            return json({ title: "Report unavailable" }, { status: 503 });
        return json({
            stateVersion: 2,
            // labels here mirror the server contract: friendly names reach the client
            // only as part of the default report; column metadata stays neutral.
            defaultState: { page: { index: 1, size: 25 }, view: { mode: "grid" }, labels: { ID: "Ident" } },
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

test("labels resolve client-side: default report seeds them, rename overrides, clearing restores", async () => {
    requests.length = 0;
    visibleReports = [{ name: "orders", title: "Orders" }];

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    // Wait on rendered outcomes, never on request counts: requests are recorded
    // synchronously at click time, long before the response has been rendered.
    const settle = async condition => {
        for (let attempt = 0; attempt < 40 && !condition(); attempt++)
            await new Promise(resolve => setTimeout(resolve, 5));
    };
    const headerText = () => report.shadowRoot.querySelector("th.ir-th-menu")?.textContent.trim();

    // The server sent neutral column metadata (label "ID"); the friendly name
    // arrived only inside defaultState.labels and is applied by the client.
    await settle(() => headerText() === "Ident");
    assert.equal(headerText(), "Ident", "the default report's labels should drive the header");

    const rename = async value => {
        report.shadowRoot.querySelector("th.ir-th-menu").click();
        [...report.shadowRoot.querySelectorAll(".ir-menu-item")]
            .find(item => item.textContent.includes("Rename"))
            .click();
        const input = report.shadowRoot.querySelector(".ir-dialog input");
        input.value = value;
        report.shadowRoot.querySelector(".ir-dialog .ir-btn-primary").click();
        await settle(() => !report.shadowRoot.querySelector(".ir-dialog"));
        // Booleans only: a DOM element in a failed assertion makes the reporter
        // serialize the whole happy-dom graph.
        assert.equal(!report.shadowRoot.querySelector(".ir-dialog"), true, "the dialog should close on success");
        return JSON.parse(requests.filter(r => r.url.endsWith("/query")).at(-1).body);
    };

    assert.deepEqual((await rename("Ticket")).labels, { ID: "Ticket" });
    assert.equal(headerText(), "Ticket", "the override should render without server involvement");

    // Clearing drops the entry — display falls back to the server's neutral label —
    // but the map itself stays: an explicit {} still overrides a report default.
    assert.deepEqual((await rename("")).labels, {});
    assert.equal(headerText(), "ID");

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
