import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { translate } from "../../src/client/core/localization.js";
import { renderChips } from "../../src/client/report/render/chips.js";
import { normalizeReportState } from "../../src/client/report/state.js";

const window = new Window({ url: "https://host.example/report" });
Object.assign(globalThis, {
    window,
    document: window.document,
    HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot,
    Node: window.Node,
});

const columns = [
    { name: "STATUS", label: "Status", type: "text" },
    { name: "__count", label: "Count", type: "number" },
    { name: "ir1", label: "Half", type: "number", computed: true },
];

function widget(doc) {
    return {
        doc,
        schema: { columns, capabilities: { aggregateFunctions: {} } },
        lastResult: { availableColumns: columns },
        els: { search: document.createElement("input") },
        t(key, values) { return translate(this, key, values); },
        applyOrBanner(mutate) { mutate(this.doc); return Promise.resolve(); },
        apply(mutate) { mutate(this.doc); return Promise.resolve(); },
        notify() {},
        showError(error) { throw error; },
    };
}

test("chips show intermediate ordinary composables read-only and mutate only the active owner", async () => {
    const doc = normalizeReportState({
        activeTable: "decorated",
        tables: {
            source: {
                from: "definition",
                composables: [
                    { kind: "filter", filters: [{ expr: "STATUS IS NOT NULL", enabled: true }] },
                ],
            },
            grouped: {
                from: "source",
                composables: [
                    { kind: "group", by: ["STATUS"], values: [] },
                    { kind: "compute", computed: [{ id: "ir1", label: "Half", expr: "__count / 2", enabled: true }] },
                    { kind: "filter", filters: [{ expr: "ir1 > 0", enabled: true }] },
                    { kind: "break", breaks: ["STATUS"] },
                    { kind: "aggregate", aggregates: [{ col: "__count", fn: "sum" }] },
                    { kind: "highlight", highlights: [{ expr: "ir1 > 10", color: "red", enabled: true }] },
                ],
            },
            decorated: {
                from: "grouped",
                schema: columns,
                composables: [
                    { kind: "filter", filters: [{ expr: "EARLIER", enabled: true }] },
                    { kind: "foreign-decoration", payload: { keep: true } },
                    { kind: "filter", filters: [{ expr: "CURRENT", enabled: true }] },
                ],
            },
        },
    }, 25);
    const w = widget(doc);
    const container = document.createElement("div");

    renderChips(w, container);

    const ordinary = [...container.querySelectorAll(".ir-chip")]
        .filter(chip => chip.dataset.kind !== "view");
    assert.deepEqual(
        ordinary.map(chip => chip.textContent),
        [
            "Filter STATUS IS NOT NULL",
            "ƒ Half",
            "Filter ir1 > 0",
            "Filter EARLIER",
            "Filter CURRENT",
        ]);

    const inherited = ordinary.filter(chip => chip.dataset.inherited === "true");
    assert.equal(inherited.length, 3);
    assert.ok(inherited.every(chip => !chip.querySelector("button, input")),
        "ancestor settings are visible without toggle, edit, or remove controls");
    assert.ok(!ordinary.some(chip => /Break|sum|ir1 > 10/.test(chip.textContent)),
        "ancestor terminal controls ignored by the server fold stay hidden");

    const earlier = ordinary.find(chip => chip.textContent.includes("EARLIER"));
    assert.equal(earlier.dataset.inherited, "false");
    assert.equal(earlier.querySelector("button, input"), null,
        "an earlier repeated active node is preserved read-only");

    const current = ordinary.find(chip => chip.textContent.includes("CURRENT"));
    assert.ok(current.querySelector("input"));
    assert.equal(current.querySelectorAll("button").length, 2);

    const toggle = current.querySelector("input");
    toggle.checked = false;
    toggle.dispatchEvent(new window.Event("change", { bubbles: true }));
    await Promise.resolve();
    assert.equal(doc.tables.decorated.composables[2].filters[0].enabled, false);

    current.querySelector(".ir-chip-x").click();
    await Promise.resolve();
    assert.deepEqual(doc.tables.decorated.composables[2].filters, []);
    assert.deepEqual(doc.tables.decorated.composables[0].filters, [{ expr: "EARLIER", enabled: true }]);
    assert.deepEqual(doc.tables.decorated.composables[1], {
        kind: "foreign-decoration",
        payload: { keep: true },
    });
    assert.deepEqual(doc.tables.grouped.composables[2].filters, [{ expr: "ir1 > 0", enabled: true }]);
});

test("highlight chips form one stable priority set across repeated-node permutations", () => {
    const first = {
        kind: "highlight",
        highlights: [{
            id: "h3", name: "Row Thirty", expr: "STATUS = 'third'", enabled: true,
            scope: "row", style: { bg: "#333333" },
        }],
    };
    const second = {
        kind: "highlight",
        highlights: [
            {
                id: "h2", name: "Row Twenty", sequence: 20, expr: "STATUS = 'second'", enabled: true,
                scope: "row", style: { bg: "#222222" },
            },
            {
                id: "H1", name: "Cell Ten", expr: "STATUS = 'first'", enabled: true,
                scope: "cell", col: "STATUS", style: { bg: "#111111" },
            },
        ],
    };

    for (const nodes of [[first, second], [second, first]]) {
        const doc = normalizeReportState({
            activeTable: "base",
            tables: {
                base: {
                    from: "definition",
                    schema: columns,
                    composables: structuredClone(nodes),
                },
            },
        }, 25);
        const container = document.createElement("div");

        renderChips(widget(doc), container);

        assert.deepEqual(
            [...container.querySelectorAll('[data-kind="highlight"]')].map(node => ({
                name: node.querySelector("b")?.textContent,
                sequence: Number(/#(\d+)/.exec(node.textContent)?.[1]),
            })),
            [
                { name: "Row Twenty", sequence: 20 },
                { name: "Row Thirty", sequence: 30 },
                { name: "Cell Ten", sequence: 10 },
            ]);
    }
});
