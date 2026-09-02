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
<p><a href="#searching">Jump</a></p><h2 id="searching">Searching</h2></article></body></html>`;
const HELP_FR = HELP_EN.replace('lang="en"', 'lang="fr-CA"').replace("Searching", "Recherche");

const requests = [];
const json = value => new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
});
const html = markup => new Response(markup, { status: 200, headers: { "Content-Type": "text/html" } });
globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET" });
    const target = String(url);
    if (target.endsWith("/help.fr-CA.html")) return html(HELP_FR);
    if (target.endsWith("/help.en.html")) return html(HELP_EN);
    if (target.endsWith("/schema")) {
        return json({
            defaultState: { page: { index: 1, size: 25 }, ...reportState() },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [{ name: "ID", label: "ID", type: "number" }],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
            features: [],
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

test("the Help button survives an empty feature list and opens the packaged guide", async () => {
    requests.length = 0;
    const report = await mount();
    const button = report.shadowRoot.querySelector(".ir-helpbtn");
    assert.equal(button.hidden, false, "help is not a report feature");
    assert.equal(button.getAttribute("aria-label"), "Help");
    assert.equal(report.shadowRoot.querySelector(".ir-actionsbtn").hidden, true, "control: the empty whitelist hid Actions");

    button.click();
    const dialog = report.shadowRoot.querySelector(".ir-dialog.ir-help-window");
    assert.equal(dialog.querySelector(".ir-dialog-title-text").textContent, "Help");
    await settle(() => dialog.querySelector(".ir-help"));

    assert.deepEqual(helpRequests(), ["help.en.html"]);
    assert.equal(dialog.querySelector(".ir-help h1").textContent, "Interactive Report");
    assert.equal(!!dialog.querySelector("style"), true, "the page's scoped stylesheet travels with it");
    assert.equal(!dialog.querySelector("title"), true, "head-only elements are dropped");
    assert.equal(dialog.querySelectorAll(".ir-dialog-footer button").length, 1, "informational window: Close only");

    // The guide is cached per locale: a second opening does not refetch.
    dialog.querySelector(".ir-dialog-x").click();
    assert.equal(!report.shadowRoot.querySelector(".ir-help-window"), true);
    button.click();
    const reopened = report.shadowRoot.querySelector(".ir-help-window");
    await settle(() => reopened.querySelector(".ir-help"));
    assert.deepEqual(helpRequests(), ["help.en.html"]);

    report.remove();
});

// The English-fallback and missing-page cases live in ir-help-fallback.test.js: the guide is
// cached per locale for the life of the module, so they need a process of their own.
test("the guide follows the widget locale", async () => {
    requests.length = 0;
    const report = await mount("fr-CA");
    report.shadowRoot.querySelector(".ir-helpbtn").click();
    const dialog = report.shadowRoot.querySelector(".ir-help-window");
    assert.equal(dialog.querySelector(".ir-dialog-title-text").textContent, "Aide");
    await settle(() => dialog.querySelector(".ir-help"));
    assert.deepEqual(helpRequests(), ["help.fr-CA.html"]);
    assert.equal(dialog.querySelector(".ir-help h2").textContent, "Recherche");
    dialog.querySelector(".ir-dialog-x").click();
    report.remove();
});
