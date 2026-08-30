import assert from "node:assert/strict";
import test from "node:test";
import { doSearch } from "../../src/client/report/search.js";

test("scoped search adds a filter to the completed active table", () => {
    const doc = {
        activeTable: "summary",
        tables: {
            source: {
                from: "definition",
                schema: [{ name: "AMOUNT", label: "Amount", type: "number" }],
                composables: [],
            },
            summary: {
                from: "source",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "ir1", label: "Revenue", type: "number" },
                ],
                composables: [{
                    kind: "group",
                    by: ["STATUS"],
                    values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }],
                }],
            },
        },
    };
    const widget = {
        doc,
        schema: { columns: doc.tables.source.schema },
        searchScopeCol: "ir1",
        els: { search: { value: "1000" } },
        applyOrBanner: mutate => mutate(doc),
        showError: error => { throw error; },
        t: key => key,
    };

    doSearch(widget);

    assert.deepEqual(doc.tables.source.composables, []);
    assert.deepEqual(doc.tables.summary.composables.at(-1), {
        kind: "filter",
        filters: [{ enabled: true, expr: "ir1 = 1000" }],
    });
});
