import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";

const window = new Window({ url: "https://host.example/reports/orders" });
globalThis.document = window.document;
globalThis.CustomEvent = window.CustomEvent;

const { applyCreateButton, renderCreateButton } =
    await import("../../src/client/report/create-link.js");

// A minimal report controller: schema, localization, the toolbar slot, and an event target.
function controller(createLink) {
    const element = document.createElement("div");
    const events = [];
    element.addEventListener("ir-create", event => events.push(event));
    return {
        schema: { createLink },
        t: key => ({ "toolbar.create": "Create" })[key] ?? key,
        els: { createSlot: document.createElement("span") },
        dispatchEvent: event => element.dispatchEvent(event),
        events,
        element,
    };
}

test("navigate-mode create links are primary anchors with the plus icon and label", () => {
    const w = controller({ url: "/orders/new", label: "New order", target: "_self", mode: "navigate" });
    const anchor = renderCreateButton(w);

    assert.equal(anchor.tagName, "A");
    assert.equal(anchor.className, "ir-btn ir-btn-primary ir-createbtn");
    assert.equal(anchor.getAttribute("href"), "/orders/new");
    assert.equal(anchor.getAttribute("title"), "New order");
    assert.equal(anchor.textContent.trim(), "New order", "visible text is the accessible name");
    assert.equal(!!anchor.querySelector(".ir-icon svg"), true);
    assert.equal(anchor.hasAttribute("target"), false);
    assert.equal(anchor.hasAttribute("rel"), false);

    const blank = renderCreateButton(controller({ url: "/orders/new", target: "_blank" }));
    assert.equal(blank.getAttribute("target"), "_blank");
    assert.equal(blank.getAttribute("rel"), "noopener");
    assert.equal(blank.textContent.trim(), "Create", "the localized default label");
});

test("navigate-mode create links dispatch ir-create first and a prevented event cancels navigation", () => {
    const w = controller({ url: "/orders/new", label: "New order" });
    const anchor = renderCreateButton(w);

    let click = new window.MouseEvent("click", { bubbles: true, cancelable: true });
    anchor.dispatchEvent(click);
    assert.equal(w.events.length, 1);
    assert.equal(w.events[0].bubbles && w.events[0].composed && w.events[0].cancelable, true);
    assert.deepEqual(w.events[0].detail, { url: "/orders/new" });
    assert.equal(click.defaultPrevented, false);

    w.element.addEventListener("ir-create", event => event.preventDefault());
    click = new window.MouseEvent("click", { bubbles: true, cancelable: true });
    anchor.dispatchEvent(click);
    assert.equal(click.defaultPrevented, true);
});

test("event-mode create links are buttons that only dispatch ir-create, URL optional", () => {
    const w = controller({ label: "New order", mode: "event" });
    const button = renderCreateButton(w);

    assert.equal(button.tagName, "BUTTON");
    assert.equal(button.getAttribute("type"), "button");
    assert.equal(button.className, "ir-btn ir-btn-primary ir-createbtn");
    assert.equal(button.textContent.trim(), "New order");
    assert.equal(button.hasAttribute("href"), false);

    button.click();
    assert.equal(w.events.length, 1);
    assert.deepEqual(w.events[0].detail, { url: null }, "no URL configured, none offered");

    const withUrl = controller({ url: "/orders/new", mode: "event" });
    renderCreateButton(withUrl).click();
    assert.deepEqual(withUrl.events[0].detail, { url: "/orders/new" }, "a configured URL rides the event");
});

test("unsafe or missing URLs render nothing in navigate mode", () => {
    assert.equal(renderCreateButton(controller({ url: "javascript:alert(1)" })), null);
    assert.equal(renderCreateButton(controller({ label: "New order" })), null, "navigate mode needs a URL");
    assert.equal(renderCreateButton(controller(null)), null);
    assert.equal(renderCreateButton({ schema: null, t: k => k }), null, "no schema yet, no button");
});

test("the toolbar slot shows exactly the current schema's control", () => {
    const w = controller({ url: "/orders/new", label: "New order" });
    applyCreateButton(w);
    assert.equal(w.els.createSlot.hidden, false);
    assert.equal(w.els.createSlot.children.length, 1);
    assert.equal(w.els.createSlot.firstElementChild.className, "ir-btn ir-btn-primary ir-createbtn");

    w.schema = { createLink: null };
    applyCreateButton(w);
    assert.equal(w.els.createSlot.hidden, true, "a definition without a create link hides the slot");
    assert.equal(w.els.createSlot.children.length, 0);

    assert.doesNotThrow(() => applyCreateButton({ schema: {}, t: k => k }), "no skeleton yet is a no-op");
});
