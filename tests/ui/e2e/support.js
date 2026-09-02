import { randomUUID } from "node:crypto";
import { expect } from "@playwright/test";

const queryPath = /\/api\/reports\/[^/]+\/query$/;

export async function openWorkbench(page) {
    await page.goto("/");
    await expect(page.getByRole("table")).toBeVisible();
    await expect(page.getByRole("button", { name: "Actions", exact: true })).toBeEnabled();
    await expect(page.locator('interactive-report [part~="surface"]'))
        .toHaveAttribute("aria-busy", "false");
}

export async function waitForQuery(page, action, predicate = response => response.ok()) {
    const response = page.waitForResponse(candidate =>
        candidate.request().method() === "POST"
        && queryPath.test(new URL(candidate.url()).pathname)
        && predicate(candidate));
    await action();
    const matched = await response;
    await expect(page.locator('interactive-report [part~="surface"]'))
        .toHaveAttribute("aria-busy", "false");
    return matched;
}

export async function clickAction(page, ...names) {
    await page.getByRole("button", { name: "Actions", exact: true }).click();
    for (const name of names) await page.getByRole("menuitem", { name, exact: true }).click();
}

export async function reportId(request, reportName = "orders", options = {}) {
    const response = await request.get(`/api/reports/${encodeURIComponent(reportName)}`, options);
    if (!response.ok())
        throw new Error(`Could not list report documents (${response.status()}): ${await response.text()}`);
    const report = (await response.json()).find(candidate => candidate.isDefault);
    if (!report)
        throw new Error(`No visible default document exists for report '${reportName}'.`);
    return report.id;
}

export async function createSavedState(request, state, prefix = "composition") {
    const title = `${prefix}-${randomUUID()}`;
    const anchorId = await reportId(request);
    const response = await request.post(`/api/reports/${anchorId}/saved`, {
        data: { title, state },
    });
    if (response.status() !== 201)
        throw new Error(`Could not create saved report (${response.status()}): ${await response.text()}`);
    return { id: (await response.json()).id, title };
}

export async function deleteSavedState(request, saved) {
    if (!saved?.id) return;
    const response = await request.delete(`/api/reports/${saved.id}`);
    if (response.status() !== 204)
        throw new Error(`Could not delete saved report ${saved.id} (${response.status()}): ${await response.text()}`);
}

export async function loadSavedState(page, saved) {
    const response = waitForQuery(page, () =>
        page.getByRole("combobox", { name: "Saved Report" }).selectOption(String(saved.id)));
    return response;
}

export function visibleGridRows(page) {
    return page.getByRole("table").locator("tbody tr.ir-row");
}
