// Modal dialogs. Each dialog is owned by the widget that opened it so widget
// teardown can close whatever is still up; the apply button is ApiError-aware
// and keeps the dialog open showing the precise validation problem on failure.

import { el, icon } from "./dom.js";
import { closePopups } from "./menu.js";

const dialogsByOwner = new WeakMap();

/// Widget teardown: close every dialog this host still owns.
export function closeDialogsOwnedBy(host) {
    for (const dialog of [...(dialogsByOwner.get(host) ?? [])]) dialog.close();
    dialogsByOwner.delete(host);
}

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
                    const applied = await onApply(dlg);
                    if (applied !== false) dlg.close();
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
