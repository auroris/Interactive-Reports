// Transactional state-document lifecycle: apply is atomic against throwing
// mutators, overlapping operations roll back to the last VALIDATED state (never
// to an aborted or concurrently saved intermediate), and saved-report loads are
// last-request-wins. Also the honest-error and degraded-list-refresh policies.

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
let holdSavedDocuments = false;
const heldSavedDocuments = [];
let holdSaves = false;
const heldSaves = [];
let holdSavedLists = false;
const heldSavedLists = [];
let savedMutationResult = null;

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
    const method = options.method ?? "GET";
    requests.push({ url: String(url), method, body: options.body });
    const path = String(url);
    if (path.endsWith("/schema")) {
        return Promise.resolve(json({
            defaultState: {
                page: { index: 1, size: 25 },
                ...reportState(),
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
    if (path.endsWith("/saved") && method === "GET") {
        if (holdSavedLists) {
            return new Promise(resolve => heldSavedLists.push({
                url: path,
                succeed: reports => resolve(json(reports)),
            }));
        }
        return Promise.resolve(savedListStatus === 200
            ? json(savedReports)
            : new Response(null, { status: savedListStatus }));
    }
    if (path.endsWith("/saved") && method === "POST") {
        if (holdSaves) {
            return new Promise(resolve => heldSaves.push({
                body: options.body,
                succeed: summary => resolve(json(summary, 201)),
            }));
        }
        return Promise.resolve(json(savedMutationResult, 201));
    }
    const savedId = /\/saved\/([^/]+)$/.exec(path)?.[1];
    if (savedId && method === "GET") {
        if (holdSavedDocuments) {
            return new Promise(resolve => heldSavedDocuments.push({
                id: savedId,
                succeed: document => resolve(json(document)),
            }));
        }
        return Promise.resolve(savedDocuments.has(savedId)
            ? json(savedDocuments.get(savedId))
            : new Response(null, { status: 404 }));
    }
    if (savedId && method === "DELETE")
        return Promise.resolve(new Response(null, { status: 204 }));
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

const savedSelect = report => report.shadowRoot.querySelector(".ir-saved-select");

const selectSaved = (report, id) => {
    const select = savedSelect(report);
    select.value = id;
    select.dispatchEvent(new window.Event("change", { bubbles: true }));
};

const clickAction = (report, label) => {
    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    const item = [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item")]
        .find(candidate => candidate.textContent.includes(label));
    assert.ok(item, `Actions contains ${label}`);
    item.click();
};

const beginSaveAs = (report, title) => {
    clickAction(report, "Save As");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    assert.ok(dialog, "Save As opens a dialog");
    dialog.querySelector('input[type="text"]').value = title;
    dialog.querySelector(".ir-btn-primary").click();
};

const errorText = report =>
    report.shadowRoot.querySelector(".ir-banner-error")?.textContent ?? "";
const warnText = report =>
    report.shadowRoot.querySelector(".ir-banner-warn")?.textContent ?? "";

test("an invalid public replacement leaves the accepted document untouched", async () => {
    requests.length = 0;
    const report = await mount();

    const before = report.getReportDocument();
    await assert.rejects(
        report.submitReportDocument({ ...before, extension: 1n }),
        /JSON-compatible object/);

    assert.deepEqual(report.getReportDocument(), before, "no partial replacement may survive");
    assert.equal(requests.filter(r => r.url.endsWith("/query")).length, 1,
        "an invalid replacement must not reach the server");

    report.remove();
});

test("overlapping submissions roll back to the last validated state, not an aborted intermediate", async () => {
    requests.length = 0;
    const report = await mount();
    assert.equal(report.getReportDocument().search ?? "", "");

    holdQueries = true;
    const a = report.getReportDocument();
    a.search = "AAA";
    const applyA = report.submitReportDocument(a);
    await settle(() => heldQueries.length === 1);
    const b = report.getReportDocument();
    b.search = "BBB";
    const applyB = report.submitReportDocument(b);
    await settle(() => heldQueries.length === 2);

    const rejection = assert.rejects(applyB, /validation/i);
    heldQueries[1].fail({ title: "Report state failed validation" }, 400);
    await rejection;
    await applyA;   // A's aborted query resolves quietly — it must not commit
    holdQueries = false;
    heldQueries.length = 0;

    assert.equal(report.getReportDocument().search ?? "", "",
        "the rollback must land on validated ground, not on A's never-validated mutation");

    // The widget stays operational: a fresh submission commits normally.
    const next = report.getReportDocument();
    next.search = "ok";
    await report.submitReportDocument(next);
    assert.equal(report.getReportDocument().search, "ok");

    report.remove();
});

test("rapid filter-chip removals coalesce into one query with the final document", async () => {
    requests.length = 0;
    const report = await mount();
    const document = report.getReportDocument();
    document.tables.base.composables.push({
        kind: "filter",
        filters: [
            { expr: "ID > 1", enabled: true },
            { expr: "ID > 2", enabled: true },
            { expr: "ID > 3", enabled: true },
        ],
    });
    await report.submitReportDocument(document);

    const before = requests.filter(request => request.url.endsWith("/query")).length;
    const removeButtons = [...report.shadowRoot.querySelectorAll(
        '.ir-chip[data-kind="filter"] .ir-chip-x')];
    assert.equal(removeButtons.length, 3);

    removeButtons.forEach(button => button.click());
    assert.equal(requests.filter(request => request.url.endsWith("/query")).length, before,
        "the burst must remain client-side until the trailing edge");

    await settle(() => report.shadowRoot.querySelectorAll('.ir-chip[data-kind="filter"]').length === 0);
    const queries = requests.filter(request => request.url.endsWith("/query"));
    assert.equal(queries.length, before + 1, "all removals produce one server query");
    const submitted = JSON.parse(queries.at(-1).body);
    assert.deepEqual(
        submitted.tables.base.composables.find(composable => composable.kind === "filter").filters,
        [],
        "the single request carries every deletion");

    const hostDocument = report.getReportDocument();
    hostDocument.search = "host refresh";
    const hostSubmission = report.submitReportDocument(hostDocument);
    assert.equal(requests.filter(request => request.url.endsWith("/query")).length, before + 2,
        "an explicit host submission bypasses the user debounce");
    await hostSubmission;

    report.remove();
});

test("saved-report loads are last-request-wins even when GET responses arrive out of order", async () => {
    requests.length = 0;
    const savedA = { id: "saved-a", title: "A", mine: true };
    const savedB = { id: "saved-b", title: "B", mine: true };
    savedReports = [savedA, savedB];
    const report = await mount();

    holdSavedDocuments = true;
    heldSavedDocuments.length = 0;
    selectSaved(report, savedA.id);
    selectSaved(report, savedB.id);
    await settle(() => heldSavedDocuments.length === 2);

    const state = search => ({
        search,
        page: { index: 1, size: 25 },
        ...reportState(),
    });
    heldSavedDocuments.find(request => request.id === savedB.id)
        .succeed({ summary: savedB, state: state("B") });
    await settle(() => report.getReportDocument().search === "B");
    heldSavedDocuments.find(request => request.id === savedA.id)
        .succeed({ summary: savedA, state: state("A") });
    await new Promise(resolve => setTimeout(resolve, 10));

    assert.equal(report.getReportDocument().search, "B");
    assert.equal(savedSelect(report).value, savedB.id);

    holdSavedDocuments = false;
    heldSavedDocuments.length = 0;
    savedReports = [];
    report.remove();
});

test("a concurrent save cannot promote an unvalidated live document to last-good", async () => {
    requests.length = 0;
    savedReports = [];
    const report = await mount();

    holdSaves = true;
    holdQueries = true;
    heldSaves.length = 0;
    heldQueries.length = 0;

    beginSaveAs(report, "Saved A");
    await settle(() => heldSaves.length === 1);
    const bad = report.getReportDocument();
    bad.search = "BAD";
    const apply = report.submitReportDocument(bad);
    await settle(() => heldQueries.length === 1);

    const summary = { id: "saved-a", title: "Saved A", mine: true };
    savedReports = [summary];
    heldSaves[0].succeed(summary);
    await settle(() => savedSelect(report).value === summary.id);

    const rejection = assert.rejects(apply, /validation/i);
    heldQueries[0].fail({ title: "Report state failed validation" }, 400);
    await rejection;

    assert.equal(report.getReportDocument().search ?? "", "",
        "the failed query restores the previously rendered document");

    holdSaves = false;
    holdQueries = false;
    heldSaves.length = 0;
    heldQueries.length = 0;
    savedReports = [];
    report.remove();
});

test("a successful save remains in the local list when its refresh fails", async () => {
    requests.length = 0;
    savedReports = [];
    savedListStatus = 200;
    const report = await mount();

    savedMutationResult = { id: "saved-new", title: "New report", mine: true };
    savedListStatus = 500;
    beginSaveAs(report, "New report");
    await settle(() => warnText(report).includes("could not be refreshed"));

    assert.equal(!!savedSelect(report).querySelector('option[value="saved-new"]'), true);
    assert.equal(savedSelect(report).value, "saved-new");
    assert.equal(report.shadowRoot.querySelector(".ir-saved").hidden, false);
    assert.match(warnText(report), /could not be refreshed/i);

    savedMutationResult = null;
    savedListStatus = 200;
    report.remove();
});

test("a saved-list refresh cannot cross a report switch", async () => {
    requests.length = 0;
    savedReports = [];
    savedListStatus = 200;
    const report = await mount();

    savedMutationResult = { id: "saved-orders", title: "Orders copy", mine: true };
    holdSavedLists = true;
    heldSavedLists.length = 0;
    beginSaveAs(report, "Orders copy");
    await settle(() => heldSavedLists.length === 1);
    assert.match(heldSavedLists[0].url, /\/orders\/saved$/);

    holdSavedLists = false;
    const invoiceSaved = { id: "saved-invoices", title: "Invoices copy", mine: true };
    savedReports = [invoiceSaved];
    report.setAttribute("report", "invoices");
    await settle(() => report.reportName === "invoices"
        && savedSelect(report).querySelector(`option[value="${invoiceSaved.id}"]`));

    heldSavedLists[0].succeed([
        { id: "late-orders", title: "Late orders response", mine: true },
    ]);
    await new Promise(resolve => setTimeout(resolve, 10));

    assert.equal(!!savedSelect(report).querySelector('option[value="late-orders"]'), false,
        "the completed Orders request must not replace the Invoices list");
    assert.equal(savedSelect(report).value, "",
        "the current report's selector remains on its own default state");

    savedMutationResult = null;
    savedReports = [];
    heldSavedLists.length = 0;
    report.remove();
});

test("a successful delete stays removed from the local list when its refresh fails", async () => {
    requests.length = 0;
    const summary = { id: "saved-delete", title: "Delete me", mine: true };
    savedReports = [summary];
    savedDocuments = new Map([[summary.id, {
        summary,
        state: {
            search: "delete",
            page: { index: 1, size: 25 },
            ...reportState(),
        },
    }]]);
    savedListStatus = 200;
    const report = await mount();
    selectSaved(report, summary.id);
    await settle(() => report.getReportDocument().search === "delete");

    savedListStatus = 500;
    clickAction(report, "Delete");
    await settle(() => report.shadowRoot.querySelector("dialog.ir-dialog-modal"));
    report.shadowRoot.querySelector("dialog.ir-dialog-modal .ir-btn-primary").click();
    await settle(() => warnText(report).includes("could not be refreshed"));

    assert.equal(!!savedSelect(report).querySelector(`option[value="${summary.id}"]`), false);
    assert.notEqual(savedSelect(report).value, summary.id);
    assert.match(warnText(report), /could not be refreshed/i);

    savedDocuments = new Map();
    savedReports = [];
    savedListStatus = 200;
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
        state: { search: "Acme", page: { index: 1, size: 25 }, ...reportState() },
    }]]);
    const report = await mount();

    failNextQuery = { problem: { title: "The query could not run" }, status: 500 };
    const select = report.shadowRoot.querySelector(".ir-saved-select");
    select.value = "saved-1";
    select.dispatchEvent(new window.Event("change", { bubbles: true }));
    await settle(() => errorText(report).includes("could not run"));

    assert.equal(report.getReportDocument().search ?? "", "",
        "the working copy reverts to the validated state");
    assert.equal(report.shadowRoot.querySelector(".ir-search-input").value, "",
        "the search box follows the reverted doc");
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
    assert.equal(savedSelect(report).value, "");
    assert.equal(report.getReportDocument().search ?? "", "");

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
            schema: { GONE: "number" },   // authored against a schema that moved on
            search: "Acme",
            page: { index: 1, size: 25 },
            ...reportState(),
        },
    }]]);
    const report = await mount();

    const select = report.shadowRoot.querySelector(".ir-saved-select");
    select.value = "stale-1";
    select.dispatchEvent(new window.Event("change", { bubbles: true }));
    // Wait on a rendered outcome (the search chip), not on the request log.
    await settle(() => report.shadowRoot.querySelector(".ir-chips")?.textContent.includes("Acme"));

    assert.equal(errorText(report), "", "no client-side drift gate — the document runs");
    assert.equal(savedSelect(report).value, "stale-1");
    assert.equal(report.getReportDocument().search, "Acme");
    assert.equal("schema" in report.getReportDocument(), false,
        "the retired snapshot key is dropped on adoption");
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
