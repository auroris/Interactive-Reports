// Help-page fallback and failure paths. Separate from ir-help.test.js because the client
// caches the guide per locale for the life of the module: these cases need a locale whose
// first request fails, which a process that already loaded it cannot reproduce.

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

const HELP_EN = `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Guide</title>
<style>.ir-help h1 { color: red; }</style></head>
<body class="ir-help-page"><article class="ir-help"><h1 id="top">Interactive Report</h1>
<h2 id="searching">Searching</h2></article></body></html>`;

const requests = [];
const json = value => new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
});
let englishHelpAvailable = true;

globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET" });
    const target = String(url);
    if (target.endsWith("/help.fr-CA.html")) return new Response(null, { status: 404 });
    if (target.endsWith("/help.en.html")) {
        return englishHelpAvailable
            ? new Response(HELP_EN, { status: 200, headers: { "Content-Type": "text/html" } })
            : new Response(null, { status: 404 });
    }
    if (target.endsWith("/schema")) {
        return json({
            defaultState: { page: { index: 1, size: 25 }, ...reportState() },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [{ name: "ID", label: "ID", type: "number" }],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
        });
    }
    if (target.endsWith("/whoami")) return json({ identity: "test-user" });
    const family = /^\/help-api\/([^/?]+)$/.exec(target)?.[1];
    if ((options.method ?? "GET") === "GET" && family)
        return json([{ id: 1, reportName: family, title: "Default", isDefault: true, isGlobal: true }]);
    const document = /^\/help-api\/([^/?]+)\/(\d+)$/.exec(target);
    if ((options.method ?? "GET") === "GET" && document) {
        return json({
            summary: { id: Number(document[2]), reportName: document[1], title: "Default", isDefault: true, isGlobal: true },
            state: {},
        });
    }
    if (target.endsWith("/query")) {
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

await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 40 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount(lang) {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/help-api");
    if (lang) report.setAttribute("lang", lang);
    document.body.append(report);
    await settle(() => report.shadowRoot?.querySelector("tbody tr"));
    return report;
}

const helpRequests = () => requests.filter(r => /\/help\.[A-Za-z-]+\.html$/.test(r.url)).map(r => r.url.split("/").pop());

test("a locale without a guide falls back to the English page", async () => {
    requests.length = 0;
    const report = await mount("fr-CA");
    report.shadowRoot.querySelector(".ir-helpbtn").click();
    const dialog = report.shadowRoot.querySelector(".ir-help-window");
    assert.equal(dialog.querySelector(".ir-dialog-title-text").textContent, "Aide", "chrome stays localized");
    await settle(() => dialog.querySelector(".ir-help"));
    assert.deepEqual(helpRequests(), ["help.fr-CA.html", "help.en.html"]);
    assert.equal(dialog.querySelector(".ir-help h2").textContent, "Searching");
    dialog.querySelector(".ir-dialog-x").click();
    report.remove();
});

test("a missing guide reports the failure inside the window", async () => {
    englishHelpAvailable = false;
    requests.length = 0;
    const report = await mount();
    report.shadowRoot.querySelector(".ir-helpbtn").click();
    const dialog = report.shadowRoot.querySelector(".ir-help-window");
    await settle(() => !dialog.querySelector(".ir-dialog-error").hidden);
    assert.deepEqual(helpRequests(), ["help.en.html"]);
    assert.equal(dialog.querySelector(".ir-dialog-error").textContent, "The help page could not be loaded.");
    // A failure is not cached: the next opening tries again.
    dialog.querySelector(".ir-dialog-x").click();
    englishHelpAvailable = true;
    report.shadowRoot.querySelector(".ir-helpbtn").click();
    const retry = report.shadowRoot.querySelector(".ir-help-window");
    await settle(() => retry.querySelector(".ir-help"));
    assert.deepEqual(helpRequests(), ["help.en.html", "help.en.html"]);
    report.remove();
});
