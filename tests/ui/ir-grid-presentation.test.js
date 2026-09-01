// Grid presentation precedence: break headings format through the canonical
// per-column formatter (masks and the page-wide decimal rule included), and
// highlights land on the cells so they beat per-column inline styles — row
// scope first, cell scope last.

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
                ...reportState({
                        breaks: ["AMOUNT"],
                        formats: {
                            ID: { bg: "blue" },
                            AMOUNT: { mask: "integer" },
                        },
                        highlights: [
                            {
                                id: "h1", name: "Big row", sequence: 10, enabled: true,
                                scope: "row", expr: "AMOUNT > 0",
                                style: { bg: "red", fg: "white" },
                            },
                            {
                                id: "h2", name: "Key cell", sequence: 20, enabled: true,
                                scope: "cell", col: "ID", expr: "ID = 1",
                                style: { bg: "green" },
                            },
                        ],
                    }),
            },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: [
                { name: "ID", label: "ID", type: "number" },
                { name: "AMOUNT", label: "Amount", type: "number" },
            ],
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
        });
    }
    if (path.endsWith("/whoami")) return json({ identity: "test-user" });
    const family = /^\/grid-api\/([^/?]+)$/.exec(path)?.[1];
    if (family)
        return json([{ id: 1, reportName: family, title: "Default", isDefault: true, isGlobal: true }]);
    const document = /^\/grid-api\/([^/?]+)\/(\d+)$/.exec(path);
    if (document) {
        return json({
            summary: { id: Number(document[2]), reportName: document[1], title: "Default", isDefault: true, isGlobal: true },
            state: {},
        });
    }
    if (path.endsWith("/query")) {
        return json({
            columns: [
                { name: "ID", label: "ID", type: "number" },
                { name: "AMOUNT", label: "Amount", type: "number" },
            ],
            rows: [{ ID: 1, AMOUNT: "1234.5" }, { ID: 2, AMOUNT: "2000" }],
            page: { index: 1, size: 25 },
            totalRows: 2,
            aggregates: {},
            highlights: [{ row: 0, id: "h1" }, { row: 0, id: "h2", col: "ID" }],
            ignored: [],
        });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 60 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

test("break headings apply the break column's mask and decimal rule", async () => {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", "orders");
    report.setAttribute("api-base", "/grid-api");
    document.body.append(report);
    await settle(() => report.shadowRoot?.querySelectorAll("tr.ir-break-header").length === 2);

    const headings = [...report.shadowRoot.querySelectorAll("tr.ir-break-header")]
        .map(tr => tr.textContent.trim());
    assert.equal(headings[0], "Amount: 1,235",
        "the heading formats like a cell of that column — mask applied, not the raw value");
    assert.equal(headings[1], "Amount: 2,000");

    // Highlight precedence on the data rows: the row style covers every cell
    // (beating the ID column's inline background), the cell style lands last.
    const rows = report.shadowRoot.querySelectorAll("tr.ir-row");
    const highlightedCell = rows[0].querySelector("td");
    assert.equal(highlightedCell.style.background, "green", "the cell-scoped highlight wins last");
    assert.equal(highlightedCell.style.color, "white", "the row highlight's text color persists");
    const plainCell = rows[1].querySelector("td");
    assert.equal(plainCell.style.background, "blue", "the column format still styles unhighlighted rows");

    report.remove();
});
