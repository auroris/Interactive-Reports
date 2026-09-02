import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";

const window = new Window({ url: "https://host.example/reports/orders" });
globalThis.document = window.document;
globalThis.CustomEvent = window.CustomEvent;

const { activeEditLink, renderEditCell, substituteEditUrl } =
    await import("../../src/client/report/render/edit-link.js");

// The host element the pencil dispatches from; renderers receive the report controller, whose
// dispatchEvent forwards to it, so a bare element stands in for both.
function host() {
    const element = document.createElement("div");
    element.events = [];
    element.addEventListener("ir-edit", event => element.events.push(event));
    return element;
}

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
    const anchor = renderEditCell(editLink, { ID: 42 }, host());

    assert.equal(anchor.tagName, "A");
    assert.equal(anchor.className, "ir-cell-edit");
    assert.equal(anchor.getAttribute("href"), "/orders/42/edit");
    assert.equal(anchor.getAttribute("aria-label"), "Edit order");
    assert.equal(anchor.getAttribute("title"), "Edit order");
    assert.equal(anchor.hasAttribute("target"), false, "_self is the default — no target, no rel");
    assert.equal(anchor.hasAttribute("rel"), false);
    assert.equal(!!anchor.querySelector(".ir-icon svg"), true, "the pencil icon, aria-hidden");
    assert.equal(anchor.textContent.trim(), "", "icon-only: the accessible name is the aria-label");

    const blank = renderEditCell({ ...editLink, target: "_blank" }, { ID: 1 }, host());
    assert.equal(blank.getAttribute("target"), "_blank");
    assert.equal(blank.getAttribute("rel"), "noopener");

    const unnamed = renderEditCell({ urlTemplate: "/orders/{ID}/edit" }, { ID: 1 }, host());
    assert.equal(unnamed.getAttribute("aria-label"), "Edit", "the default accessible name");
});

test("withheld and unsafe links render empty cells", () => {
    assert.equal(renderEditCell({ urlTemplate: "/orders/{ID}/edit" }, { ID: null }, host()), "");
    // Defense in depth: the definition is trusted config, but the substituted
    // result still passes the renderer protocol allowlist.
    assert.equal(renderEditCell({ urlTemplate: "javascript:{ID}" }, { ID: "alert(1)" }, host()), "");
    // Event mode hands the URL to the host, so the same allowlist guards it.
    assert.equal(
        renderEditCell({ urlTemplate: "javascript:{ID}", mode: "event" }, { ID: "alert(1)" }, host()), "");
});

test("navigate-mode anchors dispatch ir-edit first and a prevented event cancels navigation", () => {
    const element = host();
    const row = { ID: 42, LABEL: "first", HIDDEN_KEY: "k" };
    const anchor = renderEditCell({ urlTemplate: "/orders/{ID}/edit" }, row, element);

    let click = new window.MouseEvent("click", { bubbles: true, cancelable: true });
    anchor.dispatchEvent(click);
    assert.equal(element.events.length, 1);
    const [event] = element.events;
    assert.equal(event.bubbles && event.composed && event.cancelable, true);
    assert.equal(event.detail.url, "/orders/42/edit");
    assert.deepEqual(event.detail.row, row, "the whole row rides along, hidden key included");
    assert.notEqual(event.detail.row, row, "listeners get a copy, not render state");
    assert.equal(click.defaultPrevented, false, "nobody objected: the anchor navigates");

    element.addEventListener("ir-edit", e => e.preventDefault());
    click = new window.MouseEvent("click", { bubbles: true, cancelable: true });
    anchor.dispatchEvent(click);
    assert.equal(click.defaultPrevented, true, "a prevented ir-edit keeps the page where it is");
});

test("event-mode pencils are buttons that only dispatch ir-edit", () => {
    const element = host();
    const editLink = { urlTemplate: "/orders/{ID}/edit", label: "Edit order", target: "_blank", mode: "event" };
    const button = renderEditCell(editLink, { ID: 7 }, element);

    assert.equal(button.tagName, "BUTTON");
    assert.equal(button.getAttribute("type"), "button");
    assert.equal(button.className, "ir-cell-edit");
    assert.equal(button.getAttribute("aria-label"), "Edit order");
    assert.equal(button.getAttribute("title"), "Edit order");
    assert.equal(button.hasAttribute("href"), false);
    assert.equal(button.hasAttribute("target"), false, "no navigation, so no target");
    assert.equal(!!button.querySelector(".ir-icon svg"), true, "same pencil icon");

    button.click();
    assert.equal(element.events.length, 1);
    assert.equal(element.events[0].detail.url, "/orders/7/edit", "the substituted URL is still offered to the host");
    assert.equal(element.events[0].detail.row.ID, 7);

    assert.equal(renderEditCell(editLink, { ID: null }, element), "", "a NULL key still withholds the pencil");
});

test("the edit link is a grid-row affordance only", () => {
    const w = { schema: { editLink: { urlTemplate: "/orders/{ID}/edit" } } };
    assert.deepEqual(activeEditLink(w, "grid"), w.schema.editLink);
    assert.equal(activeEditLink(w, "groupBy"), null);
    assert.equal(activeEditLink(w, "pivot"), null);
    assert.equal(activeEditLink(w, "chart"), null);
    assert.equal(activeEditLink({ schema: {} }, "grid"), null);
});
