// Transactional state-document lifecycle: apply is atomic against throwing
// mutators, overlapping applies roll back to the last VALIDATED state (never to
// an aborted intermediate), and failed saved-report loads restore doc,
// selection, and search together. Also the honest-error policies: whoami and
// saved-list failures other than 404 surface instead of passing for "disabled".

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

// Configurable fetch: statuses per endpoint, a one-shot query failure, and a
// hold mode that parks /query requests for manual, out-of-order settlement.
const requests = [];
let whoamiStatus = 200;
let savedListStatus = 200;
let savedReports = [];
let savedDocuments = new Map();
let failNextQuery = null;   // { problem, status } consumed by the next /query
let holdQueries = false;
const heldQueries = [];

const json = (value, status = 200) => new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
});
const queryResult = () => json({
    columns: [{ name: "ID", label: "ID", type: "number" }],
    rows: [{ ID: 1 }],
    page: { index: 1, size: 25 },
    totalRows: 1,
    aggregates: {},
    highlights: [],
    ignored: [],
});

globalThis.fetch = (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET", body: options.body });
    const path = String(url);
    if (path.endsWith("/schema")) {
        return Promise.resolve(json({
            stateVersion: 3,
            defaultState: {
                v: 3,
                page: { index: 1, size: 25 },
                pipeline: [{ shape: { kind: "source" }, layer: {} }],
            },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [{ name: "ID", label: "ID", type: "number" }],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
        }));
    }
    if (path.endsWith("/whoami")) {
        return Promise.resolve(whoamiStatus === 200
            ? json({ identity: "test-user" })
            : new Response(null, { status: whoamiStatus }));
    }
    if (path.endsWith("/saved")) {
        return Promise.resolve(savedListStatus === 200
            ? json(savedReports)
            : new Response(null, { status: savedListStatus }));
    }
    const savedId = /\/saved\/([^/]+)$/.exec(path)?.[1];
    if (savedId) {
        return Promise.resolve(savedDocuments.has(savedId)
            ? json(savedDocuments.get(savedId))
            : new Response(null, { status: 404 }));
    }
    if (path.endsWith("/query")) {
        if (holdQueries) {
            return new Promise((resolve, reject) => {
                const abort = () => reject(Object.assign(new Error("aborted"), { name: "AbortError" }));
                if (options.signal?.aborted) return abort();
                options.signal?.addEventListener("abort", abort);
                heldQueries.push({
                    body: options.body,
                    succeed: () => resolve(queryResult()),
                    fail: (problem, status) => resolve(json(problem, status)),
                });
            });
        }
        if (failNextQuery) {
            const { problem, status } = failNextQuery;
            failNextQuery = null;
            return Promise.resolve(json(problem, status));
        }
        return Promise.resolve(queryResult());
    }
    return Promise.resolve(new Response(null, { status: 404 }));
};

await import("../../src/InteractiveReport.AspNetCore/Ui/dist/ir.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 60 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount() {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/txn-api");
    document.body.append(report);
    await settle(() => report.shadowRoot?.querySelector("tbody tr"));
    return report;
}

const errorText = report =>
    report.shadowRoot.querySelector(".ir-banner-error")?.textContent ?? "";
const warnText = report =>
    report.shadowRoot.querySelector(".ir-banner-warn")?.textContent ?? "";

test("a mutator that throws mid-way leaves the live document untouched", async () => {
    requests.length = 0;
    const report = await mount();

    const docBefore = report.doc;
    const serialized = JSON.stringify(report.doc);
    await assert.rejects(
        report.apply(d => {
            d.search = "partial";
            (d.pipeline[0].layer.filters ??= []).push({ enabled: true, expr: "ID = 1" });
            throw new Error("staged validation failed");
        }),
        /staged validation failed/);

    assert.equal(report.doc === docBefore, true, "the live doc object must not be replaced");
    assert.equal(JSON.stringify(report.doc), serialized, "no partial mutation may survive");
    assert.equal(requests.filter(r => r.url.endsWith("/query")).length, 1,
        "a failed mutator must not reach the server");

    report.remove();
});

test("overlapping applies roll back to the last validated state, not an aborted intermediate", async () => {
    requests.length = 0;
    const report = await mount();
    assert.equal(report.doc.search ?? "", "");

    holdQueries = true;
    const applyA = report.apply(d => { d.search = "AAA"; });
    await settle(() => heldQueries.length === 1);
    const applyB = report.apply(d => { d.search = "BBB"; });
    await settle(() => heldQueries.length === 2);

    const rejection = assert.rejects(applyB, /validation/i);
    heldQueries[1].fail({ title: "Report state failed validation" }, 400);
    await rejection;
    await applyA;   // A's aborted query resolves quietly — it must not commit
    holdQueries = false;
    heldQueries.length = 0;

    assert.equal(report.doc.search ?? "", "",
        "the rollback must land on validated ground, not on A's never-validated mutation");

    // The widget stays operational: a fresh apply commits normally.
    await report.apply(d => { d.search = "ok"; });
    assert.equal(report.doc.search, "ok");

    report.remove();
});

test("a saved-report load whose query fails restores doc, selection, and search together", async () => {
    requests.length = 0;
    savedReports = [{
        id: "saved-1", reportName: "orders", title: "Acme Only",
        isGlobal: false, owner: "test-user", mine: true,
    }];
    savedDocuments = new Map([["saved-1", {
        summary: savedReports[0],
        state: { v: 3, search: "Acme", page: { index: 1, size: 25 }, pipeline: [{ shape: { kind: "source" }, layer: {} }] },
    }]]);
    const report = await mount();

    failNextQuery = { problem: { title: "The query could not run" }, status: 500 };
    const select = report.shadowRoot.querySelector(".ir-saved-select");
    select.value = "saved-1";
    select.dispatchEvent(new window.Event("change", { bubbles: true }));
    await settle(() => errorText(report).includes("could not run"));

    assert.equal(report.currentSaved, null,
        "the failed load must not leave the new report selected over the old grid");
    assert.equal(report.doc.search ?? "", "", "the working copy reverts to the validated state");
    assert.equal(report.els.search.value, "", "the search box follows the reverted doc");
    assert.equal(select.value, "", "the select returns to the previous selection");

    report.remove();
    savedReports = [];
    savedDocuments = new Map();
});

test("a saved report deleted elsewhere reports precisely and refreshes the list", async () => {
    requests.length = 0;
    savedReports = [{
        id: "ghost-1", reportName: "orders", title: "Ghost",
        isGlobal: false, owner: "test-user", mine: true,
    }];
    savedDocuments = new Map();   // the row exists in the list; the document is gone
    const report = await mount();

    const select = report.shadowRoot.querySelector(".ir-saved-select");
    select.value = "ghost-1";
    select.dispatchEvent(new window.Event("change", { bubbles: true }));
    await settle(() => errorText(report).includes("no longer available"));

    assert.match(errorText(report), /no longer available/i,
        "a missing saved report must not present as 'Report not found'");
    assert.equal(report.currentSaved, null);
    assert.equal(report.doc.search ?? "", "");

    report.remove();
    savedReports = [];
});

test("a saved report with a stale recorded schema is adopted — the server is the judge", async () => {
    requests.length = 0;
    savedReports = [{
        id: "stale-1", reportName: "orders", title: "Stale",
        isGlobal: false, owner: "test-user", mine: true,
    }];
    savedDocuments = new Map([["stale-1", {
        summary: savedReports[0],
        state: {
            v: 3,
            schema: { GONE: "number" },   // authored against a schema that moved on
            search: "Acme",
            page: { index: 1, size: 25 },
            pipeline: [{ shape: { kind: "source" }, layer: {} }],
        },
    }]]);
    const report = await mount();

    const select = report.shadowRoot.querySelector(".ir-saved-select");
    select.value = "stale-1";
    select.dispatchEvent(new window.Event("change", { bubbles: true }));
    // Wait on a rendered outcome (the search chip), not on the request log.
    await settle(() => report.shadowRoot.querySelector(".ir-chips")?.textContent.includes("Acme"));

    assert.equal(errorText(report), "", "no client-side drift gate — the document runs");
    assert.equal(report.currentSaved?.id, "stale-1");
    assert.equal(report.doc.search, "Acme");
    assert.equal("schema" in report.doc, false, "the retired snapshot key is dropped on adoption");
    const posted = JSON.parse(requests.filter(r => r.url.endsWith("/query")).at(-1).body);
    assert.equal("schema" in posted, false, "and never travels back to the server");

    report.remove();
    savedReports = [];
    savedDocuments = new Map();
});

test("whoami failures other than 404/401 warn instead of passing for anonymous", async () => {
    requests.length = 0;
    whoamiStatus = 500;
    const report = await mount();

    assert.match(warnText(report), /sign-in state could not be determined/i);
    assert.equal(!!report.shadowRoot.querySelector("tbody tr"), true, "the report still loads");
    report.remove();

    whoamiStatus = 404;
    const quiet = await mount();
    assert.equal(warnText(quiet), "", "404 means the endpoint is disabled — no warning");
    quiet.remove();
    whoamiStatus = 200;
});

test("a saved-list failure surfaces instead of presenting as 'no saved reports'", async () => {
    requests.length = 0;
    savedListStatus = 500;
    const report = await mount();

    assert.match(warnText(report), /saved reports could not be loaded/i);
    assert.equal(!!report.shadowRoot.querySelector("tbody tr"), true, "the report still loads");
    report.remove();

    savedListStatus = 404;
    const disabled = await mount();
    assert.equal(warnText(disabled), "", "404 means the feature is off — no warning");
    disabled.remove();
    savedListStatus = 200;
});
