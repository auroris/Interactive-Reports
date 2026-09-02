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
    assert.equal(applyMask("12345678901234567890.125", "number", "0.00"), "12345678901234567890.13");
    assert.equal(applyMask("-12345678901234567890.125", "number", "0.00"), "-12345678901234567890.13");
    assert.equal(hasFraction("999999999999999999.000000001"), true);
    assert.equal(digits(formatInteger("9223372036854775807")), "9223372036854775807");
});

test("currency, percentage, and date/time format codes use the exact scalar formatter", () => {
    const currency = applyMask("9007199254740993.125", "number", "$#,##0.00");
    const percent = applyMask("0.1234567890123456789", "number", "0.00%");

    assert.equal(digits(currency), "900719925474099313");
    assert.equal(digits(percent), "1235");
    assert.match(percent, /%/);
    assert.equal(applyMask("2026-08-07T14:30:45", "date", "yyyy-mm-dd hh:mm:ss"), "2026-08-07 14:30:45");
    assert.match(applyMask("2026-08-07T14:30:45", "date", "h:mm:ss AM/PM"), /30.*45/);
});

test("number, date, and boolean presentation follows the report locale", () => {
    const decimal = applyMask("1234.5", "number", "#,##0.00", "fr-CA");
    const date = applyMask("2026-08-07T14:30:45", "date", "mmmm d, yyyy", "fr-CA");

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
                    AMOUNT: { mask: "0.00" },
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
    const source = reportState({ formats: { AMOUNT: { mask: "0.00" } } });
    const w = {
        doc: reportState(
            { formats: { AMOUNT: { mask: "0.00" } } },
            { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] }),
        lastResult: {
            columns: [
                { name: "STATUS", label: "Status", type: "text", computed: false },
                { name: "ir1", label: "sum(Amount)", type: "number", computed: false, formatSource: "AMOUNT" },
            ],
            rows: [{ STATUS: "SHIPPED", ir1: "9007199254740993.125" }],
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
    w.doc.tables.groupBy.composables.push({ kind: "formats", formats: { ir1: { mask: "#,##0" } } });
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[1].textContent, "9,007,199,254,740,993");

    w.doc = source;
    sourceComposableOf(w.doc, "formats").formats.ir1 = { mask: "0.00" };
    w.lastResult.columns = [{ name: "ir1", label: "Computed", type: "number", computed: true }];
    w.lastResult.rows = [{ ir1: "999999999999999999.999" }];
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[0].textContent, "1000000000000000000.00");

    w.doc = reportState(
        { formats: sourceComposableOf(source, "formats").formats },
        {
            kind: "pivot", rows: ["CUSTOMER"], cols: ["STATUS"],
                values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }],
        });
    w.lastResult.columns = [
        { name: "CUSTOMER", label: "Customer", type: "text", computed: false },
        { name: "ir7100000000000000001", label: "SHIPPED", type: "number", computed: false, formatSource: "AMOUNT" },
    ];
    w.lastResult.rows = [{ CUSTOMER: "Acme", ir7100000000000000001: "12345678901234567890.125" }];
    w.lastResult.aggregates = { ir7100000000000000001: { sum: "24691357802469135780.25" } };
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[1].textContent, "12345678901234567890.13");
    assert.equal(table.querySelector("tr.ir-grand-total td:last-child").textContent, "24691357802469135780.25");
});

test("the chart data table retains an exact masked metric", () => {
    const w = {
        doc: reportState(
            { formats: { AMOUNT: { mask: "0.00" } } },
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
    const columns = [{ name: "ir1", label: "sum(Amount)", type: "number", formatSource: "AMOUNT" }];
    const w = {
        doc: {
            activeTable: "decorated",
            tables: {
                source: {
                    from: "definition",
                    composables: [{ kind: "formats", formats: { AMOUNT: { mask: "0.00" } } }],
                },
                grouped: {
                    from: "source",
                    composables: [
                        { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                        { kind: "formats", formats: { ir1: { mask: "#,##0" } } },
                    ],
                },
                decorated: { from: "grouped", schema: columns, composables: [] },
            },
        },
        lastResult: {
            availableColumns: columns,
            columns,
            rows: [{ ir1: "9007199254740993.125" }],
            aggregates: {}, breakTotals: [], highlights: [],
        },
        schema: { columns: [{ name: "AMOUNT", label: "Amount", type: "number" }] },
    };
    const table = document.createElement("table");

    renderGrid(w, table);

    assert.equal(table.querySelector("tbody td").textContent, "9,007,199,254,740,993");
});

test("named-table boundaries export masks without leaking owner renderers or styles", () => {
    const parentFormat = {
        mask: "$#,##0.00",
        align: "center",
        bold: true,
        italic: true,
        fg: "#111111",
        bg: "#eeeeee",
        classes: ["financial"],
        displayAs: "link",
        urlColumn: "URL",
        textColumn: "AMOUNT",
        command: "open",
        keyColumn: "ID",
    };
    const groupedFormat = {
        mask: "#,##0",
        bold: true,
        displayAs: "image",
        urlColumn: "URL",
    };
    const doc = {
        activeTable: "base",
        tables: {
            base: {
                from: "definition",
                composables: [{ kind: "formats", formats: { AMOUNT: parentFormat } }],
            },
            plain: {
                from: "base",
                composables: [],
            },
            grouped: {
                from: "base",
                composables: [
                    { kind: "formats", formats: { ir1: groupedFormat } },
                    { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                ],
            },
            second: {
                from: "grouped",
                composables: [
                    { kind: "group", by: ["STATUS"], values: [{ id: "ir2", col: "ir1", fn: "sum" }] },
                ],
            },
        },
    };
    const w = { doc };

    assert.deepEqual(
        formatForColumn(w, { name: "AMOUNT", type: "number" }),
        parentFormat,
        "the declaring active table keeps its complete renderer and style");

    doc.activeTable = "plain";
    assert.deepEqual(
        formatForColumn(w, { name: "AMOUNT", type: "number" }),
        { mask: "$#,##0.00" },
        "an unchanged child column receives only the safe scalar mask");

    doc.activeTable = "grouped";
    assert.deepEqual(
        formatForColumn(w, { name: "ir1", type: "number", formatSource: "AMOUNT" }),
        groupedFormat,
        "a direct active-table format remains complete");

    doc.activeTable = "second";
    assert.deepEqual(
        formatForColumn(w, { name: "ir2", type: "number", formatSource: "ir1" }),
        { mask: "#,##0" },
        "a later shape inherits the intermediate metric mask, not its renderer/style");
});

test("same-table source formats do not leak backward through a Shape", () => {
    const doc = {
        activeTable: "grouped",
        tables: {
            base: {
                from: "definition",
                composables: [{ kind: "formats", formats: { AMOUNT: { mask: "0.00" } } }],
            },
            grouped: {
                from: "base",
                composables: [
                    { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                    { kind: "formats", formats: { AMOUNT: { mask: "#,##0" } } },
                ],
            },
        },
    };

    assert.deepEqual(
        formatForColumn({ doc }, { name: "ir1", type: "number", formatSource: "AMOUNT" }),
        { mask: "0.00" },
        "the generated metric inherits its imported mask, not an unknown same-table source assignment");
});

test("removed source formats cannot cross two completed Shape schemas", () => {
    const column = {
        name: "ir2", label: "sum(sum(Amount))", type: "number", formatSource: "ir1",
    };
    const doc = {
        activeTable: "second",
        tables: {
            base: {
                from: "definition",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    { name: "AMOUNT", label: "Amount", type: "number" },
                ],
                composables: [{ kind: "formats", formats: { AMOUNT: { mask: "0.00" } } }],
            },
            first: {
                from: "base",
                schema: [
                    { name: "STATUS", label: "Status", type: "text" },
                    {
                        name: "ir1", label: "sum(Amount)", type: "number",
                        formatSource: "AMOUNT",
                    },
                ],
                composables: [
                    { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                    // AMOUNT is not in this completed export. The server ignores
                    // this stale assignment, so the client must not export it.
                    { kind: "formats", formats: { AMOUNT: { mask: "#,##0" } } },
                ],
            },
            second: {
                from: "first",
                schema: [column],
                composables: [
                    { kind: "group", by: [], values: [{ id: "ir2", col: "ir1", fn: "sum" }] },
                ],
            },
        },
    };

    assert.deepEqual(formatForColumn({ doc }, column), { mask: "0.00" },
        "only the mask carried by first's real ir1 export reaches the second Shape");
});

test("a retained dimension format cannot cross through a sibling metric", () => {
    const column = {
        name: "ir2", label: "sum(sum(Amount))", type: "number",
    };
    const doc = {
        activeTable: "second",
        tables: {
            base: {
                from: "definition",
                schema: [{ name: "AMOUNT", label: "Amount", type: "number" }],
                composables: [],
            },
            first: {
                from: "base",
                schema: [
                    { name: "AMOUNT", label: "Amount", type: "number" },
                    {
                        name: "ir1", label: "sum(Amount)", type: "number",
                    },
                ],
                composables: [
                    { kind: "group", by: ["AMOUNT"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                    { kind: "formats", formats: { AMOUNT: { mask: "#,##0" } } },
                ],
            },
            second: {
                from: "first",
                schema: [column],
                composables: [
                    { kind: "group", by: [], values: [{ id: "ir2", col: "ir1", fn: "sum" }] },
                ],
            },
        },
    };

    assert.equal(formatForColumn({ doc }, column), null);
});

test("a later shape inherits a direct format from its intermediate metric", () => {
    const column = { name: "ir2", label: "sum(sum(Amount))", type: "number", formatSource: "ir1" };
    const w = {
        doc: {
            activeTable: "second",
            tables: {
                source: {
                    from: "definition",
                    composables: [{ kind: "formats", formats: { AMOUNT: { mask: "$#,##0.00" } } }],
                },
                first: {
                    from: "source",
                    composables: [
                        { kind: "group", by: ["STATUS"], values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }] },
                        { kind: "formats", formats: { ir1: { mask: "#,##0" } } },
                    ],
                },
                second: {
                    from: "first",
                    schema: [column],
                    composables: [
                        { kind: "group", by: ["STATUS"], values: [{ id: "ir2", col: "ir1", fn: "sum" }] },
                    ],
                },
            },
        },
    };

    assert.equal(formatForColumn(w, column).mask, "#,##0");
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

test("highlight rendering normalizes precedence and excludes disabled rules", () => {
    const columns = [
        { name: "AMOUNT", label: "Amount", type: "number" },
        { name: "STATUS", label: "Status", type: "text" },
    ];
    const rules = [
        { id: "row-low", sequence: 10, enabled: true, style: { bg: "#111111" } },
        { id: "row-high", sequence: 20, enabled: true, style: { bg: "#222222" } },
        { id: "row-disabled", sequence: 30, enabled: false, style: { bg: "#ff0000" } },
        { id: "cell-low", sequence: 10, enabled: true, style: { bg: "#333333" } },
        { id: "cell-high", sequence: 20, enabled: true, style: { bg: "#444444" } },
        { id: "cell-disabled", sequence: 30, enabled: false, style: { bg: "#00ff00" } },
    ];
    const hits = [
        { row: 0, id: "row-high" },
        { row: 0, id: "row-disabled" },
        { row: 0, id: "row-low" },
        { row: 0, id: "cell-high", col: "AMOUNT" },
        { row: 0, id: "cell-disabled", col: "AMOUNT" },
        { row: 0, id: "cell-low", col: "AMOUNT" },
    ];

    for (const [ruleOrder, hitOrder] of [
        [rules, hits],
        [[...rules].reverse(), [...hits].reverse()],
    ]) {
        const w = {
            doc: reportState({
                breaks: [],
                sorts: [],
                formats: {},
                highlights: structuredClone(ruleOrder),
            }),
            schema: { columns },
            lastResult: {
                availableColumns: columns,
                columns,
                rows: [{ AMOUNT: 10, STATUS: "open" }],
                page: { index: 1, size: 10 },
                totalRows: 1,
                aggregates: {},
                breakTotals: [],
                highlights: structuredClone(hitOrder),
            },
        };
        const table = document.createElement("table");

        renderGrid(w, table);

        // Row highlights paint the cells, with cell scope applied afterward.
        // Disabled rules remain in the normalized order but never paint.
        const row = table.querySelector("tr.ir-row");
        assert.equal(row.children[0].style.background, "#444444");
        assert.equal(row.children[1].style.background, "#222222");
    }
});
