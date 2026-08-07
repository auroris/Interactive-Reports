import assert from "node:assert/strict";
import test from "node:test";
import {
    normalizeReportState,
    scopedSearchExpression,
    serializeReportState,
} from "../../src/InteractiveReport.AspNetCore/Ui/src/report/state.js";

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
