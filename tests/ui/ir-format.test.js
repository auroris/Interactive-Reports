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
import { renderColumnValue } from "../../src/client/report/render/column-renderers.js";
import { renderChartView } from "../../src/client/report/render/chart-view.js";
import { renderGrid } from "../../src/client/report/render/grid.js";
import { renderPager } from "../../src/client/report/render/pager.js";

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

test("link text composes the source column's ordinary formatter", () => {
    const w = {
        doc: {
            formats: {
                CUSTOMER: { displayAs: "link", urlColumn: "URL", textColumn: "AMOUNT" },
                AMOUNT: { mask: "plain" },
            },
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
    const w = {
        doc: {
            formats: { AMOUNT: { mask: "plain" } },
            view: { mode: "groupBy", groupBy: ["STATUS"], values: [{ col: "AMOUNT", fn: "sum" }] },
        },
        lastResult: {
            columns: [
                { name: "STATUS", label: "Status", type: "text", computed: false },
                { name: "v0", label: "sum(Amount)", type: "number", computed: false, formatSource: "AMOUNT" },
            ],
            rows: [{ STATUS: "SHIPPED", v0: "9007199254740993.125" }],
            aggregates: {}, breakTotals: [], highlights: [],
        },
        schema: { columns: [{ name: "STATUS", label: "Status", type: "text" }, { name: "AMOUNT", label: "Amount", type: "number" }] },
    };
    const table = document.createElement("table");
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[1].textContent, "9007199254740993.13");

    w.doc.view = { mode: "grid" };
    w.doc.formats.c1 = { mask: "plain" };
    w.lastResult.columns = [{ name: "c1", label: "Computed", type: "number", computed: true }];
    w.lastResult.rows = [{ c1: "999999999999999999.999" }];
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[0].textContent, "1000000000000000000.00");

    w.doc.view = { mode: "pivot" };
    w.lastResult.columns = [
        { name: "STATUS", label: "Status", type: "text", computed: false },
        { name: "p0_0", label: "SHIPPED", type: "number", computed: false, formatSource: "AMOUNT" },
    ];
    w.lastResult.rows = [{ STATUS: "Acme", p0_0: "12345678901234567890.125" }];
    renderGrid(w, table);
    assert.equal(table.querySelector("tbody tr").children[1].textContent, "12345678901234567890.13");
});

test("the chart data table retains an exact masked metric", () => {
    const w = {
        doc: {
            formats: { AMOUNT: { mask: "plain" } },
            view: { mode: "chart", type: "bar", label: "STATUS", value: "AMOUNT", fn: "sum" },
        },
        lastResult: {
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

test("pager arithmetic and display preserve an Int64 count", () => {
    const w = {
        doc: { view: { mode: "grid" } },
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
        doc: { view: { mode: "grid" } },
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
            view: { mode: "grid" },
            breaks: ["REGION"],
            sorts: [],
            highlights: [],
            formats: {},
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
    const columns = [{ name: "AMOUNT", label: "Amount", type: "number" }];
    const w = {
        doc: {
            view: { mode: "grid" },
            breaks: [],
            sorts: [],
            formats: {},
            highlights: [
                { id: "row-low", sequence: 10, style: { bg: "#111111" } },
                { id: "row-high", sequence: 20, style: { bg: "#222222" } },
                { id: "cell-low", sequence: 10, style: { bg: "#333333" } },
                { id: "cell-high", sequence: 20, style: { bg: "#444444" } },
            ],
        },
        schema: { columns },
        lastResult: {
            availableColumns: columns,
            columns,
            rows: [{ AMOUNT: 10 }],
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

    const row = table.querySelector("tr.ir-row");
    assert.equal(row.style.background, "#222222");
    assert.equal(row.children[0].style.background, "#444444");
});
