import { test, expect } from "@playwright/test";
import { openWorkbench, waitForQuery, visibleGridRows } from "./support.js";

const lovPath = /\/api\/reports\/[^/]+\/lov$/;

async function waitForLov(page, search, action) {
    const response = page.waitForResponse(candidate => {
        if (candidate.request().method() !== "POST"
            || !lovPath.test(new URL(candidate.url()).pathname)) return false;
        return candidate.request().postDataJSON()?.search === search && candidate.ok();
    });
    await action();
    return response;
}

test("filter LOV searches by substring and accepts an asterisk prefix filter", async ({ page }) => {
    await openWorkbench(page);

    const customerHeader = page
        .getByRole("columnheader", { name: "Customer Name", exact: true })
        .getByRole("button");
    await customerHeader.click();
    await waitForLov(page, "", () =>
        page.getByRole("menuitem", { name: "Filter by Value…", exact: true }).click());

    const dialog = page.getByRole("dialog", { name: "Customer Name Values", exact: true });
    const options = dialog.getByRole("option");
    await expect(options).toHaveCount(12);
    const allValues = await options.allTextContents();

    const search = dialog.getByRole("searchbox", { name: "Search values", exact: true });
    await waitForLov(page, "A", () => search.fill("A"));

    const expectedMatches = allValues.filter(value => value.toLocaleLowerCase().includes("a"));
    expect(expectedMatches.length).toBeGreaterThan(2);
    expect(expectedMatches.some(value => !value.toLocaleLowerCase().startsWith("a"))).toBe(true);
    await expect(options).toHaveText(expectedMatches);

    // In the accepted filter value, * is the public wildcard. A\* would instead
    // mean a literal asterisk, so this deliberately enters A*.
    await search.fill("A*");
    const query = await waitForQuery(page, () =>
        dialog.getByRole("button", { name: "Use Typed Value", exact: true }).click());
    const result = await query.json();

    const filter = result.document.tables.orders.composables
        .find(composable => composable.kind === "filter").filters.at(-1);
    expect(filter).toEqual({
        expr: "WILDCARD_MATCH(CUSTOMER, 'A*')",
        enabled: true,
    });
    expect(Number(result.totalRows)).toBeGreaterThan(0);
    expect(result.rows.every(row => row.CUSTOMER.toLocaleLowerCase().startsWith("a"))).toBe(true);

    const visibleCustomers = visibleGridRows(page).locator("td:nth-child(2)");
    await expect.poll(() => visibleCustomers.count()).toBeGreaterThan(0);
    expect((await visibleCustomers.allTextContents())
        .every(value => value.toLocaleLowerCase().startsWith("a"))).toBe(true);
});
