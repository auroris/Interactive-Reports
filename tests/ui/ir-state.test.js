import assert from "node:assert/strict";
import test from "node:test";
import {
    activateTail,
    configuredTail,
    expressionReferencesColumn,
    lookupValue,
    modeOf,
    nextFreeId,
    normalizeReportState,
    pivotRowDims,
    removeSourceComputedColumn,
    removeStageComputedColumn,
    schemaMismatch,
    schemaSnapshot,
    scopedSearchExpression,
    serializeReportState,
    sourceLayer,
    stageOf,
    tailOf,
} from "../../src/client/report/state.js";
import { visibleStageColumnNames } from "../../src/client/report/stage.js";

const src = layer => ({ shape: { kind: "source" }, layer });
const group = (by, values = [], layer = undefined) =>
    ({ shape: { kind: "group", by, values }, layer });
const spread = (cols, layer = undefined, totals = undefined) =>
    ({ shape: { kind: "spread", cols, ...(totals ? { totals } : {}) }, layer });

test("normalization clones input, guarantees the source stage, and resets only the page index", () => {
    const input = {
        pipeline: [src({ filters: [{ expr: "AMOUNT > 1" }] })],
        page: { index: 9, size: 75 },
    };
    const state = normalizeReportState(input, 25);

    assert.notEqual(state, input);
    assert.deepEqual(state.page, { index: 1, size: 75 });
    assert.deepEqual(input.page, { index: 9, size: 75 });
    assert.equal(state.pipeline[0].shape.kind, "source");
    assert.deepEqual(sourceLayer(state).filters, [{ expr: "AMOUNT > 1" }]);
    assert.deepEqual(state.shelf, {});

    const empty = normalizeReportState(null, 25);
    assert.equal(empty.pipeline.length, 1);
    assert.equal(modeOf(empty), "grid");
});

test("normalization mirrors server default resolution: pipeline replaces wholesale", () => {
    const defaults = {
        search: "open",
        pipeline: [src({ filters: [{ expr: "STATUS = 'OPEN'" }], sorts: [{ col: "AMOUNT", dir: "desc" }] })],
    };
    const inherited = normalizeReportState(null, 25, defaults);
    assert.deepEqual(sourceLayer(inherited).sorts, [{ col: "AMOUNT", dir: "desc" }]);

    const cleared = normalizeReportState({ search: "", pipeline: [src({ filters: [], sorts: [] })] }, 25, defaults);
    assert.equal(cleared.search, "");
    assert.deepEqual(sourceLayer(cleared).filters, []);
    assert.deepEqual(sourceLayer(cleared).sorts, []);
});

test("the mode derives from the tail; switching parks tails on the shelf losslessly", () => {
    const state = normalizeReportState({
        pipeline: [
            src({}),
            group(["CUSTOMER", "STATUS"], [{ id: "m1", col: "AMOUNT", fn: "sum" }],
                { computed: [{ id: "c2", expr: "m1 / __count" }] }),
            spread(["STATUS"], { labels: { 'm1@["SHIPPED"]': "Shipped" } }, true),
        ],
    }, 25);

    assert.equal(modeOf(state), "pivot");
    assert.deepEqual(pivotRowDims(state), ["CUSTOMER"]);

    activateTail(state, "chart", [{ shape: { kind: "chart", type: "pie", label: "STATUS", fn: "count" } }]);
    assert.equal(modeOf(state), "chart");
    // The pivot tail survives on the shelf, layers included.
    const parked = configuredTail(state, "pivot");
    assert.equal(parked.length, 2);
    assert.equal(parked[0].layer.computed[0].id, "c2");
    assert.equal(parked[1].layer.labels['m1@["SHIPPED"]'], "Shipped");

    activateTail(state, "grid");
    assert.equal(modeOf(state), "grid");
    assert.equal(tailOf(state).length, 0);

    // Switching back restores the parked tail and removes it from the shelf.
    activateTail(state, "pivot");
    assert.equal(modeOf(state), "pivot");
    assert.equal(state.shelf.pivot, undefined);
    assert.equal(stageOf(state, "group").layer.computed[0].id, "c2");

    const saved = serializeReportState(state, 3);
    const reloaded = normalizeReportState(saved, 25);
    assert.equal(modeOf(reloaded), "pivot");
    assert.equal(configuredTail(reloaded, "chart")[0].shape.type, "pie");
});

test("serialization preserves explicit clears and removes working fields", () => {
    const result = serializeReportState({
        search: "",
        pipeline: [src({ filters: [], columns: [] })],
        _transient: true,
        omitted: undefined,
    }, 7);

    assert.deepEqual(result, {
        v: 7,
        search: "",
        pipeline: [{ shape: { kind: "source" }, layer: { filters: [], columns: [] } }],
    });
});

test("source label overrides inherit from defaults and an emptied map survives as an explicit clear", () => {
    const defaults = { pipeline: [src({ labels: { ORDER_ID: "Order #" } })] };
    const inherited = normalizeReportState(null, 25, defaults);
    assert.deepEqual(sourceLayer(inherited).labels, { ORDER_ID: "Order #" });

    delete sourceLayer(inherited).labels.ORDER_ID;
    assert.deepEqual(serializeReportState(inherited, 3).pipeline[0].layer.labels, {});
});

test("source computed deletion strips the source layer precisely and drops dependent tails whole", () => {
    const state = normalizeReportState({
        pipeline: [
            src({
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
            }),
            group(["CUSTOMER"], [{ id: "m1", col: "c1", fn: "sum" }]),
            spread(["CUSTOMER"]),
        ],
        shelf: {
            groupBy: [group(["REGION"], [{ id: "m1", col: "c1", fn: "sum" }])],
            chart: [{ shape: { kind: "chart", type: "bar", label: "CUSTOMER", value: "AMOUNT", fn: "sum" } }],
        },
    }, 25);

    const dropped = removeSourceComputedColumn(state, "C1");

    const layer = sourceLayer(state);
    assert.deepEqual(layer.computed.map(rule => rule.id), ["c2"]);
    assert.deepEqual(layer.columns, ["CUSTOMER", "c2"]);
    assert.deepEqual(layer.labels, { CUSTOMER: "Customer" });
    assert.deepEqual(layer.sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(layer.breaks, ["CUSTOMER"]);
    assert.deepEqual(layer.aggregates, [{ col: "AMOUNT", fn: "sum" }]);
    assert.deepEqual(layer.filters.map(rule => rule.expr), ["CUSTOMER = 'c1'", "c10 > 100"]);
    assert.deepEqual(layer.highlights.map(rule => rule.id), ["h3"]);
    assert.deepEqual(layer.formats, {
        CUSTOMER: { bold: true },
        AMOUNT: { mask: "currency:CAD" },
    });

    // The pivot tail consumed c1 as a metric source: deleted whole (T0 coarse).
    assert.equal(modeOf(state), "grid");
    assert.deepEqual(dropped.sort(), ["groupBy", "pivot"]);
    assert.equal(state.shelf.groupBy, undefined);
    // The shelved chart never referenced c1 and survives.
    assert.equal(state.shelf.chart[0].shape.type, "bar");
});

test("a tail that references the computed column only through a dim also dies whole", () => {
    const grouped = normalizeReportState({
        pipeline: [src({ computed: [{ id: "c1", expr: "AMOUNT * 2" }] }), group(["c1"])],
    }, 25);
    const dropped = removeSourceComputedColumn(grouped, "c1");
    assert.equal(modeOf(grouped), "grid");
    assert.deepEqual(dropped, ["groupBy"]);

    const charted = normalizeReportState({
        pipeline: [
            src({ computed: [{ id: "c1", expr: "AMOUNT * 2" }] }),
            { shape: { kind: "chart", type: "bar", label: "STATUS", value: "c1", fn: "sum" } },
        ],
    }, 25);
    removeSourceComputedColumn(charted, "c1");
    assert.equal(modeOf(charted), "grid");
});

test("stage computed deletion strips the group layer and the cell family's presentation", () => {
    const state = normalizeReportState({
        pipeline: [
            src({}),
            group(["CUSTOMER", "STATUS"], [{ id: "m1", col: "AMOUNT", fn: "sum" }], {
                computed: [
                    { id: "c2", label: "Share", expr: "m1 / __count" },
                    { id: "c3", label: "Uses c2? No", expr: "m1 * 2" },
                ],
                sorts: [{ col: "c2", dir: "desc" }, { col: "CUSTOMER", dir: "asc" }],
                highlights: [
                    { id: "h1", scope: "cell", col: "c2", expr: "m1 > 0" },
                    { id: "h2", scope: "row", expr: "c2 > 10" },
                    { id: "h3", scope: "row", expr: "m1 > 100" },
                ],
                columns: ["CUSTOMER", "STATUS", "__count", "m1", "c2", "c3"],
                labels: { c2: "Share" },
                formats: { c2: { mask: "percent1" } },
            }),
            spread(["STATUS"], {
                labels: { 'c2@["SHIPPED"]': "Shipped Share", 'm1@["SHIPPED"]': "Shipped Total" },
                formats: { 'c2@["SHIPPED"]': { bold: true } },
            }),
        ],
    }, 25);

    removeStageComputedColumn(state, stageOf(state, "group"), "C2");

    const layer = stageOf(state, "group").layer;
    assert.deepEqual(layer.computed.map(r => r.id), ["c3"]);
    assert.deepEqual(layer.sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(layer.highlights.map(r => r.id), ["h3"]);
    assert.deepEqual(layer.columns, ["CUSTOMER", "STATUS", "__count", "m1", "c3"]);
    assert.deepEqual(layer.labels, {});
    assert.deepEqual(layer.formats, {});
    const spreadLayer = stageOf(state, "spread").layer;
    assert.deepEqual(spreadLayer.labels, { 'm1@["SHIPPED"]': "Shipped Total" });
    assert.deepEqual(spreadLayer.formats, {});
});

test("the schema snapshot matches on additions and mismatches on removals and retypes", () => {
    const live = [
        { name: "ORDER_ID", type: "number" },
        { name: "CUSTOMER", type: "text" },
        { name: "BRAND_NEW", type: "text" },
    ];

    assert.equal(schemaMismatch(undefined, live), null, "no snapshot skips the check");
    assert.equal(schemaMismatch({ order_id: "number" }, live), null, "case-insensitive match");
    assert.equal(schemaMismatch({ ORDER_ID: "number", CUSTOMER: "text" }, live), null, "additions pass");

    const removed = schemaMismatch({ GONE: "text" }, live);
    assert.match(removed[0], /GONE was removed/);
    const retyped = schemaMismatch({ ORDER_ID: "text" }, live);
    assert.match(retyped[0], /ORDER_ID changed from text to number/);

    assert.deepEqual(
        schemaSnapshot([{ name: "A", type: "number" }, { name: "B", type: "date" }]),
        { A: "number", B: "date" });
});

test("expression column references skip quoted contents and longer identifiers", () => {
    assert.equal(expressionReferencesColumn("c1 > 0", "C1"), true);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'c1'", "c1"), false);
    assert.equal(expressionReferencesColumn("c10 > 0", "c1"), false);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'it''s c1'", "c1"), false);
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

test("case-insensitive state lookup and generated ids share their canonical helpers", () => {
    assert.deepEqual(lookupValue({ Amount: { mask: "currency" } }, "AMOUNT"), { mask: "currency" });
    assert.equal(lookupValue({ Amount: 1 }, "missing"), undefined);
    assert.equal(nextFreeId(new Set(["c1", "c3"]), "c"), "c2");
});

test("visible stage columns discard stale explicit names", () => {
    const w = { doc: { layer: { columns: ["ORDER_ID", "REMOVED"] } } };
    const ctx = {
        columns: [{ name: "ORDER_ID" }, { name: "CUSTOMER" }],
        columnsLayer: doc => doc.layer,
    };
    assert.deepEqual(visibleStageColumnNames(ctx, w), ["ORDER_ID"]);
});
