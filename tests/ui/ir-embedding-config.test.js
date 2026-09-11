import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { hydratedResult, reportState } from "./report-state-fixture.js";
import { reportControlNames } from "../../src/client/report/schema.js";

const window = new Window({ url: "https://host.example/" });
Object.assign(globalThis, {
    window, document: window.document, HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot, Node: window.Node, customElements: window.customElements,
    CustomEvent: window.CustomEvent, requestAnimationFrame: callback => setTimeout(callback, 0),
    Option: function(text = "", value = "") { const option = window.document.createElement("option"); option.text = text; option.value = value; return option; },
});
const requests = [];
let failQuery = false;
const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
globalThis.fetch = async (url, options = {}) => {
    const path = String(url);
    requests.push({ path, method: options.method ?? "GET", body: options.body && JSON.parse(options.body) });
    if (path.endsWith("/whoami")) return json({ identity: "reader" });
    if (path.endsWith("/schema")) return json({ title: "Orders", defaultState: reportState(),
        columns: [{ name: "ID", label: "ID", type: "number" }], features: reportControlNames,
        editLink: { urlTemplate: "/orders/{ID}" }, limits: { defaultPageSize: 25, maxPageSize: 100 },
        capabilities: { aggregateFunctions: {}, expressionFunctions: [] } });
    if (path.endsWith("/orders")) return json([]);
    if (path.includes("/default") || path.endsWith("/87")) {
        const result = hydratedResult();
        const size = new URL(path, "https://host.example").searchParams.get("pageSize");
        if (size) { result.document.page = { index: 1, size: Number(size) }; result.page = result.document.page; }
        return json({ summary: path.endsWith("/87") ? { id: 87, title: "Saved", reportName: "orders" } : null, result });
    }
    if (path.endsWith("/query")) {
        if (failQuery) return json({ title: "Invalid starting document" }, 400);
        const result = hydratedResult(JSON.parse(options.body));
        result.page = result.document.page ?? { index: 1, size: 25 };
        return json(result);
    }
    return json({}, 404);
};
await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir.js");

async function mount(configure) {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.apiBase = "/embedding";
    configure?.(report);
    const completed = new Promise(resolve => report.addEventListener("ir-query-complete", resolve, { once: true }));
    document.body.append(report);
    await Promise.race([completed, new Promise((_, reject) => setTimeout(() => reject(new Error("Report failed to load")), 1500))]);
    return report;
}

test("initial documents query directly, detach input, and isolate two embeddings", async () => {
    requests.length = 0;
    const initial = reportState(); initial.search = "pending";
    const first = await mount(report => { report.initialDocument = initial; report.initialPageSize = 12; initial.search = "changed outside"; });
    assert.equal(first.getReportDocument().search, "pending");
    assert.equal(first.getReportDocument().page.size, 12);
    assert.equal(requests.filter(request => request.path.endsWith("/query")).length, 1);
    assert.equal(requests.some(request => request.path.includes("/default")), false);
    const state = first.initialDocument; state.search = "mutated getter";
    assert.equal(first.initialDocument.search, "pending");
    const second = await mount();
    assert.notEqual(second.getReportDocument().search, "pending");
    assert.equal(first.getReportDocument().search, "pending");
    first.remove(); second.remove();
});

test("cosmetic attributes and control properties preserve working state and do not query", async () => {
    const report = await mount(element => { element.reportTitle = "<Orders>"; element.controls = ["search", "filter"]; });
    const before = report.getReportDocument(); const count = requests.length;
    assert.equal(report.shadowRoot.querySelector(".ir-heading").textContent, "<Orders>");
    assert.equal(report.isControlEnabled("download"), false);
    report.setControlEnabled("download", true);
    assert.equal(report.isControlEnabled("download"), true);
    report.columnPresentation = { ID: { label: "Identifier", helpText: "Record key" } };
    assert.match(report.shadowRoot.querySelector("thead").textContent, /Identifier/);
    report.controls = [];
    report.clearControlOverrides();
    assert.equal(report.isControlEnabled("search"), false);
    report.controls = null;
    assert.equal(report.isControlEnabled("search"), true);
    report.reportTitle = null;
    assert.equal(report.shadowRoot.querySelector(".ir-heading").hidden, true);
    assert.deepEqual(report.getReportDocument(), before);
    assert.equal(requests.length, count);
    report.remove();
});

test("saved selection wins over starting state; page size seeds the default without a second query", async () => {
    requests.length = 0;
    const report = await mount(element => { element.initialDocument = reportState(); element.initialPageSize = 8; element.setAttribute("saved-report", "87"); });
    assert.equal(report.reportId, "87");
    assert.equal(requests.some(request => request.path.includes("pageSize=")), false);
    assert.equal(requests.some(request => request.method === "POST"), false);
    report.remove();
    const defaultReport = await mount(element => { element.initialPageSize = 8; });
    assert.equal(defaultReport.getReportDocument().page.size, 8);
    assert.ok(requests.some(request => request.path.endsWith("/default?pageSize=8")));
    defaultReport.remove();
});

test("configuration rejects invalid values and edit keys without a server projection", async () => {
    const report = await mount();
    assert.throws(() => { report.controls = ["unknown"]; }, TypeError);
    assert.throws(() => { report.initialPageSize = 0; }, TypeError);
    assert.throws(() => { report.columnPresentation = { ID: { filterable: true } }; }, TypeError);
    assert.throws(() => { report.createLink = { url: "javascript:alert(1)" }; }, TypeError);
    assert.throws(() => { report.editLink = { urlTemplate: "/edit/{SECRET}" }; }, /server editLink/);
    report.editLink = { urlTemplate: "/form?id={ID}", label: "Open" };
    assert.match(report.shadowRoot.querySelector(".ir-cell-edit").getAttribute("href"), /\/form\?id=1/);
    report.createLink = { mode: "event", label: "New order" };
    let created = false; report.addEventListener("ir-create", () => { created = true; });
    report.shadowRoot.querySelector(".ir-createbtn").click();
    assert.equal(created, true);
    report.remove();
});

test("invalid initial query does not fall back to another default", async () => {
    requests.length = 0; failQuery = true;
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders"); report.apiBase = "/embedding"; report.initialDocument = reportState();
    document.body.append(report);
    for (let attempt = 0; attempt < 100 && !report.shadowRoot.querySelector('[role="alert"]')?.textContent; attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
    assert.match(report.shadowRoot.querySelector('[role="alert"]').textContent, /Invalid starting document/);
    assert.equal(requests.some(request => request.path.includes("/default")), false);
    report.remove(); failQuery = false;
});
