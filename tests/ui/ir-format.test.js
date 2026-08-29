import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import {
    applyMask,
    formatInteger,
    formatValue,
    hasFraction,
    parseReportNumber,
} from "../../src/client/report/render/format.js";
import { formatForColumn, renderColumnValue } from "../../src/client/report/render/column-renderers.js";
import { canRenderChart, chartResultColumns, renderChartView } from "../../src/client/report/render/chart-view.js";
import { renderGrid } from "../../src/client/report/render/grid.js";
import { renderPager } from "../../src/client/report/render/pager.js";
import { reportState, sourceComposableOf } from "./report-state-fixture.js";

const window = new Window({ url: "https://host.example/reports/orders" });
Object.assign(globalThis, {
    window,
    document: window.document,
    Node: window.Node,
    Option: function Option(text = "", value = "") {
        const option = window.document.createElement("option");
        option.text = text;
        option.value = value;
        return option;
    },
});

const digits = value => value.replace(/\D/g, "");
test("exact numeric strings format without an IEEE-754 conversion", () => {
    assert.equal(parseReportNumber("9.007199254740993e15").toFixed(0), "9007199254740993");
    assert.equal(formatValue("9007199254740993", "number"), "9007199254740993");
    assert.equal(applyMask("12345678901234567890.125", "number", "plain"), "12345678901234567890.13");
    assert.equal(applyMask("-12345678901234567890.125", "number", "plain"), "-12345678901234567890.13");
    assert.equal(hasFraction("999999999999999999.000000001"), true);
    assert.equal(digits(formatInteger("9223372036854775807")), "9223372036854775807");
});

test("currency, percentage, and expanded date/time masks use the exact scalar formatter", () => {
    const currency = applyMask("9007199254740993.125", "number", "currency:USD");
    const percent = applyMask("0.1234567890123456789", "number", "percent2");

    assert.equal(digits(currency), "900719925474099313");
    assert.equal(digits(percent), "1235");
    assert.match(percent, /%/);
    assert.equal(applyMask("2026-08-07T14:30:45", "date", "datetimeSeconds"), "2026-08-07 14:30:45");
    assert.match(applyMask("2026-08-07T14:30:45", "date", "timeSeconds"), /30.*45/);
});

test("number, date, and boolean presentation follows the report locale", () => {
    const decimal = applyMask("1234.5", "number", "decimal2", "fr-CA");
    const date = applyMask("2026-08-07T14:30:45", "date", "dateLong", "fr-CA");

    assert.match(decimal, /1[\s\u00a0\u202f]234,50/);
    assert.match(date.toLocaleLowerCase("fr-CA"), /août/);
    assert.equal(formatValue(true, "boolean", false, null, "fr-CA"), "vrai");
    assert.equal(formatValue(false, "boolean", false, null, "fr-CA"), "faux");
});

test("link text composes the source column's ordinary formatter", () => {
    const w = {
        doc: {
            ...reportState({
                formats: {
                    CUSTOMER: { displayAs: "link", urlColumn: "URL", textColumn: "AMOUNT" },
                    AMOUNT: { mask: "plain" },
                },
            }),
        },
        lastResult: {
            availableColumns: [
                { name: "CUSTOMER", label: "Customer", type: "text" },
                { name: "URL", label: "URL", type: "text" },
                { name: "AMOUNT", label: "Amount", type: "number" },
            ],
        },
    };
    const rendered = renderColumnValue(
        w,
        { CUSTOMER: "Acme", URL: "/orders/1", AMOUNT: "9007199254740993.125" },
        w.lastResult.availableColumns[0]);

    assert.equal(rendered.tagName, "A");
    assert.equal(rendered.textContent, "9007199254740993.13");
});

test("computed, group, and pivot values all use the normal mask path", () => {
    const source = reportState({ formats: { AMOUNT: { mask: "plain" } } });
    const w = {
        doc: reportState(
            { formats: { AMOUNT: { mask: "plain" } } },
            { kind: "group", by: ["STATUS"], values: [{ id: "m1", col: "AMOUNT", fn: "sum" }] }),
        lastResult: {
            columns: [
                { name: "STATUS", label: "Status", type: "text", computed: false },
                { name: "m1", label: "sum(Amount)", type: "number", computed: false, formatSource: "AMOUNT" },
            ],
            rows: [{ STATUS: "SHIPPED", m1: "9007199254740993.125" }],
            aggregates: {}, breakTotals: [], highlights: [],
        },
        schema: {
            columns: [
                { name: "STATUS", label: "Status", type: "text" },
                { name: "CUSTOMER", label: "Customer", type: "text" },
                { name: "AMOUNT", label: "Amount", type: "number" },
            ],
        },
    };
    const table = document.createElement("table");
    renderGrid(w, table);
    // The metric column inherits the source column's mask through formatSource.
    assert.equal(table.querySelector("tbody tr").children[1].textContent, "9007199254740993.13");

    // A Format node owned by the grouped table overrides the inherited source mask.
    w.doc.tables.groupBy.composables.push({ kind: "formats", formats: { m1: { mask: "integer" } } });
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[1].textContent, "9,007,199,254,740,993");

    w.doc = source;
    sourceComposableOf(w.doc, "formats").formats.c1 = { mask: "plain" };
    w.lastResult.columns = [{ name: "c1", label: "Computed", type: "number", computed: true }];
    w.lastResult.rows = [{ c1: "999999999999999999.999" }];
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[0].textContent, "1000000000000000000.00");

    w.doc = reportState(
        { formats: sourceComposableOf(source, "formats").formats },
        {
            kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"],
                values: [{ id: "m1", col: "AMOUNT", fn: "sum" }],
        });
    w.lastResult.columns = [
        { name: "CUSTOMER", label: "Customer", type: "text", computed: false },
        { name: 'm1@["SHIPPED"]', label: "SHIPPED", type: "number", computed: false, formatSource: "AMOUNT" },
    ];
    w.lastResult.rows = [{ CUSTOMER: "Acme", 'm1@["SHIPPED"]': "12345678901234567890.125" }];
    w.lastResult.aggregates = { 'm1@["SHIPPED"]': { sum: "24691357802469135780.25" } };
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[1].textContent, "12345678901234567890.13");
    assert.equal(table.querySelector("tr.ir-grand-total td:last-child").textContent, "24691357802469135780.25");
});

test("the chart data table retains an exact masked metric", () => {
    const w = {
        doc: reportState(
            { formats: { AMOUNT: { mask: "plain" } } },
            { kind: "chart", type: "bar", label: "STATUS", value: "AMOUNT", fn: "sum" }),
        lastResult: {
            // Chart metrics keep the synthetic v0/__count response names.
            columns: [
                { name: "STATUS", label: "Status", type: "text", computed: false },
                { name: "v0", label: "sum(Amount)", type: "number", computed: false, formatSource: "AMOUNT" },
            ],
            rows: [{ STATUS: "SHIPPED", v0: "9007199254740993.125" }],
        },
        schema: { columns: [{ name: "STATUS", label: "Status", type: "text" }, { name: "AMOUNT", label: "Amount", type: "number" }] },
    };
    const container = document.createElement("div");
    const chartModule = { renderChart: () => null };

    renderChartView(w, container, chartModule);

    assert.equal(container.querySelector(".ir-chart-table tbody td:last-child").textContent, "9007199254740993.13");
});

test("chart result columns use the server-disambiguated aggregate metric name", () => {
    const w = {
        doc: reportState({}, {
            kind: "chart", type: "bar", label: "v0", value: "AMOUNT", fn: "sum",
        }),
        lastResult: {
            columns: [
                { name: "v0", label: "Bucket", type: "text" },
                { name: "v0_metric", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" },
            ],
            rows: [{ v0: "SHIPPED", v0_metric: 42 }],
        },
        schema: {
            columns: [
                { name: "v0", label: "Bucket", type: "text" },
                { name: "AMOUNT", label: "Amount", type: "number" },
            ],
        },
    };

    assert.deepEqual(chartResultColumns(w).map(column => column.name), ["v0", "v0_metric"]);
    assert.equal(canRenderChart(w), true);
});

test("a count chart renders when its label owns the __count name", () => {
    const w = {
        doc: reportState({}, {
            kind: "chart", type: "pie", label: "__count", fn: "count",
        }),
        lastResult: {
            columns: [
                { name: "__count", label: "Bucket", type: "text" },
                { name: "__count_metric", label: "Count", type: "number" },
            ],
            rows: [{ __count: "SHIPPED", __count_metric: 3 }],
        },
        schema: { columns: [{ name: "__count", label: "Bucket", type: "text" }] },
    };
    const container = document.createElement("div");

    renderChartView(w, container, { renderChart: () => null });

    assert.deepEqual(chartResultColumns(w).map(column => column.name), ["__count", "__count_metric"]);
    assert.equal(canRenderChart(w), true);
    assert.deepEqual(
        [...container.querySelectorAll(".ir-chart-table tbody td")].map(cell => cell.textContent),
        ["SHIPPED", "3"]);
});

test("presentation formats compose through intermediate table ancestry", () => {
    const columns = [{ name: "m1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" }];
    const w = {
        doc: {
            activeTable: "decorated",
            tables: {
                source: {
                    from: "definition",
                    composables: [{ kind: "formats", formats: { AMOUNT: { mask: "plain" } } }],
                },
                grouped: {
                    from: "source",
                    composables: [
                        { kind: "group", by: ["STATUS"], values: [{ id: "m1", col: "AMOUNT", fn: "sum" }] },
                        { kind: "formats", formats: { m1: { mask: "integer" } } },
                    ],
                },
                decorated: { from: "grouped", schema: columns, composables: [] },
            },
        },
        lastResult: {
            availableColumns: columns,
            columns,
            rows: [{ m1: "9007199254740993.125" }],
            aggregates: {}, breakTotals: [], highlights: [],
        },
        schema: { columns: [{ name: "AMOUNT", label: "Amount", type: "number" }] },
    };
    const table = document.createElement("table");

    renderGrid(w, table);

    assert.equal(table.querySelector("tbody td").textContent, "9,007,199,254,740,993");
});

test("a later shape inherits a direct format from its intermediate metric", () => {
    const column = { name: "m2", label: "sum(sum(Amount))", type: "number", formatSource: "m1" };
    const w = {
        doc: {
            activeTable: "second",
            tables: {
                source: {
                    from: "definition",
                    composables: [{ kind: "formats", formats: { AMOUNT: { mask: "currency:CAD" } } }],
                },
                first: {
                    from: "source",
                    composables: [
                        { kind: "group", by: ["STATUS"], values: [{ id: "m1", col: "AMOUNT", fn: "sum" }] },
                        { kind: "formats", formats: { m1: { mask: "integer" } } },
                    ],
                },
                second: {
                    from: "first",
                    schema: [column],
                    composables: [
                        { kind: "group", by: ["STATUS"], values: [{ id: "m2", col: "m1", fn: "sum" }] },
                    ],
                },
            },
        },
    };

    assert.equal(formatForColumn(w, column).mask, "integer");
});

test("a chart with a required output column hidden is a valid non-chartable table", () => {
    const w = {
        doc: reportState({}, {
            kind: "chart", type: "bar", label: "STATUS", value: "AMOUNT", fn: "sum",
        }),
        lastResult: {
            columns: [{ name: "v0", label: "sum(Amount)", type: "number" }],
            rows: [{ v0: 10 }],
        },
    };
    const container = document.createElement("div");

    assert.equal(canRenderChart(w), false);
    assert.equal(renderChartView(w, container, {}), null);
    assert.equal(container.childElementCount, 0);
});

test("pager arithmetic and display preserve an Int64 count", () => {
    const w = {
        doc: reportState(),
        schema: { limits: { maxPageSize: 500 } },
        lastResult: {
            page: { index: 1, size: 25 },
            totalRows: "9223372036854775807",
            rows: Array.from({ length: 25 }, () => ({})),
            elapsedMs: "1",
        },
        applyOrBanner() {},
    };
    const container = document.createElement("div");

    renderPager(w, container);

    assert.equal(digits(container.querySelector(".ir-page-info").textContent), "1259223372036854775807");
    assert.equal(container.querySelector('[aria-label="Next page"]').disabled, false);
});

test("an All result is one unpaged range with no next page", () => {
    const w = {
        doc: reportState(),
        lastResult: {
            page: { index: 1, size: 0 },
            totalRows: "9223372036854775807",
            rows: Array.from({ length: 3 }, () => ({})),
            elapsedMs: "1",
        },
        applyOrBanner() {},
    };
    const container = document.createElement("div");

    renderPager(w, container);

    assert.equal(digits(container.querySelector(".ir-page-info").textContent), "139223372036854775807");
    assert.equal(container.querySelector('[aria-label="Previous page"]').disabled, true);
    assert.equal(container.querySelector('[aria-label="Next page"]').disabled, true);
    assert.equal(container.querySelector("select"), null);
});

test("control breaks own their columns and defer subtotal and grand total to logical boundaries", () => {
    const columns = [
        { name: "REGION", label: "Region", type: "text" },
        { name: "AMOUNT", label: "Amount", type: "number" },
    ];
    const w = {
        doc: {
            ...reportState({
                breaks: ["REGION"],
                sorts: [],
                highlights: [],
                formats: {},
            }),
        },
        schema: { columns },
        lastResult: {
            availableColumns: columns,
            columns,
            rows: [{ REGION: "West", AMOUNT: 10 }, { REGION: "West", AMOUNT: 20 }],
            page: { index: 1, size: 2 },
            totalRows: 4,
            aggregates: { AMOUNT: { sum: 100 } },
            breakTotals: [{ key: { REGION: "West" }, rows: 4, aggregates: { AMOUNT: { sum: 100 } } }],
            breakContinues: true,
            highlights: [],
        },
    };
    const table = document.createElement("table");

    renderGrid(w, table);

    assert.deepEqual([...table.querySelectorAll("thead th")].map(cell => cell.textContent), ["Amount"]);
    assert.equal(table.querySelectorAll("tr.ir-row").length, 2);
    assert.equal(table.querySelectorAll("tr.ir-row td").length, 2, "the break column is absent from detail rows");
    assert.equal(table.querySelectorAll("tr.ir-break-total").length, 0, "the group continues");
    assert.equal(table.querySelectorAll("tr.ir-grand-total").length, 0, "the report continues");

    w.lastResult.page = { index: 2, size: 2 };
    w.lastResult.breakContinues = false;
    renderGrid(w, table);

    assert.equal(table.querySelectorAll("tr.ir-break-total").length, 1);
    assert.equal(table.querySelectorAll("tr.ir-grand-total").length, 1);
});

test("higher highlight sequences win within a scope and cell scope wins over row scope", () => {
    const columns = [
        { name: "AMOUNT", label: "Amount", type: "number" },
        { name: "STATUS", label: "Status", type: "text" },
    ];
    const w = {
        doc: {
            ...reportState({
                breaks: [],
                sorts: [],
                formats: {},
                highlights: [
                    { id: "row-low", sequence: 10, style: { bg: "#111111" } },
                    { id: "row-high", sequence: 20, style: { bg: "#222222" } },
                    { id: "cell-low", sequence: 10, style: { bg: "#333333" } },
                    { id: "cell-high", sequence: 20, style: { bg: "#444444" } },
                ],
            }),
        },
        schema: { columns },
        lastResult: {
            availableColumns: columns,
            columns,
            rows: [{ AMOUNT: 10, STATUS: "open" }],
            page: { index: 1, size: 10 },
            totalRows: 1,
            aggregates: {},
            breakTotals: [],
            highlights: [
                { row: 0, id: "row-low" },
                { row: 0, id: "row-high" },
                { row: 0, id: "cell-low", col: "AMOUNT" },
                { row: 0, id: "cell-high", col: "AMOUNT" },
            ],
        },
    };
    const table = document.createElement("table");

    renderGrid(w, table);

    // Row highlights paint the CELLS (a column format's inline background would
    // beat a tr-level style): the sequence winner shows on cells without a cell
    // hit, and the cell-scope winner overrides on its own cell.
    const row = table.querySelector("tr.ir-row");
    assert.equal(row.children[0].style.background, "#444444");
    assert.equal(row.children[1].style.background, "#222222");
});
