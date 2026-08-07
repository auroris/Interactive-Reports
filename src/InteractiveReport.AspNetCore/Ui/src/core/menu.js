// Popup menus anchored to a toolbar button or column header. One menu is open at
// a time; document-level listeners close it on outside pointer, Escape, scroll,
// or resize, and arrow keys move focus through the items.

import { el } from "./dom.js";

let activePopup = null;
let activePopupOwner = null;

export function closePopups() {
    activePopup?.();
    activePopup = null;
    activePopupOwner = null;
}

/// Widget teardown: close the menu only if this host opened it.
export function closeMenuOwnedBy(host) {
    if (activePopupOwner === host) closePopups();
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
    const menu = el("div", { class: "ir-popup", part: "menu", role: "menu" });
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

    mount.append(menu);

    // Fixed positioning against the viewport; flip when it would overflow.
    const a = anchor.getBoundingClientRect();
    menu.style.position = "fixed";
    menu.style.visibility = "hidden";
    menu.style.left = "0"; menu.style.top = "0";
    const m = menu.getBoundingClientRect();
    let left = Math.min(a.left, window.innerWidth - m.width - 8);
    let top = a.bottom + 2;
    if (top + m.height > window.innerHeight - 8) top = Math.max(8, a.top - m.height - 2);
    menu.style.left = `${Math.max(8, left)}px`;
    menu.style.top = `${top}px`;
    menu.style.visibility = "";

    const onDocDown = e => {
        const path = e.composedPath?.() ?? [e.target];
        if (!path.includes(menu) && !path.includes(anchor)) closePopups();
    };
    const onKey = e => {
        if (e.key === "Escape") { closePopups(); anchor.focus?.(); return; }
        if (e.key !== "ArrowDown" && e.key !== "ArrowUp" && e.key !== "Home" && e.key !== "End") return;
        const focusable = [...menu.querySelectorAll(".ir-menu-item:not([disabled])")];
        if (!focusable.length) return;
        e.preventDefault();
        const idx = focusable.indexOf(root.activeElement ?? document.activeElement);
        const next = e.key === "Home" ? 0
            : e.key === "End" ? focusable.length - 1
            : e.key === "ArrowDown" ? (idx + 1) % focusable.length
            : (idx - 1 + focusable.length) % focusable.length;
        focusable[next].focus();
    };
    // Scroll closes the menu — but scroll events already in flight when it opened
    // (e.g. the page just jumped) must not kill it on arrival.
    let scrollArmed = false;
    requestAnimationFrame(() => { scrollArmed = true; });
    const onScroll = () => { if (scrollArmed) closePopups(); };

    document.addEventListener("mousedown", onDocDown, true);
    document.addEventListener("keydown", onKey, true);
    window.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", onScroll);

    activePopup = () => {
        document.removeEventListener("mousedown", onDocDown, true);
        document.removeEventListener("keydown", onKey, true);
        window.removeEventListener("scroll", onScroll, true);
        window.removeEventListener("resize", onScroll);
        menu.remove();
    };

    menu.querySelector(".ir-menu-item:not([disabled])")?.focus();
    return menu;
}
