import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";

const window = new Window({ url: "https://host.example/reports/orders" });
Object.assign(globalThis, {
    window,
    document: window.document,
    Node: window.Node,
    HTMLElement: window.HTMLElement,
    ShadowRoot: window.ShadowRoot,
    requestAnimationFrame: callback => setTimeout(callback, 0),
});

const { openDialog } = await import("../../src/client/core/dialog.js");
const { popupMenu, closePopups } = await import("../../src/client/core/menu.js");

// Escape reaches the dialog through a document-level capture listener, as in a browser.
const escape = target => {
    const event = new window.KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true, composed: true });
    target.dispatchEvent(event);
    return event;
};
const dialogOpen = () => Boolean(document.querySelector(".ir-dialog"));

// A modeless editor such as Filter: the user keeps working in the report and its menus while
// it is open, so Escape must go to whatever actually has focus.
function openModeless() {
    return openDialog({
        title: "Filter",
        build: body => body.append(Object.assign(document.createElement("input"), { id: "filter-text" })),
        onApply: () => true,
    });
}

test.afterEach(() => {
    closePopups();
    document.body.replaceChildren();
});

test("Escape inside a modeless dialog closes it", () => {
    openModeless();
    assert.equal(dialogOpen(), true);

    const event = escape(document.getElementById("filter-text"));
    assert.equal(event.defaultPrevented, true, "the dialog consumes its own Escape");
    assert.equal(dialogOpen(), false);
});

test("Escape inside an open menu closes the menu and leaves the modeless dialog open", () => {
    openModeless();
    const anchor = document.createElement("button");
    document.body.append(anchor);
    const menu = popupMenu(anchor, [{ label: "Columns…", onPick: () => {} }]);

    escape(menu.querySelector(".ir-menu-item"));
    assert.equal(document.querySelector(".ir-popup"), null, "the menu handles its own Escape");
    assert.equal(dialogOpen(), true, "the dialog is not the thing being dismissed");
});

test("Escape pressed in the host page is not swallowed by a modeless dialog", () => {
    openModeless();
    const hostField = document.createElement("input");
    document.body.append(hostField);

    const event = escape(hostField);
    assert.equal(event.defaultPrevented, false, "the host keeps its key");
    assert.equal(dialogOpen(), true);
});
