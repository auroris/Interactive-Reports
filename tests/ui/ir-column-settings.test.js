import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";

const window = new Window({ url: "https://host.example/dashboard" });
function Option(text = "", value = "", defaultSelected = false, selected = false) {
    const option = window.document.createElement("option");
    option.text = text;
    option.value = value;
    option.defaultSelected = defaultSelected;
    option.selected = selected;
    return option;
}
Object.assign(globalThis, {
    window,
    document: window.document,
    HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot,
    customElements: window.customElements,
    Option,
    Node: window.Node,
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

const FEATURES = {
    nocheckbox: ["columnSettings"],
};

const requests = [];
const json = value => new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
});
const ALL_COLUMNS = [
    { name: "ID", label: "ID", type: "number" },
    { name: "NAME", label: "Name", type: "text" },
];
const ROW = { ID: 1234.5, NAME: "x" };

globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET", body: options.body });
    const report = /\/([^/]+)\/(schema|query|saved)$/.exec(String(url))?.[1];
    if (String(url).endsWith("/schema")) {
        return json({
            stateVersion: 2,
            defaultState: { page: { index: 1, size: 25 }, view: { mode: "grid" } },
            limits: { defaultPageSize: 25, maxPageSize: 100 },
            columns: ALL_COLUMNS,
            capabilities: { aggregateFunctions: {}, expressionFunctions: [] },
            ...(FEATURES[report] ? { features: FEATURES[report] } : {}),
        });
    }
    if (String(url).endsWith("/whoami")) return json({ identity: "test-user" });
    if (String(url).endsWith("/saved")) return json([]);
    if (String(url).endsWith("/query")) {
        // Honor the posted visible-columns list so renders reflect visibility edits.
        const doc = options.body ? JSON.parse(options.body) : {};
        const visible = doc.columns?.length
            ? ALL_COLUMNS.filter(c => doc.columns.includes(c.name))
            : ALL_COLUMNS;
        return json({
            columns: visible,
            rows: [Object.fromEntries(visible.map(c => [c.name, ROW[c.name]]))],
            page: { index: 1, size: 25 },
            totalRows: 1,
            aggregates: {},
            highlights: [],
            ignored: [],
        });
    }
    return new Response(null, { status: 404 });
};

await import("../../src/InteractiveReport.AspNetCore/Ui/dist/ir.js");

const settle = async condition => {
    for (let attempt = 0; attempt < 40 && !condition(); attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
};

async function mount(reportName) {
    const report = document.createElement("interactive-report");
    report.setAttribute("report", reportName);
    report.setAttribute("api-base", "/settings-api");
    document.body.append(report);
    await settle(() => report.shadowRoot?.querySelector("tbody tr"));
    return report;
}

const clickMenuItem = (report, label) => [...report.shadowRoot.querySelectorAll(".ir-menu-item")]
    .find(item => item.textContent.includes(label))
    .click();

const applyDialog = async report => {
    report.shadowRoot.querySelector(".ir-dialog .ir-btn-primary").click();
    await settle(() => !report.shadowRoot.querySelector(".ir-dialog"));
    assert.equal(!report.shadowRoot.querySelector(".ir-dialog"), true, "the dialog should close on success");
    return JSON.parse(requests.filter(r => r.url.endsWith("/query")).at(-1).body);
};

test("column settings write doc.formats and the grid renders mask, alignment, and style", async () => {
    requests.length = 0;
    const report = await mount("orders");

    report.shadowRoot.querySelector("th.ir-th-menu").click();
    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    assert.equal(!!dialog, true);

    const [colSel, alignSel, maskSel] = dialog.querySelectorAll("select");
    assert.equal(colSel.value, "ID", "the invoking column is preselected");
    alignSel.value = "center";
    maskSel.value = "integer";
    maskSel.dispatchEvent(new window.Event("input", { bubbles: true }));
    const boldChk = dialog.querySelectorAll('input[type="checkbox"]')[1];
    boldChk.checked = true;
    const classesInp = dialog.querySelector('input[type="text"]');
    classesInp.value = "amount-column emphasized";
    classesInp.dispatchEvent(new window.Event("input", { bubbles: true }));

    assert.equal(dialog.querySelector(".ir-format-preview").textContent, "1,235",
        "the preview shows the masked sample value");
    assert.equal(dialog.querySelector(".ir-format-preview").classList.contains("amount-column"), true,
        "custom classes participate in the live preview");

    const doc = await applyDialog(report);
    assert.deepEqual(doc.formats, {
        ID: {
            mask: "integer", align: "center", bold: true,
            classes: ["amount-column", "emphasized"],
        },
    });

    const th = report.shadowRoot.querySelector("th");
    const td = report.shadowRoot.querySelector("tbody tr td");
    assert.equal(td.textContent, "1,235");
    assert.equal(td.style.textAlign, "center");
    assert.equal(td.style.fontWeight, "600");
    assert.equal(th.style.textAlign, "center");
    assert.equal(th.classList.contains("amount-column"), true);
    assert.equal(td.classList.contains("emphasized"), true);

    report.remove();
});

test("column settings reject component-reserved CSS classes", async () => {
    requests.length = 0;
    const report = await mount("orders");

    report.shadowRoot.querySelector("th.ir-th-menu").click();
    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    const classesInp = dialog.querySelector('input[type="text"]');
    classesInp.value = "ir-empty";
    dialog.querySelector(".ir-btn-primary").click();

    await settle(() => !dialog.querySelector(".ir-dialog-error").hidden);
    assert.match(dialog.querySelector(".ir-dialog-error").textContent, /invalid or reserved/i);
    assert.equal(!!report.shadowRoot.querySelector(".ir-dialog"), true,
        "invalid class input keeps the dialog open");

    report.remove();
});

test("the visible checkbox edits doc.columns: hiding removes, re-showing appends to the end", async () => {
    requests.length = 0;
    const report = await mount("orders");

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    clickMenuItem(report, "Column Settings");
    let dialog = report.shadowRoot.querySelector(".ir-dialog");
    const visChk = () => dialog.querySelectorAll('input[type="checkbox"]')[0];
    assert.equal(visChk().checked, true);
    visChk().checked = false;

    let doc = await applyDialog(report);
    assert.deepEqual(doc.columns, ["NAME"], "unchecking hides the column (no second source of truth)");
    await settle(() => report.shadowRoot.querySelectorAll("th").length === 1);
    assert.equal(report.shadowRoot.querySelectorAll("th").length, 1);

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    clickMenuItem(report, "Column Settings");
    dialog = report.shadowRoot.querySelector(".ir-dialog");
    const colSel = dialog.querySelector("select");
    colSel.value = "ID";
    colSel.dispatchEvent(new window.Event("change", { bubbles: true }));
    assert.equal(visChk().checked, false, "the hidden column loads as not visible");
    visChk().checked = true;

    doc = await applyDialog(report);
    assert.deepEqual(doc.columns, ["NAME", "ID"], "re-shown columns append to the end");

    report.remove();
});

test("without the columns feature the dialog offers no visibility checkbox", async () => {
    requests.length = 0;
    const report = await mount("nocheckbox");

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    const labels = [...report.shadowRoot.querySelectorAll(".ir-popup .ir-menu-item, .ir-popup .ir-menu-heading")]
        .map(item => item.textContent.replace("✓", "").trim());
    assert.deepEqual(labels, ["Column Settings…", "Report", "Reset"]);

    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    // bold, italic, text color, background — but no Visible checkbox.
    assert.equal(dialog.querySelectorAll('input[type="checkbox"]').length, 4);

    report.remove();
});
