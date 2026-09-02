import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { inputComposableLocation } from "../../src/client/report/state.js";
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
let whoami = { identity: "test-user" };
const json = (value, init = {}) => new Response(JSON.stringify(value), {
    status: init.status ?? 200,
    headers: { "Content-Type": "application/json" },
});

globalThis.fetch = async (url, options = {}) => {
    const path = String(url);
    const method = options.method ?? "GET";
    requests.push({ url: path, method, body: options.body });
    if (path.endsWith("/schema")) {
        return json({
            // labels here mirror the server contract: friendly names reach the client
            // only as part of the default report; column metadata stays neutral.
            defaultState: {
                page: { index: 1, size: 25 },
                ...reportState({ labels: { ID: "Ident" } }),
            },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [{ name: "ID", label: "ID", type: "number" }],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
        });
    }
    if (path.endsWith("/whoami")) return json(whoami);
    const family = /^\/custom-report-api\/([^/?]+)$/.exec(path)?.[1];
    if (method === "GET" && family) {
        const visible = savedReports.filter(report => report.reportName === family);
        return json(visible.length ? visible : [{
            id: 1, reportName: family, title: "Default", isDefault: true, isGlobal: true,
        }]);
    }
    const document = /^\/custom-report-api\/([^/?]+)\/([^/?]+)$/.exec(path);
    if (method === "GET" && document) {
        const [, reportName, savedId] = document;
        if (savedDocuments.has(savedId)) return json(savedDocuments.get(savedId));
        const listed = savedReports.find(report => String(report.id) === savedId);
        return json({
            summary: listed ?? {
                id: Number(savedId), reportName, title: "Default", isDefault: true, isGlobal: true,
            },
            state: {},
        });
    }
    if (path.endsWith("/query")) {
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
    if (path.endsWith("/orders/csv")) {
        return new Response("\ufeffIdent\r\n1\r\n", {
            headers: {
                "Content-Type": "text/csv; charset=utf-8",
                "Content-Disposition": 'attachment; filename="orders.csv"',
                "X-IR-Truncated": "true",
            },
        });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir.js");

test("the report is style-isolated and uses its explicit API base", async () => {
    document.head.append(Object.assign(document.createElement("style"), {
        textContent: "button, table, .ir-toolbar { display: none !important; }",
    }));

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api/");
    report.setAttribute("stylesheet", "/styles/orders-report.css?v=3");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.ok(report.shadowRoot, "the component should render behind a shadow root");
    const customLink = report.shadowRoot.querySelector("link[data-ir-host-stylesheet]");
    assert.equal(
        customLink?.getAttribute("href"),
        "/styles/orders-report.css?v=3",
        "the configured sheet must be linked inside the shadow root");

    // The cascade contract: packaged styles come before the custom stylesheet so
    // its equal-specificity rules win. Adopted sheets always sort last, so a root
    // hosting a custom stylesheet demotes to a style node placed before the link;
    // roots without one share the single parsed packaged sheet.
    const styleNode = report.shadowRoot.querySelector("style[data-ir-styles]");
    assert.ok(styleNode, "a root with a custom stylesheet uses the style-node path");
    assert.equal(report.shadowRoot.adoptedStyleSheets?.length ?? 0, 0,
        "a demoted root must not also adopt the packaged sheet");
    const rootChildren = [...report.shadowRoot.children];
    assert.ok(rootChildren.indexOf(styleNode) < rootChildren.indexOf(customLink),
        "packaged styles must precede the custom stylesheet");
    const peer = document.createElement("interactive-report");
    const twin = document.createElement("interactive-report");
    assert.ok(peer.shadowRoot.adoptedStyleSheets?.length,
        "a root without a custom stylesheet adopts the packaged sheet");
    assert.equal(peer.shadowRoot.adoptedStyleSheets[0], twin.shadowRoot.adoptedStyleSheets[0],
        "widget instances share one parsed packaged stylesheet");
    assert.ok(report.shadowRoot.querySelector(".ir-toolbar"), "the report UI should render in the shadow root");
    assert.equal(report.shadowRoot.querySelector(".ir-toolbar").getAttribute("part"), "toolbar");
    assert.equal(report.shadowRoot.querySelector(".ir-table").getAttribute("part"), "table");
    assert.equal(report.shadowRoot.querySelector(".ir-report-select"), null);
    assert.equal(document.querySelector("link[data-ir-css]"), null, "the bundle should not inject global CSS");
    assert.equal(document.querySelector("link[data-ir-host-stylesheet]"), null,
        "the report stylesheet must not leak into the page head");
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
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    assert.equal(dialog.getAttribute("part"), "dialog");
    assert.equal(dialog.getAttribute("popover"), "manual", "editor windows should be modeless manual popovers");
    assert.equal(dialog.hasAttribute("aria-modal"), false, "editor windows should leave the report available");
    assert.equal(report.shadowRoot.querySelector(".ir-overlay"), null, "modeless windows should not install a blocking overlay");
    assert.equal(
        dialog.getAttribute("aria-labelledby"),
        dialog.querySelector(".ir-dialog-title-text").id,
        "the visible title should provide the window's accessible name");
    assert.equal(dialog.querySelector(".ir-dialog-title").tabIndex, 0, "the move handle should be keyboard reachable");

    report.remove();
    assert.equal(report.shadowRoot.querySelector(".ir-dialog"), null, "transient UI should be disposed on unmount");
});

test("a host can retrieve the current export without initiating a browser download", async () => {
    requests.length = 0;
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api");
    report.setAttribute("download-base", "/custom-download-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    const artifact = await report.getExport("CSV");
    assert.equal(artifact.filename, "orders.csv");
    assert.equal(artifact.contentType, "text/csv; charset=utf-8");
    assert.equal(artifact.truncated, true);
    // Blob.text() decodes and consumes the UTF-8 BOM; the raw Blob retains it.
    assert.equal(await artifact.blob.text(), "Ident\r\n1\r\n");

    const request = requests.find(r => r.url === "/custom-download-api/orders/csv");
    assert.ok(request, "the public method uses the file-download client endpoint");
    assert.equal(request.method, "POST");
    assert.equal("v" in JSON.parse(request.body), false);
    assert.equal(JSON.parse(request.body).tables.base.from, "definition");
    assert.equal(document.querySelector('a[download="orders.csv"]'), null,
        "retrieval must not synthesize a browser download anchor");

    report.remove();
});

test("the configured report is loaded directly and can be changed through its attribute", async () => {
    requests.length = 0;

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api");
    report.setAttribute("stylesheet", "/styles/orders-report.css?v=3");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/orders/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    report.setAttribute("report", "order-feed");
    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/order-feed/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.equal(report.reportId, "1");
    assert.equal(report.shadowRoot.querySelector("link[data-ir-host-stylesheet]")?.getAttribute("href"),
        "/styles/orders-report.css?v=3",
        "the host-owned stylesheet survives report changes");
    report.styleSheet = "/styles/replacement.css";
    assert.equal(report.getAttribute("stylesheet"), "/styles/replacement.css");
    assert.equal(report.shadowRoot.querySelector("link[data-ir-host-stylesheet]")?.getAttribute("href"),
        "/styles/replacement.css", "the reflected property replaces the shadow-root link");
    report.styleSheet = null;
    assert.equal(report.shadowRoot.querySelector("link[data-ir-host-stylesheet]"), null,
        "clearing the reflected property removes the link");
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

test("the report chrome and dialogs follow the host language", async () => {
    requests.length = 0;

    const report = document.createElement("interactive-report");
    report.setAttribute("lang", "fr-CA");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 40 && !requests.some(r => r.url.endsWith("/orders/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));

    assert.equal(report.shadowRoot.querySelector(".ir-search-input").placeholder, "Rechercher");
    assert.match(report.shadowRoot.querySelector(".ir-saved-label").textContent, /Rapport enregistré/);
    assert.match(report.shadowRoot.querySelector(".ir-actionsbtn").textContent, /Actions/);

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    const columns = [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item")]
        .find(item => item.textContent.includes("Colonnes"));
    assert.ok(columns, "the French Actions menu contains the columns command");
    columns.click();

    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    assert.equal(dialog.querySelector(".ir-dialog-title-text").textContent, "Sélectionner les colonnes");
    assert.equal(dialog.querySelector(".ir-dialog-footer .ir-btn:not(.ir-btn-primary)").textContent, "Annuler");

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
        report.shadowRoot.querySelector("th.ir-th-menu .ir-th-button").click();
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
        const doc = JSON.parse(requests.filter(r => r.url.endsWith("/query")).at(-1).body);
        return inputComposableLocation(doc, "labels")?.composable?.labels ?? {};
    };

    assert.deepEqual(await rename("Ticket"), { ID: "Ticket" });
    assert.equal(headerText(), "Ticket", "the override should render without server involvement");

    // Clearing drops the entry — display falls back to the server's neutral label —
    // but the map itself stays: an explicit {} still overrides a report default.
    assert.deepEqual(await rename(""), {});
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
    report.setAttribute("saved-report", "saved-1");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/orders/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    assert.equal(report.shadowRoot.querySelector(".ir-saved-select").value, "saved-1");
    assert.ok(requests.some(r => r.url === "/custom-report-api/orders/saved-1"));
    const queries = requests.filter(r => r.url === "/custom-report-api/orders/query");
    assert.equal(queries.length, 1, "Default should not be queried before the requested saved report");
    assert.equal(JSON.parse(queries[0].body).search, "Acme");

    report.remove();
    savedReports = [];
    savedDocuments = new Map();
});

test("the flagged default report represents the schema Default", async () => {
    requests.length = 0;
    savedReports = [{
        id: "default-1", reportName: "orders", title: "Default",
        isGlobal: true, isDefault: true, owner: null, mine: false,
    }, {
        id: "global-2", reportName: "orders", title: "Executive",
        isGlobal: true, isDefault: false, owner: "admin", mine: false,
    }];

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 20 && !requests.some(r => r.url.endsWith("/orders/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 1));

    const select = report.shadowRoot.querySelector(".ir-saved-select");
    assert.equal(select.value, "default-1");
    assert.equal(select.options[0].text, "Default");
    assert.equal(select.options[0].value, "default-1");
    assert.equal(select.querySelector('optgroup[label="Public"] option')?.text, "Default");
    assert.equal(select.value, "default-1");
    assert.equal(requests.some(r => r.url.endsWith("/orders/default-1")), true,
        "the default document is retrieved independently of the schema");

    report.remove();
    savedReports = [];
});

test("a read-only saved report never offers Save or Delete, even to an administrator", async () => {
    requests.length = 0;
    whoami = { identity: "test-user", isAdministrator: true };
    savedReports = [{
        id: "configured-1", reportName: "orders", title: "Configured View",
        isGlobal: true, owner: null, mine: false, isReadOnly: true,
    }];
    savedDocuments = new Map([["configured-1", {
        summary: savedReports[0],
        state: { page: { index: 1, size: 25 }, view: { mode: "grid" } },
    }]]);

    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("saved-report", "configured-1");
    report.setAttribute("api-base", "/custom-report-api");
    document.body.append(report);

    for (let attempt = 0; attempt < 40 && !requests.some(r => r.url.endsWith("/orders/query")); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    // Saved-report commands live in the Report submenu.
    [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item")]
        .find(item => item.querySelector(".ir-menu-label").textContent === "Report")
        .click();
    const labels = [...report.shadowRoot.querySelectorAll(".ir-submenu .ir-menu-item .ir-menu-label")]
        .map(item => item.textContent.trim());
    assert.equal(labels.includes("Save As…"), true);
    assert.equal(labels.includes("Save"), false);
    assert.equal(labels.includes("Delete…"), false);

    report.remove();
    whoami = { identity: "test-user" };
    savedReports = [];
    savedDocuments = new Map();
});
