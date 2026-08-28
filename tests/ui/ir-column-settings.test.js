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
    { name: "URL", label: "URL", type: "text" },
    { name: "IMAGE_URL", label: "Image URL", type: "text" },
    { name: "c1", label: "Computed URL", type: "text", computed: true },
];
const ROW = {
    ID: 1234.5,
    NAME: "Example",
    URL: "/customers/42",
    IMAGE_URL: "https://images.example/42.png",
    c1: "/computed/42",
};

globalThis.fetch = async (url, options = {}) => {
    requests.push({ url: String(url), method: options.method ?? "GET", body: options.body });
    const report = /\/([^/]+)\/(schema|query|saved)$/.exec(String(url))?.[1];
    if (String(url).endsWith("/schema")) {
        return json({
            stateVersion: 3,
            defaultState: {
                v: 3,
                pipeline: [{ shape: { kind: "source" }, layer: { columns: ["ID", "NAME"] } }],
                page: { index: 1, size: 25 },
            },
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
        const layer = doc.pipeline?.[0]?.layer ?? {};
        const visible = layer.columns?.length
            ? ALL_COLUMNS.filter(c => layer.columns.includes(c.name))
            : ALL_COLUMNS.filter(c => ["ID", "NAME"].includes(c.name));
        const projected = [...visible];
        for (const [name, format] of Object.entries(layer.formats ?? {})) {
            if (!visible.some(c => c.name === name) || !["link", "image"].includes(format.displayAs)) continue;
            for (const source of [format.urlColumn, format.displayAs === "link" ? format.textColumn : null]) {
                const column = ALL_COLUMNS.find(c => c.name === source);
                if (column && !projected.includes(column)) projected.push(column);
            }
        }
        return json({
            availableColumns: ALL_COLUMNS,
            columns: visible,
            rows: [Object.fromEntries(projected.map(c => [c.name, ROW[c.name]]))],
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

const fieldControl = (dialog, label) => [...dialog.querySelectorAll(".ir-field")]
    .find(field => field.querySelector(":scope > .ir-field-label")?.textContent === label)
    ?.querySelector("input, select");

const applyDialog = async report => {
    report.shadowRoot.querySelector(".ir-dialog .ir-btn-primary").click();
    await settle(() => !report.shadowRoot.querySelector(".ir-dialog"));
    assert.equal(!report.shadowRoot.querySelector(".ir-dialog"), true, "the dialog should close on success");
    const doc = JSON.parse(requests.filter(r => r.url.endsWith("/query")).at(-1).body);
    // Grid presentation lives on the posted pipeline's source layer.
    return doc.pipeline?.[0]?.layer ?? {};
};

test("column settings write doc.formats and the grid renders mask, alignment, and style", async () => {
    requests.length = 0;
    const report = await mount("orders");

    report.shadowRoot.querySelector("th.ir-th-menu .ir-th-button").click();
    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    assert.equal(!!dialog, true);

    const colSel = fieldControl(dialog, "Column");
    const alignSel = fieldControl(dialog, "Alignment");
    const maskSel = fieldControl(dialog, "Format Mask");
    assert.equal(colSel.value, "ID", "the invoking column is preselected");
    assert.ok([...maskSel.options].some(option => option.value === "currency:CAD"));
    assert.ok([...maskSel.options].some(option => option.value === "percent2"));
    assert.ok([...maskSel.options].some(option => option.value === "decimal3"));
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

test("column settings configure a link renderer with hidden URL and visible text columns", async () => {
    requests.length = 0;
    const report = await mount("links");

    report.shadowRoot.querySelector("th.ir-th-menu .ir-th-button").click();
    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    const column = fieldControl(dialog, "Column");
    column.value = "NAME";
    column.dispatchEvent(new window.Event("change", { bubbles: true }));

    const displayAs = fieldControl(dialog, "Display As");
    displayAs.value = "link";
    displayAs.dispatchEvent(new window.Event("input", { bubbles: true }));
    fieldControl(dialog, "URL Column").value = "URL";
    fieldControl(dialog, "Link Text Column").value = "NAME";

    const doc = await applyDialog(report);
    assert.deepEqual(doc.formats, {
        NAME: { displayAs: "link", urlColumn: "URL", textColumn: "NAME" },
    });

    const headers = report.shadowRoot.querySelectorAll("th");
    const link = report.shadowRoot.querySelectorAll("tbody tr td")[1].querySelector("a.ir-cell-link");
    assert.equal(headers.length, 2, "the URL source remains hidden");
    assert.equal(link.textContent, "Example");
    assert.equal(link.getAttribute("href"), "/customers/42");

    report.remove();
});

test("column settings configure an image renderer", async () => {
    requests.length = 0;
    const report = await mount("images");

    report.shadowRoot.querySelector("th.ir-th-menu .ir-th-button").click();
    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    const column = fieldControl(dialog, "Column");
    column.value = "NAME";
    column.dispatchEvent(new window.Event("change", { bubbles: true }));
    const displayAs = fieldControl(dialog, "Display As");
    displayAs.value = "image";
    displayAs.dispatchEvent(new window.Event("input", { bubbles: true }));
    fieldControl(dialog, "URL Column").value = "IMAGE_URL";

    const doc = await applyDialog(report);
    assert.deepEqual(doc.formats, {
        NAME: { displayAs: "image", urlColumn: "IMAGE_URL" },
    });

    const image = report.shadowRoot.querySelectorAll("tbody tr td")[1].querySelector("img.ir-cell-image");
    assert.equal(image.getAttribute("src"), "https://images.example/42.png");
    assert.equal(image.getAttribute("alt"), "Name",
        "an image cell is data, not decoration — the column heading describes it");
    assert.equal(report.shadowRoot.querySelectorAll("th").length, 2, "the image URL source remains hidden");

    report.remove();
});

test("a hidden computed column offers display settings and can source another column renderer", async () => {
    requests.length = 0;
    const report = await mount("computed-renderer");

    report.shadowRoot.querySelector(".ir-actionsbtn").click();
    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");
    const column = fieldControl(dialog, "Column");
    const displayAs = fieldControl(dialog, "Display As");

    column.value = "c1";
    column.dispatchEvent(new window.Event("change", { bubbles: true }));
    assert.equal(dialog.querySelector('.ir-checkline input[type="checkbox"]').checked, false,
        "the computed column starts hidden");
    assert.equal([...column.options].find(option => option.value === "c1").text.startsWith("ƒ "), true,
        "the dialog identifies the computed column");
    displayAs.value = "image";
    displayAs.dispatchEvent(new window.Event("input", { bubbles: true }));
    fieldControl(dialog, "URL Column").value = "c1";

    column.value = "NAME";
    column.dispatchEvent(new window.Event("change", { bubbles: true }));
    displayAs.value = "link";
    displayAs.dispatchEvent(new window.Event("input", { bubbles: true }));
    fieldControl(dialog, "URL Column").value = "c1";
    fieldControl(dialog, "Link Text Column").value = "NAME";

    const doc = await applyDialog(report);
    assert.deepEqual(doc.columns, ["ID", "NAME"], "the computed source remains hidden");
    assert.deepEqual(doc.formats, {
        c1: { displayAs: "image", urlColumn: "c1" },
        NAME: { displayAs: "link", urlColumn: "c1", textColumn: "NAME" },
    });

    const link = report.shadowRoot.querySelectorAll("tbody tr td")[1].querySelector("a.ir-cell-link");
    assert.equal(link.textContent, "Example");
    assert.equal(link.getAttribute("href"), "/computed/42");
    assert.equal(report.shadowRoot.querySelectorAll("th").length, 2);

    report.remove();
});

test("column settings reject component-reserved CSS classes without leaking staged edits", async () => {
    requests.length = 0;
    const report = await mount("orders");

    report.shadowRoot.querySelector("th.ir-th-menu .ir-th-button").click();
    clickMenuItem(report, "Column Settings");
    const dialog = report.shadowRoot.querySelector(".ir-dialog");

    // Stage a VALID edit on ID first, then an invalid class on NAME: the failed
    // apply must discard the whole visit, not leave ID's edit in the live doc.
    const maskSel = fieldControl(dialog, "Format Mask");
    maskSel.value = "integer";
    maskSel.dispatchEvent(new window.Event("input", { bubbles: true }));
    const column = fieldControl(dialog, "Column");
    column.value = "NAME";
    column.dispatchEvent(new window.Event("change", { bubbles: true }));
    const classesInp = dialog.querySelector('input[type="text"]');
    classesInp.value = "ir-empty";
    dialog.querySelector(".ir-btn-primary").click();

    await settle(() => !dialog.querySelector(".ir-dialog-error").hidden);
    assert.match(dialog.querySelector(".ir-dialog-error").textContent, /invalid or reserved/i);
    assert.equal(!!report.shadowRoot.querySelector(".ir-dialog"), true,
        "invalid class input keeps the dialog open");
    assert.equal(report.doc.pipeline[0].layer.formats?.ID, undefined,
        "an earlier staged column's edit must not survive a later column's failure");

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
