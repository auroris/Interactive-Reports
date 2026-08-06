// DOM primitives for the report UI: element builder, popup menus, modal dialogs.
// Everything renders through createElement/textContent — report data never passes
// through innerHTML (the only innerHTML below is our own static icon markup).

import cssText from "./ir.css";

export function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
        if (v === undefined || v === null) continue;
        if (k === "class") node.className = v;
        else if (k === "part") node.setAttribute("part", v);
        else if (k === "for") node.htmlFor = v;
        else if (k === "dataset") Object.assign(node.dataset, v);
        else if (k === "style") Object.assign(node.style, v);
        else if (k.startsWith("on")) node[k] = v;
        else if (k in node) node[k] = v;
        else node.setAttribute(k, v);
    }
    node.append(...children.flat(Infinity).filter(c => c !== null && c !== undefined && c !== false));
    return node;
}

/// Give each widget an isolated rendering boundary. Keeping both the DOM and the
/// stylesheet in the shadow root prevents host-page resets and utility classes
/// from leaking in, and prevents the widget's rules from leaking out.
export function createWidgetRoot(host) {
    const root = host.attachShadow({ mode: "open" });
    const style = el("style", { "data-ir-styles": "" });
    style.textContent = cssText;
    const mount = el("div", { part: "surface" });
    root.append(style, mount);
    return { root, mount };
}

const ICONS = {
    search: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><circle cx="6.5" cy="6.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.6"/><line x1="10" y1="10" x2="14" y2="14" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',
    caret: '<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 5.5 8 11l5-5.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    grid: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M1.5 9.5h13 M6 2.5v11 M11 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>',
    group: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="1.5" y="2" width="6" height="3" fill="currentColor" opacity=".55"/><rect x="1.5" y="6.5" width="10" height="3" fill="currentColor" opacity=".8"/><rect x="1.5" y="11" width="13" height="3" fill="currentColor"/></svg>',
    pivot: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M6 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/><circle cx="10.5" cy="10" r="1.4" fill="currentColor"/></svg>',
    close: '<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 3l10 10M13 3L3 13" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
};

export function icon(name) {
    const span = el("span", { class: "ir-icon", "aria-hidden": "true" });
    span.innerHTML = ICONS[name] ?? "";
    return span;
}

/// Notice banner: kinds error | warn | ok. Pass onDismiss for a close button.
export function banner(kind, text, onDismiss) {
    return el("div", { class: `ir-banner ir-banner-${kind}` },
        el("span", { class: "ir-banner-text" }, text),
        onDismiss ? el("button", {
            type: "button", class: "ir-banner-x", "aria-label": "Dismiss", onclick: onDismiss,
        }, icon("close")) : null);
}

// --- popup menus -------------------------------------------------------------

let activePopup = null;
let activePopupOwner = null;
const dialogsByOwner = new WeakMap();

export function closePopups() {
    activePopup?.();
    activePopup = null;
    activePopupOwner = null;
}

/// Release document-level listeners and transient UI when a host framework
/// removes a component from the page.
export function disposeWidget(host) {
    if (activePopupOwner === host) closePopups();
    for (const dialog of [...(dialogsByOwner.get(host) ?? [])]) dialog.close();
    dialogsByOwner.delete(host);
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

// --- modal dialogs -----------------------------------------------------------

/**
 * Open a modal dialog. build(body, dlg) fills the content; onApply(dlg) runs on the
 * primary button — return a promise and the dialog closes on success or shows the
 * error (ApiError-aware) and stays open on failure. Omit onApply for a plain
 * informational dialog with a single Close button.
 */
export function openDialog({ owner, title, width, cls, build, applyLabel = "Apply", onApply, destructive = false }) {
    closePopups();
    const root = owner?.shadowRoot ?? document;
    const mount = root instanceof ShadowRoot ? root : document.body;
    const restoreFocus = root.activeElement ?? document.activeElement;

    const errorBox = el("div", { class: "ir-dialog-error", hidden: true });
    const body = el("div", { class: "ir-dialog-body" });
    let ownedDialogs = null;
    if (owner) {
        ownedDialogs = dialogsByOwner.get(owner) ?? new Set();
        dialogsByOwner.set(owner, ownedDialogs);
    }
    let closed = false;

    const dlg = {
        root: null,
        body,
        close() {
            if (closed) return;
            closed = true;
            dlg.root.remove();
            document.removeEventListener("keydown", onKey, true);
            ownedDialogs?.delete(dlg);
            restoreFocus?.focus?.();
        },
        setError(err) {
            errorBox.replaceChildren();
            if (err == null) { errorBox.hidden = true; return; }
            const messages = [];
            if (err.errors && typeof err.errors === "object") {
                if (err.problem?.title) messages.push(err.problem.title);
                for (const list of Object.values(err.errors)) messages.push(...list);
            } else {
                messages.push(typeof err === "string" ? err : err.message || "Something went wrong.");
            }
            errorBox.append(...messages.map(m => el("div", {}, m)));
            errorBox.hidden = false;
        },
    };

    const applyBtn = onApply
        ? el("button", {
            type: "button",
            class: "ir-btn ir-btn-primary" + (destructive ? " ir-btn-danger" : ""),
            onclick: async () => {
                dlg.setError(null);
                const buttons = dlg.root.querySelectorAll(".ir-dialog-footer button");
                buttons.forEach(b => b.disabled = true);
                try {
                    await onApply(dlg);
                    dlg.close();
                } catch (err) {
                    dlg.setError(err);
                } finally {
                    buttons.forEach(b => b.disabled = false);
                }
            },
        }, applyLabel)
        : null;

    const cancelBtn = el("button", {
        type: "button",
        class: "ir-btn",
        onclick: () => dlg.close(),
    }, onApply ? "Cancel" : "Close");

    dlg.root = el("div", { class: "ir-overlay" + (cls ? ` ${cls}` : ""), part: "dialog-overlay" },
        el("div", { class: "ir-dialog", part: "dialog", role: "dialog", "aria-modal": "true", style: width ? { width } : {} },
            el("div", { class: "ir-dialog-title" }, title,
                el("button", { type: "button", class: "ir-dialog-x", "aria-label": "Close", onclick: () => dlg.close() }, icon("close"))),
            errorBox,
            body,
            el("div", { class: "ir-dialog-footer" }, cancelBtn, applyBtn)));

    const onKey = e => {
        if (e.key === "Escape") { e.stopPropagation(); dlg.close(); return; }
        const target = e.composedPath?.()[0] ?? e.target;
        if (e.key === "Enter" && applyBtn && target.tagName !== "TEXTAREA" && target.tagName !== "BUTTON") {
            e.preventDefault();
            applyBtn.click();
            return;
        }
        if (e.key !== "Tab") return;
        // Cycle focus inside the dialog.
        const focusable = [...dlg.root.querySelectorAll("button, input, select, textarea, [tabindex]")]
            .filter(n => !n.disabled && n.offsetParent !== null);
        if (!focusable.length) return;
        const first = focusable[0], last = focusable[focusable.length - 1];
        const activeElement = root.activeElement ?? document.activeElement;
        if (e.shiftKey && activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && activeElement === last) { e.preventDefault(); first.focus(); }
    };
    document.addEventListener("keydown", onKey, true);

    build(body, dlg);
    mount.append(dlg.root);
    ownedDialogs?.add(dlg);
    (body.querySelector("input, select, textarea") ?? applyBtn ?? cancelBtn).focus();
    return dlg;
}

export function confirmDialog(owner, title, message, confirmLabel = "Delete") {
    return new Promise(resolve => {
        let confirmed = false;
        const dlg = openDialog({
            owner,
            title,
            width: "26rem",
            applyLabel: confirmLabel,
            destructive: true,
            build: body => body.append(el("p", { class: "ir-confirm-text" }, message)),
            onApply: () => { confirmed = true; },
        });
        const origClose = dlg.close;
        dlg.close = () => { origClose(); resolve(confirmed); };
    });
}

// --- small form helpers ------------------------------------------------------

export function labeled(text, control, opts = {}) {
    return el("label", { class: "ir-field" + (opts.inline ? " ir-field-inline" : "") },
        el("span", { class: "ir-field-label" }, text), control);
}

/// options: array of strings or {value, label}; groups: [{label, options}] also allowed.
export function sel(options, value) {
    const node = el("select", { class: "ir-select" });
    const opt = o => typeof o === "string" ? new Option(o, o) : new Option(o.label, o.value);
    for (const o of options) {
        if (o.options) {
            const g = el("optgroup", { label: o.label });
            g.append(...o.options.map(opt));
            node.append(g);
        } else node.append(opt(o));
    }
    if (value !== undefined && value !== null) node.value = value;
    return node;
}
