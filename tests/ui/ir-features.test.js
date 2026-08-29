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

// Feature whitelists by report name; absent = the field is omitted (legacy server).
const FEATURES = {
    kiosk: ["search", "sort", "download"],
    chipsy: ["sort"],
    none: [],
};
const DEFAULT_STATES = {
    chipsy: {
        page: { index: 1, size: 25 },
        search: "acme",
        ...reportState({ filters: [{ enabled: true, expr: "ID = 1" }] }),
    },
};

const requests = [];
const json = value => new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
});

globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET", body: options.body });
    const report = /\/([^/]+)\/(schema|query|saved)$/.exec(String(url))?.[1];
    if (String(url).endsWith("/schema")) {
        return json({
            defaultState: DEFAULT_STATES[report] ?? {
                page: { index: 1, size: 25 },
                ...reportState(),
            },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [{ name: "ID", label: "ID", type: "number" }],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
            ...(FEATURES[report] ? { features: FEATURES[report] } : {}),
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

const settle = async condition => {
    for (let attempt = 0; attempt < 40 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount(reportName) {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", reportName);
    report.setAttribute("api-base", "/feature-api");
    document.body.append(report);
    await settle(() => report.shadowRoot?.querySelector("tbody tr"));
    return report;
}

const menuLabels = report => [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item, .ir-popup .ir-menu-heading")]
    .map(item => item.textContent.replace("✓", "").trim());

test("a whitelisted report hides the chrome its features do not cover", async () => {
    requests.length = 0;
    const report = await mount("kiosk");

    assert.equal(report.shadowRoot.querySelector(".ir-search").hidden, false, "search stays: whitelisted");
    assert.equal(report.shadowRoot.querySelector(".ir-search").tagName, "FORM");
    assert.equal(report.shadowRoot.querySelector(".ir-search").getAttribute("role"), "search");
    assert.equal(report.shadowRoot.querySelector(".ir-viewbtns").hidden, true, "no alternate view feature → the switcher goes");
    assert.equal(report.shadowRoot.querySelector(".ir-actionsbtn").hidden, false);
    assert.equal(requests.some(r => r.url.endsWith("/saved")), false, "savedReports off → the saved list is never fetched");

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    const labels = menuLabels(report);
    assert.deepEqual(labels, ["Sort…", "Report", "Reset", "Download", "CSV"]);

    // The header menu only offers what survived: sorting.
    const headerButton = report.shadowRoot.querySelector("th.ir-th-menu .ir-th-button");
    assert.equal(headerButton.getAttribute("aria-haspopup"), "menu");
    headerButton.click();
    assert.equal(headerButton.getAttribute("aria-expanded"), "true");
    const headerLabels = menuLabels(report);
    assert.deepEqual(headerLabels, ["Sort Ascending", "Sort Descending"]);

    report.remove();
});

test("a schema without a features field (older server) leaves everything on", async () => {
    requests.length = 0;
    const report = await mount("orders");

    assert.equal(report.shadowRoot.querySelector(".ir-search").hidden, false);
    assert.equal(report.shadowRoot.querySelector(".ir-viewbtns").hidden, false);
    assert.equal(report.shadowRoot.querySelector('.ir-viewbtn[data-mode="grid"]').getAttribute("aria-pressed"), "true");
    assert.equal(requests.some(r => r.url.endsWith("/saved")), true);

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    const labels = menuLabels(report);
    assert.equal(labels.includes("Columns…"), true);
    assert.equal(labels.includes("Pagination…"), true);
    assert.equal(labels.includes("Save As…"), true);
    assert.equal(labels.includes("CSV"), true);

    report.remove();
});

test("state owned by an absent feature renders as a locked chip", async () => {
    requests.length = 0;
    const report = await mount("chipsy");

    // The default report carries a search and a filter, but neither feature is
    // whitelisted: the chips must show the truth without offering a way to touch it.
    await settle(() => report.shadowRoot.querySelectorAll(".ir-chip").length === 2);
    const chips = [...report.shadowRoot.querySelectorAll(".ir-chip")];
    assert.equal(chips.length, 2);
    for (const chip of chips) {
        assert.equal(chip.querySelectorAll("button").length, 0, "locked chips offer no edit or remove");
        assert.equal(chip.querySelectorAll("input").length, 0, "locked chips offer no enable toggle");
        assert.equal(!!chip.querySelector(".ir-chip-static"), true);
    }
    assert.equal(report.shadowRoot.querySelector(".ir-search").hidden, true);

    report.remove();
});

test("an empty whitelist yields a static grid: no toolbar chrome, inert headers", async () => {
    requests.length = 0;
    const report = await mount("none");

    assert.equal(report.shadowRoot.querySelector(".ir-search").hidden, true);
    assert.equal(report.shadowRoot.querySelector(".ir-viewbtns").hidden, true);
    assert.equal(report.shadowRoot.querySelector(".ir-actionsbtn").hidden, true, "no surviving entry → no Actions button");
    // Boolean, not the element: a DOM node in a failed assertion makes the
    // reporter serialize the whole happy-dom graph.
    assert.equal(!report.shadowRoot.querySelector("th.ir-th-menu"), true, "headers stop advertising a menu");

    report.remove();
});
