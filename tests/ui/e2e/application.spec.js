import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { test, expect } from "@playwright/test";

const queryPath = /\/api\/reports\/[^/]+\/query$/;

// v3 documents: the source layer carries grid state; the tail stages are the view.
const sourceLayerOf = state => state.pipeline?.[0]?.layer ?? {};
const stageOf = (state, kind) => (state.pipeline ?? []).find(s => s.shape?.kind === kind)?.shape ?? null;
const modeOf = state => {
    const kinds = (state.pipeline ?? []).slice(1).map(s => s.shape?.kind);
    return kinds.includes("chart") ? "chart"
        : kinds.includes("spread") ? "pivot"
        : kinds.includes("group") ? "groupBy"
        : "grid";
};

async function openWorkbench(page) {
    await page.goto("/");
    await expect(page.getByRole("table")).toBeVisible();
    await expect(page.getByRole("button", { name: "Actions", exact: true })).toBeEnabled();
}

async function runAndWaitForQuery(page, action) {
    const response = page.waitForResponse(candidate =>
        candidate.request().method() === "POST"
        && queryPath.test(new URL(candidate.url()).pathname)
        && candidate.ok());
    await action();
    return response;
}

async function clickAction(page, name) {
    await page.getByRole("button", { name: "Actions", exact: true }).click();
    await page.getByRole("menuitem", { name, exact: true }).click();
}

async function search(page, value) {
    await page.getByRole("searchbox", { name: "Search" }).fill(value);
    await runAndWaitForQuery(page, () => page.getByRole("button", { name: "Go", exact: true }).click());
}

test("explains how to build the client when its bundle is missing", async ({ page }) => {
    await page.route("**/api/reports/ui/ir.js", route => route.fulfill({
        status: 404,
        contentType: "text/plain",
        body: "Not found",
    }));

    await page.goto("/");

    await expect(page.getByText(
        "You're seeing this message because you forgot to run npm install && npm run build.",
        { exact: true },
    )).toBeVisible();
});

test("loads the configured primary report, queries data, paginates from Actions, and changes reports", async ({ page }) => {
    await openWorkbench(page);

    const catalogResponse = await page.request.get("/api/reports");
    expect(catalogResponse.status()).toBe(404);
    await expect(page.locator("#identity")).toHaveText("workbench-dev · admin");
    await expect(page.getByRole("combobox", { name: "Report", exact: true })).toHaveCount(0);
    await expect(page.getByRole("columnheader")).toHaveText([
        "Order #", "Customer Name", "Region", "Status", "Amount", "Ordered On▼", "Notes", "With Tax",
    ]);
    const report = page.locator("interactive-report");
    await expect(report.locator('link[data-ir-custom-styles]')).toHaveAttribute("href", "/report-overrides.css");
    await expect(page.getByRole("columnheader", { name: "Amount", exact: true })).toHaveClass(/amount-column/);
    await expect(page.getByRole("table").locator("tbody a.ir-cell-link").first())
        .toHaveAttribute("href", /^\/orders\/\d+$/);
    await expect(page.getByText("1 – 50 of 500 rows", { exact: true })).toBeVisible();

    await search(page, "Acme Corp");
    const filteredRows = page.getByRole("table").locator("tbody tr");
    await expect.poll(() => filteredRows.count()).toBeGreaterThan(15);
    await expect.poll(() => filteredRows.evaluateAll(rows =>
        rows.every(row => row.textContent.includes("Acme Corp")))).toBe(true);

    await clickAction(page, "Pagination…");
    let dialog = page.getByRole("dialog");
    const limit = dialog.getByRole("combobox", { name: "Limit" });
    await expect(limit.locator("option")).toHaveText(["10", "50", "100", "500", "1000", "All"]);
    await limit.selectOption("10");
    await runAndWaitForQuery(page, () =>
        dialog.getByRole("button", { name: "Apply", exact: true }).click());
    await expect(filteredRows).toHaveCount(10);
    await expect(page.getByText(/^1 – 10 of \d+ rows$/)).toBeVisible();

    await runAndWaitForQuery(page, () =>
        page.getByRole("button", { name: "Next page" }).click());
    await expect(page.getByText(/^11 – 20 of \d+ rows$/)).toBeVisible();

    await clickAction(page, "Pagination…");
    dialog = page.getByRole("dialog");
    await dialog.getByRole("combobox", { name: "Limit" }).selectOption("0");
    const allResponse = await runAndWaitForQuery(page, () =>
        dialog.getByRole("button", { name: "Apply", exact: true }).click());
    const allResult = await allResponse.json();
    expect(allResult.page).toEqual({ index: 1, size: 0 });
    expect(allResult.rows).toHaveLength(Number(allResult.totalRows));
    await expect(page.getByRole("button", { name: "Next page" })).toBeDisabled();

    await runAndWaitForQuery(page, () =>
        page.locator("interactive-report").evaluate(element => element.setAttribute("report", "order-feed")));
    await expect(page.getByRole("columnheader")).toHaveText(["Order #", "Customer", "Amount"]);
    await expect(page.getByRole("searchbox", { name: "Search" })).toHaveValue("");
    await expect(page.getByText("1 – 50 of 500 rows", { exact: true })).toBeVisible();
});

test("editor windows are named, modeless, movable, and leave the report interactive", async ({ page }) => {
    await openWorkbench(page);
    await clickAction(page, "Pagination…");

    const dialog = page.getByRole("dialog", { name: "Pagination", exact: true });
    await expect(dialog).toHaveAttribute("popover", "manual");
    await expect(dialog).not.toHaveAttribute("aria-modal", "true");
    expect(await dialog.evaluate(element => element.matches(":popover-open"))).toBe(true);

    const titleBar = dialog.locator(".ir-dialog-title");
    const before = await dialog.boundingBox();
    const handle = await titleBar.boundingBox();
    expect(before).not.toBeNull();
    expect(handle).not.toBeNull();
    await page.mouse.move(handle.x + 24, handle.y + handle.height / 2);
    await page.mouse.down();
    await page.mouse.move(handle.x + 94, handle.y + handle.height / 2 + 40);
    await page.mouse.up();

    const afterPointer = await dialog.boundingBox();
    expect(afterPointer.x).toBeGreaterThan(before.x + 50);
    expect(afterPointer.y).toBeGreaterThan(before.y + 25);

    await titleBar.focus();
    await page.keyboard.press("Alt+ArrowLeft");
    const afterKeyboard = await dialog.boundingBox();
    expect(afterKeyboard.x).toBeLessThan(afterPointer.x - 5);

    await runAndWaitForQuery(page, () =>
        page.getByRole("button", { name: "Next page" }).click());
    await expect(page.getByText("51 – 100 of 500 rows", { exact: true })).toBeVisible();
    await expect(dialog).toBeVisible();
    expect(await dialog.evaluate(element => element.matches(":popover-open"))).toBe(true);

    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await clickAction(page, "Reset");
    const confirmation = page.getByRole("dialog", { name: "Reset", exact: true });
    await expect(confirmation).toHaveAttribute("open", "");
    await expect(confirmation).toHaveAttribute("aria-modal", "true");
    expect(await confirmation.evaluate(element => element.tagName)).toBe("DIALOG");
    await confirmation.getByRole("button", { name: "Cancel", exact: true }).click();
});

test("exports the current report state as CSV", async ({ page }) => {
    await openWorkbench(page);
    await search(page, "Acme Corp");

    const downloadPromise = page.waitForEvent("download");
    await clickAction(page, "CSV");
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toBe("orders.csv");

    const path = await download.path();
    const csv = await readFile(path, "utf8");
    const lines = csv.trim().split(/\r?\n/);
    expect(lines[0]).toContain("Order #,Customer Name,Region,Status,Amount,Ordered On,Notes,With Tax");
    expect(lines.length).toBeGreaterThan(1);
    expect(lines.slice(1).every(line => line.includes("Acme Corp"))).toBe(true);
    expect(lines.slice(1).every(line => line.includes('<a class=""ir-cell-link"" href=""/orders/'))).toBe(true);
});

test("sorts with explicit null placement from the Actions dialog", async ({ page }) => {
    await openWorkbench(page);
    await clickAction(page, "Sort…");

    const dialog = page.getByRole("dialog");
    await dialog.getByRole("combobox", { name: "Column" }).selectOption("NOTES");
    await dialog.getByRole("combobox", { name: "Direction" }).selectOption("desc");
    await dialog.getByRole("combobox", { name: "Null Sorting" }).selectOption("last");

    const response = await runAndWaitForQuery(page, () =>
        dialog.getByRole("button", { name: "Apply", exact: true }).click());
    expect(sourceLayerOf(response.request().postDataJSON()).sorts).toEqual([
        { col: "NOTES", dir: "desc", nulls: "last" },
    ]);

    await clickAction(page, "Sort…");
    const reopened = page.getByRole("dialog");
    const nullSorting = reopened.getByRole("combobox", { name: "Null Sorting" });
    await expect(nullSorting).toHaveValue("last");
    await nullSorting.selectOption("");
    const defaultResponse = await runAndWaitForQuery(page, () =>
        reopened.getByRole("button", { name: "Apply", exact: true }).click());
    expect(sourceLayerOf(defaultResponse.request().postDataJSON()).sorts).toEqual([
        { col: "NOTES", dir: "desc" },
    ]);
});

test("names highlights and saves their explicit precedence sequence", async ({ page }) => {
    await openWorkbench(page);
    await clickAction(page, "Highlight…");

    const dialog = page.getByRole("dialog");
    await dialog.getByRole("textbox", { name: "Name" }).fill("Large orders");
    await dialog.getByRole("spinbutton", { name: "Sequence" }).fill("40");
    await dialog.getByRole("textbox", { name: "Expression" }).fill("AMOUNT > 5000");

    const response = await runAndWaitForQuery(page, () =>
        dialog.getByRole("button", { name: "Apply", exact: true }).click());
    expect(sourceLayerOf(response.request().postDataJSON()).highlights).toEqual([
        {
            id: "h1",
            name: "Large orders",
            sequence: 40,
            enabled: true,
            scope: "row",
            expr: "AMOUNT > 5000",
            style: { bg: "#fff3cd" },
        },
    ]);
    await expect(page.getByText("Large orders", { exact: false })).toBeVisible();
});

test("configures an aggregate chart and returns to the data grid", async ({ page }) => {
    await openWorkbench(page);
    await page.getByRole("button", { name: "Chart", exact: true }).click();

    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("Chart");
    const selects = dialog.locator("select");
    await selects.nth(1).selectOption("STATUS");
    await selects.nth(3).selectOption("AMOUNT");
    await selects.nth(2).selectOption("sum");

    await runAndWaitForQuery(page, () =>
        dialog.getByRole("button", { name: "Apply", exact: true }).click());
    await expect(page.getByRole("img", {
        name: "Bar chart of Sum of Amount by Status. 4 data points.",
    })).toBeVisible();
    await expect(page.getByText("View chart data", { exact: true })).toBeVisible();

    await runAndWaitForQuery(page, () =>
        page.getByRole("button", { name: "Grid", exact: true }).click());
    await expect(page.getByRole("table")).toBeVisible();
    await expect(page.getByRole("columnheader")).toHaveCount(8);
});

test("adds correctly aggregated total rows to a pivot without a right-side total column", async ({ page }) => {
    await openWorkbench(page);
    await page.getByRole("button", { name: "Pivot", exact: true }).click();

    const dialog = page.getByRole("dialog");
    let selects = dialog.locator("select");
    await selects.nth(0).selectOption("CUSTOMER");
    await selects.nth(1).selectOption("STATUS");
    await dialog.getByRole("button", { name: "+ Value", exact: true }).click();

    selects = dialog.locator("select");
    await selects.nth(3).selectOption("AMOUNT");
    await selects.nth(2).selectOption("sum");
    await dialog.getByRole("checkbox", { name: "Show total rows", exact: true }).check();

    const response = await runAndWaitForQuery(page, () =>
        dialog.getByRole("button", { name: "Apply", exact: true }).click());
    const posted = response.request().postDataJSON();
    expect(stageOf(posted, "group")).toEqual({
        kind: "group",
        by: ["CUSTOMER", "STATUS"],
        values: [{ id: "m1", col: "AMOUNT", fn: "sum" }],
    });
    expect(stageOf(posted, "spread")).toEqual({ kind: "spread", cols: ["STATUS"], totals: true });

    await expect(page.locator("tr.ir-grand-total")).toHaveCount(1);
    await expect(page.locator("tr.ir-grand-total")).toContainText("Sum:");
    await expect(page.getByRole("columnheader", { name: "Total", exact: true })).toHaveCount(0);
});

test("a saved report retains its grid, pivot, and chart configurations with pivot as its default", async ({ page, request }) => {
    const title = `views-${randomUUID()}`;
    let savedId;

    try {
        await openWorkbench(page);

        await page.getByRole("button", { name: "Chart", exact: true }).click();
        let dialog = page.getByRole("dialog");
        let selects = dialog.locator("select");
        await selects.nth(1).selectOption("STATUS");
        await selects.nth(3).selectOption("AMOUNT");
        await selects.nth(2).selectOption("sum");
        await runAndWaitForQuery(page, () =>
            dialog.getByRole("button", { name: "Apply", exact: true }).click());

        await page.getByRole("button", { name: "Pivot", exact: true }).click();
        dialog = page.getByRole("dialog");
        selects = dialog.locator("select");
        await selects.nth(0).selectOption("CUSTOMER");
        await selects.nth(1).selectOption("STATUS");
        await dialog.getByRole("button", { name: "+ Value", exact: true }).click();
        selects = dialog.locator("select");
        await selects.nth(3).selectOption("AMOUNT");
        await selects.nth(2).selectOption("sum");
        const pivotResponse = await runAndWaitForQuery(page, () =>
            dialog.getByRole("button", { name: "Apply", exact: true }).click());
        const configured = pivotResponse.request().postDataJSON();
        expect(modeOf(configured)).toBe("pivot");
        expect(configured.shelf.chart[0].shape.kind).toBe("chart");

        await clickAction(page, "Save As…");
        const saveDialog = page.getByRole("dialog");
        await saveDialog.getByPlaceholder("Saved report name").fill(title);
        const saveResponsePromise = page.waitForResponse(response =>
            response.request().method() === "POST"
            && new URL(response.url()).pathname === "/api/reports/orders/saved");
        await saveDialog.getByRole("button", { name: "Save", exact: true }).click();
        const saveResponse = await saveResponsePromise;
        savedId = (await saveResponse.json()).id;
        const savedState = saveResponse.request().postDataJSON().state;
        expect(modeOf(savedState)).toBe("pivot");
        expect(Object.keys(savedState.shelf)).toEqual(["chart"]);
        expect(savedState.schema.AMOUNT).toBe("number");

        await runAndWaitForQuery(page, () =>
            page.getByRole("button", { name: "Grid", exact: true }).click());
        const chartResponse = await runAndWaitForQuery(page, () =>
            page.getByRole("button", { name: "Chart", exact: true }).click());
        expect(modeOf(chartResponse.request().postDataJSON())).toBe("chart");
        await expect(page.getByRole("dialog")).toHaveCount(0);

        await page.reload();
        await expect(page.getByRole("table")).toBeVisible();
        const loadResponse = await runAndWaitForQuery(page, () =>
            page.getByRole("combobox", { name: "Saved Report" }).selectOption(savedId));
        expect(modeOf(loadResponse.request().postDataJSON())).toBe("pivot");
        await expect(page.getByRole("button", { name: "Pivot", exact: true })).toHaveClass(/ir-active/);

        const reloadedChart = await runAndWaitForQuery(page, () =>
            page.getByRole("button", { name: "Chart", exact: true }).click());
        expect(modeOf(reloadedChart.request().postDataJSON())).toBe("chart");
        await expect(page.getByRole("dialog")).toHaveCount(0);
    } finally {
        if (savedId) await request.delete(`/api/reports/saved/${savedId}`);
    }
});

test("deleting a computed column also deletes its references", async ({ page }) => {
    await openWorkbench(page);

    const computed = page.locator('.ir-chip[data-kind="computed"]');
    await expect(computed).toContainText("With Tax");
    const response = await runAndWaitForQuery(page, () =>
        computed.getByRole("button", { name: "Remove", exact: true }).click());
    const layer = sourceLayerOf(response.request().postDataJSON());

    expect(layer.computed).toEqual([]);
    expect(layer.columns).not.toContain("c1");
    expect(layer.formats).not.toHaveProperty("c1");
    await expect(page.getByText(/unknown column 'c1'/)).toHaveCount(0);
});

test("Save As confirms and replaces an existing report instead of creating a duplicate", async ({ page, request }) => {
    const title = `replace-${randomUUID()}`;

    try {
        await openWorkbench(page);
        await clickAction(page, "Save As…");
        let dialog = page.getByRole("dialog");
        await dialog.getByPlaceholder("Saved report name").fill(title);
        const createPromise = page.waitForResponse(response =>
            response.request().method() === "POST"
            && new URL(response.url()).pathname === "/api/reports/orders/saved");
        await dialog.getByRole("button", { name: "Save", exact: true }).click();
        const created = await createPromise;
        const savedId = (await created.json()).id;

        await runAndWaitForQuery(page, () =>
            page.getByRole("combobox", { name: "Saved Report" }).selectOption(""));
        await search(page, "Acme Corp");

        await clickAction(page, "Save As…");
        dialog = page.getByRole("dialog");
        await dialog.getByPlaceholder("Saved report name").fill(title.toUpperCase());
        await dialog.getByRole("button", { name: "Save", exact: true }).click();

        const confirmation = page.locator(".ir-dialog").filter({ hasText: "Replace Saved Report" });
        await expect(confirmation).toContainText(`Replace "${title}"?`);
        const replacePromise = page.waitForResponse(response =>
            response.request().method() === "PUT"
            && new URL(response.url()).pathname === `/api/reports/saved/${savedId}`);
        await confirmation.getByRole("button", { name: "Replace", exact: true }).click();
        const replaced = await replacePromise;
        expect(replaced.request().postDataJSON().state.search).toBe("Acme Corp");

        const visible = await request.get("/api/reports/orders/saved");
        const matches = (await visible.json()).filter(report =>
            report.title.toLocaleLowerCase() === title.toLocaleLowerCase());
        expect(matches).toHaveLength(1);
        expect(matches[0].id).toBe(savedId);
    } finally {
        const visible = await request.get("/api/reports/orders/saved");
        if (visible.ok()) {
            for (const report of await visible.json())
                if (report.title.toLocaleLowerCase() === title.toLocaleLowerCase())
                    await request.delete(`/api/reports/saved/${report.id}`);
        }
    }
});

test("saves and reloads a report, then administers its complete lifecycle", async ({ page, request }) => {
    const title = `e2e-${randomUUID()}`;
    let savedId;

    try {
        await openWorkbench(page);
        await search(page, "Acme Corp");

        await clickAction(page, "Save As…");
        const saveDialog = page.getByRole("dialog");
        await expect(saveDialog).toContainText("Save Report As");
        await saveDialog.getByPlaceholder("Saved report name").fill(title);

        const saveResponsePromise = page.waitForResponse(response =>
            response.request().method() === "POST"
            && new URL(response.url()).pathname === "/api/reports/orders/saved");
        await saveDialog.getByRole("button", { name: "Save", exact: true }).click();
        const saveResponse = await saveResponsePromise;
        expect(saveResponse.status()).toBe(201);
        savedId = (await saveResponse.json()).id;

        await expect(page.getByText("Report saved.", { exact: true })).toBeVisible();
        const savedSelect = page.getByRole("combobox", { name: "Saved Report" });
        await expect(savedSelect.locator(`option[value="${savedId}"]`)).toHaveText(title);

        await search(page, "Globex");
        await runAndWaitForQuery(page, () => savedSelect.selectOption(""));
        await expect(page.getByRole("searchbox", { name: "Search" })).toHaveValue("");
        await runAndWaitForQuery(page, () => page.locator("interactive-report").evaluate(
            (element, savedReport) => element.setAttribute("saved-report", savedReport), title));
        await expect(page.getByRole("searchbox", { name: "Search" })).toHaveValue("Acme Corp");
        await expect(savedSelect).toHaveValue(savedId);

        await page.reload();
        await expect(page.getByRole("table")).toBeVisible();
        await runAndWaitForQuery(page, () =>
            page.getByRole("combobox", { name: "Saved Report" }).selectOption(savedId));
        await expect(page.getByRole("searchbox", { name: "Search" })).toHaveValue("Acme Corp");

        await page.getByRole("link", { name: "Saved-report admin" }).click();
        await expect(page).toHaveURL(/\/admin\.html$/);
        const row = page.getByRole("row").filter({ hasText: title });
        await expect(row).toBeVisible();
        await expect(row).toContainText("workbench-dev");
        await expect(row).toContainText("Private");

        await row.getByRole("button", { name: "State", exact: true }).click();
        const stateDialog = page.getByRole("dialog");
        await expect(stateDialog.locator("pre")).toContainText('"search": "Acme Corp"');
        await stateDialog.getByText("Close", { exact: true }).click();

        const publishResponse = page.waitForResponse(response =>
            response.request().method() === "PUT"
            && new URL(response.url()).pathname === `/api/reports/saved/${savedId}`);
        await row.getByRole("button", { name: "Publish", exact: true }).click();
        expect((await publishResponse).ok()).toBe(true);
        await expect(row).toContainText("Global");

        await row.getByRole("button", { name: "Reassign…", exact: true }).click();
        const reassignDialog = page.getByRole("dialog");
        await reassignDialog.getByLabel("New owner (identity value)").fill("e2e-owner");
        const reassignResponse = page.waitForResponse(response =>
            response.request().method() === "PUT"
            && new URL(response.url()).pathname === `/api/reports/saved/${savedId}`);
        await reassignDialog.getByRole("button", { name: "Reassign", exact: true }).click();
        expect((await reassignResponse).ok()).toBe(true);
        await expect(row).toContainText("e2e-owner");

        await row.getByRole("button", { name: "Delete…", exact: true }).click();
        const deleteDialog = page.getByRole("dialog");
        const deleteResponse = page.waitForResponse(response =>
            response.request().method() === "DELETE"
            && new URL(response.url()).pathname === `/api/reports/saved/${savedId}`);
        await deleteDialog.getByRole("button", { name: "Delete", exact: true }).click();
        expect((await deleteResponse).status()).toBe(204);
        await expect(row).toBeHidden();
        savedId = undefined;
    } finally {
        if (savedId)
            await request.delete(`/api/reports/saved/${savedId}`);
    }
});

test("admin uploads a validated report document and downloads its canonical file", async ({ page, request }) => {
    const title = `document-${randomUUID()}`;
    let importedId;

    try {
        await page.goto("/admin.html");
        await expect(page.getByRole("button", { name: "Upload JSON…", exact: true })).toBeVisible();
        await page.getByRole("button", { name: "Upload JSON…", exact: true }).click();

        const dialog = page.getByRole("dialog");
        await dialog.getByLabel("Report name", { exact: true }).fill("orders");
        await dialog.getByLabel("Report document JSON", { exact: true }).setInputFiles({
            name: "candidate.json",
            mimeType: "application/json",
            buffer: Buffer.from(JSON.stringify({
                title,
                primary: true,
                state: {
                    v: 3,
                    pipeline: [{
                        shape: { kind: "source" },
                        layer: { filters: [{ expr: "AMOUNT > 100" }] },
                    }],
                },
            })),
        });

        const uploadResponsePromise = page.waitForResponse(response =>
            response.request().method() === "POST"
            && new URL(response.url()).pathname === "/api/reports/admin/orders/documents");
        await dialog.getByRole("button", { name: "Upload", exact: true }).click();
        const uploadResponse = await uploadResponsePromise;
        expect(uploadResponse.status()).toBe(201);
        importedId = (await uploadResponse.json()).id;

        const row = page.getByRole("row").filter({ hasText: title });
        await expect(row).toContainText("Private");

        const downloadPromise = page.waitForEvent("download");
        await row.getByRole("button", { name: "Download", exact: true }).click();
        const downloaded = await downloadPromise;
        expect(downloaded.suggestedFilename()).toMatch(/^orders\..+\.json$/);
        const document = JSON.parse(await readFile(await downloaded.path(), "utf8"));
        expect(document.title).toBe(title);
        expect(document.primary).toBe(false);
        expect(sourceLayerOf(document.state).filters).toEqual([{ expr: "AMOUNT > 100", enabled: true }]);
    } finally {
        if (importedId)
            await request.delete(`/api/reports/saved/${importedId}`);
    }
});

test("a feature-whitelisted report pares the UI down and the server enforces the rest", async ({ page, request }) => {
    await openWorkbench(page);
    await runAndWaitForQuery(page, () =>
        page.locator("interactive-report").evaluate(element => element.setAttribute("report", "orders-kiosk")));

    // search + sort + download survive; views, saved reports, and the rest are gone.
    await expect(page.getByRole("searchbox", { name: "Search" })).toBeVisible();
    await expect(page.getByRole("group", { name: "View" })).toBeHidden();
    await expect(page.getByRole("combobox", { name: "Saved Report" })).toBeHidden();
    await page.getByRole("button", { name: "Actions", exact: true }).click();
    await expect(page.getByRole("menuitem")).toHaveText(["Sort…", "Reset", "CSV"]);
    await page.keyboard.press("Escape");

    // The definition's default filter is visible but locked: no toggle, edit, or remove.
    await expect(page.locator(".ir-chip")).toHaveCount(1);
    await expect(page.locator(".ir-chip")).toContainText("AMOUNT > 100");
    await expect(page.locator(".ir-chip button, .ir-chip input")).toHaveCount(0);

    // Header menus offer only what survived.
    await page.getByRole("columnheader", { name: "Order #" }).click();
    await expect(page.getByRole("menuitem")).toHaveText(["Sort Ascending", "Sort Descending"]);
    await page.keyboard.press("Escape");

    // Server enforcement: saved-report creation is refused, download still works.
    const denied = await request.post("/api/reports/orders-kiosk/saved", {
        data: { title: "should not exist", state: {} },
    });
    expect(denied.status()).toBe(403);

    const downloadPromise = page.waitForEvent("download");
    await clickAction(page, "CSV");
    expect((await downloadPromise).suggestedFilename()).toBe("orders-kiosk.csv");
});

test("column settings restyle a column from the header menu", async ({ page }) => {
    await openWorkbench(page);

    await page.getByRole("columnheader", { name: "Amount", exact: true }).click();
    await page.getByRole("menuitem", { name: "Column Settings…", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("Column Settings");

    await expect(dialog.getByLabel("Column", { exact: true })).toHaveValue("AMOUNT");
    await dialog.getByLabel("Alignment", { exact: true }).selectOption("center");
    await dialog.getByLabel("Format Mask", { exact: true }).selectOption("integer");
    await dialog.getByRole("checkbox", { name: "Bold" }).check();

    await runAndWaitForQuery(page, () =>
        dialog.getByRole("button", { name: "Apply", exact: true }).click());

    const amountCell = page.getByRole("table").locator("tbody tr").first().locator("td").nth(4);
    await expect(amountCell).toHaveText(/^[\d,]+$/);
    await expect(amountCell).toHaveCSS("text-align", "center");
    await expect(amountCell).toHaveCSS("font-weight", "600");
    await expect(page.getByRole("columnheader", { name: "Amount", exact: true }))
        .toHaveCSS("text-align", "center");
});

test.describe("non-administrator", () => {
    test.use({ extraHTTPHeaders: { "X-Workbench-User": "ordinary-user" } });

    test("cannot probe a protected report and receives a precise admin denial", async ({ page }) => {
        await openWorkbench(page);
        await expect(page.locator("#identity")).toHaveText("ordinary-user");
        await expect(page.getByRole("combobox", { name: "Report", exact: true })).toHaveCount(0);
        const protectedResponse = await page.request.get("/api/reports/regional-summary/schema");
        expect(protectedResponse.status()).toBe(404);

        await page.goto("/admin.html");
        await expect(page.getByText(
            "Administrator access required. Add your identity to InteractiveReport:Administrators.",
            { exact: true })).toBeVisible();
    });
});
