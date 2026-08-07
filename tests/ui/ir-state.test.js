import assert from "node:assert/strict";
import test from "node:test";
import {
    expressionReferencesColumn,
    normalizeReportState,
    removeComputedColumnReferences,
    scopedSearchExpression,
    serializeReportState,
} from "../../src/client/report/state.js";

test("normalization clones input and resets only the page index", () => {
    const input = { filters: [{ expr: "AMOUNT > 1" }], page: { index: 9, size: 75 } };
    const state = normalizeReportState(input, 25);

    assert.notEqual(state, input);
    assert.deepEqual(state.page, { index: 1, size: 75 });
    assert.deepEqual(input.page, { index: 9, size: 75 });
});

test("normalization mirrors server default resolution while preserving explicit clears", () => {
    const defaults = {
        search: "open",
        filters: [{ expr: "STATUS = 'OPEN'" }],
        sorts: [{ col: "AMOUNT", dir: "desc" }],
    };
    const state = normalizeReportState({ search: "", filters: [] }, 25, defaults);

    assert.equal(state.search, "");
    assert.deepEqual(state.filters, []);
    assert.deepEqual(state.sorts, [{ col: "AMOUNT", dir: "desc" }]);
});

test("serialization preserves explicit clears and removes working fields", () => {
    const result = serializeReportState({
        search: "",
        filters: [],
        sorts: [],
        columns: [],
        view: { mode: "grid" },
        _transient: true,
        omitted: undefined,
    }, 7);

    assert.deepEqual(result, {
        v: 7,
        search: "",
        filters: [],
        sorts: [],
        columns: [],
        view: { mode: "grid" },
    });
});

test("label overrides inherit from defaults and an emptied map survives as an explicit clear", () => {
    const defaults = { labels: { ORDER_ID: "Order #" } };
    const inherited = normalizeReportState(null, 25, defaults);
    assert.deepEqual(inherited.labels, { ORDER_ID: "Order #" });

    delete inherited.labels.ORDER_ID;
    assert.deepEqual(serializeReportState(inherited, 2).labels, {});
});

test("computed-column deletion removes every dependent instruction", () => {
    const state = {
        computed: [
            { id: "c1", label: "With Tax", expr: "AMOUNT * 1.05" },
            { id: "c2", label: "Other", expr: "AMOUNT * 2" },
        ],
        columns: ["CUSTOMER", "c1", "c2"],
        labels: { c1: "Taxed", CUSTOMER: "Customer" },
        sorts: [{ col: "c1", dir: "desc" }, { col: "CUSTOMER", dir: "asc" }],
        breaks: ["c1", "CUSTOMER"],
        aggregates: [{ col: "c1", fn: "sum" }, { col: "AMOUNT", fn: "sum" }],
        filters: [
            { expr: "c1 > 100" },
            { expr: "CUSTOMER = 'c1'" },
            { expr: "c10 > 100" },
        ],
        highlights: [
            { id: "h1", scope: "cell", col: "c1", expr: "AMOUNT > 0" },
            { id: "h2", scope: "row", expr: "C1 > 100" },
            { id: "h3", scope: "row", expr: "CUSTOMER = 'c1'" },
        ],
        formats: {
            c1: { mask: "currency:CAD" },
            CUSTOMER: { displayAs: "link", urlColumn: "c1", textColumn: "CUSTOMER", bold: true },
            AMOUNT: { mask: "currency:CAD" },
        },
        view: {
            mode: "pivot",
            rows: ["CUSTOMER"],
            cols: ["STATUS"],
            values: [{ col: "c1", fn: "sum" }, { col: "AMOUNT", fn: "sum" }],
            totals: true,
        },
    };

    removeComputedColumnReferences(state, "C1");

    assert.deepEqual(state.computed.map(rule => rule.id), ["c2"]);
    assert.deepEqual(state.columns, ["CUSTOMER", "c2"]);
    assert.deepEqual(state.labels, { CUSTOMER: "Customer" });
    assert.deepEqual(state.sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(state.breaks, ["CUSTOMER"]);
    assert.deepEqual(state.aggregates, [{ col: "AMOUNT", fn: "sum" }]);
    assert.deepEqual(state.filters.map(rule => rule.expr), ["CUSTOMER = 'c1'", "c10 > 100"]);
    assert.deepEqual(state.highlights.map(rule => rule.id), ["h3"]);
    assert.deepEqual(state.formats, {
        CUSTOMER: { bold: true },
        AMOUNT: { mask: "currency:CAD" },
    });
    assert.deepEqual(state.view, {
        mode: "pivot",
        rows: ["CUSTOMER"],
        cols: ["STATUS"],
        values: [{ col: "AMOUNT", fn: "sum" }],
        totals: true,
    });
});

test("computed-column deletion leaves quoted and longer identifiers alone and abandons invalid views", () => {
    assert.equal(expressionReferencesColumn("c1 > 0", "C1"), true);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'c1'", "c1"), false);
    assert.equal(expressionReferencesColumn("c10 > 0", "c1"), false);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'it''s c1'", "c1"), false);

    const grouped = { computed: [{ id: "c1" }], view: { mode: "groupBy", groupBy: ["c1"], values: [] } };
    removeComputedColumnReferences(grouped, "c1");
    assert.deepEqual(grouped.view, { mode: "grid" });

    const charted = { computed: [{ id: "c1" }], view: { mode: "chart", label: "STATUS", value: "c1", fn: "sum" } };
    removeComputedColumnReferences(charted, "c1");
    assert.deepEqual(charted.view, { mode: "grid" });
});

test("scoped text search emits an escaped expression rule", () => {
    assert.equal(
        scopedSearchExpression("CUSTOMER", "text", "O'Brien"),
        "CONTAINS(CUSTOMER, 'O''Brien')");
});

test("scoped search emits typed number, date, and boolean predicates", () => {
    assert.equal(scopedSearchExpression("AMOUNT", "number", "-12.50"), "AMOUNT = -12.50");
    assert.equal(
        scopedSearchExpression("ORDER_DATE", "date", "2026-08-06"),
        "ORDER_DATE = TO_DATE('2026-08-06')");
    assert.equal(scopedSearchExpression("ACTIVE", "bool", "false"), "NOT ACTIVE");
});

test("scoped search rejects invalid typed input before requesting the server", () => {
    assert.throws(() => scopedSearchExpression("AMOUNT", "number", "twelve"), /not a number/);
    assert.throws(() => scopedSearchExpression("ORDER_DATE", "date", "08\/06\/2026"), /ISO date/);
});
