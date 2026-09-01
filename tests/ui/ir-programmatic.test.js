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
    CustomEvent: window.CustomEvent,
    Option,
    Node: window.Node,
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

const requests = [];
let failNextQuery = null;
const json = (value, status = 200) => new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
});

globalThis.fetch = async (url, options = {}) => {
    const request = { url: String(url), method: options.method ?? "GET", body: options.body };
    requests.push(request);
    if (request.url.endsWith("/schema")) {
        return json({
            defaultState: {
                page: { index: 1, size: 25 },
                ...reportState(),
            },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [{ name: "ID", label: "ID", type: "number" }],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
            // A suggestion, deliberately narrower than the controls exercised below.
            features: ["search"],
        });
    }
    if (request.url.endsWith("/whoami")) return json({ identity: "test-user" });
    if (request.url.endsWith("/saved")) return json([]);
    if (request.url.endsWith("/query")) {
        if (failNextQuery) {
            const failure = failNextQuery;
            failNextQuery = null;
            return json(failure.problem, failure.status);
        }
        const document = JSON.parse(request.body);
        return json({
            document,
            columns: [{ name: "ID", label: "ID", type: "number" }],
            rows: [{ ID: 1 }],
            page: { index: document.page?.index ?? 1, size: document.page?.size ?? 25 },
            totalRows: 1,
            aggregates: {},
            highlights: [],
            ignored: [],
            elapsedMs: 1,
        });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.AspNetCore/Ui/dist/ir.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 60 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount(configure = null) {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/programmatic-api");
    configure?.(report);
    document.body.append(report);
    await settle(() => report.shadowRoot?.querySelector("tbody tr"));
    return report;
}

test("the public document API is detached, transactional, and emits query lifecycle events", async () => {
    requests.length = 0;
    const unattached = document.createElement("interactive-report");
    assert.throws(() => unattached.getReportDocument(), { name: "InvalidStateError" });

    const before = [];
    const complete = [];
    const report = await mount(element => {
        element.addEventListener("ir-before-query", event => {
            before.push({ source: event.detail.source, requestId: event.detail.requestId });
            if (event.detail.source === "initial") event.detail.document.search = "initial hook";
            if (event.detail.source === "host") event.detail.document.search = "host hook";
        });
        element.addEventListener("ir-query-complete", event => {
            complete.push({
                source: event.detail.source,
                document: structuredClone(event.detail.document),
                submitted: structuredClone(event.detail.submitted),
            });
            // Completion payloads are detached observations; changing them cannot mutate the widget.
            event.detail.document.search = "event tamper";
            event.detail.result.document.search = "result tamper";
        });
    });

    assert.equal(before[0].source, "initial");
    assert.equal(complete[0].source, "initial");
    assert.equal(report.getReportDocument().search, "initial hook");
    assert.equal(JSON.parse(requests.find(request => request.url.endsWith("/query")).body).search, "initial hook");

    const snapshot = report.getReportDocument();
    snapshot.search = "local only";
    assert.equal(report.getReportDocument().search, "initial hook", "the getter must not leak the working object");

    snapshot.page.index = 3;
    snapshot.extension = { retained: true };
    const result = await report.submitReportDocument(snapshot);
    assert.equal(result.document.search, "host hook");
    assert.equal(result.document.page.index, 3, "whole-document submission honors its requested page");
    assert.deepEqual(result.document.extension, { retained: true });
    assert.equal(report.getReportDocument().search, "host hook");
    assert.equal(complete.at(-1).source, "host");
    assert.equal(complete.at(-1).submitted.search, "host hook");

    // Returned results are detached from both lastResult and the current document.
    result.document.search = "caller tamper";
    assert.equal(report.getReportDocument().search, "host hook");

    const validated = report.getReportDocument();
    failNextQuery = { problem: { title: "Rejected document" }, status: 400 };
    const rejected = structuredClone(validated);
    rejected.search = "rejected";
    await assert.rejects(report.submitReportDocument(rejected), /Rejected document/);
    assert.deepEqual(report.getReportDocument(), validated, "a failed replacement rolls back atomically");

    const queryCount = requests.filter(request => request.url.endsWith("/query")).length;
    const cancel = event => {
        if (event.detail.source === "host") event.preventDefault();
    };
    report.addEventListener("ir-before-query", cancel);
    const canceled = structuredClone(validated);
    canceled.search = "canceled";
    assert.equal(await report.submitReportDocument(canceled), undefined);
    assert.equal(requests.filter(request => request.url.endsWith("/query")).length, queryCount);
    assert.deepEqual(report.getReportDocument(), validated, "a canceled replacement also restores validated state");
    report.removeEventListener("ir-before-query", cancel);

    await assert.rejects(
        report.submitReportDocument({ ...validated, extension: 1n }),
        /JSON-compatible object/);

    assert.equal(new Set(before.map(event => event.requestId)).size, before.length,
        "each attempted query receives a stable unique request id");
    report.remove();
});

test("client control overrides win over server suggestions and global disabled is reversible", async () => {
    requests.length = 0;
    const report = await mount();

    assert.equal(report.isControlEnabled("search"), true);
    assert.equal(report.isControlEnabled("filter"), false);
    assert.equal(report.shadowRoot.querySelector(".ir-search").hidden, false);
    assert.equal(requests.some(request => request.url.endsWith("/saved")), false);

    assert.equal(report.setControlEnabled("FILTER", true), true, "control names are case-insensitive");
    assert.equal(report.isControlEnabled("filter"), true);
    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    assert.equal(
        [...report.shadowRoot.querySelectorAll(".ir-menu-item")]
            .some(item => item.textContent.includes("Filter")),
        true,
        "a client can expose a control the server did not suggest");

    report.setControlOverrides({ search: false, sort: true, filter: false });
    assert.equal(report.shadowRoot.querySelector(".ir-search").hidden, true);
    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    const labels = [...report.shadowRoot.querySelectorAll(".ir-menu-item")]
        .map(item => item.textContent.trim());
    assert.equal(labels.some(label => label.includes("Sort")), true);
    assert.equal(labels.some(label => label.includes("Filter")), false);
    assert.deepEqual(report.getControlOverrides(), { filter: false, search: false, sort: true });

    report.setControlEnabled("search", null);
    assert.equal(report.isControlEnabled("search"), true, "null resumes following the server suggestion");
    assert.equal(report.shadowRoot.querySelector(".ir-search").hidden, false);

    report.setControlEnabled("savedReports", true);
    await settle(() => requests.some(request => request.url.endsWith("/saved")));
    assert.equal(report.isControlEnabled("savedReports"), true);

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    assert.ok(report.shadowRoot.querySelector(".ir-popup"));
    report.disabled = true;
    assert.equal(report.hasAttribute("disabled"), true);
    assert.equal(report.shadowRoot.querySelector('[part~="surface"]').hasAttribute("inert"), true);
    assert.equal(report.shadowRoot.querySelector('[part~="surface"]').getAttribute("aria-disabled"), "true");
    assert.equal(report.shadowRoot.querySelector(".ir-popup"), null, "disabling closes transient controls");
    report.disabled = false;
    assert.equal(report.shadowRoot.querySelector('[part~="surface"]').hasAttribute("inert"), false);

    report.clearControlOverrides();
    assert.deepEqual(report.getControlOverrides(), {});
    assert.equal(report.isControlEnabled("search"), true);
    assert.equal(report.isControlEnabled("filter"), false);
    assert.throws(() => report.setControlEnabled("not-a-control", true), /Unknown report control/);

    report.remove();
});
