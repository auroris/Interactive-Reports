// Renders the labelled figures used by docs/USER-GUIDE.md from the live packaged UI, so the
// pictures always show the real component with its real stylesheet. `npm run build:screenshots`
// starts and stops Workbench automatically. Direct invocation requires a running Workbench
// (default http://127.0.0.1:5042; override with IR_DOCS_BASE) and the Playwright Chromium that
// `npm install` already provides:
//
//   dotnet run --project samples/Workbench --urls http://127.0.0.1:5042
//   node scripts/docs-screenshots.mjs
//
// Output: docs/images/*.png at 2x device pixels.

import { chromium } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import path from "node:path";

const base = process.env.IR_DOCS_BASE ?? "http://127.0.0.1:5042";
const outDir = path.resolve("docs/images");
await mkdir(outDir, { recursive: true });

const browser = await chromium.launch();
// Starting width only: fitToolbar() widens the window until the toolbar sits on one row.
const page = await browser.newPage({
    viewport: { width: Number(process.env.IR_DOCS_WIDTH ?? 1000), height: 760 },
    deviceScaleFactor: 2,
});

/** Waits until the packaged report has rendered at least one data row. */
const waitForRows = () => page.waitForFunction(() => {
    const root = document.querySelector("interactive-report")?.shadowRoot;
    return root?.querySelector(".ir-table tbody tr") && root.host.getAttribute("aria-busy") !== "true";
}, null, { timeout: 30_000 });

/**
 * Draws numbered callout badges over shadow-root elements and returns the union of their
 * rectangles plus the badge overflow, in viewport pixels.
 *
 * @param {Array<{selector: string, index?: number, at?: "tl"|"tr"|"bl"}>} targets - Elements inside the report's shadow root, in label order.
 */
const label = targets => page.evaluate(targets => {
    const root = document.querySelector("interactive-report").shadowRoot;
    document.querySelectorAll(".docs-badge").forEach(node => node.remove());
    let union = null;
    targets.forEach((target, i) => {
        const nodes = root.querySelectorAll(target.selector);
        const node = nodes[target.index ?? 0];
        if (!node) throw new Error(`No element for ${target.selector}`);
        const rect = node.getBoundingClientRect();
        const badge = document.createElement("div");
        badge.className = "docs-badge";
        badge.textContent = String(i + 1);
        const size = 22;
        const x = target.at === "tr" ? rect.right - size / 2 : rect.left - size / 2;
        const y = target.at === "bl" ? rect.bottom - size / 2 : rect.top - size / 2;
        Object.assign(badge.style, {
            position: "fixed", left: `${x}px`, top: `${y}px`, width: `${size}px`, height: `${size}px`,
            borderRadius: "50%", background: "#d6336c", color: "#fff", font: "700 12px/22px system-ui, sans-serif",
            textAlign: "center", boxShadow: "0 0 0 2px #fff, 0 2px 6px rgba(0,0,0,.35)", zIndex: 2147483647,
            pointerEvents: "none",
        });
        document.body.append(badge);
        const box = { left: x, top: y, right: Math.max(x + size, rect.right), bottom: Math.max(y + size, rect.bottom) };
        union = union ? {
            left: Math.min(union.left, box.left), top: Math.min(union.top, box.top),
            right: Math.max(union.right, box.right), bottom: Math.max(union.bottom, box.bottom),
        } : box;
    });
    return union;
}, targets);

/** Screenshots the host element region (or a supplied box) with padding. */
async function shoot(name, box, pad = 12, padBottom = pad) {
    const host = await page.evaluate(() => {
        const rect = document.querySelector("interactive-report").getBoundingClientRect();
        return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom };
    });
    const b = box ?? host;
    const clip = {
        x: Math.max(0, Math.min(b.left, host.left) - pad),
        y: Math.max(0, b.top - pad),
        width: Math.max(b.right, host.right) - Math.min(b.left, host.left) + pad * 2,
        height: b.bottom - b.top + pad + padBottom,
    };
    await page.screenshot({ path: path.join(outDir, name), clip });
    await page.evaluate(() => document.querySelectorAll(".docs-badge").forEach(node => node.remove()));
    console.log(`wrote docs/images/${name}`);
}

/**
 * The toolbar wraps onto a second row when the window is narrow, which would scatter the
 * numbered callouts. Widen the viewport in steps until every visible control shares one row.
 */
async function fitToolbar() {
    for (let width = page.viewportSize().width; width <= 1800; width += 150) {
        await page.setViewportSize({ width, height: page.viewportSize().height });
        const oneRow = await page.evaluate(() => {
            const toolbar = document.querySelector("interactive-report").shadowRoot.querySelector(".ir-toolbar");
            // Compare row centres: the flex spacer has no height, so its top is meaningless.
            const centres = [...toolbar.children]
                .filter(node => !node.hidden && !node.classList.contains("ir-spacer"))
                .map(node => { const r = node.getBoundingClientRect(); return r.top + r.height / 2; });
            return Math.max(...centres) - Math.min(...centres) < 4;
        });
        if (oneRow) return;
    }
    throw new Error("The toolbar still wraps at 1800px; check the host page layout.");
}

const open = async (report, ready = waitForRows) => {
    await page.goto(`${base}/api/reports/${report}/view`);
    await ready();
    await fitToolbar();
};

// 1. Toolbar with every control visible.
await open("orders");
const toolbarBox = await label([
    { selector: ".ir-search-scope" },
    { selector: ".ir-search-input" },
    { selector: ".ir-go" },
    { selector: ".ir-viewbtns" },
    { selector: ".ir-actionsbtn" },
    { selector: ".ir-saved" },
    { selector: ".ir-helpbtn" },
]);
{
    const toolbar = await page.evaluate(() => {
        const rect = document.querySelector("interactive-report").shadowRoot.querySelector(".ir-toolbar").getBoundingClientRect();
        return { top: rect.top, bottom: rect.bottom };
    });
    // Stop at the toolbar's bottom border so the chip strip beneath does not peek in.
    await shoot("toolbar.png", { ...toolbarBox, top: Math.min(toolbarBox.top, toolbar.top), bottom: toolbar.bottom }, 14, 0);
}

// 2. Actions menu, opened.
await page.locator("interactive-report .ir-actionsbtn").click();
await page.locator("interactive-report .ir-popup").waitFor();
{
    const menu = await page.evaluate(() => {
        const rect = document.querySelector("interactive-report").shadowRoot.querySelector(".ir-popup").getBoundingClientRect();
        return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom };
    });
    const toolbar = await page.evaluate(() => {
        const rect = document.querySelector("interactive-report").shadowRoot.querySelector(".ir-toolbar").getBoundingClientRect();
        return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom };
    });
    await shoot("actions-menu.png", {
        left: Math.min(menu.left, toolbar.left), top: toolbar.top,
        right: Math.max(menu.right, toolbar.right), bottom: menu.bottom,
    });
    await page.keyboard.press("Escape");
}

// 3. Column header menu, opened on the Amount column.
{
    await page.locator("interactive-report .ir-th-button", { hasText: "Amount" }).first().click();
    await page.locator("interactive-report .ir-popup").waitFor();
    const box = await page.evaluate(() => {
        const root = document.querySelector("interactive-report").shadowRoot;
        const head = root.querySelector(".ir-table thead").getBoundingClientRect();
        const menu = root.querySelector(".ir-popup").getBoundingClientRect();
        return {
            left: Math.min(head.left, menu.left), top: head.top,
            right: Math.max(head.right, menu.right), bottom: menu.bottom,
        };
    });
    await shoot("header-menu.png", box);
    await page.keyboard.press("Escape");
}

// 4. Report anatomy: a report with breaks, aggregates, a filter, a highlight, and a search.
await open("big-orders");
await page.evaluate(async () => {
    const report = document.querySelector("interactive-report");
    const doc = report.getReportDocument();
    const table = doc.tables[doc.activeTable];
    table.composables.push(
        // Breaks sort by REGION, so EAST leads; keep that group short enough to close on page 1.
        { kind: "filter", filters: [{ expr: "REGION <> 'EAST' OR AMOUNT > 24000", enabled: true }] },
        { kind: "highlight", highlights: [{ id: "h1", name: "Acme", sequence: 10, enabled: true, scope: "row", expr: "CUSTOMER = 'Acme Corp'", style: { bg: "#fff3cd" } }] });
    doc.search = "corp";
    doc.page = { index: 1, size: 10 };
    await report.submitReportDocument(doc);
});
await waitForRows();
const anatomy = await label([
    { selector: ".ir-toolbar" },
    { selector: ".ir-chips" },
    { selector: ".ir-table thead" },
    { selector: ".ir-break-header" },
    { selector: ".ir-break-total" },
    { selector: ".ir-pager" },
]);
await shoot("report-anatomy.png", anatomy);

// 5. README hero: an unlabelled report showing the main surfaces at once.
await open("orders");
await page.evaluate(async () => {
    const report = document.querySelector("interactive-report");
    const doc = report.getReportDocument();
    const table = doc.tables[doc.activeTable];
    table.composables.push(
        { kind: "filter", filters: [{ expr: "STATUS <> 'CANCELLED'", enabled: true }] },
        { kind: "break", breaks: ["REGION"] },
        { kind: "aggregate", aggregates: [{ col: "AMOUNT", fn: "sum" }] },
        { kind: "highlight", highlights: [{ id: "h1", name: "Large order", sequence: 10, enabled: true, scope: "cell", col: "AMOUNT", expr: "AMOUNT > 20000", style: { bg: "#e9f5ea", fg: "#1e5b24" } }] });
    doc.page = { index: 1, size: 12 };
    await report.submitReportDocument(doc);
});
await waitForRows();
await shoot("hero.png");

// 6. Chart view of the same data.
await open("orders");
await page.evaluate(async () => {
    const report = document.querySelector("interactive-report");
    const doc = report.getReportDocument();
    doc.tables.chart = {
        from: doc.activeTable, schema: null,
        composables: [{ kind: "chart", type: "bar", label: "REGION", value: "AMOUNT", fn: "sum", sort: { by: "value", dir: "desc" }, orientation: "vertical" }],
    };
    doc.activeTable = "chart";
    await report.submitReportDocument(doc);
});
await page.waitForFunction(() => {
    const root = document.querySelector("interactive-report")?.shadowRoot;
    return root?.querySelector("canvas") && root.host.getAttribute("aria-busy") !== "true";
}, null, { timeout: 30_000 });
await page.waitForTimeout(600);
await shoot("chart-view.png");

await browser.close();
