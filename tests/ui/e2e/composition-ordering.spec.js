import { readFile } from "node:fs/promises";
import { test, expect } from "@playwright/test";
import {
    clickAction,
    createSavedState,
    deleteSavedState,
    loadSavedState,
    openWorkbench,
    visibleGridRows,
} from "./support.js";

const canonicalKinds = ["group", "compute", "filter", "formats", "select", "sort"];
const shuffledKinds = ["filter", "select", "compute", "sort", "group", "formats"];

const group = {
    kind: "group",
    by: ["REGION"],
    values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }],
};
const compute = {
    kind: "compute",
    computed: [
        { id: "ir2", label: "Average Amount", expr: "ir1 / __count", enabled: true },
        { id: "ir3", label: "Adjusted Average", expr: "ROUND(ir2 + 1, 2)", enabled: true },
    ],
};
const filter = {
    kind: "filter",
    filters: [{ expr: "ir3 > 0", enabled: true }],
};
const formats = {
    kind: "formats",
    formats: { ir3: { mask: "$#,##0.00" } },
};
const select = { kind: "select", columns: ["REGION", "ir3"] };
const sort = { kind: "sort", sorts: [{ col: "ir3", dir: "desc" }] };
const composableByKind = { group, compute, filter, formats, select, sort };

const orderedState = kinds => ({
    activeTable: "summary",
    page: { index: 1, size: 0 },
    tables: {
        summary: {
            from: "definition",
            schema: null,
            composables: kinds.map(kind => structuredClone(composableByKind[kind])),
        },
    },
});

const responseSchema = result => ({
    availableColumns: result.availableColumns,
    columns: result.columns,
    tableSchema: result.document.tables.summary.schema,
});

async function gridSnapshot(page) {
    return {
        headers: await page.getByRole("columnheader").allTextContents(),
        rows: await visibleGridRows(page).evaluateAll(rows => rows.map(row =>
            [...row.cells].map(cell => cell.textContent.trim()))),
    };
}

async function downloadCsv(page) {
    const downloadPromise = page.waitForEvent("download");
    await clickAction(page, "Download", "CSV");
    const download = await downloadPromise;
    return readFile(await download.path(), "utf8");
}

test("semantic phases make shuffled composables equivalent without rewriting their storage order", async ({ page, request }) => {
    let canonical;
    let shuffled;
    try {
        canonical = await createSavedState(request, orderedState(canonicalKinds), "canonical-order");
        const adversarial = orderedState(shuffledKinds);
        adversarial.tables.summary.composables
            .find(item => item.kind === "compute").computed.reverse();
        shuffled = await createSavedState(request, adversarial, "shuffled-order");

        await openWorkbench(page);

        const canonicalResponse = await loadSavedState(page, canonical);
        const canonicalResult = await canonicalResponse.json();
        expect(canonicalResult.rows.length).toBeGreaterThan(0);
        const canonicalGrid = await gridSnapshot(page);
        const canonicalCsv = await downloadCsv(page);

        const shuffledResponse = await loadSavedState(page, shuffled);
        const submitted = shuffledResponse.request().postDataJSON();
        const shuffledResult = await shuffledResponse.json();
        const shuffledGrid = await gridSnapshot(page);
        const shuffledCsv = await downloadCsv(page);

        expect(submitted.tables.summary.composables.map(item => item.kind)).toEqual(shuffledKinds);
        expect(submitted.tables.summary.composables
            .find(item => item.kind === "compute").computed.map(rule => rule.id))
            .toEqual(["ir3", "ir2"]);
        expect(shuffledResult.document.tables.summary.composables.map(item => item.kind)).toEqual(shuffledKinds);
        expect(responseSchema(shuffledResult)).toEqual(responseSchema(canonicalResult));
        expect(shuffledResult.rows).toEqual(canonicalResult.rows);
        expect(shuffledGrid).toEqual(canonicalGrid);
        expect(shuffledCsv).toBe(canonicalCsv);
    } finally {
        await deleteSavedState(request, canonical);
        await deleteSavedState(request, shuffled);
    }
});

test("a child consumes exported relation and mask state while parent result presentation stays local", async ({ page, request }) => {
    const saved = await createSavedState(request, {
        activeTable: "child",
        page: { index: 1, size: 0 },
        tables: {
            parent: {
                from: "definition",
                schema: null,
                composables: [
                    { kind: "select", columns: ["CUSTOMER"] },
                    { kind: "sort", sorts: [{ col: "STATUS", dir: "desc" }] },
                    {
                        kind: "highlight",
                        highlights: [{
                            id: "h1",
                            name: "Parent only",
                            sequence: 10,
                            enabled: true,
                            scope: "row",
                            expr: "ir1 > 0",
                            style: { bg: "#ff0000", fg: "#ffffff" },
                        }],
                    },
                    { kind: "break", breaks: ["STATUS"] },
                    { kind: "aggregate", aggregates: [{ col: "ir1", fn: "sum" }] },
                    {
                        kind: "formats",
                        formats: {
                            ir1: {
                                mask: "$#,##0.00",
                                bold: true,
                                fg: "#123456",
                                bg: "#ffeeaa",
                                classes: ["amount-column"],
                                displayAs: "link",
                                urlColumn: "REGION",
                                textColumn: "CUSTOMER",
                            },
                        },
                    },
                    {
                        kind: "filter",
                        filters: [{ expr: "REGION = 'NORTH'", enabled: true }],
                    },
                    {
                        kind: "compute",
                        computed: [{
                            id: "ir1",
                            label: "Parent Extended",
                            expr: "AMOUNT * 2",
                            enabled: true,
                        }],
                    },
                ],
            },
            child: {
                from: "parent",
                schema: null,
                composables: [
                    {
                        kind: "group",
                        by: ["REGION", "STATUS"],
                        values: [{ id: "ir2", col: "ir1", fn: "sum" }],
                    },
                    { kind: "select", columns: ["REGION", "STATUS", "__count", "ir2"] },
                    { kind: "sort", sorts: [{ col: "STATUS", dir: "asc" }] },
                ],
            },
        },
    }, "composition-boundary");

    try {
        await openWorkbench(page);
        const response = await loadSavedState(page, saved);
        const result = await response.json();

        expect(result.ignored).toEqual([]);
        expect(result.columns.map(column => column.name)).toEqual(["REGION", "STATUS", "__count", "ir2"]);
        expect(result.columns.find(column => column.name === "ir2").formatSource).toBe("ir1");
        expect(result.rows.length).toBeGreaterThan(0);
        expect(result.rows.every(row => row.REGION === "NORTH")).toBe(true);
        const statuses = result.rows.map(row => row.STATUS);
        expect(statuses).toEqual([...statuses].sort());
        const sourceRows = result.rows.reduce((count, row) => count + Number(row.__count), 0);
        expect(sourceRows).toBeGreaterThan(0);
        expect(sourceRows).toBeLessThan(500);
        expect(result.aggregates).toEqual({});
        expect(result.breakTotals).toEqual([]);
        expect(result.highlights).toEqual([]);

        const rows = visibleGridRows(page);
        await expect(rows).toHaveCount(result.rows.length);
        expect(await rows.locator("td:first-child").allTextContents())
            .toEqual(result.rows.map(() => "NORTH"));
        expect(await rows.locator("td:nth-child(2)").allTextContents()).toEqual(statuses);
        const metricCells = rows.locator("td:nth-child(4)");
        expect((await metricCells.allTextContents())
            .every(value => /^CA\$/.test(value))).toBe(true);

        await expect(page.locator("table a.ir-cell-link")).toHaveCount(0);
        await expect(page.locator("table .amount-column")).toHaveCount(0);
        await expect(page.locator("tr.ir-break-header, tr.ir-break-total, tr.ir-grand-total")).toHaveCount(0);
        expect(await metricCells.evaluateAll(cells => cells.every(cell =>
            cell.style.fontWeight === ""
            && cell.style.color === ""
            && cell.style.background === ""
            && cell.style.backgroundColor === ""))).toBe(true);
        expect(await rows.locator("td").evaluateAll(cells => cells.every(cell =>
            cell.style.background === "" && cell.style.backgroundColor === ""))).toBe(true);

        const csv = await downloadCsv(page);
        const lines = csv.trim().split(/\r?\n/);
        expect(lines[0]).toBe(result.columns.map(column => column.label).join(","));
        expect(lines).toHaveLength(result.rows.length + 1);
        expect(lines.slice(1).every(line => line.startsWith("NORTH,"))).toBe(true);
        expect(csv).not.toContain("<a");
        expect(csv).not.toContain("ir-cell-link");
        expect(csv).not.toContain("Customer Name");
        expect(csv).not.toContain("Ordered On");
    } finally {
        await deleteSavedState(request, saved);
    }
});
