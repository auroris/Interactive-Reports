import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { test, expect } from "@playwright/test";

const queryPath = /\/api\/reports\/[^/]+\/query$/;

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

test("loads the synthetic default report, queries data, searches, pages, and changes the configured report", async ({ page }) => {
    await openWorkbench(page);

    const catalogResponse = await page.request.get("/api/reports");
    expect(catalogResponse.status()).toBe(404);
    await expect(page.locator("#identity")).toHaveText("workbench-dev · admin");
    await expect(page.getByRole("combobox", { name: "Report", exact: true })).toHaveCount(0);
    await expect(page.getByRole("columnheader")).toHaveText([
        "Order #", "Customer Name", "Region", "Status", "Amount", "Ordered On▼", "Notes", "With Tax",
    ]);
    await expect(page.getByText("1 – 50 of 500 rows", { exact: true })).toBeVisible();

    await search(page, "Acme Corp");
    const filteredRows = page.getByRole("table").locator("tbody tr");
    await expect.poll(() => filteredRows.count()).toBeGreaterThan(15);
    await expect.poll(() => filteredRows.evaluateAll(rows =>
        rows.every(row => row.textContent.includes("Acme Corp")))).toBe(true);

    await runAndWaitForQuery(page, () =>
        page.getByRole("combobox", { name: "Rows per page" }).selectOption("15"));
    await expect(filteredRows).toHaveCount(15);
    await expect(page.getByText(/^1 – 15 of \d+ rows$/)).toBeVisible();

    await runAndWaitForQuery(page, () =>
        page.getByRole("button", { name: "Next page" }).click());
    await expect(page.getByText(/^16 – 30 of \d+ rows$/)).toBeVisible();

    await runAndWaitForQuery(page, () =>
        page.locator("interactive-report").evaluate(element => element.setAttribute("report", "order-feed")));
    await expect(page.getByRole("columnheader")).toHaveText(["Order #", "Customer", "Amount"]);
    await expect(page.getByRole("searchbox", { name: "Search" })).toHaveValue("");
    await expect(page.getByText("1 – 50 of 500 rows", { exact: true })).toBeVisible();
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
