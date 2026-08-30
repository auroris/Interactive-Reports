import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { translate } from "../../src/client/core/localization.js";
import { computeDialog, highlightDialog } from "../../src/client/report/dialogs/rules.js";
import { groupByDialog } from "../../src/client/report/dialogs/view.js";
import { terminalComposableLocation } from "../../src/client/report/state.js";

const window = new Window({ url: "https://host.example/report" });
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
    Node: window.Node,
    Option,
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

const columns = [
    { name: "STATUS", label: "Status", type: "text" },
    { name: "AMOUNT", label: "Amount", type: "number" },
];

const settle = async condition => {
    for (let attempt = 0; attempt < 40 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

function widget() {
    const host = document.createElement("div");
    document.body.append(host);
    const shadowRoot = host.attachShadow({ mode: "open" });
    return {
        host,
        shadowRoot,
        doc: {
            activeTable: "base",
            tables: {
                base: {
                    from: "definition",
                    schema: structuredClone(columns),
                    composables: [],
                },
                grouped: {
                    from: "base",
                    composables: [{
                        kind: "group",
                        by: ["STATUS"],
                        values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }],
                    }],
                },
            },
        },
        schema: {
            columns,
            capabilities: {
                aggregateFunctions: { number: ["sum", "avg"], other: [] },
                expressionFunctions: [],
            },
        },
        t(key, values) { return translate(this, key, values); },
        apply(mutate) {
            const next = structuredClone(this.doc);
            mutate(next);
            this.doc = next;
            return Promise.resolve();
        },
    };
}

test("computed and metric dialogs author from the shared ir namespace", async () => {
    const w = widget();

    computeDialog(w);
    let dialog = w.shadowRoot.querySelector(".ir-dialog");
    dialog.querySelector('input[type="text"]').value = "Taxed";
    dialog.querySelector("textarea").value = "AMOUNT * 1.05";
    dialog.querySelector(".ir-btn-primary").click();
    await settle(() => !w.shadowRoot.querySelector(".ir-dialog"));

    assert.equal(
        terminalComposableLocation(w.doc, "compute", "base").composable.computed[0].id,
        "ir2",
        "the computed column skips the metric already owned by another table");

    groupByDialog(w);
    dialog = w.shadowRoot.querySelector(".ir-dialog");
    const metricFieldset = [...dialog.querySelectorAll("fieldset")]
        .find(fieldset => fieldset.querySelector("legend")?.textContent === "Aggregate values");
    metricFieldset.querySelector(".ir-add-row").click();
    const newMetric = metricFieldset.querySelectorAll(".ir-dlgrow")[1];
    const [fn, column] = newMetric.querySelectorAll("select");
    column.value = "AMOUNT";
    column.dispatchEvent(new window.Event("change", { bubbles: true }));
    fn.value = "avg";
    dialog.querySelector(".ir-btn-primary").click();
    await settle(() => !w.shadowRoot.querySelector(".ir-dialog"));

    assert.deepEqual(
        terminalComposableLocation(w.doc, "group", "grouped").composable.values,
        [
            { id: "ir1", col: "AMOUNT", fn: "sum" },
            { id: "ir3", col: "AMOUNT", fn: "avg" },
        ]);

    w.host.remove();
});

test("highlight authoring reserves ids and precedence across repeated-node permutations", async () => {
    const first = {
        kind: "highlight",
        highlights: [{
            id: "h1", name: "First", sequence: 10, enabled: false,
            scope: "row", expr: "AMOUNT > 10", style: { bg: "#111111" },
        }],
    };
    const second = {
        kind: "highlight",
        highlights: [{
            id: "h2", name: "Second", enabled: true,
            scope: "row", expr: "AMOUNT > 20", style: { bg: "#222222" },
        }],
    };

    for (const nodes of [[first, second], [second, first]]) {
        const w = widget();
        w.doc.tables.base.composables = structuredClone(nodes);

        highlightDialog(w);
        const dialog = w.shadowRoot.querySelector(".ir-dialog");
        assert.equal(dialog.querySelector('input[type="number"]').value, "30");
        assert.match(
            [...dialog.querySelectorAll(".ir-dialog-note")]
                .map(note => note.textContent).join(" "),
            /Cell highlights apply after row highlights.*higher sequence numbers apply later.*Disabled highlights keep their place/);
        dialog.querySelector("textarea").value = "AMOUNT > 30";
        dialog.querySelector(".ir-btn-primary").click();
        await settle(() => !w.shadowRoot.querySelector(".ir-dialog"));

        const rules = w.doc.tables.base.composables
            .filter(node => node.kind === "highlight")
            .flatMap(node => node.highlights ?? []);
        const authored = rules.find(rule => rule.id === "h3");
        assert.equal(authored.sequence, 30);
        assert.equal(authored.expr, "AMOUNT > 30");

        w.host.remove();
    }
});
