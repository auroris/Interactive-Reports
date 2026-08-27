import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";

const window = new Window({ url: "https://host.example/reports/orders" });
globalThis.document = window.document;

const { activeEditLink, renderEditCell, substituteEditUrl } =
    await import("../../src/client/report/render/edit-link.js");

test("template substitution URL-encodes values and withholds the link on missing data", () => {
    assert.equal(
        substituteEditUrl("/orders/{ORDER_ID}/edit?r={REGION}", { ORDER_ID: 42, REGION: "north east" }),
        "/orders/42/edit?r=north%20east");
    assert.equal(
        substituteEditUrl("/orders/{ORDER_ID}", { ORDER_ID: "A/B &?" }),
        "/orders/A%2FB%20%26%3F",
        "reserved characters cannot break out of their path segment");
    assert.equal(
        substituteEditUrl("/orders/{ORDER_ID}", { order_id: 7 }),
        "/orders/7",
        "row keys resolve case-insensitively for hand-written hosts");

    assert.equal(substituteEditUrl("/orders/{ORDER_ID}", { ORDER_ID: null }), null);
    assert.equal(substituteEditUrl("/orders/{ORDER_ID}", { ORDER_ID: undefined }), null);
    assert.equal(substituteEditUrl("/orders/{GHOST}", { ORDER_ID: 1 }), null, "a missing column is a withheld link");
});

test("edit cells render icon-only anchors with accessible names and native navigation", () => {
    const editLink = { urlTemplate: "/orders/{ID}/edit", label: "Edit order", target: "_self" };
    const anchor = renderEditCell(editLink, { ID: 42 });

    assert.equal(anchor.tagName, "A");
    assert.equal(anchor.className, "ir-cell-edit");
    assert.equal(anchor.getAttribute("href"), "/orders/42/edit");
    assert.equal(anchor.getAttribute("aria-label"), "Edit order");
    assert.equal(anchor.getAttribute("title"), "Edit order");
    assert.equal(anchor.hasAttribute("target"), false, "_self is the default — no target, no rel");
    assert.equal(anchor.hasAttribute("rel"), false);
    assert.equal(!!anchor.querySelector(".ir-icon svg"), true, "the pencil icon, aria-hidden");
    assert.equal(anchor.textContent.trim(), "", "icon-only: the accessible name is the aria-label");

    const blank = renderEditCell({ ...editLink, target: "_blank" }, { ID: 1 });
    assert.equal(blank.getAttribute("target"), "_blank");
    assert.equal(blank.getAttribute("rel"), "noopener");

    const unnamed = renderEditCell({ urlTemplate: "/orders/{ID}/edit" }, { ID: 1 });
    assert.equal(unnamed.getAttribute("aria-label"), "Edit", "the default accessible name");
});

test("withheld and unsafe links render empty cells", () => {
    assert.equal(renderEditCell({ urlTemplate: "/orders/{ID}/edit" }, { ID: null }), "");
    // Defense in depth: the definition is trusted config, but the substituted
    // result still passes the renderer protocol allowlist.
    assert.equal(renderEditCell({ urlTemplate: "javascript:{ID}" }, { ID: "alert(1)" }), "");
});

test("the edit link is a grid-row affordance only", () => {
    const w = { schema: { editLink: { urlTemplate: "/orders/{ID}/edit" } } };
    assert.deepEqual(activeEditLink(w, "grid"), w.schema.editLink);
    assert.equal(activeEditLink(w, "groupBy"), null);
    assert.equal(activeEditLink(w, "pivot"), null);
    assert.equal(activeEditLink(w, "chart"), null);
    assert.equal(activeEditLink({ schema: {} }, "grid"), null);
});
