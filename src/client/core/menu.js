// Popup-menu coordinator for toolbar and column-header controls. Native auto
// popovers own top-layer display, light dismiss, and close requests. CSS anchor positioning
// owns placement where available; the small geometry fallback keeps older hosts usable.
// Menu-specific arrow-key focus remains application behavior.

import { el } from "./dom.js";
import { hidePopover, showPopover } from "./popover.js";

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
 * Opens one popup menu anchored to an element. Items may be actions, headings, notes, or the `"-"` separator token.
 *
 * @param {HTMLElement} anchor - The toolbar or column-header control that owns focus and ARIA state.
 * @param {Array<object|string>} items - Action definitions (`label`, `onPick`, `disabled`, `checked`, `hint`), `{heading}`, `{note}`, or `"-"`.
 * @returns {HTMLDivElement} The mounted menu element.
 *
 * Side effects: closes any previous popup, mounts the menu, changes anchor styles and ARIA attributes, registers abortable listeners, and focuses the first enabled item.
 */
export function popupMenu(anchor, items) {
    closePopups();
    const id = `ir-popup-${++popupSequence}`;
    const menu = el("div", {
        id, class: "ir-popup", part: "menu", role: "menu", popover: "auto",
    });
    const root = anchor.getRootNode();
    const mount = root instanceof ShadowRoot ? root : document.body;
    activePopupOwner = root instanceof ShadowRoot ? root.host : null;

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
        const btn = el("button", {
            type: "button",
            class: "ir-menu-item" + (item.checked ? " ir-checked" : ""),
            role: "menuitem",
            disabled: item.disabled === true,
            onclick: () => { closePopups(); anchor.focus?.(); item.onPick?.(); },
        }, el("span", { class: "ir-menu-check", "aria-hidden": "true" }, item.checked ? "✓" : ""),
           el("span", { class: "ir-menu-label" }, item.label),
           item.hint ? el("span", { class: "ir-menu-hint" }, item.hint) : null);
        menu.append(btn);
    }

    const anchorName = `--ir-popup-anchor-${popupSequence}`;
    const oldAnchorName = anchor.style.getPropertyValue("anchor-name");
    const oldAnchorPriority = anchor.style.getPropertyPriority("anchor-name");
    anchor.style.setProperty("anchor-name", anchorName);
    menu.style.setProperty("position-anchor", anchorName);
    anchor.setAttribute("aria-haspopup", "menu");
    anchor.setAttribute("aria-expanded", "true");
    anchor.setAttribute("aria-controls", id);

    const nativePopover = typeof menu.showPopover === "function";
    const anchored = anchorPositioningAvailable();
    const controller = new AbortController();
    let closed = false;
    let scrollArmed = false;

    const onDocDown = event => {
        const path = event.composedPath?.() ?? [event.target];
        if (!path.includes(menu) && !path.includes(anchor)) closePopups();
    };
    // Interaction rule: scrolling the newly constrained menu is expected. Scrolling an ancestor
    // invalidates fallback coordinates and closes it as before.
    const onScroll = event => {
        if (event?.target === menu) return;
        if (scrollArmed) closePopups();
    };
    const cleanup = () => {
        if (closed) return;
        closed = true;
        controller.abort();
        anchor.setAttribute("aria-expanded", "false");
        anchor.removeAttribute("aria-controls");
        if (oldAnchorName) anchor.style.setProperty("anchor-name", oldAnchorName, oldAnchorPriority);
        else anchor.style.removeProperty("anchor-name");
        if (activePopup === close) {
            activePopup = null;
            activePopupOwner = null;
        }
        menu.remove();
    };
    const close = () => {
        if (closed) return;
        hidePopover(menu);
        cleanup();
    };

    menu.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            event.preventDefault();
            closePopups();
            anchor.focus?.();
            return;
        }
        if (event.key === "Tab") {
            // Menus are not a tab stop: close, and let the un-prevented Tab continue from the
            // anchor to its natural neighbor.
            closePopups();
            anchor.focus?.();
            return;
        }
        if (event.key !== "ArrowDown" && event.key !== "ArrowUp"
            && event.key !== "Home" && event.key !== "End") return;
        const focusable = [...menu.querySelectorAll(".ir-menu-item:not([disabled])")];
        if (!focusable.length) return;
        event.preventDefault();
        const idx = focusable.indexOf(root.activeElement ?? document.activeElement);
        const next = event.key === "Home" ? 0
            : event.key === "End" ? focusable.length - 1
            : event.key === "ArrowDown" ? (idx + 1) % focusable.length
            : (idx - 1 + focusable.length) % focusable.length;
        focusable[next].focus();
    });
    menu.addEventListener("toggle", event => {
        if (event.newState === "closed") cleanup();
    });

    activePopup = close;
    mount.append(menu);
    menu.style.visibility = "hidden";
    showPopover(menu, { source: anchor });
    if (!nativePopover)
        document.addEventListener("mousedown", onDocDown, { capture: true, signal: controller.signal });

    if (!anchored) {
        constrainFallbackHeight(menu, anchor);
        placeFallback(menu, anchor);
        requestAnimationFrame(() => { scrollArmed = true; });
        window.addEventListener("scroll", onScroll, { capture: true, signal: controller.signal });
        window.addEventListener("resize", onScroll, { signal: controller.signal });
    }
    menu.style.visibility = "";
    menu.querySelector(".ir-menu-item:not([disabled])")?.focus();
    return menu;
}
