import assert from "node:assert/strict";
import test from "node:test";
import {
    activeChain, activeShapeLocation, activeTableSchema, assignShapeMetricIds,
    composableLocations,
    composedFormats, composedLabels, createView, editInputComposable,
    editTerminalComposable, expressionReferencesColumn, inputComposableLocation,
    invalidateChangedSchemas, lookupValue, modeOf, nextFreeId,
    nextSyntheticColumnId, normalizeReportState, normalizedHighlightRules,
    ownShapeLocations, pruneRetiredChartOutputs,
    pruneRetiredMetrics, pruneRetiredPivotOutputs,
    removeInputComputedColumn, removeTerminalComputedColumn, replaceComposable,
    resolveCreationBase, resolveView, scopedSearchExpression, selectView,
    serializeReportState, setMapEntry, shapeLocations,
    terminalComposableLocation, viewCandidates,
} from "../../src/client/report/state.js";
import {
    shapeEditable, shapeInputColumns, tableContext, terminalTableColumns,
    visibleTableColumnNames,
} from "../../src/client/report/table.js";

const fieldKinds = {
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

const nodesFor = (fields = {}) => Object.entries(fields).flatMap(([property, value]) => {
    const spec = fieldKinds[property];
    return spec && value !== undefined ? [{ kind: spec[0], [spec[1]]: value }] : [];
});
const report = (input = {}) => ({
    activeTable: "base",
    tables: { base: { from: "definition", composables: nodesFor(input) } },
});
const node = (doc, kind, tableId = doc.activeTable) =>
    terminalComposableLocation(doc, kind, tableId)?.composable;
const inputNode = (doc, kind) => inputComposableLocation(doc, kind)?.composable;

test("normalization clones the document, supplies a definition table, and resets only page index", () => {
    const input = {
        ...report({ filters: [{ expr: "AMOUNT > 1" }] }),
        page: { index: 9, size: 75 },
    };
    const state = normalizeReportState(input, 25);

    assert.notEqual(state, input);
    assert.deepEqual(state.page, { index: 1, size: 75 });
    assert.deepEqual(input.page, { index: 9, size: 75 });
    assert.equal(state.tables.base.from, "definition");
    assert.deepEqual(inputNode(state, "filter").filters, [{ expr: "AMOUNT > 1" }]);

    const empty = normalizeReportState(null, 25);
    assert.deepEqual(Object.keys(empty.tables), ["base"]);
    assert.equal(empty.activeTable, "base");
    assert.equal(modeOf(empty), "grid");
});

test("foreign token casing and whitespace classify like the server without rewriting the document", () => {
    const state = normalizeReportState({
        activeTable: "  PIVOTED  ",
        tables: {
            Base: {
                from: "  DeFiNiTiOn ",
                composables: [{ kind: " FiLtEr ", filters: [{ expr: "AMOUNT > 0" }] }],
            },
            Pivoted: {
                from: "  bAsE ",
                composables: [
                    { kind: " PiVoT ", rows: ["CUSTOMER"], cols: ["STATUS"], values: [] },
                    { kind: " SoRt ", sorts: [{ col: "CUSTOMER", dir: "asc" }] },
                ],
            },
        },
    }, 25);

    assert.equal(modeOf(state), "pivot");
    assert.deepEqual(activeChain(state).map(entry => entry.id), ["Base", "Pivoted"]);
    assert.equal(inputComposableLocation(state, "filter")?.composable.kind, " FiLtEr ");
    assert.equal(terminalComposableLocation(state, "sort")?.composable.kind, " SoRt ");
    assert.deepEqual(viewCandidates(state, "pivot").map(candidate => candidate.tableId), ["Pivoted"]);

    const serialized = serializeReportState(state);
    assert.equal(serialized.activeTable, "  PIVOTED  ");
    assert.equal(serialized.tables.Base.from, "  DeFiNiTiOn ");
    assert.equal(serialized.tables.Pivoted.from, "  bAsE ");
    assert.equal(serialized.tables.Pivoted.composables[0].kind, " PiVoT ");

    const edited = structuredClone(state);
    editTerminalComposable(edited, "SORT", composable => {
        composable.sorts = [];
    });
    assert.equal(edited.tables.Pivoted.composables[1].kind, "sort",
        "a touched node is emitted canonically");
});

test("normalization follows whole-document defaults and serialization preserves explicit clears", () => {
    const defaults = {
        search: "open",
        ...report({
            filters: [{ expr: "STATUS = 'OPEN'" }],
            sorts: [{ col: "AMOUNT", dir: "desc" }],
        }),
    };
    const inherited = normalizeReportState(null, 25, defaults);
    assert.deepEqual(inputNode(inherited, "sort").sorts, [{ col: "AMOUNT", dir: "desc" }]);

    const cleared = normalizeReportState({
        search: "",
        ...report({ filters: [], sorts: [], columns: [] }),
        _transient: true,
        omitted: undefined,
    }, 25, defaults);
    const saved = serializeReportState(cleared);
    assert.equal(saved.search, "");
    assert.deepEqual(inputComposableLocation(saved, "filter").composable.filters, []);
    assert.deepEqual(inputComposableLocation(saved, "sort").composable.sorts, []);
    assert.deepEqual(inputComposableLocation(saved, "select").composable.columns, []);
    assert.equal("_transient" in saved, false);
    assert.equal("omitted" in saved, false);
});

test("retired schema snapshots never enter the working copy", () => {
    const fromDocument = normalizeReportState({ schema: { GONE: "number" }, ...report() }, 25);
    assert.equal("schema" in fromDocument, false);
    const fromDefaults = normalizeReportState(null, 25, { schema: { OLD: "text" }, ...report() });
    assert.equal("schema" in fromDefaults, false);
});

test("toolbar identity comes only from shapes directly owned by the active table", () => {
    const state = normalizeReportState({
        activeTable: "pivoted",
        tables: {
            base: { from: "definition", composables: [] },
            pivoted: {
                from: "base",
                composables: [{ kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"], values: [] }],
            },
            decorated: {
                from: "pivoted",
                composables: [{ kind: "labels", labels: { CUSTOMER: "Account" } }],
            },
            grouped: {
                from: "pivoted",
                composables: [{ kind: "group", by: ["CUSTOMER"], values: [] }],
            },
            multiple: {
                from: "base",
                composables: [
                    { kind: "group", by: ["CUSTOMER"], values: [] },
                    { kind: "chart", type: "bar", label: "CUSTOMER", fn: "count" },
                ],
            },
        },
    }, 25);

    assert.equal(modeOf(state), "pivot");
    state.activeTable = "decorated";
    assert.equal(modeOf(state), "custom",
        "an ordinary descendant neither inherits Pivot identity nor masquerades as Base");
    assert.equal(activeShapeLocation(state), null);
    assert.equal(ownShapeLocations(state).length, 0);
    assert.deepEqual(shapeLocations(state).map(location => location.composable.kind), ["pivot"]);
    state.activeTable = "grouped";
    assert.equal(modeOf(state), "groupBy", "the directly owned Group wins over an inherited Pivot");
    state.activeTable = "multiple";
    assert.equal(modeOf(state), "custom", "several directly owned shapes are preserved without a lossy mode");
});

test("view resolution switches a unique exact table and reports ambiguity explicitly", () => {
    const state = normalizeReportState({
        activeTable: "base",
        tables: {
            base: { from: "definition", composables: [] },
            groupWest: { from: "base", composables: [{ kind: "group", by: ["REGION"], values: [] }] },
            pivoted: { from: "base", composables: [{ kind: "pivot", rows: ["REGION"], cols: [], values: [] }] },
            groupEast: { from: "base", composables: [{ kind: "group", by: ["TEAM"], values: [] }] },
            decoratedPivot: { from: "pivoted", composables: [{ kind: "sort", sorts: [] }] },
        },
    }, 25);

    const pivot = resolveView(state, "pivot");
    assert.equal(pivot.status, "available");
    assert.equal(pivot.candidate.tableId, "pivoted");
    assert.equal(viewCandidates(state, "pivot").length, 1,
        "a descendant without its own Pivot is not another Pivot candidate");
    assert.deepEqual(viewCandidates(state, "grid").map(candidate => candidate.tableId), ["base"],
        "a foreign ordinary descendant does not make the definition-backed Base ambiguous");
    assert.equal(selectView(state, "pivot"), true);
    assert.equal(state.activeTable, "pivoted");
    assert.equal(resolveView(state, "pivot").status, "active");

    state.activeTable = "base";
    const grouped = resolveView(state, "groupBy");
    assert.equal(grouped.status, "ambiguous");
    assert.deepEqual(grouped.candidates.map(candidate => candidate.tableId), ["groupWest", "groupEast"]);
    assert.equal(selectView(state, "groupBy"), false, "map order must not choose a foreign table");
    assert.equal(selectView(state, "groupBy", "groupEast"), true);
    assert.equal(state.activeTable, "groupEast");
});

test("view creation uses an exact direct-from-definition base and creates one shape node", () => {
    const state = normalizeReportState({
        activeTable: "base",
        tables: {
            base: {
                from: "definition",
                schema: [{ name: "REGION", label: "Region", type: "text" }],
                composables: [{ kind: "filter", filters: [] }],
            },
            groupBy: { from: "base", composables: [{ kind: "group", by: ["OLD"], values: [] }] },
        },
    }, 25);
    const base = resolveCreationBase(state);
    assert.equal(base.status, "active");
    assert.equal(base.candidate.tableId, "base");

    const created = createView(
        state,
        "groupBy",
        { kind: " GROUP ", by: ["REGION"], values: [] },
        base.candidate.tableId);
    assert.equal(created.tableId, "groupBy2");
    assert.equal(state.activeTable, "groupBy2");
    assert.equal(state.tables.groupBy2.from, "base");
    assert.deepEqual(state.tables.groupBy2.composables, [
        { kind: "group", by: ["REGION"], values: [] },
    ]);

    state.tables.derived = { from: "base", composables: [] };
    assert.throws(
        () => createView(state, "chart", { kind: "chart", type: "bar", label: "REGION", fn: "count" }, "derived"),
        /base table is unavailable/i);

    const foreign = normalizeReportState({
        activeTable: "missing",
        tables: {
            alpha: { from: "definition", composables: [] },
            beta: { from: "definition", composables: [] },
        },
    }, 25);
    const ambiguous = resolveCreationBase(foreign);
    assert.equal(ambiguous.status, "ambiguous");
    assert.deepEqual(ambiguous.candidates.map(candidate => candidate.tableId), ["alpha", "beta"]);
});

test("shape edits replace one exact node and preserve all sibling composables", () => {
    const state = normalizeReportState({
        activeTable: "pivoted",
        tables: {
            base: { from: "definition", schema: [{ name: "CUSTOMER", type: "text" }], composables: [] },
            pivoted: {
                from: "base",
                composables: [
                    { kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"], values: [] },
                    { kind: "filter", filters: [{ expr: "CUSTOMER <> 'A'" }] },
                    { kind: "filter", filters: [{ expr: "CUSTOMER <> 'B'" }] },
                ],
            },
        },
    }, 25);
    const location = activeShapeLocation(state, "pivot");
    const siblings = structuredClone(state.tables.pivoted.composables.slice(1));
    const replaced = replaceComposable(state, location, { ...location.composable, totals: true });

    assert.equal(replaced.composable.totals, true);
    assert.deepEqual(state.tables.pivoted.composables.slice(1), siblings);
    assert.equal(shapeEditable(replaced), true);
    assert.deepEqual(shapeInputColumns({ doc: state, schema: { columns: [] } }, replaced)
        .map(column => column.name), ["CUSTOMER"]);

    state.tables.foreign = {
        from: "base",
        composables: [
            { kind: "filter", filters: [{ expr: "ACTIVE" }] },
            { kind: "chart", type: "bar", label: "CUSTOMER", fn: "count" },
        ],
    };
    state.activeTable = "foreign";
    const storedAfterFilter = activeShapeLocation(state, "chart");
    assert.equal(shapeEditable(storedAfterFilter), true,
        "shape editability follows natural semantics rather than array position");
    assert.deepEqual(shapeInputColumns({ doc: state, schema: { columns: [] } }, storedAfterFilter)
        .map(column => column.name), ["CUSTOMER"]);
});

test("terminal edits target the final exact node owned by the selected table", () => {
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
                    { kind: "filter", filters: [{ expr: "INHERITED" }] },
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
    const earlier = structuredClone(state.tables.decorated.composables[0]);
    const foreign = structuredClone(state.tables.decorated.composables[1]);

    editTerminalComposable(state, "filter", composable => composable.filters.push({ expr: "NEW" }));
    editTerminalComposable(state, "sort", composable => { composable.sorts = [{ col: "STATUS", dir: "asc" }]; });

    assert.deepEqual(state.tables.decorated.composables[0], earlier);
    assert.deepEqual(state.tables.decorated.composables[1], foreign);
    assert.deepEqual(state.tables.decorated.composables[2].filters, [{ expr: "CURRENT" }, { expr: "NEW" }]);
    assert.deepEqual(state.tables.decorated.composables[3], {
        kind: "sort", sorts: [{ col: "STATUS", dir: "asc" }],
    });
});

test("input edits target the final exact root node independent of shape position", () => {
    const state = normalizeReportState({
        activeTable: "derived",
        tables: {
            root: {
                from: "definition",
                composables: [
                    { kind: "filter", filters: [{ expr: "INPUT" }] },
                    { kind: "pivot", rows: ["CUSTOMER"], cols: [], values: [] },
                    { kind: "filter", filters: [{ expr: "OUTPUT" }] },
                ],
            },
            derived: { from: "root", composables: [] },
        },
    }, 25);

    assert.deepEqual(inputNode(state, "filter").filters, [{ expr: "OUTPUT" }]);
    editInputComposable(state, "filter", composable => composable.filters.push({ expr: "ADDED" }));
    assert.deepEqual(state.tables.root.composables[0].filters, [{ expr: "INPUT" }]);
    assert.deepEqual(state.tables.root.composables[2].filters, [{ expr: "OUTPUT" }, { expr: "ADDED" }]);
});

test("located composables retain exact ancestry ownership and editability", () => {
    const state = normalizeReportState({
        activeTable: "decorated",
        tables: {
            source: { from: "definition", composables: [{ kind: "filter", filters: [{ expr: "SOURCE" }] }] },
            grouped: {
                from: "source",
                composables: [
                    { kind: "group", by: ["STATUS"], values: [] },
                    { kind: "compute", computed: [{ id: "ir1", expr: "__count / 2" }] },
                    { kind: "break", breaks: ["STATUS"] },
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
    const locations = composableLocations(state);

    assert.deepEqual(locations.map(location => [location.tableId, location.composable.kind]), [
        ["source", "filter"], ["grouped", "group"], ["grouped", "compute"],
        ["grouped", "break"], ["decorated", "filter"],
        ["decorated", "foreign-decoration"], ["decorated", "filter"],
    ]);
    assert.equal(locations.find(location => location.composable.kind === "compute").inherited, true);
    assert.equal(locations.find(location => location.composable.kind === "compute").participates, true);
    assert.equal(locations.find(location => location.composable.kind === "compute").afterShape, true,
        "same-table ordinary operations naturally follow the owned shape");
    assert.equal(locations.find(location => location.composable.kind === "compute").source, false,
        "an ordinary composable owned beside a shape is not definition-input state");
    assert.equal(locations[0].source, true);
    assert.equal(locations.find(location => location.composable.kind === "break").participates, false);
    assert.equal(locations.find(location => location.composable?.payload?.keep).authorable, false);
    assert.deepEqual(locations.filter(location => location.authorable)
        .map(location => location.composable.filters?.[0]?.expr), ["CURRENT"]);
});

test("the active table schema and direct shape determine generic table context", () => {
    const state = normalizeReportState({
        activeTable: "pivoted",
        tables: {
            source: {
                from: "definition",
                schema: [{ name: "AMOUNT", label: "Amount", type: "number" }],
                composables: [{ kind: "labels", labels: { CUSTOMER: "Account", AMOUNT: "Revenue" } }],
            },
            pivoted: {
                from: "source",
                schema: [
                    { name: "CUSTOMER", label: "Customer", type: "text" },
                    { name: "metric", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [
                    { kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"], values: [] },
                    { kind: "labels", labels: { metric: "Current metric" } },
                    { kind: "select", columns: ["customer", "REMOVED"] },
                ],
            },
        },
    }, 25);
    const w = {
        doc: state,
        schema: { columns: [{ name: "AMOUNT", label: "Amount", type: "number" }] },
        lastResult: { availableColumns: [{ name: "STALE", type: "text" }] },
    };

    assert.deepEqual(activeTableSchema(state).map(column => column.name), ["CUSTOMER", "metric"]);
    assert.deepEqual(terminalTableColumns(w).map(column => [column.name, column.label]), [
        ["CUSTOMER", "Account"], ["metric", "Current metric"],
    ]);
    const context = tableContext(w);
    assert.equal(context.mode, "pivot");
    assert.deepEqual(context.dims, ["CUSTOMER"]);
    assert.deepEqual(visibleTableColumnNames(context, w), ["CUSTOMER"]);
    assert.equal(context.caps.visibility, true);
    assert.equal(context.caps.displayAs, true);
});

test("generated labels resolve through the completed parent table", () => {
    const state = normalizeReportState({
        activeTable: "second",
        tables: {
            base: {
                from: "definition",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "AMOUNT", label: "Amount", type: "number" },
                ],
                composables: [],
            },
            first: {
                from: "base",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [
                    { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                    { kind: "labels", labels: { ir1: "Sales" } },
                ],
            },
            second: {
                from: "first",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "ir2", label: "sum(sum(Amount))", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [
                    { kind: "group", by: ["STATUS"], values: [{ id: "ir2", col: "ir1", fn: "sum" }] },
                ],
            },
        },
    }, 25);

    assert.deepEqual(terminalTableColumns({ doc: state }).map(column => [column.name, column.label]), [
        ["STATUS", "Status"],
        ["ir2", "sum(Sales)"],
    ]);
});

test("a later label override cannot rewrite an already-generated child metric", () => {
    const state = normalizeReportState({
        activeTable: "second",
        tables: {
            base: {
                from: "definition",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "AMOUNT", label: "Amount", type: "number" },
                ],
                composables: [],
            },
            first: {
                from: "base",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [
                    { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                    { kind: "labels", labels: { ir1: "Sales" } },
                ],
            },
            second: {
                from: "first",
                schema: [
                    { name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                    { name: "ir2", label: "sum(sum(Amount))", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [
                    { kind: "labels", labels: { ir1: "Segment" } },
                    { kind: "group", by: ["ir1"], values: [{ id: "ir2", col: "ir1", fn: "sum" }] },
                ],
            },
        },
    }, 25);

    assert.deepEqual(terminalTableColumns({ doc: state }).map(column => [column.name, column.label]), [
        ["ir1", "Segment"],
        ["ir2", "sum(Sales)"],
    ]);
});

test("compute tokens include inherited and local computed dependency candidates", () => {
    const state = normalizeReportState({
        activeTable: "child",
        tables: {
            parent: {
                from: "definition",
                schema: [
                    { name: "AMOUNT", label: "Amount", type: "number" },
                    { name: "ir1", label: "Taxed", type: "number", computed: true },
                ],
                composables: [
                    { kind: "compute", computed: [{ id: "ir1", label: "Taxed", expr: "AMOUNT * 1.05" }] },
                ],
            },
            child: {
                from: "parent",
                schema: [
                    { name: "AMOUNT", label: "Amount", type: "number" },
                    { name: "ir1", label: "Taxed", type: "number", computed: true },
                    { name: "ir2", label: "Rounded", type: "number", computed: true },
                ],
                composables: [
                    { kind: "compute", computed: [{ id: "ir2", label: "Rounded", expr: "ROUND(ir1, 0)" }] },
                ],
            },
        },
    }, 25);

    assert.deepEqual(tableContext({ doc: state }).computeTokens.map(column => column.name), ["AMOUNT", "ir1", "ir2"]);
});

test("presentation maps infer the shape boundary independently of storage order", () => {
    const state = normalizeReportState({
        activeTable: "decorated",
        tables: {
            base: {
                from: "definition",
                composables: [
                    { kind: "labels", labels: { CUSTOMER: "Account", AMOUNT: "Revenue" } },
                    { kind: "formats", formats: { AMOUNT: { mask: "plain" } } },
                ],
            },
            grouped: {
                from: "base",
                composables: [
                    { kind: "formats", formats: { ir1: { mask: "integer" } } },
                    { kind: "group", by: ["CUSTOMER"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                ],
            },
            decorated: { from: "grouped", composables: [{ kind: "labels", labels: { ir1: "Sales" } }] },
        },
    }, 25);

    assert.deepEqual(composedLabels(state), {
        input: { CUSTOMER: "Account", AMOUNT: "Revenue" }, output: { ir1: "Sales" },
    });
    assert.deepEqual(composedFormats(state), {
        AMOUNT: { mask: "plain" },
        ir1: { mask: "integer" },
    });
});

test("an empty post-shape presentation map clears inherited source metadata", () => {
    const state = normalizeReportState({
        activeTable: "plain",
        tables: {
            base: {
                from: "definition",
                composables: [
                    { kind: "labels", labels: { AMOUNT: "Revenue" } },
                    { kind: "formats", formats: { AMOUNT: { mask: "currency:CAD" } } },
                ],
            },
            grouped: {
                from: "base",
                schema: [
                    { name: "CUSTOMER", label: "Customer", type: "text" },
                    { name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [
                    { kind: "group", by: ["CUSTOMER"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                ],
            },
            plain: {
                from: "grouped",
                schema: [
                    { name: "CUSTOMER", label: "Customer", type: "text" },
                    { name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                ],
                composables: [
                    { kind: "labels", labels: {} },
                    { kind: "formats", formats: {} },
                ],
            },
        },
    }, 25);

    assert.deepEqual(composedLabels(state), { input: {}, output: {} });
    assert.deepEqual(composedFormats(state), {});
    assert.equal(terminalTableColumns({ doc: state }).find(column => column.name === "ir1").label, "sum(Amount)");
});

test("same-table metadata clears once before every overlay in either storage order", () => {
    const makeState = resetFirst => {
        const labels = [
            { kind: "labels", labels: { ir1: "Sales" } },
            { kind: "labels", labels: { CUSTOMER: "Client" } },
        ];
        const formats = [
            { kind: "formats", formats: { ir1: { mask: "integer" } } },
            { kind: "formats", formats: { CUSTOMER: { bold: true } } },
        ];
        const resetLabels = { kind: "labels", labels: {} };
        const resetFormats = { kind: "formats", formats: {} };
        return normalizeReportState({
            activeTable: "plain",
            tables: {
                base: {
                    from: "definition",
                    schema: [
                        { name: "CUSTOMER", label: "Customer", type: "text" },
                        { name: "AMOUNT", label: "Amount", type: "number" },
                    ],
                    composables: [
                        { kind: "labels", labels: { CUSTOMER: "Account", AMOUNT: "Revenue" } },
                        { kind: "formats", formats: { AMOUNT: { mask: "currency:CAD" } } },
                    ],
                },
                grouped: {
                    from: "base",
                    schema: [
                        { name: "CUSTOMER", label: "Customer", type: "text" },
                        { name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                    ],
                    composables: [{
                        kind: "group",
                        by: ["CUSTOMER"],
                        values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }],
                    }],
                },
                plain: {
                    from: "grouped",
                    schema: [
                        { name: "CUSTOMER", label: "Customer", type: "text" },
                        { name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
                    ],
                    composables: resetFirst
                        ? [resetLabels, ...labels, resetFormats, ...formats]
                        : [...labels, resetLabels, ...formats, resetFormats],
                },
            },
        }, 25);
    };

    for (const state of [makeState(true), makeState(false)]) {
        assert.deepEqual(composedLabels(state), {
            input: {},
            output: { ir1: "Sales", CUSTOMER: "Client" },
        });
        assert.deepEqual(composedFormats(state), {
            ir1: { mask: "integer" },
            CUSTOMER: { bold: true },
        });
        assert.deepEqual(terminalTableColumns({ doc: state }).map(column => [column.name, column.label]), [
            ["CUSTOMER", "Client"],
            ["ir1", "Sales"],
        ]);
    }
});

test("schema invalidation follows exact changed tables and their descendants", () => {
    const before = {
        search: "", activeTable: "decorated",
        tables: {
            base: { from: "definition", schema: [{ name: "ID" }], composables: [] },
            grouped: { from: "base", schema: [{ name: "ID" }], composables: [{ kind: "group", by: ["ID"], values: [] }] },
            decorated: { from: "grouped", schema: [{ name: "ID" }], composables: [] },
            pivoted: {
                from: "base",
                schema: [{ name: "ID" }],
                composables: [{ kind: "pivot", rows: ["ID"], cols: ["STATUS"], values: [] }],
            },
            pivotChild: { from: "pivoted", schema: [{ name: "ID" }], composables: [] },
            unrelated: { from: "definition", schema: [{ name: "OTHER" }], composables: [] },
        },
    };
    const after = structuredClone(before);
    after.tables.grouped.composables[0].by = ["OTHER"];
    invalidateChangedSchemas(before, after);
    assert.notEqual(after.tables.base.schema, null);
    assert.equal(after.tables.grouped.schema, null);
    assert.equal(after.tables.decorated.schema, null);
    assert.notEqual(after.tables.unrelated.schema, null);

    const switched = structuredClone(before);
    switched.activeTable = "base";
    invalidateChangedSchemas(before, switched);
    assert.ok(Object.values(switched.tables).every(table => table.schema !== null));

    const searched = structuredClone(before);
    searched.search = "Acme";
    invalidateChangedSchemas(before, searched);
    assert.notEqual(searched.tables.base.schema, null);
    assert.notEqual(searched.tables.grouped.schema, null);
    assert.notEqual(searched.tables.decorated.schema, null);
    assert.notEqual(searched.tables.pivoted.schema, null,
        "request search runs after Pivot and cannot change its discovered schema");
    assert.notEqual(searched.tables.pivotChild.schema, null,
        "a search-only change leaves descendants of the Pivot cacheable");
    assert.notEqual(searched.tables.unrelated.schema, null);

    const removedParent = structuredClone(before);
    delete removedParent.tables.grouped;
    invalidateChangedSchemas(before, removedParent);
    assert.equal(removedParent.tables.decorated.schema, null,
        "a retained table delegating from a removed id must not keep its cache");
    assert.notEqual(removedParent.tables.base.schema, null);
    assert.notEqual(removedParent.tables.unrelated.schema, null);
});

test("storage-only composable permutations do not invalidate schema caches", () => {
    const before = {
        activeTable: "child",
        tables: {
            base: {
                from: "definition",
                schema: [{ name: "ID" }],
                composables: [
                    { kind: "filter", filters: [{ expr: "ID > 0" }] },
                    { kind: "compute", computed: [{ id: "ir1", expr: "ID * 2" }] },
                    { kind: "labels", labels: { ir1: "Double" } },
                ],
            },
            child: {
                from: "base",
                schema: [{ name: "ID" }, { name: "ir1" }],
                composables: [],
            },
        },
    };
    const after = structuredClone(before);
    after.tables.base.composables = [
        after.tables.base.composables[2],
        after.tables.base.composables[0],
        after.tables.base.composables[1],
    ];

    invalidateChangedSchemas(before, after);

    assert.notEqual(after.tables.base.schema, null);
    assert.notEqual(after.tables.child.schema, null);
});

test("metadata and owner-local edits do not invalidate exported schema caches", () => {
    const before = {
        activeTable: "child",
        tables: {
            base: {
                from: "definition",
                schema: [{ name: "ID" }],
                composables: [
                    { kind: "select", columns: ["ID"] },
                    { kind: "formats", formats: { ID: { bold: true } } },
                    { kind: "labels", labels: { ID: "Identifier" } },
                    { kind: "sort", sorts: [{ col: "ID", dir: "asc" }] },
                ],
            },
            child: {
                from: "base",
                schema: [{ name: "ID" }],
                composables: [],
            },
        },
    };
    const after = structuredClone(before);
    after.tables.base.composables[0].columns = [];
    after.tables.base.composables[1].formats.ID = { mask: "integer", italic: true };
    after.tables.base.composables[2].labels.ID = "Record";
    after.tables.base.composables[3].sorts[0].dir = "desc";

    invalidateChangedSchemas(before, after);

    assert.notEqual(after.tables.base.schema, null);
    assert.notEqual(after.tables.child.schema, null);
});

test("computed deletion cleans every repeated input node and dependent computed identity", () => {
    const state = normalizeReportState({
        activeTable: "grouped",
        tables: {
            base: {
                from: "definition",
                composables: [
                    { kind: " COMPUTE ", computed: [{ id: "ir1", expr: "AMOUNT * 2" }] },
                    { kind: " FILTER ", filters: [{ expr: "ir1 > 0" }] },
                    { kind: "compute", computed: [
                        { id: "ir2", expr: "ir1 + 1" },
                        { id: "ir3", expr: "AMOUNT + 1" },
                    ] },
                    { kind: "filter", filters: [{ expr: "ir2 > 0" }, { expr: "ir3 > 0" }] },
                    { kind: "select", columns: ["ir1", "ir3"] },
                    { kind: " SELECT ", columns: ["CUSTOMER", "ir2", "ir3"] },
                    { kind: "formats", formats: {
                        ir1: { bold: true },
                        CUSTOMER: { displayAs: "link", urlColumn: "ir2", textColumn: "CUSTOMER", italic: true },
                    } },
                ],
            },
            grouped: {
                from: "base",
                composables: [{ kind: "group", by: ["ir2"], values: [] }],
            },
        },
    }, 25);

    assert.deepEqual(removeInputComputedColumn(state, "ir1"), ["groupBy"]);
    assert.deepEqual(state.tables.base.composables[0].computed, []);
    assert.deepEqual(state.tables.base.composables[2].computed.map(rule => rule.id), ["ir3"]);
    assert.deepEqual(state.tables.base.composables[1].filters, []);
    assert.deepEqual(state.tables.base.composables[3].filters, [{ expr: "ir3 > 0" }]);
    assert.deepEqual(state.tables.base.composables[4].columns, ["ir3"]);
    assert.deepEqual(state.tables.base.composables[5].columns, ["CUSTOMER", "ir3"]);
    assert.deepEqual(state.tables.base.composables[6].formats, {
        CUSTOMER: { italic: true },
    });
    assert.equal(state.tables.grouped, undefined,
        "a shape consuming a transitively retired computed column is removed");
    assert.equal(state.activeTable, "base");
});

test("input computed deletion strips exact input nodes and removes only dependent descendants", () => {
    const state = normalizeReportState({
        activeTable: "pivoted",
        tables: {
            base: {
                from: "definition",
                composables: nodesFor({
                    computed: [{ id: "ir1", expr: "AMOUNT * 1.05" }, { id: "ir2", expr: "AMOUNT * 2" }],
                    columns: ["CUSTOMER", "ir1", "ir2"],
                    labels: { ir1: "Taxed", CUSTOMER: "Customer" },
                    sorts: [{ col: "ir1", dir: "desc" }, { col: "CUSTOMER", dir: "asc" }],
                    filters: [{ expr: "ir1 > 100" }, { expr: "CUSTOMER = 'ir1'" }, { expr: "ir10 > 100" }],
                    formats: {
                        ir1: { mask: "currency:CAD" },
                        CUSTOMER: { displayAs: "link", urlColumn: "ir1", textColumn: "CUSTOMER", bold: true },
                    },
                }),
            },
            pivoted: {
                from: "base",
                composables: [{ kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"], values: [{ id: "ir3", col: "ir1", fn: "sum" }] }],
            },
            decorated: { from: "pivoted", composables: [{ kind: "labels", labels: {} }] },
            grouped: { from: "base", composables: [{ kind: "group", by: ["ir1"], values: [] }] },
            charted: {
                from: "base",
                composables: [{ kind: "chart", type: "bar", label: "CUSTOMER", value: "AMOUNT", fn: "sum" }],
            },
        },
    }, 25);

    const dropped = removeInputComputedColumn(state, "IR1");
    assert.deepEqual(inputNode(state, "compute").computed.map(rule => rule.id), ["ir2"]);
    assert.deepEqual(inputNode(state, "select").columns, ["CUSTOMER", "ir2"]);
    assert.deepEqual(inputNode(state, "labels").labels, { CUSTOMER: "Customer" });
    assert.deepEqual(inputNode(state, "sort").sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(inputNode(state, "filter").filters.map(rule => rule.expr), ["CUSTOMER = 'ir1'", "ir10 > 100"]);
    assert.deepEqual(inputNode(state, "formats").formats, { CUSTOMER: { bold: true } });
    assert.deepEqual(dropped.sort(), ["groupBy", "pivot"]);
    assert.equal(state.tables.pivoted, undefined);
    assert.equal(state.tables.decorated, undefined);
    assert.equal(state.tables.grouped, undefined);
    assert.ok(state.tables.charted);
    assert.equal(state.activeTable, "base");
});

test("Pivot cleanup follows opaque server schema ids through unshaped descendants", () => {
    const shipped = "ir7100000000000000001";
    const pending = "ir7100000000000000002";
    const previous = {
        kind: "pivot",
        rows: ["CUSTOMER"],
        cols: ["STATUS"],
        values: [{ id: "ir3", col: "AMOUNT", fn: "sum" }],
    };
    const state = normalizeReportState({
        activeTable: "decorated",
        tables: {
            base: { from: "definition", composables: [] },
            pivoted: {
                from: "base",
                schema: [
                    { name: "CUSTOMER" },
                    { name: shipped },
                    { name: pending },
                ],
                composables: [structuredClone(previous)],
            },
            decorated: {
                from: "pivoted",
                composables: nodesFor({
                    computed: [{ id: "ir4", expr: `${shipped} / 2` }, { id: "ir5", expr: "CUSTOMER || '!'" }],
                    filters: [{ expr: `${pending} > 0` }],
                    sorts: [{ col: shipped, dir: "desc" }, { col: "CUSTOMER", dir: "asc" }],
                    highlights: [
                        { id: "h1", scope: "cell", col: pending, expr: "CUSTOMER IS NOT NULL" },
                        { id: "h2", scope: "row", expr: "CUSTOMER IS NOT NULL" },
                    ],
                    columns: ["CUSTOMER", shipped, pending, "ir4", "ir5"],
                    labels: { [shipped]: "Shipped", CUSTOMER: "Customer" },
                    formats: { [pending]: { bold: true }, CUSTOMER: { italic: true } },
                }),
            },
        },
    }, 25);

    pruneRetiredPivotOutputs(state, "pivoted", previous, {
        ...previous,
        values: [],
    }, ["ir3"]);
    assert.deepEqual(node(state, "compute").computed.map(rule => rule.id), ["ir5"]);
    assert.deepEqual(node(state, "filter").filters, []);
    assert.deepEqual(node(state, "sort").sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(node(state, "highlight").highlights.map(rule => rule.id), ["h2"]);
    assert.deepEqual(node(state, "select").columns, ["CUSTOMER", "ir5"]);
    assert.deepEqual(node(state, "labels").labels, { CUSTOMER: "Customer" });
    assert.deepEqual(node(state, "formats").formats, { CUSTOMER: { italic: true } });
    assert.deepEqual(state.tables.pivoted.composables[0].values, [{ id: "ir3", col: "AMOUNT", fn: "sum" }]);

    removeTerminalComputedColumn(state, "ir5");
    assert.deepEqual(node(state, "compute").computed, []);
});

test("Pivot cleanup removes descendant Shapes that consume retired opaque cells", () => {
    const cell = "ir7150000000000000001";
    const previous = {
        kind: "pivot",
        rows: ["CUSTOMER"],
        cols: ["STATUS"],
        values: [{ id: "ir3", col: "AMOUNT", fn: "sum" }],
    };
    const state = normalizeReportState({
        activeTable: "leaf",
        tables: {
            base: { from: "definition", composables: [] },
            pivoted: {
                from: "base",
                schema: [{ name: "CUSTOMER" }, { name: cell }],
                composables: [structuredClone(previous)],
            },
            grouped: {
                from: "pivoted",
                composables: [{ kind: "group", by: [cell], values: [] }],
            },
            leaf: { from: "grouped", composables: [] },
        },
    }, 25);

    pruneRetiredPivotOutputs(state, "pivoted", previous, {
        ...previous,
        cols: ["REGION"],
    });

    assert.equal(state.tables.grouped, undefined);
    assert.equal(state.tables.leaf, undefined);
    assert.equal(state.activeTable, "pivoted");
});

test("computed cleanup retires an action whose key column disappears", () => {
    const state = normalizeReportState({
        activeTable: "base",
        tables: {
            base: {
                from: "definition",
                composables: nodesFor({
                    computed: [{ id: "ir1", expr: "AMOUNT * 2" }],
                    formats: {
                        ACTION: {
                            displayAs: "action",
                            command: "open-order",
                            keyColumn: "ir1",
                            bold: true,
                        },
                        SAFE_ACTION: {
                            displayAs: "action",
                            command: "open-customer",
                            keyColumn: "CUSTOMER",
                        },
                    },
                }),
            },
        },
    }, 25);

    removeTerminalComputedColumn(state, "ir1");

    assert.deepEqual(node(state, "formats").formats, {
        ACTION: { bold: true },
        SAFE_ACTION: {
            displayAs: "action",
            command: "open-customer",
            keyColumn: "CUSTOMER",
        },
    });
});

test("metric cleanup traverses repeated terminal nodes, including read-only predecessors", () => {
    const cell = "ir7200000000000000001";
    const previous = {
        kind: "pivot",
        rows: ["CUSTOMER"],
        cols: ["STATUS"],
        values: [{ id: "ir3", col: "AMOUNT", fn: "sum" }],
    };
    const state = normalizeReportState({
        activeTable: "pivoted",
        tables: {
            base: { from: "definition", composables: [] },
            pivoted: {
                from: "base",
                schema: [{ name: "CUSTOMER" }, { name: cell }],
                composables: [
                    structuredClone(previous),
                    { kind: " FILTER ", filters: [{ expr: `\`${cell}\` > 0` }] },
                    { kind: "filter", filters: [{ expr: `\`${cell}\` < 1000` }, { expr: "CUSTOMER <> ''" }] },
                    { kind: "sort", sorts: [{ col: cell, dir: "desc" }] },
                    { kind: " SORT ", sorts: [{ col: "CUSTOMER", dir: "asc" }, { col: cell, dir: "asc" }] },
                    { kind: "formats", formats: {
                        [cell]: { bold: true },
                        CUSTOMER: { displayAs: "link", urlColumn: cell, textColumn: "CUSTOMER", italic: true },
                    } },
                ],
            },
        },
    }, 25);

    pruneRetiredPivotOutputs(state, "pivoted", previous, {
        ...previous,
        values: [],
    }, ["ir3"]);
    assert.deepEqual(state.tables.pivoted.composables[1].filters, []);
    assert.deepEqual(state.tables.pivoted.composables[2].filters, [{ expr: "CUSTOMER <> ''" }]);
    assert.deepEqual(state.tables.pivoted.composables[3].sorts, []);
    assert.deepEqual(state.tables.pivoted.composables[4].sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(state.tables.pivoted.composables[5].formats, {
        CUSTOMER: { italic: true },
    });
});

test("Pivot column-dimension edits retire old cell families but stable dimensions preserve them", () => {
    const shipped = "ir7300000000000000001";
    const count = "ir7300000000000000002";
    const previous = {
        kind: "pivot",
        rows: ["CUSTOMER"],
        cols: ["STATUS"],
        values: [{ id: "ir3", col: "AMOUNT", fn: "sum" }],
    };
    const makeState = () => normalizeReportState({
        activeTable: "pivoted",
        tables: {
            base: { from: "definition", composables: [] },
            pivoted: {
                from: "base",
                schema: [
                    { name: "CUSTOMER" },
                    { name: shipped },
                    { name: count },
                ],
                composables: [
                    structuredClone(previous),
                    { kind: "sort", sorts: [
                        { col: shipped, dir: "desc" },
                        { col: count, dir: "desc" },
                        { col: "CUSTOMER", dir: "asc" },
                    ] },
                    { kind: "labels", labels: { [shipped]: "Shipped", CUSTOMER: "Customer" } },
                ],
            },
        },
    }, 25);

    const stable = makeState();
    pruneRetiredPivotOutputs(stable, "pivoted", previous, {
        ...previous,
        cols: ["status"],
        totals: true,
    });
    assert.deepEqual(node(stable, "sort").sorts.map(rule => rule.col), [shipped, count, "CUSTOMER"]);

    const changed = makeState();
    pruneRetiredPivotOutputs(changed, "pivoted", previous, {
        ...previous,
        cols: ["REGION"],
    });
    assert.deepEqual(node(changed, "sort").sorts, [{ col: "CUSTOMER", dir: "asc" }]);
    assert.deepEqual(node(changed, "labels").labels, { CUSTOMER: "Customer" });
});

test("Chart edits retire only output names which no longer exist", () => {
    const previous = {
        kind: "chart",
        type: "bar",
        label: "STATUS",
        value: "AMOUNT",
        fn: "sum",
    };
    const state = normalizeReportState({
        activeTable: "charted",
        tables: {
            base: { from: "definition", composables: [] },
            charted: {
                from: "base",
                composables: [
                    structuredClone(previous),
                    { kind: "filter", filters: [{ expr: "STATUS <> ''" }, { expr: "v0 > 0" }] },
                    { kind: "sort", sorts: [{ col: "STATUS", dir: "asc" }] },
                    { kind: "sort", sorts: [{ col: "v0", dir: "desc" }] },
                    { kind: "select", columns: ["STATUS", "v0"] },
                    { kind: "labels", labels: { STATUS: "Status", v0: "Revenue" } },
                ],
            },
        },
    }, 25);
    const replacement = { ...previous, label: "REGION", type: "line" };

    pruneRetiredChartOutputs(state, "charted", previous, replacement);
    assert.deepEqual(state.tables.charted.composables[1].filters, [{ expr: "v0 > 0" }]);
    assert.deepEqual(state.tables.charted.composables[2].sorts, []);
    assert.deepEqual(state.tables.charted.composables[3].sorts, [{ col: "v0", dir: "desc" }]);
    assert.deepEqual(state.tables.charted.composables[4].columns, ["v0"]);
    assert.deepEqual(state.tables.charted.composables[5].labels, { v0: "Revenue" });
});

test("active chains are case-insensitive and reject cycles or missing parents", () => {
    const valid = {
        activeTable: "CHILD",
        tables: {
            Base: { from: "definition", composables: [] },
            Child: { from: "base", composables: [] },
        },
    };
    assert.deepEqual(activeChain(valid).map(entry => entry.id), ["Base", "Child"]);
    assert.deepEqual(activeChain({
        activeTable: "a",
        tables: { a: { from: "b", composables: [] }, b: { from: "a", composables: [] } },
    }), []);
    assert.deepEqual(activeChain({ activeTable: "child", tables: { child: { from: "missing", composables: [] } } }), []);
});

test("expression references skip quoted contents and longer identifiers", () => {
    assert.equal(expressionReferencesColumn("c1 > 0", "C1"), true);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'c1'", "c1"), false);
    assert.equal(expressionReferencesColumn("c10 > 0", "c1"), false);
    assert.equal(expressionReferencesColumn("CUSTOMER = 'it''s c1'", "c1"), false);
    assert.equal(expressionReferencesColumn("ir7100000000000000001 > 0", "ir7100000000000000001"), true);
    assert.equal(expressionReferencesColumn("ir7100000000000000001 > 0", "ir3"), false);
    assert.equal(expressionReferencesColumn("COST$ > 0", "cost$"), true);
    assert.equal(expressionReferencesColumn("ORDER# = 1", "order#"), true);
    assert.equal(expressionReferencesColumn("COST$ADJUSTED > 0", "COST$"), false);
});

test("scoped search emits escaped and typed predicates", () => {
    assert.equal(scopedSearchExpression("CUSTOMER", "text", "O'Brien"), "CONTAINS(CUSTOMER, 'O''Brien')");
    assert.equal(scopedSearchExpression("Customer Name", "text", "Acme"), "CONTAINS(`Customer Name`, 'Acme')");
    assert.equal(scopedSearchExpression("AMOUNT", "number", "-12.50"), "AMOUNT = -12.50");
    assert.equal(scopedSearchExpression("ORDER_DATE", "date", "2026-08-06"), "ORDER_DATE = TO_DATE('2026-08-06')");
    assert.equal(scopedSearchExpression("ACTIVE", "bool", "false"), "NOT ACTIVE");
    assert.equal(scopedSearchExpression("AMOUNT", "number", "1 234,50", "fr-CA"), "AMOUNT = 1234.50");
    assert.equal(scopedSearchExpression("ACTIVE", "bool", "vrai", "fr-CA"), "ACTIVE");
    assert.throws(() => scopedSearchExpression("AMOUNT", "number", "twelve"), /not a number/);
    assert.throws(() => scopedSearchExpression("ORDER_DATE", "date", "08\/06\/2026"), /ISO date/);
});

test("case-insensitive map helpers prevent stale casing variants", () => {
    assert.deepEqual(lookupValue({ Amount: { mask: "currency" } }, "AMOUNT"), { mask: "currency" });
    assert.equal(lookupValue({ Amount: 1 }, "missing"), undefined);
    assert.equal(nextFreeId(new Set(["h1", "h3"]), "h"), "h2");

    const formats = { amount: { mask: "integer" }, OTHER: { bold: true } };
    setMapEntry(formats, "AMOUNT", { mask: "currency:CAD" });
    assert.deepEqual(formats, { OTHER: { bold: true }, AMOUNT: { mask: "currency:CAD" } });
    setMapEntry(formats, "Amount", undefined);
    assert.deepEqual(formats, { OTHER: { bold: true } });
});

test("missing highlight precedence uses stable ids and unused slots", () => {
    const rules = [
        { id: "h3" },
        { id: "h2", sequence: 20 },
        { id: "H1" },
    ];
    const priorities = values => normalizedHighlightRules(values)
        .map(entry => [entry.rule.id, entry.sequence]);

    assert.deepEqual(priorities(rules), [
        ["H1", 10],
        ["h2", 20],
        ["h3", 30],
    ]);
    assert.deepEqual(priorities([rules[2], rules[0], rules[1]]), [
        ["H1", 10],
        ["h2", 20],
        ["h3", 30],
    ]);
});

test("computed columns and shape metrics share one document-wide ir namespace", () => {
    const doc = {
        activeTable: "grouped",
        tables: {
            base: {
                from: "definition",
                schema: [{ name: "IR1" }, { name: "AMOUNT" }],
                composables: [{
                    kind: "compute",
                    computed: [{ id: "ir2", expr: "AMOUNT * 2" }],
                }],
            },
            grouped: {
                from: "base",
                composables: [{
                    kind: "group",
                    by: ["STATUS"],
                    values: [{ id: "ir4", col: "AMOUNT", fn: "sum" }],
                }],
            },
        },
    };
    const extraColumns = [{ name: "ir3" }];

    assert.equal(nextSyntheticColumnId(doc, extraColumns), "ir5");

    const assigned = assignShapeMetricIds(
        doc,
        [
            { col: "AMOUNT", fn: "sum" },
            { col: "AMOUNT", fn: "avg" },
        ],
        [{ id: "ir4", col: "AMOUNT", fn: "sum" }],
        extraColumns);
    assert.deepEqual(assigned, {
        values: [
            { id: "ir4", col: "AMOUNT", fn: "sum" },
            { id: "ir5", col: "AMOUNT", fn: "avg" },
        ],
        retired: [],
    });

    doc.tables.grouped.composables[0].values = assigned.values;
    assert.equal(nextSyntheticColumnId(doc, extraColumns), "ir6",
        "a later computed column cannot reuse the metric id");
});
