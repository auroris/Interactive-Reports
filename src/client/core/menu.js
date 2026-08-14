// Popup menus anchored to a toolbar button or column-header button. Native auto
// popovers own top-layer display, light dismiss, and close requests. CSS anchor
// positioning owns placement where available; the small geometry fallback keeps
// older hosts usable. Menu-specific arrow-key focus remains application behavior.

import { el } from "./dom.js";

let activePopup = null;
let activePopupOwner = null;
let popupSequence = 0;

export function closePopups() {
    const close = activePopup;
    activePopup = null;
    activePopupOwner = null;
    close?.();
}

/// Widget teardown: close the menu only if this host opened it.
export function closeMenuOwnedBy(host) {
    if (activePopupOwner === host) closePopups();
}

const popoverIsOpen = node => {
    try { return node.matches(":popover-open"); }
    catch { return node.hasAttribute("data-ir-popover-open"); }
};

const anchorPositioningAvailable = () =>
    window.CSS?.supports?.("position-anchor: --ir-popup-anchor") === true;

function placeFallback(menu, anchor) {
    const a = anchor.getBoundingClientRect();
    const m = menu.getBoundingClientRect();
    let left = Math.min(a.left, window.innerWidth - m.width - 8);
    let top = a.bottom + 2;
    if (top + m.height > window.innerHeight - 8)
        top = Math.max(8, a.top - m.height - 2);
    menu.style.left = `${Math.max(8, left)}px`;
    menu.style.top = `${top}px`;
}

/**
 * Open a popup menu anchored to an element.
 * items: array of
 *   { label, onPick, disabled?, checked?, hint? }  — an actionable entry
 *   { heading }                                    — a non-interactive section label
 *   "-"                                            — a separator
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
        const btn = el("button", {
            type: "button",
            class: "ir-menu-item" + (item.checked ? " ir-checked" : ""),
            role: "menuitem",
            disabled: item.disabled === true,
            onclick: () => { closePopups(); item.onPick?.(); },
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
    let closed = false;
    let scrollArmed = false;

    const onDocDown = event => {
        const path = event.composedPath?.() ?? [event.target];
        if (!path.includes(menu) && !path.includes(anchor)) closePopups();
    };
    const onScroll = () => { if (scrollArmed) closePopups(); };
    const cleanup = () => {
        if (closed) return;
        closed = true;
        document.removeEventListener("mousedown", onDocDown, true);
        window.removeEventListener("scroll", onScroll, true);
        window.removeEventListener("resize", onScroll);
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
        if (nativePopover && popoverIsOpen(menu)) {
            try { menu.hidePopover(); } catch { /* removal below is the fallback */ }
        }
        cleanup();
    };

    menu.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            event.preventDefault();
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
    if (nativePopover) {
        try { menu.showPopover({ source: anchor }); }
        catch { menu.showPopover(); }
    } else {
        menu.setAttribute("data-ir-popover-open", "");
        document.addEventListener("mousedown", onDocDown, true);
    }

    if (!anchored) {
        placeFallback(menu, anchor);
        requestAnimationFrame(() => { scrollArmed = true; });
        window.addEventListener("scroll", onScroll, true);
        window.addEventListener("resize", onScroll);
    }
    menu.style.visibility = "";
    menu.querySelector(".ir-menu-item:not([disabled])")?.focus();
    return menu;
}
