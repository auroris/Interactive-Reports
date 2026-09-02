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

const { popupMenu, closePopups } = await import("../../src/client/core/menu.js");

// Assertions compare strings and booleans, never DOM nodes: a failing node comparison makes the
// assertion message inspect happy-dom's object graph, which never finishes.
const key = (target, name) => target.dispatchEvent(new window.KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true }));
const hover = target => target.dispatchEvent(new window.Event("pointerenter"));
const labelOf = item => item?.querySelector?.(".ir-menu-label")?.textContent ?? item?.tagName ?? null;
const active = () => labelOf(document.activeElement);
const labelsOf = menu => [...menu.children]
    .filter(child => child.classList.contains("ir-menu-item"))
    .map(labelOf);

// Pointer samples for the aim test go through the document, as in a browser.
const move = (x, y) => document.dispatchEvent(new window.MouseEvent("pointermove", { clientX: x, clientY: y, bubbles: true }));
// happy-dom lays nothing out, so a submenu's box is stubbed where a test needs geometry.
const placeAt = (element, rect) => {
    element.getBoundingClientRect = () => ({ ...rect, width: rect.right - rect.left, height: rect.bottom - rect.top });
};

function openMenu({ parentDisabled = false, secondParent = false } = {}) {
    const anchor = document.createElement("button");
    anchor.id = "actions";
    document.body.append(anchor);
    const picked = [];
    const menu = popupMenu(anchor, [
        { label: "Columns…", onPick: () => picked.push("columns") },
        {
            label: "Pagination",
            hint: "50",
            disabled: parentDisabled,
            items: [
                { label: "10", onPick: () => picked.push(10) },
                { label: "50", checked: true, onPick: () => picked.push(50) },
                "-",
                { note: "All returns every matching row in one page." },
            ],
        },
        { label: "Sort…", onPick: () => picked.push("sort") },
        ...(secondParent ? [{ label: "Download", items: [{ label: "CSV", onPick: () => picked.push("csv") }] }] : []),
    ]);
    const [columns, pagination, sort, download] = [...menu.children].filter(child => child.classList.contains("ir-menu-item"));
    const submenu = () => menu.querySelector(".ir-submenu");
    const submenuItems = () => [...(submenu()?.querySelectorAll(".ir-menu-item") ?? [])];
    return { anchor, menu, picked, columns, pagination, sort, download, submenu, submenuItems, hasSubmenu: () => Boolean(submenu()) };
}

// A submenu box to the right of and below the parent entry, as the anchored layout places it.
const SUBMENU_BOX = { left: 200, right: 400, top: 40, bottom: 300 };

test.afterEach(() => {
    closePopups();
    document.body.replaceChildren();
});

test("a submenu parent shows its hint and arrow, and opens a nested menu inside its parent on click", () => {
    const { menu, picked, pagination, submenu, submenuItems, hasSubmenu } = openMenu();

    assert.equal(pagination.getAttribute("aria-haspopup"), "menu");
    assert.equal(pagination.getAttribute("aria-expanded"), "false");
    assert.equal(pagination.querySelector(".ir-menu-hint").textContent, "50");
    assert.equal(Boolean(pagination.querySelector(".ir-menu-arrow")), true, "a parent entry is marked with an arrow");
    assert.equal(hasSubmenu(), false, "submenus open on demand");

    pagination.click();
    assert.equal(hasSubmenu(), true, "the submenu mounts");
    const child = submenu();
    assert.equal(child.parentNode === menu, true, "the submenu nests inside its parent so popovers stack");
    assert.equal(child.getAttribute("role"), "menu");
    assert.equal(child.getAttribute("part"), "menu submenu");
    assert.equal(pagination.getAttribute("aria-expanded"), "true");
    assert.equal(pagination.getAttribute("aria-controls"), child.id);
    assert.deepEqual(labelsOf(child), ["10", "50"]);
    assert.equal(child.querySelector(".ir-checked .ir-menu-label").textContent, "50", "the current choice is ticked");
    assert.equal(child.querySelector(".ir-menu-note").textContent, "All returns every matching row in one page.");
    assert.equal(active(), "10", "click moves focus into the submenu");

    submenuItems()[0].click();
    assert.deepEqual(picked, [10]);
    assert.equal(document.querySelector(".ir-popup"), null, "picking closes the whole stack");
    assert.equal(document.activeElement?.id, "actions", "focus returns to the control that opened the menu");
});

test("ArrowRight opens a submenu; ArrowLeft and Escape close only that level", () => {
    const { menu, pagination, submenuItems, hasSubmenu } = openMenu();

    pagination.focus();
    key(pagination, "ArrowRight");
    assert.equal(hasSubmenu(), true, "ArrowRight opens the submenu");
    assert.equal(active(), "10", "the submenu's first entry takes focus");

    key(submenuItems()[0], "ArrowLeft");
    assert.equal(hasSubmenu(), false, "ArrowLeft closes the submenu");
    assert.equal(menu.isConnected, true, "the parent menu stays open");
    assert.equal(pagination.getAttribute("aria-expanded"), "false");
    assert.equal(active(), "Pagination", "focus returns to the parent entry");

    key(pagination, "ArrowRight");
    key(submenuItems()[0], "Escape");
    assert.equal(hasSubmenu(), false, "Escape inside the submenu closes the submenu");
    assert.equal(menu.isConnected, true);
    assert.equal(active(), "Pagination");

    key(pagination, "Escape");
    assert.equal(document.querySelector(".ir-popup"), null, "Escape at the top level closes the menu");
    assert.equal(document.activeElement?.id, "actions");
});

test("hover opens a submenu without moving focus, and moving on closes it", () => {
    const { menu, pagination, sort, submenuItems, hasSubmenu } = openMenu();

    hover(pagination);
    assert.equal(hasSubmenu(), true, "hover opens the submenu");
    assert.equal(active(), "Columns…", "hover leaves focus where it was");

    hover(sort);
    assert.equal(hasSubmenu(), false, "hovering a sibling closes the submenu");

    pagination.focus();
    hover(pagination);
    assert.equal(hasSubmenu(), true);
    key(pagination, "ArrowDown");
    assert.equal(active(), "Sort…", "arrow keys move within the parent level");
    assert.equal(hasSubmenu(), false, "moving on closes the submenu");

    key(sort, "ArrowUp");
    assert.equal(active(), "Pagination");
    key(pagination, "Escape");
    assert.equal(document.querySelector(".ir-popup"), null, "Escape with no submenu open closes the menu");
    assert.equal(menu.isConnected, false);
});

test("submenu arrow keys cycle the submenu's own entries", () => {
    const { menu, pagination, submenuItems, hasSubmenu } = openMenu();

    pagination.focus();
    key(pagination, "ArrowRight");
    assert.equal(active(), "10");
    key(submenuItems()[0], "ArrowDown");
    assert.equal(active(), "50", "ArrowDown moves within the submenu");
    key(submenuItems()[1], "ArrowDown");
    assert.equal(active(), "10", "the cycle wraps within the submenu, not into the parent");
    key(submenuItems()[0], "End");
    assert.equal(active(), "50");
    assert.equal(hasSubmenu(), true);
    assert.equal(menu.isConnected, true);

    key(submenuItems()[1], "Escape");
    assert.equal(hasSubmenu(), false);
    assert.equal(active(), "Pagination");
    hover(pagination);
    key(pagination, "Escape");
    assert.equal(hasSubmenu(), false, "Escape on the parent entry closes an open submenu first");
    assert.equal(menu.isConnected, true, "the menu itself stays open");
});

test("a submenu stays open while the pointer heads towards it and closes once it turns away", () => {
    const { pagination, sort, submenu, hasSubmenu } = openMenu();

    hover(pagination);
    placeAt(submenu(), SUBMENU_BOX);
    move(100, 50);
    move(120, 70);
    hover(sort);
    assert.equal(hasSubmenu(), true, "crossing a sibling while aiming at the submenu keeps it open");
    assert.equal(pagination.getAttribute("aria-expanded"), "true");

    move(140, 90);
    assert.equal(hasSubmenu(), true, "still aiming: still held");
    move(140, 90);
    assert.equal(hasSubmenu(), true, "a stationary sample is not a change of mind");

    move(120, 110);
    assert.equal(hasSubmenu(), false, "the hold ends the moment the pointer stops aiming");
    assert.equal(pagination.getAttribute("aria-expanded"), "false");
});

test("reaching the submenu ends the hold, and a sibling entered without aiming closes it at once", () => {
    const { pagination, sort, submenu, hasSubmenu } = openMenu();

    hover(pagination);
    placeAt(submenu(), SUBMENU_BOX);
    move(100, 50);
    move(120, 70);
    hover(sort);
    assert.equal(hasSubmenu(), true);
    submenu().dispatchEvent(new window.Event("pointerenter"));
    move(120, 110);
    assert.equal(hasSubmenu(), true, "once the pointer is inside the submenu its direction no longer matters");

    move(150, 100);
    move(130, 120);
    hover(sort);
    assert.equal(hasSubmenu(), false, "heading away from the submenu: a sibling closes it with no delay");
});

test("when a hold ends over another parent entry, that entry's submenu opens instead", () => {
    const { pagination, download, submenu, hasSubmenu } = openMenu({ secondParent: true });

    hover(pagination);
    placeAt(submenu(), SUBMENU_BOX);
    move(100, 50);
    move(120, 70);
    hover(download);
    assert.equal(pagination.getAttribute("aria-expanded"), "true", "the crossed parent stays inert while the hold lasts");
    assert.equal(download.getAttribute("aria-expanded"), "false");

    move(120, 110);
    assert.equal(pagination.getAttribute("aria-expanded"), "false");
    assert.equal(download.getAttribute("aria-expanded"), "true", "the entry under the pointer takes effect once the hold ends");
    assert.deepEqual(labelsOf(submenu()), ["CSV"]);
    assert.equal(hasSubmenu(), true);
});

test("a pointer that stops dead mid-flight releases the hold after the safety timeout", t => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    const { pagination, download, submenu, hasSubmenu } = openMenu({ secondParent: true });

    hover(pagination);
    placeAt(submenu(), SUBMENU_BOX);
    move(100, 50);
    move(120, 70);
    hover(download);
    assert.equal(hasSubmenu(), true);

    t.mock.timers.tick(400);
    move(140, 90);
    t.mock.timers.tick(400);
    assert.equal(pagination.getAttribute("aria-expanded"), "true", "each aiming move restarts the safety timer");

    t.mock.timers.tick(250);
    assert.equal(pagination.getAttribute("aria-expanded"), "false", "a stalled pointer releases the hold");
    assert.equal(download.getAttribute("aria-expanded"), "true", "and the entry it stopped on takes effect");
});

test("a disabled submenu parent neither opens nor takes arrow focus", () => {
    const { columns, pagination, hasSubmenu } = openMenu({ parentDisabled: true });

    assert.equal(pagination.disabled, true);
    hover(pagination);
    assert.equal(hasSubmenu(), false);
    key(pagination, "ArrowRight");
    assert.equal(hasSubmenu(), false);
    key(columns, "ArrowDown");
    assert.equal(active(), "Sort…", "the disabled entry is skipped");
});
