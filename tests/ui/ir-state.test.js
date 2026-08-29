import assert from "node:assert/strict";
import test from "node:test";
import {
    activeTableLayer,
    activeTableSchema,
    activateTail,
    configuredTail,
    expressionReferencesColumn,
    locatedComposables,
    lookupValue,
    modeOf,
    nextFreeId,
    normalizeReportState,
    pivotRowDims,
    removeSourceComputedColumn,
    removeStageComputedColumn,
    scopedSearchExpression,
    serializeReportState,
    setMapEntry,
    sourceLayer,
    stageOf,
    tailOf,
} from "../../src/client/report/state.js";
import {
    shapeInputColumns,
    stageContext,
    terminalTableColumns,
    visibleStageColumnNames,
} from "../../src/client/report/stage.js";

const group = (by, values = [], layer = undefined) =>
    ({ shape: { kind: "group", by, values }, layer });
const pivot = (rows, cols, values = [], layer = undefined, totals = undefined) =>
    ({ shape: { kind: "pivot", rows, cols, values, ...(totals ? { totals } : {}) }, layer });

const layerKinds = {
    computed: ["compute", "computed"],
    filters: ["filter", "filters"],
    sorts: ["sort", "sorts"],
    breaks: ["break", "breaks"],
    aggregates: ["aggregate", "aggregates"],
    highlights: ["highlight", "highlights"],
    columns: ["select", "columns"],
    labels: ["labels", "labels"],
    formats: ["formats", "formats"],
};
const composables = layer => Object.entries(layer ?? {}).flatMap(([property, value]) => {
    const spec = layerKinds[property];
    return spec && value !== undefined ? [{ kind: spec[0], [spec[1]]: value }] : [];
});
const tableFor = (stage, from = "source") => ({
    from,
    composables: [stage.shape, ...composables(stage.layer)],
});
const report = (source = {}, active = null, alternatives = {}) => {
    const tables = { source: { from: "definition", composables: composables(source) } };
    for (const [id, stage] of Object.entries(alternatives)) tables[id] = tableFor(stage);
    let activeTable = "source";
    if (active) {
        activeTable = active.shape.kind === "group" ? "groupBy" : active.shape.kind;
        tables[activeTable] = tableFor(active);
    }
    return { activeTable, tables };
};

test("normalization clones input, guarantees a definition table, and resets only the page index", () => {
    const input = {
        ...report({ filters: [{ expr: "AMOUNT > 1" }] }),
        page: { index: 9, size: 75 },
    };
    const state = normalizeReportState(input, 25);

    assert.notEqual(state, input);
    assert.deepEqual(state.page, { index: 1, size: 75 });
    assert.deepEqual(input.page, { index: 9, size: 75 });
    assert.equal(state.tables.source.from, "definition");
    assert.deepEqual(sourceLayer(state).filters, [{ expr: "AMOUNT > 1" }]);

    const empty = normalizeReportState(null, 25);
    assert.equal(Object.keys(empty.tables).length, 1);
    assert.equal(modeOf(empty), "grid");
});

test("normalization mirrors server default resolution: tables replace wholesale", () => {
    const defaults = {
        search: "open",
        ...report({ filters: [{ expr: "STATUS = 'OPEN'" }], sorts: [{ col: "AMOUNT", dir: "desc" }] }),
    };
    const inherited = normalizeReportState(null, 25, defaults);
    assert.deepEqual(sourceLayer(inherited).sorts, [{ col: "AMOUNT", dir: "desc" }]);

    const cleared = normalizeReportState({ search: "", ...report({ filters: [], sorts: [] }) }, 25, defaults);
    assert.equal(cleared.search, "");
    assert.deepEqual(sourceLayer(cleared).filters, []);
    assert.deepEqual(sourceLayer(cleared).sorts, []);
});

test("the mode derives from composables; switching preserves named tables losslessly", () => {
    const state = normalizeReportState({
        ...report(
            {},
            pivot(
                ["CUSTOMER"],
                ["STATUS"],
                [{ id: "m1", col: "AMOUNT", fn: "sum" }],
                {
                    computed: [{ id: "c2", expr: '`m1@["SHIPPED"]` / 2' }],
                    labels: { 'm1@["SHIPPED"]': "Shipped" },
                },
                true)),
    }, 25);

    assert.equal(modeOf(state), "pivot");
    assert.deepEqual(pivotRowDims(state), ["CUSTOMER"]);

    activateTail(state, "chart", [{ shape: { kind: "chart", type: "pie", label: "STATUS", fn: "count" } }]);
    assert.equal(modeOf(state), "chart");
    // The pivot table remains in the map, layers included.
    const parked = configuredTail(state, "pivot");
    assert.equal(parked.length, 1);
    assert.equal(parked[0].layer.computed[0].id, "c2");
    assert.equal(parked[0].layer.labels['m1@["SHIPPED"]'], "Shipped");

    activateTail(state, "grid");
    assert.equal(modeOf(state), "grid");
    assert.equal(tailOf(state).length, 0);

    // Switching back only changes activeTable.
    activateTail(state, "pivot");
    assert.equal(modeOf(state), "pivot");
    assert.ok(state.tables.pivot);
    assert.equal(stageOf(state, "pivot").layer.computed[0].id, "c2");

    const saved = serializeReportState(state);
    const reloaded = normalizeReportState(saved, 25);
    assert.equal(modeOf(reloaded), "pivot");
    assert.equal(configuredTail(reloaded, "chart")[0].shape.type, "pie");
});

test("serialization preserves explicit clears and removes working fields", () => {
    const result = serializeReportState({
        search: "",
        ...report({ filters: [], columns: [] }),
        _transient: true,
        omitted: undefined,
    });

    assert.deepEqual(result, {
        search: "",
        activeTable: "source",
        tables: {
            source: {
                from: "definition",
                composables: [
                    { kind: "filter", filters: [] },
                    { kind: "select", columns: [] },
                ],
            },
        },
    });
});

test("source label overrides inherit from defaults and an emptied map survives as an explicit clear", () => {
    const defaults = report({ labels: { ORDER_ID: "Order #" } });
    const inherited = normalizeReportState(null, 25, defaults);
    assert.deepEqual(sourceLayer(inherited).labels, { ORDER_ID: "Order #" });

    delete sourceLayer(inherited).labels.ORDER_ID;
    assert.deepEqual(serializeReportState(inherited).tables.source.composables[0].labels, {});
});

test("source computed deletion strips the source layer precisely and drops dependent tails whole", () => {
    const state = normalizeReportState({
        ...report(
            {
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
            },
            pivot(["CUSTOMER"], ["STATUS"], [{ id: "m1", col: "c1", fn: "sum" }]),
            {
                groupBy: group(["REGION"], [{ id: "m1", col: "c1", fn: "sum" }]),
                chart: { shape: { kind: "chart", type: "bar", label: "CUSTOMER", value: "AMOUNT", fn: "sum" } },
            }),
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
    assert.equal(state.tables.groupBy, undefined);
    // The independent chart never referenced c1 and survives.
    assert.equal(state.tables.chart.composables[0].type, "bar");
});

test("a tail that references the computed column only through a dim also dies whole", () => {
    const grouped = normalizeReportState({
        ...report({ computed: [{ id: "c1", expr: "AMOUNT * 2" }] }, group(["c1"])),
    }, 25);
    const dropped = removeSourceComputedColumn(grouped, "c1");
    assert.equal(modeOf(grouped), "grid");
    assert.deepEqual(dropped, ["groupBy"]);

    const charted = normalizeReportState({
        ...report(
            { computed: [{ id: "c1", expr: "AMOUNT * 2" }] },
            { shape: { kind: "chart", type: "bar", label: "STATUS", value: "c1", fn: "sum" } }),
    }, 25);
    removeSourceComputedColumn(charted, "c1");
    assert.equal(modeOf(charted), "grid");
});

test("stage computed deletion strips only its owning derived layer", () => {
    const state = normalizeReportState({
        ...report({}, group(["CUSTOMER", "STATUS"], [{ id: "m1", col: "AMOUNT", fn: "sum" }], {
                computed: [
                    { id: "c2", label: "Share", expr: "m1 / __count" },
                    { id: "c3", label: "Uses c2? No", expr: "m1 * 2" },
                ],
                sorts: [{ col: "c2", dir: "desc" }, { col: "CUSTOMER", dir: "asc" }],
                breaks: ["c2", "CUSTOMER"],
                aggregates: [{ col: "c2", fn: "avg" }, { col: "m1", fn: "sum" }],
                highlights: [
                    { id: "h1", scope: "cell", col: "c2", expr: "m1 > 0" },
                    { id: "h2", scope: "row", expr: "c2 > 10" },
                    { id: "h3", scope: "row", expr: "m1 > 100" },
                ],
                columns: ["CUSTOMER", "STATUS", "__count", "m1", "c2", "c3"],
                labels: { c2: "Share" },
                formats: { c2: { mask: "percent1" } },
            })),
    }, 25);

    removeStageComputedColumn(state, stageOf(state, "group"), "C2");

    const layer = stageOf(state, "group").layer;
    assert.deepEqual(layer.computed.map(r => r.id), ["c3"]);
    assert.deepEqual(layer.sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(layer.breaks, ["CUSTOMER"]);
    assert.deepEqual(layer.aggregates, [{ col: "m1", fn: "sum" }]);
    assert.deepEqual(layer.highlights.map(r => r.id), ["h3"]);
    assert.deepEqual(layer.columns, ["CUSTOMER", "STATUS", "__count", "m1", "c3"]);
    assert.deepEqual(layer.labels, {});
    assert.deepEqual(layer.formats, {});
});

test("retiring a Pivot metric prunes its generated cell family", () => {
    const shipped = 'm1@["SHIPPED"]';
    const pending = 'm1@["PENDING"]';
    const state = normalizeReportState({
        ...report({}, pivot(["CUSTOMER"], ["STATUS"], [{ id: "m1", col: "AMOUNT", fn: "sum" }], {
                computed: [
                    { id: "c2", expr: '`m1@["SHIPPED"]` / 2' },
                    { id: "c3", expr: "CUSTOMER || '!'" },
                ],
                filters: [{ expr: '`m1@["PENDING"]` > 0' }],
                sorts: [{ col: shipped, dir: "desc" }, { col: "CUSTOMER", dir: "asc" }],
                highlights: [
                    { id: "h1", scope: "cell", col: pending, expr: "CUSTOMER IS NOT NULL" },
                    { id: "h2", scope: "row", expr: "CUSTOMER IS NOT NULL" },
                ],
                columns: ["CUSTOMER", shipped, pending, "c2", "c3"],
                labels: { [shipped]: "Shipped", CUSTOMER: "Customer" },
                formats: { [pending]: { bold: true }, CUSTOMER: { italic: true } },
            })),
    }, 25);

    const stage = stageOf(state, "pivot");
    removeStageComputedColumn(state, stage, "m1");

    assert.deepEqual(stage.layer.computed.map(rule => rule.id), ["c3"]);
    assert.deepEqual(stage.layer.filters, []);
    assert.deepEqual(stage.layer.sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(stage.layer.highlights.map(rule => rule.id), ["h2"]);
    assert.deepEqual(stage.layer.columns, ["CUSTOMER", "c3"]);
    assert.deepEqual(stage.layer.labels, { CUSTOMER: "Customer" });
    assert.deepEqual(stage.layer.formats, { CUSTOMER: { italic: true } });
});

test("the retired schema-snapshot key never enters the working copy", () => {
    // Legacy saved documents and server-stamped defaults may still carry it;
    // the client is liberal about the rest but has no use for this key.
    const fromDocument = normalizeReportState({ schema: { GONE: "number" }, ...report() }, 25);
    assert.equal("schema" in fromDocument, false);

    const fromDefaults = normalizeReportState(null, 25, { schema: { OLD: "text" }, ...report() });
    assert.equal("schema" in fromDefaults, false);
});

test("expression column references skip quoted contents and longer identifiers", () => {
    assert.equal(expressionReferencesColumn("c1 > 0", "C1"), true);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'c1'", "c1"), false);
    assert.equal(expressionReferencesColumn("c10 > 0", "c1"), false);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'it''s c1'", "c1"), false);
    assert.equal(expressionReferencesColumn('`m1@["SHIPPED"]` > 0', 'm1@["SHIPPED"]'), true);
    assert.equal(expressionReferencesColumn('`m1@["SHIPPED"]` > 0', "m1"), false);
    assert.equal(expressionReferencesColumn('`m1@["SHIPPED"]` > 0', "m1", { pivotFamily: true }), true);
});

test("scoped text search emits an escaped expression rule", () => {
    assert.equal(
        scopedSearchExpression("CUSTOMER", "text", "O'Brien"),
        "CONTAINS(CUSTOMER, 'O''Brien')");
    assert.equal(
        scopedSearchExpression("Customer Name", "text", "Acme"),
        "CONTAINS(`Customer Name`, 'Acme')");
});

test("scoped search emits typed number, date, and boolean predicates", () => {
    assert.equal(scopedSearchExpression("AMOUNT", "number", "-12.50"), "AMOUNT = -12.50");
    assert.equal(
        scopedSearchExpression("ORDER_DATE", "date", "2026-08-06"),
        "ORDER_DATE = TO_DATE('2026-08-06')");
    assert.equal(scopedSearchExpression("ACTIVE", "bool", "false"), "NOT ACTIVE");
    assert.equal(scopedSearchExpression("AMOUNT", "number", "1 234,50", "fr-CA"), "AMOUNT = 1234.50");
    assert.equal(scopedSearchExpression("ACTIVE", "bool", "vrai", "fr-CA"), "ACTIVE");
    assert.equal(scopedSearchExpression("ACTIVE", "bool", "faux", "fr-CA"), "NOT ACTIVE");
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

test("map writes remove case-variant keys so stale entries cannot shadow new settings", () => {
    const formats = { amount: { mask: "integer" }, OTHER: { bold: true } };
    setMapEntry(formats, "AMOUNT", { mask: "currency:CAD" });
    assert.deepEqual(formats, { OTHER: { bold: true }, AMOUNT: { mask: "currency:CAD" } });
    assert.deepEqual(lookupValue(formats, "amount"), { mask: "currency:CAD" });
    setMapEntry(formats, "Amount", undefined);
    assert.deepEqual(formats, { OTHER: { bold: true } });
});

test("visible stage columns discard stale explicit names and canonicalize casing", () => {
    const w = { doc: { layer: { columns: ["order_id", "REMOVED"] } } };
    const ctx = {
        columns: [{ name: "ORDER_ID" }, { name: "CUSTOMER" }],
        columnsLayer: doc => doc.layer,
    };
    assert.deepEqual(visibleStageColumnNames(ctx, w), ["ORDER_ID"]);
});

test("terminal edits target one exact active-table composable and preserve repeated foreign nodes", () => {
    const state = normalizeReportState({
        activeTable: "decorated",
        tables: {
            input: {
                from: "definition",
                composables: [{ kind: "filter", filters: [{ expr: "ACTIVE" }] }],
            },
            pivoted: {
                from: "input",
                composables: [
                    { kind: "filter", filters: [{ expr: "AMOUNT > 0" }] },
                    { kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"], values: [] },
                    { kind: "filter", filters: [{ expr: "CUSTOMER IS NOT NULL" }] },
                ],
            },
            decorated: {
                from: "pivoted",
                schema: [
                    { name: "CUSTOMER", label: "Customer", type: "text" },
                    { name: "m1@[\"OPEN\"]", label: "Open", type: "number" },
                ],
                composables: [
                    { kind: "labels", labels: { CUSTOMER: "Earlier label" } },
                    { kind: "filter", filters: [{ expr: "CUSTOMER <> 'Internal'" }] },
                    { kind: "foreign-decoration", payload: { keep: true } },
                    { kind: "filter", filters: [{ expr: "`m1@[\"OPEN\"]` > 10" }] },
                    { kind: "labels", labels: { CUSTOMER: "Current label" } },
                ],
            },
        },
    }, 25);

    const earlierFilter = structuredClone(state.tables.decorated.composables[1]);
    const foreign = structuredClone(state.tables.decorated.composables[2]);
    const layer = activeTableLayer(state);
    assert.deepEqual(layer.filters, [{ expr: "`m1@[\"OPEN\"]` > 10" }]);
    layer.filters.push({ expr: "CUSTOMER IS NOT NULL" });
    layer.labels.CUSTOMER = "Edited label";
    layer.sorts = [{ col: "CUSTOMER", dir: "asc" }];

    assert.deepEqual(state.tables.decorated.composables[1], earlierFilter);
    assert.deepEqual(state.tables.decorated.composables[2], foreign);
    assert.deepEqual(state.tables.decorated.composables[3].filters, [
        { expr: "`m1@[\"OPEN\"]` > 10" },
        { expr: "CUSTOMER IS NOT NULL" },
    ]);
    assert.equal(state.tables.decorated.composables[4].labels.CUSTOMER, "Edited label");
    assert.deepEqual(state.tables.decorated.composables.at(-1), {
        kind: "sort", sorts: [{ col: "CUSTOMER", dir: "asc" }],
    });
});

test("located composables retain ancestry ownership and expose only the safe active terminal node", () => {
    const state = normalizeReportState({
        activeTable: "decorated",
        tables: {
            source: {
                from: "definition",
                composables: [{ kind: "filter", filters: [{ expr: "SOURCE" }] }],
            },
            grouped: {
                from: "source",
                composables: [
                    { kind: "group", by: ["STATUS"], values: [] },
                    { kind: "compute", computed: [{ id: "c1", expr: "__count / 2" }] },
                    { kind: "filter", filters: [{ expr: "c1 > 0" }] },
                    { kind: "break", breaks: ["STATUS"] },
                    { kind: "aggregate", aggregates: [{ col: "__count", fn: "sum" }] },
                    { kind: "highlight", highlights: [{ expr: "c1 > 10", color: "red" }] },
                ],
            },
            decorated: {
                from: "grouped",
                composables: [
                    { kind: "filter", filters: [{ expr: "EARLIER" }] },
                    { kind: "foreign-decoration", payload: { keep: true } },
                    { kind: "filter", filters: [{ expr: "CURRENT" }] },
                ],
            },
        },
    }, 25);

    const locations = locatedComposables(state);
    assert.deepEqual(
        locations.map(location => [location.tableId, location.composable.kind]),
        [
            ["source", "filter"],
            ["grouped", "group"],
            ["grouped", "compute"],
            ["grouped", "filter"],
            ["grouped", "break"],
            ["grouped", "aggregate"],
            ["grouped", "highlight"],
            ["decorated", "filter"],
            ["decorated", "foreign-decoration"],
            ["decorated", "filter"],
        ]);
    assert.equal(locations.find(location => location.tableId === "grouped" && location.composable.kind === "compute").inherited, true);
    assert.equal(locations.find(location => location.tableId === "grouped" && location.composable.kind === "filter").participates, true);
    assert.ok(locations
        .filter(location => location.tableId === "grouped" && ["break", "aggregate", "highlight"].includes(location.composable.kind))
        .every(location => !location.participates));
    assert.equal(locations.find(location => location.composable?.payload?.keep).authorable, false);
    assert.deepEqual(
        locations.filter(location => location.authorable).map(location => location.composable.filters?.[0]?.expr),
        ["CURRENT"]);
});

test("the active table schema is the generic terminal universe for every shape", () => {
    const state = normalizeReportState({
        activeTable: "decorated",
        tables: {
            source: {
                from: "definition",
                schema: [{ name: "AMOUNT", label: "Amount", type: "number" }],
                composables: [{ kind: "labels", labels: { CUSTOMER: "Account", AMOUNT: "Revenue" } }],
            },
            pivoted: {
                from: "source",
                composables: [
                    { kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"], values: [] },
                    { kind: "labels", labels: { metric: "Inherited metric" } },
                ],
            },
            decorated: {
                from: "pivoted",
                schema: [
                    { name: "CUSTOMER", label: "Customer", type: "text" },
                    { name: "cell", label: "Cell", type: "number", formatSource: "AMOUNT" },
                    { name: "metric", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [{ kind: "labels", labels: { cell: "Current cell" } }],
            },
        },
    }, 25);
    const w = {
        doc: state,
        schema: { columns: [{ name: "AMOUNT", label: "Amount", type: "number" }] },
        lastResult: { availableColumns: [{ name: "STALE", label: "Stale", type: "text" }] },
    };

    assert.deepEqual(activeTableSchema(state).map(column => column.name), ["CUSTOMER", "cell", "metric"]);
    assert.deepEqual(terminalTableColumns(w).map(column => [column.name, column.label]), [
        ["CUSTOMER", "Account"],
        ["cell", "Current cell"],
        ["metric", "Inherited metric"],
    ]);
    const ctx = stageContext(w);
    assert.equal(ctx.mode, "pivot");
    assert.deepEqual(ctx.columns.map(column => column.name), ["CUSTOMER", "cell", "metric"]);
    assert.equal(ctx.caps.visibility, true);
    assert.equal(ctx.caps.displayAs, true);
    assert.equal(ctx.caps.filter, true);
    assert.equal(ctx.caps.highlight, true);
    assert.equal(ctx.caps.sort, true);
});

test("shape dialogs use the from-table schema rather than the shaped terminal schema", () => {
    const state = normalizeReportState({
        activeTable: "grouped",
        tables: {
            input: {
                from: "definition",
                schema: [
                    { name: "CUSTOMER", label: "Customer", type: "text" },
                    { name: "AMOUNT", label: "Amount", type: "number" },
                ],
                composables: [],
            },
            grouped: {
                from: "input",
                schema: [
                    { name: "CUSTOMER", label: "Customer", type: "text" },
                    { name: "__count", label: "Count", type: "number" },
                ],
                composables: [{ kind: "group", by: ["CUSTOMER"], values: [] }],
            },
        },
    }, 25);
    const w = { doc: state, schema: { columns: [] } };
    const configured = configuredTail(state, "groupBy")[0];

    assert.deepEqual(shapeInputColumns(w, configured).map(column => column.name), ["CUSTOMER", "AMOUNT"]);
    assert.deepEqual(terminalTableColumns(w).map(column => column.name), ["CUSTOMER", "__count"]);
});

test("editing a configured shape preserves repeated terminal composables", () => {
    const state = normalizeReportState({
        ...report({}, pivot(["CUSTOMER"], ["STATUS"])),
    }, 25);
    state.tables.pivot.composables.push(
        { kind: "filter", filters: [{ expr: "CUSTOMER <> 'A'" }] },
        { kind: "filter", filters: [{ expr: "CUSTOMER <> 'B'" }] });
    const filters = structuredClone(state.tables.pivot.composables.slice(1));
    const configured = configuredTail(state, "pivot");
    configured[0].shape.totals = true;

    activateTail(state, "pivot", configured);

    assert.equal(state.tables.pivot.composables[0].totals, true);
    assert.deepEqual(state.tables.pivot.composables.slice(1), filters);
});
