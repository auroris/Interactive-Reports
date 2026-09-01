import { test, expect } from "@playwright/test";
import { reportId } from "./support.js";

// The packaged pages against the live Workbench: the viewer shell renders a full
// report (script URL inferred from the prefix — the page carries no api-base), the
// dialect-less and dataSource-configured reports resolve end to end, and the admin
// shell hosts the saved-report listing for the dev administrator identity.

test("the packaged viewer page serves a working report", async ({ page, request }) => {
    await reportId(request, "orders");
    await page.goto("/api/reports/orders/view");

    await expect(page).toHaveTitle("orders");
    await expect(page.getByRole("table")).toBeVisible();
    await expect(page.getByRole("button", { name: "Actions", exact: true })).toBeEnabled();
    const rows = page.getByRole("table").locator("tbody tr.ir-row");
    await expect.poll(() => rows.count()).toBeGreaterThan(10);
    await expect(page.getByText("1 – 50 of 500 rows", { exact: true })).toBeVisible();
});

test("the Workbench exposes a Canadian French report page", async ({ page }) => {
    await page.goto("/fr.html");

    await expect(page).toHaveTitle("InteractiveReport — Atelier français");
    await expect(page.locator("html")).toHaveAttribute("lang", "fr-CA");
    await expect(page.locator("interactive-report")).not.toHaveAttribute("lang");
    await expect(page.getByRole("searchbox", { name: "Rechercher" })).toBeVisible();
    await expect(page.getByText("1 à 50 sur 500 lignes", { exact: true })).toBeVisible();

    await page.getByRole("button", { name: "Actions", exact: true }).click();
    await expect(page.getByRole("menuitem", { name: "Colonnes…", exact: true })).toBeVisible();
});

test("a dataSource-configured report with no dialect loads through the packaged page", async ({ page, request }) => {
    await reportId(request, "order-feed");
    await page.goto("/api/reports/order-feed/view");

    await expect(page.getByRole("columnheader", { name: "Order #", exact: true })).toBeVisible();
    await expect(page.getByText("1 – 50 of 500 rows", { exact: true })).toBeVisible();
});

test("the packaged admin page hosts the saved-report listing", async ({ page }) => {
    await page.goto("/api/reports/admin");

    await expect(page).toHaveTitle("Saved report administration");
    await expect(page.getByText("Signed in as workbench-dev", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Upload JSON…", exact: true })).toBeVisible();
    await expect(page.getByRole("table")).toBeVisible();
});
