import { test, expect } from "@playwright/test";
import {
    createSavedState,
    deleteSavedState,
    loadSavedState,
    openWorkbench,
    reportId,
    visibleGridRows,
    waitForQuery,
} from "./support.js";

const staleSchema = [{
    name: "stale",
    label: "Stale advisory column",
    type: "text",
    computed: false,
}];

async function queryOrders(request, state, description = "orders query") {
    const response = await request.post("/api/reports/orders/query", { data: state });
    const body = await response.text();
    expect(response.ok(), `${description} failed (${response.status()}): ${body}`).toBe(true);
    return JSON.parse(body);
}

async function replaceSavedState(request, saved, state) {
    const response = await request.put(`/api/reports/${saved.id}`, {
        data: { state },
    });
    const body = await response.text();
    expect(response.ok(), `saved-report update failed (${response.status()}): ${body}`).toBe(true);
}

const pivotCells = result => result.columns.filter(column => column.pivotMetricId);
const pivotIdentities = result => new Map(pivotCells(result).map(column => [
    `${column.pivotMetricId}|${column.label}`,
    column.name,
]));

function multiMetricPivotState(filter = null, schemas = {}) {
    return {
        activeTable: "pivot",
        page: { index: 1, size: 50 },
        tables: {
            base: {
                from: "definition",
                schema: schemas.base ?? null,
                composables: filter
                    ? [{ kind: "filter", filters: [{ enabled: true, expr: filter }] }]
                    : [],
            },
            pivot: {
                from: "base",
                schema: schemas.pivot ?? null,
                composables: [{
                    kind: "pivot",
                    rows: ["REGION"],
                    cols: ["STATUS"],
                    values: [
                        { id: "ir20", col: "AMOUNT", fn: "sum" },
                        { id: "ir21", col: "AMOUNT", fn: "avg" },
                    ],
                }],
            },
        },
    };
}

test("a Pivot stored after its same-table transformations still executes first", async ({ page, request }) => {
    const discoveryState = {
        activeTable: "pivotSource",
        page: { index: 1, size: 50 },
        tables: {
            pivotSource: {
                from: "definition",
                schema: null,
                composables: [{
                    kind: "pivot",
                    rows: ["REGION"],
                    cols: ["STATUS"],
                    values: [{ id: "ir10", col: "AMOUNT", fn: "sum" }],
                }],
            },
        },
    };
    const discovery = await queryOrders(request, discoveryState, "Pivot discovery");
    const metricCells = new Map(pivotCells(discovery)
        .filter(column => column.pivotMetricId === "ir10")
        .map(column => [column.label, column.name]));
    const shipped = metricCells.get("SHIPPED");
    const pending = metricCells.get("PENDING");
    expect(shipped, "discovery should expose the SHIPPED Pivot cell").toBeTruthy();
    expect(pending, "discovery should expose the PENDING Pivot cell").toBeTruthy();

    const computedExpression = `COALESCE(\`${shipped}\`, 0) - COALESCE(\`${pending}\`, 0)`;
    const probeState = structuredClone(discoveryState);
    probeState.page.size = 0;
    probeState.tables.pivotSource.composables = [
        {
            kind: "compute",
            computed: [{
                id: "ir11",
                label: "Pivot delta",
                enabled: true,
                expr: computedExpression,
            }],
        },
        ...probeState.tables.pivotSource.composables,
    ];
    const probe = await queryOrders(request, probeState, "post-Pivot Compute probe");
    const probeRows = probe.rows.map(row => ({
        region: row.REGION,
        delta: Number(row.ir11),
    }));
    expect(probeRows.every(row => row.region && Number.isFinite(row.delta))).toBe(true);
    const distinctDeltas = [...new Set(probeRows.map(row => row.delta))]
        .sort((left, right) => right - left);
    expect(distinctDeltas.length, "the deterministic fixture must expose three Pivot deltas")
        .toBeGreaterThanOrEqual(3);
    // A midpoint between the second- and third-highest values keeps a strict,
    // nonempty subset containing at least two distinct values. That lets this
    // scenario observe Filter membership and Sort direction independently.
    const threshold = (distinctDeltas[1] + distinctDeltas[2]) / 2;
    const thresholdText = threshold.toFixed(6).replace(/\.?0+$/, "");
    const expectedRegions = probeRows
        .filter(row => row.delta >= threshold)
        .map(row => row.region)
        .sort();
    expect(expectedRegions.length).toBeGreaterThan(0);
    expect(expectedRegions.length).toBeLessThan(probeRows.length);

    const state = {
        activeTable: "pivotSource",
        page: { index: 1, size: 50 },
        tables: {
            pivotSource: {
                from: "definition",
                schema: staleSchema,
                // Deliberately adversarial storage order. The Pivot establishes
                // the relation before these declarations are interpreted.
                composables: [
                    { kind: "filter", filters: [{ enabled: true, expr: `ir11 >= ${thresholdText}` }] },
                    { kind: "select", columns: ["REGION", shipped, pending, "ir11"] },
                    {
                        kind: "compute",
                        computed: [{
                            id: "ir11",
                            label: "Pivot delta",
                            enabled: true,
                            expr: computedExpression,
                        }],
                    },
                    { kind: "sort", sorts: [{ col: "ir11", dir: "desc" }] },
                    {
                        kind: "pivot",
                        rows: ["REGION"],
                        cols: ["STATUS"],
                        values: [{ id: "ir10", col: "AMOUNT", fn: "sum" }],
                    },
                ],
            },
            child: {
                from: "pivotSource",
                schema: staleSchema,
                composables: [
                    { kind: "select", columns: ["REGION", "ir12"] },
                    {
                        kind: "group",
                        by: ["REGION"],
                        values: [{ id: "ir12", col: "ir11", fn: "sum" }],
                    },
                ],
            },
        },
    };

    let saved;
    try {
        saved = await createSavedState(request, state, "pivot-natural-order");
        // Saved-report writes validate and hydrate schema caches before persistence.
        // Simulate an older external document at the browser ingestion boundary so
        // this query, rather than the save endpoint, proves advisory caches are ignored.
        await page.route(`**/api/reports/${saved.id}`, async route => {
            const upstream = await route.fetch();
            const document = await upstream.json();
            document.state.tables.pivotSource.schema = structuredClone(staleSchema);
            document.state.tables.child.schema = structuredClone(staleSchema);
            await route.fulfill({ response: upstream, json: document });
        }, { times: 1 });
        await openWorkbench(page);
        const response = await loadSavedState(page, saved);
        const result = await response.json();

        const submitted = response.request().postDataJSON();
        expect(submitted.tables.pivotSource.schema).toEqual(staleSchema);
        expect(submitted.tables.child.schema).toEqual(staleSchema);
        expect(submitted.tables.pivotSource.composables.map(composable => composable.kind))
            .toEqual(["filter", "select", "compute", "sort", "pivot"]);
        expect(result.columns.map(column => column.name))
            .toEqual(["REGION", shipped, pending, "ir11"]);
        expect(result.rows.length).toBeGreaterThan(0);
        expect(result.rows.map(row => row.REGION).sort()).toEqual(expectedRegions);
        const renderedDeltas = result.rows.map(row => Number(row.ir11));
        expect(new Set(renderedDeltas).size).toBeGreaterThan(1);
        expect(renderedDeltas).toEqual([...renderedDeltas].sort((left, right) => right - left));

        const refreshedPivot = result.document.tables.pivotSource.schema;
        expect(refreshedPivot.some(column => column.name === shipped)).toBe(true);
        expect(refreshedPivot.some(column => column.name === "ir11")).toBe(true);
        expect(refreshedPivot.some(column => column.name === "stale")).toBe(false);
        expect(result.document.tables.child.schema).toEqual(staleSchema);

        const pivotHeaders = page.getByRole("columnheader");
        await expect(pivotHeaders).toHaveCount(result.columns.length);
        expect((await pivotHeaders.allTextContents()).map(text =>
            text.trim().replace(/[▲▼]\d*$/, "")))
            .toEqual(result.columns.map(column => column.label));
        await expect(pivotHeaders.nth(result.columns.findIndex(column => column.name === "ir11")))
            .toHaveAttribute("aria-sort", "descending");
        await expect(visibleGridRows(page)).toHaveCount(result.rows.length);

        const childResponse = await waitForQuery(page, () =>
            page.getByRole("button", { name: "Group By", exact: true }).click());
        const childSubmitted = childResponse.request().postDataJSON();
        const childResult = await childResponse.json();
        expect(childSubmitted.activeTable).toBe("child");
        expect(childSubmitted.tables.child.schema).toEqual(staleSchema);
        expect(childResult.columns.map(column => column.name)).toEqual(["REGION", "ir12"]);
        expect(childResult.rows.map(row => row.REGION).sort()).toEqual(expectedRegions);
        expect(childResult.rows.every(row => row.ir12 !== null && row.ir12 !== undefined)).toBe(true);

        const refreshedChild = childResult.document.tables.child.schema;
        expect(refreshedChild.some(column => column.name === "ir12")).toBe(true);
        expect(refreshedChild.some(column => column.name === "stale")).toBe(false);

        await expect(page.getByRole("columnheader"))
            .toHaveText(childResult.columns.map(column => column.label));
        await expect(visibleGridRows(page)).toHaveCount(childResult.rows.length);
        await expect(page.getByText(/unknown column/i)).toHaveCount(0);
    } finally {
        await deleteSavedState(request, saved);
    }
});

test("multi-metric Pivot provenance and opaque identities survive key removal and reload", async ({ page, request }) => {
    const fullState = multiMetricPivotState();
    let saved;

    try {
        saved = await createSavedState(request, fullState, "pivot-metric-provenance");
        await openWorkbench(page);
        const baselineResponse = await loadSavedState(page, saved);
        const baseline = await baselineResponse.json();
        const baselineCells = pivotCells(baseline);
        const baselineIds = pivotIdentities(baseline);

        expect(baselineCells).toHaveLength(8);
        expect(new Set(baselineCells.map(column => column.pivotMetricId)))
            .toEqual(new Set(["ir20", "ir21"]));
        for (const column of baselineCells) {
            const [key] = column.label.split(" · ");
            const aggregate = column.pivotMetricId === "ir20" ? "sum(Amount)" : "avg(Amount)";
            expect(column.label).toBe(`${key} · ${aggregate}`);
        }
        await expect(page.getByRole("columnheader"))
            .toHaveText(baseline.columns.map(column => column.label));

        const filteredState = multiMetricPivotState("STATUS <> 'CANCELLED'", {
            base: baseline.document.tables.base.schema,
            pivot: baseline.document.tables.pivot.schema,
        });
        await replaceSavedState(request, saved, filteredState);
        const savedSelect = page.getByRole("combobox", { name: "Saved Report" });
        await waitForQuery(page, () => savedSelect.selectOption({ label: "Default" }));
        const filteredResponse = await loadSavedState(page, saved);
        const filtered = await filteredResponse.json();
        const filteredIds = pivotIdentities(filtered);

        expect(pivotCells(filtered)).toHaveLength(6);
        expect(pivotCells(filtered).some(column => column.label.startsWith("CANCELLED · "))).toBe(false);
        for (const [identity, id] of filteredIds)
            expect(id, `opaque id changed for ${identity}`).toBe(baselineIds.get(identity));

        const restoredState = multiMetricPivotState(null, {
            base: filtered.document.tables.base.schema,
            pivot: filtered.document.tables.pivot.schema,
        });
        await replaceSavedState(request, saved, restoredState);

        await page.reload();
        await expect(page.getByRole("table")).toBeVisible();
        await expect(page.getByRole("combobox", { name: "Saved Report" })
            .locator(`option[value="${saved.id}"]`)).toHaveCount(1);
        const restoredResponse = await loadSavedState(page, saved);
        const restored = await restoredResponse.json();

        expect(pivotCells(restored)).toHaveLength(8);
        expect(pivotIdentities(restored)).toEqual(baselineIds);
        await expect(page.getByRole("columnheader"))
            .toHaveText(restored.columns.map(column => column.label));
    } finally {
        await deleteSavedState(request, saved);
    }
});

test("a scalar mask follows immediate lineage through two Shapes without renderer or style leakage", async ({ page, request }) => {
    const state = {
        activeTable: "second",
        page: { index: 1, size: 50 },
        tables: {
            base: {
                from: "definition",
                schema: null,
                composables: [
                    { kind: "select", columns: ["STATUS", "AMOUNT"] },
                    {
                        kind: "formats",
                        formats: {
                            AMOUNT: {
                                mask: "currency:USD",
                                align: "center",
                                bold: true,
                                italic: true,
                                fg: "#123456",
                                bg: "#ffeecc",
                                classes: ["amount-column"],
                                displayAs: "link",
                                urlColumn: "ORDER_URL",
                                textColumn: "AMOUNT",
                            },
                        },
                    },
                ],
            },
            first: {
                from: "base",
                schema: null,
                composables: [{
                    kind: "group",
                    by: ["STATUS"],
                    values: [{ id: "ir40", col: "AMOUNT", fn: "sum" }],
                }],
            },
            second: {
                from: "first",
                schema: null,
                composables: [
                    { kind: "select", columns: ["STATUS", "ir41"] },
                    {
                        kind: "group",
                        by: ["STATUS"],
                        values: [{ id: "ir41", col: "ir40", fn: "sum" }],
                    },
                ],
            },
        },
    };

    let saved;
    try {
        saved = await createSavedState(request, state, "shape-format-lineage");
        await openWorkbench(page);
        const response = await loadSavedState(page, saved);
        const result = await response.json();

        const firstMetric = result.document.tables.first.schema
            .find(column => column.name === "ir40");
        const secondMetric = result.document.tables.second.schema
            .find(column => column.name === "ir41");
        expect(firstMetric?.formatSource).toBe("AMOUNT");
        expect(secondMetric?.formatSource).toBe("ir40");
        expect(result.columns.find(column => column.name === "ir41")?.formatSource).toBe("ir40");

        const metricIndex = result.columns.findIndex(column => column.name === "ir41");
        expect(metricIndex).toBeGreaterThanOrEqual(0);
        const metricCell = visibleGridRows(page).first().locator("td").nth(metricIndex);
        await expect(metricCell).toContainText("$");
        await expect(metricCell.locator("a")).toHaveCount(0);
        await expect(metricCell).not.toHaveClass(/amount-column/);
        expect(await metricCell.evaluate(cell => ({
            textAlign: cell.style.textAlign,
            fontWeight: cell.style.fontWeight,
            fontStyle: cell.style.fontStyle,
            color: cell.style.color,
            background: cell.style.background,
        }))).toEqual({
            textAlign: "",
            fontWeight: "",
            fontStyle: "",
            color: "",
            background: "",
        });

        const exported = await page.locator("interactive-report").evaluate(async element => {
            const file = await element.getExport("csv");
            return {
                text: await file.blob.text(),
                contentType: file.contentType,
                filename: file.filename,
            };
        });
        expect(exported.contentType).toContain("text/csv");
        expect(exported.filename).toBe("orders.csv");
        expect(exported.text).toContain("sum(sum(Amount))");
        expect(exported.text).not.toContain("<a");
        expect(exported.text).not.toContain("ir-cell-link");
        expect(exported.text).not.toContain("amount-column");
    } finally {
        await deleteSavedState(request, saved);
    }
});
