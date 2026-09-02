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

// One managed report: a definition edit link plus per-column overrides on NOTES.
// The delivered default carries a stale sort on the restricted column, as a saved
// report from before the restriction would.
const json = value => new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
});

globalThis.fetch = async url => {
    const path = String(url);
    if (path.endsWith("/schema")) {
        return json({
            defaultState: {
                page: { index: 1, size: 25 },
                ...reportState({ columns: ["LABEL", "NOTES"], sorts: [{ col: "NOTES" }] }),
            },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [
                { name: "ID", label: "ID", type: "number" },
                { name: "LABEL", label: "Label", type: "text" },
                { name: "NOTES", label: "Notes", type: "text" },
            ],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
            editLink: { urlTemplate: "/rows/{ID}/edit", label: "Edit row", target: "_self" },
            createLink: { url: "/rows/new", label: "New row", target: "_self", mode: "navigate" },
            columnOverrides: {
                NOTES: { hideLabel: true, sortable: false, filterable: false, helpText: "Free-form notes." },
            },
        });
    }
    if (path.endsWith("/whoami")) return json({ identity: "test-user" });
    const family = /^\/column-api\/([^/?]+)$/.exec(path)?.[1];
    if (family)
        return json([{ id: 1, reportName: family, title: "Default", isDefault: true, isGlobal: true }]);
    const document = /^\/column-api\/([^/?]+)\/(\d+)$/.exec(path);
    if (document) {
        return json({
            summary: { id: Number(document[2]), reportName: document[1], title: "Default", isDefault: true, isGlobal: true },
            state: {},
        });
    }
    if (path.endsWith("/query")) {
        return json({
            columns: [
                { name: "LABEL", label: "Label", type: "text" },
                { name: "NOTES", label: "Notes", type: "text" },
            ],
            // The hidden edit-link projection: ID rides the rows without column
            // metadata. The second row withholds its pencil through a NULL key.
            rows: [
                { LABEL: "first", NOTES: "keep", ID: 41 },
                { LABEL: "second", NOTES: null, ID: null },
            ],
            page: { index: 1, size: 25 },
            totalRows: 2,
            aggregates: { LABEL: { count: "2" } },
            highlights: [],
            ignored: [{ kind: "sort", detail: "column 'NOTES' is not sortable" }],
        });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 40 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount() {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "managed");
    report.setAttribute("api-base", "/column-api");
    document.body.append(report);
    await settle(() => report.shadowRoot?.querySelector("tbody tr.ir-row"));
    return report;
}

const menuLabels = report => [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item")]
    .map(item => item.textContent.replace("✓", "").trim());

test("the edit pencil leads every grid row and withholds itself on null keys", async () => {
    const report = await mount();

    const headers = [...report.shadowRoot.querySelectorAll("thead th")];
    assert.equal(headers[0].className, "ir-th-edit");
    assert.equal(headers[0].textContent.trim(), "", "the edit header shows no text");
    assert.equal(headers[0].getAttribute("aria-label"), "Edit row");

    const rows = [...report.shadowRoot.querySelectorAll("tbody tr.ir-row")];
    const first = rows[0].children[0];
    assert.equal(first.className, "ir-td-edit");
    const anchor = first.querySelector("a.ir-cell-edit");
    assert.equal(anchor.getAttribute("href"), "/rows/41/edit", "the hidden ID builds the URL");
    assert.equal(anchor.getAttribute("aria-label"), "Edit row");
    assert.equal(!rows[1].children[0].querySelector("a"), true, "a NULL key withholds the pencil");

    // The grand-total row carries the leading cell too, keeping the Fn label in
    // the first data column.
    const total = report.shadowRoot.querySelector("tr.ir-grand-total");
    assert.equal(total.children[0].className, "ir-td-edit");
    assert.equal(total.children[0].textContent.trim(), "");
    assert.match(total.children[1].textContent, /^Count:/);

    report.remove();
});

test("the create button joins the toolbar after Actions and dispatches ir-create from the element", async () => {
    const report = await mount();
    const events = [];
    // preventDefault keeps the anchor from navigating the test window away from the fixture.
    report.addEventListener("ir-create", event => { event.preventDefault(); events.push(event.detail); });

    const toolbar = report.shadowRoot.querySelector(".ir-toolbar");
    const create = toolbar.querySelector(".ir-createbtn");
    assert.equal(create.tagName, "A");
    assert.equal(create.getAttribute("href"), "/rows/new");
    assert.equal(create.textContent.trim(), "New row");
    assert.equal(create.closest(".ir-create").hidden, false);
    const order = [...toolbar.children];
    assert.equal(
        order.indexOf(create.closest(".ir-create")) - order.indexOf(toolbar.querySelector(".ir-actionsbtn")), 1,
        "the create control immediately follows Actions");

    create.click();
    assert.deepEqual(events, [{ url: "/rows/new" }], "the event escapes the shadow root to the host element");

    // The edit pencil's event reaches the host the same way, with the hidden key.
    const edits = [];
    report.addEventListener("ir-edit", event => { event.preventDefault(); edits.push(event.detail); });
    report.shadowRoot.querySelector("tbody a.ir-cell-edit").click();
    assert.equal(edits.length, 1);
    assert.equal(edits[0].url, "/rows/41/edit");
    assert.equal(edits[0].row.ID, 41);

    report.remove();
});

test("hideLabel blanks the header cell but keeps the accessible and menu name", async () => {
    const report = await mount();

    const notesTh = [...report.shadowRoot.querySelectorAll("thead th")].at(-1);
    const button = notesTh.querySelector(".ir-th-button");
    assert.equal(button.textContent.trim(), "", "no visible header text");
    assert.equal(button.getAttribute("aria-label"), "Notes", "the real label stays the accessible name");
    assert.equal(
        report.shadowRoot.querySelectorAll(".ir-sort-dir").length, 0,
        "the stale sort on the restricted column draws no indicator");

    report.remove();
});

test("restricted columns lose sort, filter, and break entries but gain the help note", async () => {
    const report = await mount();

    const [, labelTh, notesTh] = [...report.shadowRoot.querySelectorAll("thead th")];
    notesTh.querySelector(".ir-th-button").click();
    const notesMenu = menuLabels(report);
    assert.equal(notesMenu.includes("Sort Ascending"), false);
    assert.equal(notesMenu.includes("Filter…"), false);
    assert.equal(notesMenu.includes("Control Break"), false);
    assert.equal(notesMenu.includes("Rename…"), true, "presentation entries survive");
    assert.equal(
        report.shadowRoot.querySelector(".ir-popup .ir-menu-note").textContent,
        "Free-form notes.");

    labelTh.querySelector(".ir-th-button").click();
    const labelMenu = menuLabels(report);
    assert.equal(labelMenu.includes("Sort Ascending"), true);
    assert.equal(labelMenu.includes("Filter…"), true);
    assert.equal(labelMenu.includes("Control Break"), true);
    assert.equal(!report.shadowRoot.querySelector(".ir-popup .ir-menu-note"), true, "no help configured, no note");

    report.remove();
});

test("sort and break pickers omit restricted columns while filters omit theirs", async () => {
    const report = await mount();

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item")]
        .find(item => item.textContent.includes("Sort…")).click();
    await settle(() => report.shadowRoot.querySelector(".ir-dialog select"));
    const sortOptions = [...report.shadowRoot.querySelector(".ir-dialog select").options]
        .map(option => option.value);
    assert.equal(sortOptions.includes("LABEL"), true);
    assert.equal(sortOptions.includes("NOTES"), false, "the sort picker omits the restricted column");
    report.shadowRoot.querySelector(".ir-dialog .ir-btn-cancel, .ir-dialog [aria-label='Close']")?.click();

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item")]
        .find(item => item.textContent.includes("Filter…")).click();
    await settle(() => report.shadowRoot.querySelector(".ir-dialog .ir-token-group"));
    const filterTokens = [...report.shadowRoot.querySelectorAll(".ir-dialog .ir-token-group")]
        .find(group => group.textContent.includes("Columns"));
    assert.equal(filterTokens.textContent.includes("Label"), true);
    assert.equal(filterTokens.textContent.includes("Notes"), false, "filter tokens omit non-filterable columns");

    report.remove();
});
