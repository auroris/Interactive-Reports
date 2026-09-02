// Popup-menu coordinator for toolbar and column-header controls. Native auto
// popovers own top-layer display, light dismiss, and close requests. CSS anchor positioning
// owns placement where available; the small geometry fallback keeps older hosts usable.
// Menu-specific arrow-key focus remains application behavior.
//
// An entry with `items` is a submenu parent: hovering or clicking it (ArrowRight / Enter from the
// keyboard) opens a nested menu beside the entry. The nested menu is a DOM descendant of its parent
// so native popovers treat the pair as one nested stack: the parent stays open, light dismiss inside
// the parent closes only the child, and hiding the parent hides the child with it. Picking an
// entry anywhere in the stack closes the whole stack and returns focus to the control that opened it.
//
// Crossing a sibling entry on the way to an open submenu does not close it. Direction is measured
// from the last two pointer samples: while the pointer is travelling towards the submenu's box the
// submenu is held open and the entries being crossed stay inert; the hold ends the moment the pointer
// stops aiming at it (or reaches it), and only then does the entry under the pointer take effect.
// Measuring intent this way avoids the fixed close delay that would add latency to moving away.

import { el } from "./dom.js";
import { hidePopover, popoverIsOpen, showPopover } from "./popover.js";

let activePopup = null;
let activePopupOwner = null;
let popupSequence = 0;

/**
 * Closes the one globally active popup, if present, and clears its ownership record.
 *
 * @returns {void} No value.
 *
 * Side effects: invokes the active popup's cleanup callback, which removes DOM and listeners.
 */
export function closePopups() {
    const close = activePopup;
    activePopup = null;
    activePopupOwner = null;
    close?.();
}

// Invariant: widget teardown: close the menu only if this host opened it.
/**
 * Closes the menu owned by the supplied widget host.
 *
 * @param {Element} host - The custom-element host whose menu should close.
 * @returns {void} No value.
 *
 * Side effects: closes and removes the active menu only when its recorded owner is `host`.
 */
export function closeMenuOwnedBy(host) {
    if (activePopupOwner === host) closePopups();
}

/**
 * Determines whether the browser supports CSS anchor positioning.
 *
 * @returns {boolean} Whether the three CSS features required by the anchored menu layout are supported.
 */
const anchorPositioningAvailable = () =>
    window.CSS?.supports?.("position-anchor: --ir-popup-anchor") === true
    && window.CSS.supports("position-area: bottom span-right")
    && window.CSS.supports("position-try-order: most-height");

const VIEWPORT_MARGIN = 8;
const ANCHOR_GAP = 2;
// A submenu's first entry lines up with the parent entry that opened it: the popup's top padding
// (4px) plus its border (1px). The anchored CSS layout applies the same offset as a margin.
const SUBMENU_OVERLAP = 5;
// Safety net for a submenu held open on aim, in milliseconds since the last aiming move. A hold ends
// as soon as the pointer stops aiming, so this only matters when the pointer stops dead mid-flight
// and no further pointermove arrives to settle it. Deliberately not a general-purpose close delay.
const AIM_HOLD_TIMEOUT = 600;

// Last two pointer samples in viewport coordinates, most recent last. One document listener feeds
// every menu: direction is a property of the pointer, not of any particular element.
const pointerTrail = [];
let pointerTrackingStarted = false;

/**
 * Starts sampling pointer position for the aim test. Installed once, by the first popup.
 *
 * @returns {void} No value.
 */
function startPointerTracking() {
    if (pointerTrackingStarted) return;
    pointerTrackingStarted = true;
    document.addEventListener("pointermove", event => {
        pointerTrail.push({ x: event.clientX, y: event.clientY });
        if (pointerTrail.length > 2) pointerTrail.shift();
    }, { passive: true });
}

/**
 * Determines whether a ray from (x, y) travelling along (dx, dy) crosses an axis-aligned rectangle.
 * Standard slab test; only the forward half of the ray counts, so a box behind the direction of
 * travel is not being aimed at.
 *
 * @param {number} x - The ray origin's horizontal coordinate.
 * @param {number} y - The ray origin's vertical coordinate.
 * @param {number} dx - The horizontal component of the direction of travel.
 * @param {number} dy - The vertical component of the direction of travel.
 * @param {DOMRect} rect - The box to test.
 * @returns {boolean} Whether the ray enters the box.
 */
function rayHitsRect(x, y, dx, dy, rect) {
    let tMin = 0;
    let tMax = Number.POSITIVE_INFINITY;
    for (const [origin, delta, min, max] of [[x, dx, rect.left, rect.right], [y, dy, rect.top, rect.bottom]]) {
        if (delta === 0) {
            // Parallel to this pair of edges: a hit only if already inside the slab.
            if (origin < min || origin > max) return false;
            continue;
        }
        const tNear = Math.min((min - origin) / delta, (max - origin) / delta);
        const tFar = Math.max((min - origin) / delta, (max - origin) / delta);
        tMin = Math.max(tMin, tNear);
        tMax = Math.min(tMax, tFar);
        if (tMax < tMin) return false;
    }
    return true;
}

/**
 * The pointer's direction of travel from its last two samples.
 *
 * @returns {{x: number, y: number, dx: number, dy: number}|null} The latest position and movement, or `null` when there are too few samples or no movement between them.
 */
function pointerHeading() {
    if (pointerTrail.length < 2) return null;
    const [from, to] = pointerTrail;
    const dx = to.x - from.x;
    const dy = to.y - from.y;
    // A stationary sample carries no direction and says nothing about intent either way.
    if (dx === 0 && dy === 0) return null;
    return { x: to.x, y: to.y, dx, dy };
}

/**
 * Determines whether the pointer is currently travelling towards an element.
 *
 * @param {Element} element - The element whose box is the target.
 * @returns {boolean} Whether the last movement points into the element's box.
 */
function isAimingAt(element) {
    const heading = pointerHeading();
    if (!heading) return false;
    const rect = element.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return false;
    return rayHitsRect(heading.x, heading.y, heading.dx, heading.dy, rect);
}

/**
 * Legacy geometry path: cap the menu to the larger space above or below its anchor. A menu taller than
 * either side becomes a scroll container instead of extending beyond the viewport. Modern browsers get
 * this sizing from the CSS position area.
 *
 * @param {HTMLElement} menu - The fallback menu whose maximum height will be set.
 * @param {Element} anchor - The element used to measure space above and below.
 * @returns {void} No value.
 */
function constrainFallbackHeight(menu, anchor) {
    const a = anchor.getBoundingClientRect();
    const below = window.innerHeight - VIEWPORT_MARGIN - a.bottom - ANCHOR_GAP;
    const above = a.top - ANCHOR_GAP - VIEWPORT_MARGIN;
    menu.style.maxHeight = `${Math.max(0, below, above)}px`;
}

/**
 * Positions a compatibility menu within the viewport beside its anchor.
 *
 * @param {HTMLElement} menu - The mounted fallback menu to position.
 * @param {Element} anchor - The element whose viewport rectangle anchors the menu.
 * @returns {void} No value.
 */
function placeFallback(menu, anchor) {
    const a = anchor.getBoundingClientRect();
    const m = menu.getBoundingClientRect();
    let left = Math.min(a.left, window.innerWidth - m.width - VIEWPORT_MARGIN);
    let top = a.bottom + ANCHOR_GAP;
    if (top + m.height > window.innerHeight - VIEWPORT_MARGIN)
        top = Math.max(VIEWPORT_MARGIN, a.top - m.height - ANCHOR_GAP);
    menu.style.left = `${Math.max(VIEWPORT_MARGIN, left)}px`;
    menu.style.top = `${top}px`;
}

/**
 * Legacy geometry path for a submenu: open to the right of the parent entry, aligned with it, and
 * flip to the left or slide up only when the viewport leaves no room.
 *
 * @param {HTMLElement} menu - The mounted fallback submenu to position.
 * @param {Element} parentItem - The menu entry that opened the submenu.
 * @returns {void} No value.
 */
function placeSubmenuFallback(menu, parentItem) {
    menu.style.maxHeight = `${Math.max(0, window.innerHeight - 2 * VIEWPORT_MARGIN)}px`;
    const a = parentItem.getBoundingClientRect();
    const m = menu.getBoundingClientRect();
    let left = a.right;
    if (left + m.width > window.innerWidth - VIEWPORT_MARGIN) left = a.left - m.width;
    let top = a.top - SUBMENU_OVERLAP;
    if (top + m.height > window.innerHeight - VIEWPORT_MARGIN)
        top = window.innerHeight - VIEWPORT_MARGIN - m.height;
    menu.style.left = `${Math.max(VIEWPORT_MARGIN, left)}px`;
    menu.style.top = `${Math.max(VIEWPORT_MARGIN, top)}px`;
}

/**
 * Marks an anchor as the open control of a menu: ARIA popup state plus the CSS anchor name the menu
 * positions against.
 *
 * @param {HTMLElement} anchor - The control (toolbar button, header button, or submenu parent entry).
 * @param {HTMLElement} menu - The menu the anchor controls.
 * @param {string} anchorName - The unique CSS anchor name for this pairing.
 * @returns {Function} A callback that restores the anchor's previous state.
 */
function bindAnchor(anchor, menu, anchorName) {
    const oldAnchorName = anchor.style.getPropertyValue("anchor-name");
    const oldAnchorPriority = anchor.style.getPropertyPriority("anchor-name");
    anchor.style.setProperty("anchor-name", anchorName);
    menu.style.setProperty("position-anchor", anchorName);
    anchor.setAttribute("aria-haspopup", "menu");
    anchor.setAttribute("aria-expanded", "true");
    anchor.setAttribute("aria-controls", menu.id);
    return () => {
        anchor.setAttribute("aria-expanded", "false");
        anchor.removeAttribute("aria-controls");
        if (oldAnchorName) anchor.style.setProperty("anchor-name", oldAnchorName, oldAnchorPriority);
        else anchor.style.removeProperty("anchor-name");
    };
}

/**
 * The entries of one menu level that arrow keys move between: direct children only, so an open
 * submenu's entries belong to its own cycle.
 *
 * @param {HTMLElement} menu - The menu whose own entries are listed.
 * @returns {HTMLElement[]} Enabled action and submenu-parent entries in display order.
 */
const ownFocusable = menu => [...menu.children]
    .filter(child => child.classList.contains("ir-menu-item") && !child.disabled);

/**
 * Mounts one menu level: the top-level popup or a nested submenu.
 *
 * @param {HTMLElement} anchor - The control this level opens from (a submenu's parent entry for nested levels).
 * @param {Array<object|string>} items - The level's entries.
 * @param {object} options - Level wiring.
 * @param {Element} options.mount - Where the menu element is appended.
 * @param {Node} options.root - The document or shadow root used to resolve the active element.
 * @param {HTMLElement} options.rootAnchor - The top-level control that regains focus when the stack closes.
 * @param {Function} options.pick - Handles a chosen action entry for the whole stack.
 * @param {HTMLElement|null} [options.parentMenu=null] - The enclosing menu level; `null` for the top level.
 * @param {Function} [options.onClose] - Invoked once when this level has been removed.
 * @returns {{menu: HTMLDivElement, close: Function, focusFirst: Function}} The mounted level and its controls.
 */
function mountLevel(anchor, items, { mount, root, rootAnchor, pick, parentMenu = null, onClose }) {
    const submenu = parentMenu !== null;
    const sequence = ++popupSequence;
    const menu = el("div", {
        id: `ir-popup-${sequence}`,
        class: "ir-popup" + (submenu ? " ir-submenu" : ""),
        part: submenu ? "menu submenu" : "menu",
        role: "menu",
        popover: "auto",
    });

    // One open child at a time per level. Focusing a sibling entry closes it; hovering one closes it
    // unless the pointer is on its way to the child (see the aim hold below).
    const parents = new Map();
    let child = null;
    let childOwner = null;
    // The entry the pointer is resting on, so a released hold can apply it after the fact.
    let hovered = null;
    // An active aim hold: { timer, watcher }. Present only while a child is being held open.
    let hold = null;

    const endHold = () => {
        if (!hold) return;
        clearTimeout(hold.timer);
        document.removeEventListener("pointermove", hold.watcher);
        hold = null;
    };
    const closeChild = () => { endHold(); child?.close(); };
    const openChild = (button, item, { focus }) => {
        // A native light dismiss (pointerdown on the parent entry itself) hides the child before its
        // asynchronous toggle event reaches cleanup, so an open-looking child may already be gone.
        if (child && childOwner === button && popoverIsOpen(child.menu)) {
            endHold();
            if (focus) child.focusFirst();
            return;
        }
        closeChild();
        childOwner = button;
        child = mountLevel(button, item.items, {
            mount: menu, root, rootAnchor, pick, parentMenu: menu,
            onClose: () => {
                if (childOwner !== button) return;
                endHold();
                child = null;
                childOwner = null;
            },
        });
        // Reaching the submenu is the end of the flight; nothing to hold against any more.
        child.menu.addEventListener("pointerenter", endHold);
        if (focus) child.focusFirst();
    };

    // What the entry under the pointer would have done had there been no hold: a parent opens its
    // own submenu; anything else has already been served by the child closing.
    const settleHover = () => {
        if (hovered && parents.has(hovered) && !hovered.disabled) openChild(hovered, parents.get(hovered), { focus: false });
    };
    // Keep the child open while the pointer travels towards it. Re-evaluated on every move: the
    // hold ends the moment the pointer stops aiming (a change of mind closes the child at once, not
    // after a fixed period) and the entry now under the pointer takes effect. The timer only fires
    // if the pointer stops dead mid-flight, so no further move arrives to settle it either way.
    const beginHold = () => {
        const target = child.menu;
        const settle = () => { endHold(); closeChild(); settleHover(); };
        const arm = () => {
            clearTimeout(hold.timer);
            hold.timer = setTimeout(settle, AIM_HOLD_TIMEOUT);
        };
        const watcher = () => {
            if (!hold || !pointerHeading()) return;
            if (isAimingAt(target)) arm(); else settle();
        };
        hold = { timer: 0, watcher };
        arm();
        document.addEventListener("pointermove", watcher, { passive: true });
    };
    const enterEntry = (button, item) => {
        hovered = button;
        if (child && childOwner !== button && popoverIsOpen(child.menu) && isAimingAt(child.menu)) {
            if (!hold) beginHold();
            return;
        }
        if (parents.has(button)) {
            if (!button.disabled) openChild(button, item, { focus: false });
        } else {
            closeChild();
        }
    };

    for (const item of items) {
        if (item === "-") { menu.append(el("div", { class: "ir-menu-sep", role: "separator" })); continue; }
        if (item.heading !== undefined) {
            menu.append(el("div", { class: "ir-menu-heading" }, item.heading));
            continue;
        }
        if (item.note !== undefined) {
            // Invariant: announced as a disabled item; the arrow-key cycle skips it because the
            // focus query targets .ir-menu-item only.
            menu.append(el("div", {
                class: "ir-menu-note", role: "menuitem", "aria-disabled": "true",
            }, item.note));
            continue;
        }
        const parent = Array.isArray(item.items);
        const button = el("button", {
            type: "button",
            class: "ir-menu-item" + (item.checked ? " ir-checked" : "") + (parent ? " ir-menu-parent" : ""),
            role: "menuitem",
            disabled: item.disabled === true,
            ...(parent ? { "aria-haspopup": "menu", "aria-expanded": "false" } : {}),
        }, el("span", { class: "ir-menu-check", "aria-hidden": "true" }, item.checked ? "✓" : ""),
           el("span", { class: "ir-menu-label" }, item.label),
           item.hint ? el("span", { class: "ir-menu-hint" }, item.hint) : null,
           parent ? el("span", { class: "ir-menu-arrow", "aria-hidden": "true" }, "›") : null);
        if (parent) parents.set(button, item);
        button.onpointerenter = () => enterEntry(button, item);
        button.onpointerleave = () => { if (hovered === button) hovered = null; };
        button.onclick = parent ? () => openChild(button, item, { focus: true }) : () => pick(item);
        menu.append(button);
    }

    const restoreAnchor = bindAnchor(anchor, menu, `--ir-popup-anchor-${sequence}`);
    const nativePopover = typeof menu.showPopover === "function";
    const anchored = anchorPositioningAvailable();
    const controller = new AbortController();
    let closed = false;
    let scrollArmed = false;

    const onDocDown = event => {
        const path = event.composedPath?.() ?? [event.target];
        if (!path.includes(menu) && !path.includes(anchor)) closePopups();
    };
    // Interaction rule: scrolling the newly constrained menu (or a submenu inside it) is expected.
    // Scrolling an ancestor invalidates fallback coordinates and closes it as before.
    const onScroll = event => {
        const target = event?.target;
        if (target === menu || (target instanceof Node && menu.contains(target))) return;
        if (scrollArmed) closePopups();
    };
    const cleanup = () => {
        if (closed) return;
        closed = true;
        closeChild();
        controller.abort();
        restoreAnchor();
        menu.remove();
        onClose?.();
    };
    const close = () => {
        if (closed) return;
        hidePopover(menu);
        cleanup();
    };
    const focusFirst = () => ownFocusable(menu)[0]?.focus();
    // Leaving this level closes the stack from here down and hands focus back to the entry (or
    // control) that opened it.
    const closeLevel = () => {
        if (submenu) close(); else closePopups();
        anchor.focus?.();
    };

    menu.addEventListener("keydown", event => {
        // Keys pressed inside an open submenu bubble through here after that level handled them.
        if (event.target.closest(".ir-popup") !== menu) return;
        if (event.key === "Escape") {
            event.preventDefault();
            if (child) { closeChild(); return; }
            closeLevel();
            return;
        }
        if (event.key === "Tab") {
            // Menus are not a tab stop: close, and let the un-prevented Tab continue from the
            // anchor to its natural neighbor.
            closePopups();
            rootAnchor.focus?.();
            return;
        }
        if (event.key === "ArrowRight") {
            const target = event.target.closest(".ir-menu-parent");
            if (!target || target.disabled) return;
            event.preventDefault();
            openChild(target, parents.get(target), { focus: true });
            return;
        }
        if (event.key === "ArrowLeft") {
            if (!submenu) return;
            event.preventDefault();
            closeLevel();
            return;
        }
        if (event.key !== "ArrowDown" && event.key !== "ArrowUp"
            && event.key !== "Home" && event.key !== "End") return;
        const focusable = ownFocusable(menu);
        if (!focusable.length) return;
        event.preventDefault();
        const idx = focusable.indexOf(root.activeElement ?? document.activeElement);
        const next = event.key === "Home" ? 0
            : event.key === "End" ? focusable.length - 1
            : event.key === "ArrowDown" ? (idx + 1) % focusable.length
            : (idx - 1 + focusable.length) % focusable.length;
        closeChild();
        focusable[next].focus();
    });
    // Pressing inside this level but outside its open child closes the child. Native light dismiss
    // does the same; the listener keeps the fallback path consistent.
    menu.addEventListener("mousedown", event => {
        if (!child) return;
        const path = event.composedPath?.() ?? [event.target];
        if (!path.includes(child.menu) && !path.includes(childOwner)) closeChild();
    });
    menu.addEventListener("toggle", event => {
        if (event.target === menu && event.newState === "closed") cleanup();
    });

    mount.append(menu);
    menu.style.visibility = "hidden";
    showPopover(menu, { source: anchor });
    if (!submenu && !nativePopover)
        document.addEventListener("mousedown", onDocDown, { capture: true, signal: controller.signal });

    if (!anchored) {
        if (submenu) {
            placeSubmenuFallback(menu, anchor);
        } else {
            constrainFallbackHeight(menu, anchor);
            placeFallback(menu, anchor);
            requestAnimationFrame(() => { scrollArmed = true; });
            window.addEventListener("scroll", onScroll, { capture: true, signal: controller.signal });
            window.addEventListener("resize", onScroll, { signal: controller.signal });
        }
    }
    menu.style.visibility = "";
    return { menu, close, focusFirst };
}

/**
 * Opens one popup menu anchored to an element. Items may be actions, submenu parents, headings, notes, or
 * the `"-"` separator token.
 *
 * @param {HTMLElement} anchor - The toolbar or column-header control that owns focus and ARIA state.
 * @param {Array<object|string>} items - Action definitions (`label`, `onPick`, `disabled`, `checked`, `hint`),
 *   submenu parents (`label`, `items`, optional `hint`/`disabled`), `{heading}`, `{note}`, or `"-"`.
 * @returns {HTMLDivElement} The mounted menu element.
 *
 * Side effects: closes any previous popup, mounts the menu, changes anchor styles and ARIA attributes, registers abortable listeners, and focuses the first enabled item.
 */
export function popupMenu(anchor, items) {
    closePopups();
    startPointerTracking();
    const root = anchor.getRootNode();
    const mount = root instanceof ShadowRoot ? root : document.body;
    activePopupOwner = root instanceof ShadowRoot ? root.host : null;

    const pick = item => { closePopups(); anchor.focus?.(); item.onPick?.(); };
    let level = null;
    // The active-popup record points at this whole stack; the top level's onClose clears it.
    const close = () => level?.close();
    activePopup = close;
    level = mountLevel(anchor, items, { mount, root, rootAnchor: anchor, pick, onClose: () => {
        if (activePopup === close) {
            activePopup = null;
            activePopupOwner = null;
        }
    } });
    level.focusFirst();
    return level.menu;
}
