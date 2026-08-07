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
let savedReports = [];
let savedDocuments = new Map();
const json = (value, init = {}) => new Response(JSON.stringify(value), {
    status: init.status ?? 200,
    headers: { "Content-Type": "application/json" },
});

globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET", body: options.body });
    if (String(url).endsWith("/schema")) {
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
    if (String(url).endsWith("/saved")) return json(savedReports);
    const savedId = /\/saved\/([^/]+)$/.exec(String(url))?.[1];
    if (savedId && savedDocuments.has(savedId)) return json(savedDocuments.get(savedId));
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
    assert.equal(report.shadowRoot.querySelector(".ir-report-select"), null);
    assert.equal(document.querySelector("link[data-ir-css]"), null, "the bundle should not inject global CSS");
    assert.equal(document.querySelector(".ir-toolbar"), null, "internal elements should not leak into the host DOM");
    assert.equal(report.apiBase, "/custom-report-api/");
    assert.ok(requests.every(r => r.url.startsWith("/custom-report-api/")));
    assert.ok(!requests.some(r => r.url === "/custom-report-api"), "the report catalog endpoint must not be requested");
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

test("the configured report is loaded directly and can be changed through its attribute", async () => {
    requests.length = 0;

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/orders/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    report.setAttribute("report", "order-feed");
    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/order-feed/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.equal(report.reportName, "order-feed");
    assert.ok(requests.some(r => r.url === "/custom-report-api/order-feed/schema"));
    assert.ok(requests.some(r => r.url === "/custom-report-api/order-feed/query"));
    assert.ok(!requests.some(r => r.url === "/custom-report-api"));

    report.remove();
});

test("a report attribute is required", async () => {
    requests.length = 0;

    const report = document.createElement("interactive-report");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);
    await new Promise(resolve => setTimeout(resolve, 1));

    assert.match(report.shadowRoot.querySelector(".ir-banner-error").textContent, /requires a non-empty report attribute/i);
    assert.equal(requests.length, 0);

    report.remove();
});

test("labels resolve client-side: default report seeds them, rename overrides, clearing restores", async () => {
    requests.length = 0;

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

test("saved-report loads a uniquely named saved report before the initial query", async () => {
    requests.length = 0;
    savedReports = [{
        id: "saved-1", reportName: "orders", title: "My Default",
        isGlobal: false, owner: "test-user", mine: true,
    }];
    savedDocuments = new Map([["saved-1", {
        summary: savedReports[0],
        state: { search: "Acme", page: { index: 1, size: 25 }, view: { mode: "grid" } },
    }]]);

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("saved-report", "my default");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/orders/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.equal(report.shadowRoot.querySelector(".ir-saved-select").value, "saved-1");
    assert.ok(requests.some(r => r.url === "/custom-report-api/saved/saved-1"));
    const queries = requests.filter(r => r.url === "/custom-report-api/orders/query");
    assert.equal(queries.length, 1, "the primary report should not be queried first");
    assert.equal(JSON.parse(queries[0].body).search, "Acme");

    report.remove();
    savedReports = [];
    savedDocuments = new Map();
});
