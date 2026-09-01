import { test, expect } from "@playwright/test";

import {
    clickAction,
    createSavedState,
    deleteSavedState,
    loadSavedState,
    openWorkbench,
    visibleGridRows,
    waitForQuery,
} from "./support.js";

const always = "AMOUNT >= 0";

const highlight = ({ id, name, sequence, scope, bg, enabled = true }) => ({
    id,
    name,
    sequence,
    enabled,
    scope,
    ...(scope === "cell" ? { col: "AMOUNT" } : {}),
    expr: always,
    style: { bg },
});

const highlightState = {
    activeTable: "highlightCase",
    page: { index: 1, size: 10 },
    tables: {
        highlightCase: {
            from: "definition",
            schema: null,
            // Deliberately oppose semantic order, and split the priority set
            // across repeated nodes. Array position must not decide precedence.
            composables: [
                {
                    kind: "highlight",
                    highlights: [highlight({
                        id: "h-disabled",
                        name: "Disabled black",
                        sequence: 100,
                        scope: "cell",
                        bg: "#000000",
                        enabled: false,
                    })],
                },
                {
                    kind: "highlight",
                    highlights: [highlight({
                        id: "h-cell-late",
                        name: "Cell blue",
                        sequence: 20,
                        scope: "cell",
                        bg: "#0000ff",
                    })],
                },
                {
                    kind: "highlight",
                    highlights: [highlight({
                        id: "h-cell-early",
                        name: "Cell green",
                        sequence: 10,
                        scope: "cell",
                        bg: "#00ff00",
                    })],
                },
                {
                    kind: "highlight",
                    highlights: [highlight({
                        id: "h-row",
                        name: "Row red",
                        sequence: 90,
                        scope: "row",
                        bg: "#ff0000",
                    })],
                },
            ],
        },
    },
};

const pivotTotalsState = {
    activeTable: "pivotCase",
    // Keep every grouped row on the logical final page so the Pivot total row is
    // rendered before and after the rejected transition.
    page: { index: 1, size: 50 },
    tables: {
        pivotCase: {
            from: "definition",
            schema: null,
            composables: [
                {
                    kind: "pivot",
                    rows: ["CUSTOMER"],
                    cols: ["STATUS"],
                    values: [{ id: "ir1", col: "AMOUNT", fn: "sum" }],
                    totals: true,
                },
            ],
        },
    },
};

async function expectHighlightCollision(page) {
    const headers = (await page.getByRole("columnheader").allTextContents())
        .map(text => text.trim());
    const customer = headers.indexOf("Customer");
    const amount = headers.indexOf("Amount");
    expect(customer).toBeGreaterThanOrEqual(0);
    expect(amount).toBeGreaterThanOrEqual(0);

    const cells = visibleGridRows(page).first().getByRole("cell");
    // The row rule colors every cell red. Both cell rules then target Amount;
    // sequence 20 runs after sequence 10 and therefore leaves it blue.
    await expect(cells.nth(customer)).toHaveCSS("background-color", "rgb(255, 0, 0)");
    await expect(cells.nth(amount)).toHaveCSS("background-color", "rgb(0, 0, 255)");
}

test("mixed-scope highlights share canonical precedence across nodes and pages", async ({ page, request }) => {
    let saved;
    try {
        saved = await createSavedState(request, highlightState, "highlight-order");
        await openWorkbench(page);
        const loadResponse = await loadSavedState(page, saved);
        const loaded = await loadResponse.json();

        expect(loaded.highlights.some(hit => hit.id === "h-disabled")).toBe(false);
        for (const id of ["h-row", "h-cell-early", "h-cell-late"])
            expect(loaded.highlights.some(hit => hit.id === id)).toBe(true);

        const chips = page.locator('.ir-chip[data-kind="highlight"]');
        await expect(chips).toHaveCount(4);
        await expect(chips.locator(".ir-chip-label")).toHaveText([
            "Row red #90 · AMOUNT >= 0 (row)",
            "Cell green #10 · AMOUNT >= 0 (AMOUNT cell)",
            "Cell blue #20 · AMOUNT >= 0 (AMOUNT cell)",
            "Disabled black #100 · AMOUNT >= 0 (AMOUNT cell)",
        ]);
        const disabled = chips.filter({ hasText: "Disabled black" });
        await expect(disabled).toHaveClass(/\bir-chip-off\b/);
        await expectHighlightCollision(page);

        await clickAction(page, "Highlight…");
        const dialog = page.getByRole("dialog", { name: "Highlight", exact: true });
        await expect(dialog.getByRole("spinbutton", { name: "Sequence", exact: true }))
            .toHaveValue("110");
        await dialog.getByRole("button", { name: "Cancel", exact: true }).click();

        const nextPage = await waitForQuery(page, () =>
            page.getByRole("button", { name: "Next page", exact: true }).click());
        const paged = await nextPage.json();
        expect(paged.page).toEqual({ index: 2, size: 10 });
        expect(paged.highlights.some(hit => hit.id === "h-disabled")).toBe(false);
        await expect(chips.locator(".ir-chip-label")).toHaveText([
            "Row red #90 · AMOUNT >= 0 (row)",
            "Cell green #10 · AMOUNT >= 0 (AMOUNT cell)",
            "Cell blue #20 · AMOUNT >= 0 (AMOUNT cell)",
            "Disabled black #100 · AMOUNT >= 0 (AMOUNT cell)",
        ]);
        await expect(disabled).toHaveClass(/\bir-chip-off\b/);
        await expectHighlightCollision(page);

        // Rehydrate the original saved document through the public load path.
        // Rule priority is semantic state, not a transient rendering decision.
        await openWorkbench(page);
        const reloadedResponse = await loadSavedState(page, saved);
        const reloaded = await reloadedResponse.json();
        expect(reloaded.highlights.some(hit => hit.id === "h-disabled")).toBe(false);
        await expect(chips.locator(".ir-chip-label")).toHaveText([
            "Row red #90 · AMOUNT >= 0 (row)",
            "Cell green #10 · AMOUNT >= 0 (AMOUNT cell)",
            "Cell blue #20 · AMOUNT >= 0 (AMOUNT cell)",
            "Disabled black #100 · AMOUNT >= 0 (AMOUNT cell)",
        ]);
        await expect(disabled).toHaveClass(/\bir-chip-off\b/);
        await expectHighlightCollision(page);
    } finally {
        await deleteSavedState(request, saved);
    }
});

test("Pivot totals reject active search transactionally and search succeeds once totals are off", async ({ page, request }) => {
    let saved;
    try {
        saved = await createSavedState(request, pivotTotalsState, "pivot-totals-search");
        await openWorkbench(page);
        await loadSavedState(page, saved);

        const report = page.locator("interactive-report");
        const table = page.getByRole("table");
        const search = page.getByRole("searchbox", { name: "Search", exact: true });
        const viewChip = page.locator('.ir-chip[data-kind="view"]');
        await expect(page.locator("tr.ir-grand-total")).toHaveCount(1);
        await expect(viewChip).toContainText("totals");

        const beforeDocument = await report.evaluate(element => element.getReportDocument());
        const beforeHeaders = await page.getByRole("columnheader").allTextContents();
        const beforeBody = await table.locator("tbody").innerText();

        await search.fill("Acme Corp");
        const failed = await waitForQuery(
            page,
            () => page.getByRole("button", { name: "Go", exact: true }).click(),
            response => response.status() === 400,
        );
        const problem = await failed.json();
        const preciseFailure = "tables.pivotCase.composables[0].totals: pivot totals cannot currently "
            + "be combined with request search because the totals relation is produced before the "
            + "search overlay; clear search or disable totals";
        expect(problem).toMatchObject({
            code: "IR-1201",
            title: "Report state failed validation",
            description: "One or more report settings are invalid.",
            details: preciseFailure,
        });
        await expect(page.getByRole("alert").filter({ hasText: preciseFailure })).toBeVisible();

        // A rejected edit restores all authored and rendered state, including the
        // search control. The valid Pivot matrix and its total row never flicker out.
        await expect(search).toHaveValue("");
        expect(await report.evaluate(element => element.getReportDocument())).toEqual(beforeDocument);
        expect(await page.getByRole("columnheader").allTextContents()).toEqual(beforeHeaders);
        expect(await table.locator("tbody").innerText()).toBe(beforeBody);
        await expect(page.locator("tr.ir-grand-total")).toHaveCount(1);
        await expect(viewChip).toContainText("totals");
        await expect(page.locator('.ir-chip[data-kind="search"]')).toHaveCount(0);

        await clickAction(page, "Pivot…");
        const pivotDialog = page.getByRole("dialog", { name: "Pivot", exact: true });
        const totals = pivotDialog.getByRole("checkbox", { name: "Show total rows", exact: true });
        await expect(totals).toBeChecked();
        await totals.uncheck();
        const withoutTotals = await waitForQuery(page, () =>
            pivotDialog.getByRole("button", { name: "Apply", exact: true }).click());
        const submittedPivot = withoutTotals.request().postDataJSON()
            .tables.pivotCase.composables.find(composable => composable.kind === "pivot");
        expect(submittedPivot.totals).toBeUndefined();
        await expect(page.locator("tr.ir-grand-total")).toHaveCount(0);

        await search.fill("Acme Corp");
        const accepted = await waitForQuery(page, () =>
            page.getByRole("button", { name: "Go", exact: true }).click());
        expect(accepted.request().postDataJSON().search).toBe("Acme Corp");
        const filtered = await accepted.json();
        expect(filtered.rows.length).toBeGreaterThan(0);
        expect(filtered.rows.every(row => String(row.CUSTOMER).includes("Acme Corp"))).toBe(true);
        await expect(page.locator('.ir-chip[data-kind="search"]')).toContainText("Acme Corp");
        await expect(page.getByRole("alert").filter({ hasText: preciseFailure })).toHaveCount(0);
    } finally {
        await deleteSavedState(request, saved);
    }
});
