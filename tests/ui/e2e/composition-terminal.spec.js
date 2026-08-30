import { test, expect } from "@playwright/test";
import {
    createSavedState,
    deleteSavedState,
    loadSavedState,
    openWorkbench,
    waitForQuery,
} from "./support.js";

test("break subtotals wait for a page boundary to close and grand totals wait for the final page", async ({ page, request }) => {
    const saved = await createSavedState(request, {
        activeTable: "grouped",
        page: { index: 1, size: 5 },
        tables: {
            grouped: {
                from: "definition",
                schema: null,
                composables: [
                    {
                        kind: "group",
                        by: ["STATUS", "CUSTOMER"],
                        values: [],
                    },
                    { kind: "break", breaks: ["STATUS"] },
                    { kind: "aggregate", aggregates: [{ col: "__count", fn: "sum" }] },
                    { kind: "select", columns: ["STATUS", "CUSTOMER", "__count"] },
                    { kind: "sort", sorts: [{ col: "CUSTOMER", dir: "asc" }] },
                ],
            },
        },
    }, "terminal-pages");

    try {
        await openWorkbench(page);
        let response = await loadSavedState(page, saved);
        let result = await response.json();
        const pages = [];

        for (;;) {
            const atEnd = result.page.size === 0
                || result.page.index * result.page.size >= Number(result.totalRows);

            await expect(page.locator("tbody tr.ir-row")).toHaveCount(result.rows.length);
            pages.push({
                result,
                subtotalCount: await page.locator("tr.ir-break-total").count(),
                grandTotalCount: await page.locator("tr.ir-grand-total").count(),
            });
            if (atEnd) break;

            response = await waitForQuery(page, () =>
                page.getByRole("button", { name: "Next page" }).click());
            result = await response.json();
        }

        let sawDeferredSubtotal = false;
        let sawClosedSubtotal = false;
        pages.forEach((entry, index) => {
            const statuses = entry.result.rows.map(row => row.STATUS);
            const nextStatuses = pages[index + 1]?.result.rows.map(row => row.STATUS) ?? [];
            const continues = statuses.length > 0
                && nextStatuses.length > 0
                && statuses.at(-1) === nextStatuses[0];
            const transitions = statuses.slice(1)
                .filter((status, rowIndex) => status !== statuses[rowIndex]).length;
            const expectedSubtotals = transitions + (continues || statuses.length === 0 ? 0 : 1);

            expect(entry.result.breakContinues).toBe(continues);
            expect(entry.subtotalCount).toBe(expectedSubtotals);
            expect(entry.grandTotalCount).toBe(index === pages.length - 1 ? 1 : 0);
            if (continues && expectedSubtotals === 0) sawDeferredSubtotal = true;
            if (expectedSubtotals > 0) sawClosedSubtotal = true;
        });

        expect(sawDeferredSubtotal).toBe(true);
        expect(sawClosedSubtotal).toBe(true);
        await expect(page.locator("tr.ir-grand-total")).toContainText("500");
    } finally {
        await deleteSavedState(request, saved);
    }
});
