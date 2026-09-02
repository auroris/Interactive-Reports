import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { Window } from "happy-dom";
import { readTheme } from "../../src/client/chart/theme.js";

const window = new Window({ url: "https://host.example/dashboard" });
Object.assign(globalThis, {
    window,
    document: window.document,
    HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot,
    customElements: window.customElements,
    Node: window.Node,
    getComputedStyle: window.getComputedStyle.bind(window),
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

// Import the built client bundles (which bundle ir.css via esbuild)
await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir.js");
await import("../../src/InteractiveReport.Client.Json/Ui/dist/ir-admin.js");

const irCssPath = path.resolve("src/client/ir.css");
const irCss = fs.readFileSync(irCssPath, "utf8");

test("ir.css contains complete light and dark theme declarations", () => {
    // 1. Host defaults
    assert.match(irCss, /:host\s*\{[^}]*color-scheme:\s*light;/);
    assert.match(irCss, /--ir-bg:\s*#ffffff;/);
    assert.match(irCss, /--ir-text:\s*#1f2733;/);
    assert.match(irCss, /--ir-control-border:\s*#c3cbd3;/);

    // 2. Explicit dark mode triggers on host
    assert.match(irCss, /:host\(\[theme="dark"\]\)/);
    assert.match(irCss, /:host\(\[data-theme="dark"\]\)/);
    assert.match(irCss, /:host\(\.dark\)/);
    assert.match(irCss, /:host\(\[dark\]\)/);

    // 3. Ancestor / framework context dark mode
    assert.match(irCss, /:host-context\(\[data-theme="dark"\]\)/);
    assert.match(irCss, /:host-context\(\[data-bs-theme="dark"\]\)/);
    assert.match(irCss, /:host-context\(\.dark\)/);

    // 4. System preference media query
    assert.match(irCss, /@media\s*\(prefers-color-scheme:\s*dark\)/);

    // 5. Dark mode tokens
    assert.match(irCss, /--ir-bg:\s*#1e2227;/);
    assert.match(irCss, /--ir-bg-soft:\s*#252a30;/);
    assert.match(irCss, /--ir-text:\s*#f0f3f6;/);
    assert.match(irCss, /--ir-border:\s*#383f48;/);
    assert.match(irCss, /--ir-accent:\s*#3898ec;/);
    assert.match(irCss, /--ir-chart-1:\s*#3898ec;/);

    // 6. Explicit light mode overrides
    assert.match(irCss, /:host\(\[theme="light"\]\)/);
    assert.match(irCss, /:host-context\(\[data-theme="light"\]\)/);
});

test("ir.css component elements use theme variables instead of hardcoded light colors", () => {
    // Buttons
    assert.match(irCss, /\.ir-btn\s*\{[^}]*border:\s*1px solid var\(--ir-control-border\);/);
    assert.match(irCss, /\.ir-btn:hover:not\(:disabled\)\s*\{\s*background:\s*var\(--ir-btn-hover\);/);
    assert.match(irCss, /\.ir-btn-primary:hover:not\(:disabled\)\s*\{\s*background:\s*var\(--ir-btn-primary-hover\);/);
    assert.match(irCss, /\.ir-btn-danger:hover:not\(:disabled\)\s*\{\s*background:\s*var\(--ir-btn-danger-hover\);/);

    // Form inputs
    assert.match(irCss, /\.ir-input,\s*\.ir-select,\s*\.ir-textarea\s*\{[^}]*border:\s*1px solid var\(--ir-control-border\);/);
    assert.match(irCss, /\.ir-search-input\s*\{[^}]*border:\s*1px solid var\(--ir-control-border\);/);

    // Banners
    assert.match(irCss, /\.ir-banner-error\s*\{[^}]*background:\s*var\(--ir-banner-error-bg\);/);
    assert.match(irCss, /\.ir-banner-warn\s*\{[^}]*background:\s*var\(--ir-banner-warn-bg\);/);
    assert.match(irCss, /\.ir-banner-ok\s*\{[^}]*background:\s*var\(--ir-banner-ok-bg\);/);

    // Chips
    assert.match(irCss, /\.ir-chips\s*\{[^}]*background:\s*var\(--ir-chips-bg\);/);
    assert.match(irCss, /\.ir-chip\s*\{[^}]*border:\s*1px solid var\(--ir-chip-border\);/);

    // Table rows and headers
    assert.match(irCss, /\.ir-table th\s*\{[^}]*color:\s*var\(--ir-table-th-color\);/);
    assert.match(irCss, /\.ir-th-button:hover\s*\{[^}]*background:\s*var\(--ir-th-hover\);/);
    assert.match(irCss, /\.ir-table td\s*\{[^}]*border-bottom:\s*1px solid var\(--ir-row-border\);/);
    assert.match(irCss, /\.ir-table tr\.ir-row:hover td\s*\{\s*background-color:\s*var\(--ir-row-hover\);/);

    // Break headings and totals
    assert.match(irCss, /\.ir-table tr\.ir-break-header td\s*\{[^}]*background:\s*var\(--ir-break-bg\);/);
    assert.match(irCss, /\.ir-table tr\.ir-break-total td\s*\{[^}]*background:\s*var\(--ir-break-total-bg\);/);
    assert.match(irCss, /\.ir-table tr\.ir-grand-total td\s*\{[^}]*background:\s*var\(--ir-grand-total-bg\);/);

    // Popups and dialogs
    assert.match(irCss, /\.ir-popup\s*\{[^}]*border:\s*1px solid var\(--ir-popup-border\);/);
    assert.match(irCss, /\.ir-dialog\s*\{[^}]*border:\s*1px solid var\(--ir-dialog-border\);/);
    assert.match(irCss, /dialog\.ir-dialog::backdrop\s*\{\s*background:\s*var\(--ir-dialog-backdrop\);/);

    // Shuttle and tokens
    assert.match(irCss, /\.ir-shuttle-list\s*\{[^}]*border:\s*1px solid var\(--ir-control-border\);/);
    assert.match(irCss, /\.ir-token\s*\{[^}]*background:\s*var\(--ir-token-bg\);/);
});

test("InteractiveReportElement supports the theme attribute and property", () => {
    const ir = window.document.createElement("interactive-report");
    assert.equal(ir.theme, null);

    ir.theme = "dark";
    assert.equal(ir.getAttribute("theme"), "dark");
    assert.equal(ir.theme, "dark");

    ir.setAttribute("theme", "light");
    assert.equal(ir.theme, "light");

    ir.theme = null;
    assert.equal(ir.hasAttribute("theme"), false);
    assert.equal(ir.theme, null);
});

test("InteractiveReportAdminElement supports the theme attribute and property", () => {
    const admin = window.document.createElement("interactive-report-admin");
    assert.equal(admin.theme, null);

    admin.theme = "dark";
    assert.equal(admin.getAttribute("theme"), "dark");
    assert.equal(admin.theme, "dark");

    admin.theme = null;
    assert.equal(admin.hasAttribute("theme"), false);
});

test("readTheme extracts dark mode custom properties from element styles", () => {
    const origGcs = globalThis.getComputedStyle;
    const tokens = {
        "--ir-chart-1": "#3898ec",
        "--ir-chart-2": "#f07d4f",
        "--ir-chart-3": "#27c48d",
        "--ir-chart-4": "#f5b324",
        "--ir-chart-5": "#f093b6",
        "--ir-chart-6": "#34a853",
        "--ir-chart-7": "#8a7ff0",
        "--ir-chart-8": "#f06463",
        "--ir-chart-grid": "#2c323a",
        "--ir-chart-text": "#9aa4af",
        "--ir-bg": "#1e2227",
    };
    globalThis.getComputedStyle = () => ({
        getPropertyValue: prop => tokens[prop] ?? "",
    });
    try {
        const fakeCanvas = window.document.createElement("canvas");
        const theme = readTheme(fakeCanvas);
        assert.equal(theme.palette[0], "#3898ec");
        assert.equal(theme.palette[1], "#f07d4f");
        assert.equal(theme.grid, "#2c323a");
        assert.equal(theme.text, "#9aa4af");
        assert.equal(theme.surface, "#1e2227");
    } finally {
        globalThis.getComputedStyle = origGcs;
    }
});
