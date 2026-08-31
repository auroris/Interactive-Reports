// Dialog-window coordinator. Editors are modeless manual popovers: they stay in the widget's
// shadow root, enter the browser top layer, and leave the report below them interactive. Destructive
// confirmations use a native modal <dialog>.

import { errorLines } from "./api.js";
import { el, icon } from "./dom.js";
import { errorReference, translate } from "./localization.js";
import { closePopups } from "./menu.js";
import { hidePopover, popoverIsOpen, showPopover } from "./popover.js";

const dialogsByOwner = new WeakMap();
const openDialogs = [];
let titleSequence = 0;
let fallbackZ = 1100;

/**
 * Detects the compact layout in which dialogs are not independently positioned.
 *
 * @returns {boolean} Whether the viewport is at most 640 CSS pixels wide.
 */
const compactWindow = () => window.matchMedia?.("(max-width: 640px)").matches === true;
/**
 * Constrains a numeric value to the supplied inclusive bounds.
 *
 * @param {number} value - The numeric value to constrain.
 * @param {number} min - The inclusive lower bound for the clamped value.
 * @param {number} max - The inclusive upper bound for the clamped value.
 * @returns {number} `value` constrained to the inclusive interval.
 */
const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

/**
 * Returns the viewport rectangle available for dialog placement.
 *
 * @returns {{left: number, top: number, right: number, bottom: number}} Visual-viewport bounds, falling back to layout-viewport dimensions.
 */
function viewportBounds() {
    const viewport = window.visualViewport;
    const left = viewport?.offsetLeft ?? 0;
    const top = viewport?.offsetTop ?? 0;
    const width = viewport?.width ?? window.innerWidth;
    const height = viewport?.height ?? window.innerHeight;
    return { left, top, right: left + width, bottom: top + height };
}

/**
 * Promotes an already-open modeless dialog to the end of the visual stacking order.
 *
 * @param {object} dlg - The dialog controller to promote.
 * @returns {void} No value.
 *
 * Side effects: reorders the open-dialog registry, advances fallback z-index, and may re-show a native popover while preserving focus.
 */
function activateDialog(dlg) {
    const index = openDialogs.indexOf(dlg);
    if (index < 0 || index === openDialogs.length - 1) return;
    openDialogs.splice(index, 1);
    openDialogs.push(dlg);
    dlg.root.style.zIndex = String(++fallbackZ);

    // Top-layer entries paint in insertion order; re-showing a manual popover promotes it
    // without moving its DOM or losing its component-owned styles.
    if (!dlg.modal && typeof dlg.root.showPopover === "function" && popoverIsOpen(dlg.root)) {
        const focused = dlg.root.getRootNode().activeElement;
        dlg.root.hidePopover();
        dlg.root.showPopover();
        if (dlg.root.contains(focused)) focused.focus?.({ preventScroll: true });
    }
}

/**
 * Removes a dialog from the open-dialog registry and updates active styling.
 *
 * @param {object} dlg - The dialog controller to remove.
 * @returns {void} No value.
 */
function removeOpenDialog(dlg) {
    const index = openDialogs.indexOf(dlg);
    if (index >= 0) openDialogs.splice(index, 1);
}

/**
 * Returns focus to the element that was active before the dialog opened.
 *
 * @param {HTMLElement|null} node - The element focused before the dialog opened.
 * @returns {void} No value.
 *
 * Side effects: focuses the prior element immediately or retries once on the next animation frame.
 */
function restorePreviousFocus(node) {
    const focus = () => {
        if (!node?.isConnected || node.disabled) return false;
        node.focus?.({ preventScroll: true });
        return true;
    };
    // A confirmation can be opened while its parent Apply button is disabled. Let that async
    // handler finish before making a second restoration attempt.
    if (!focus()) window.requestAnimationFrame?.(() => focus());
}

/**
 * Places a dialog within the viewport near its requested coordinates.
 *
 * @param {object} dlg - The modeless dialog controller to position.
 * @param {number} requestedLeft - The preferred horizontal dialog position in viewport coordinates.
 * @param {number} requestedTop - The preferred vertical dialog position in viewport coordinates.
 * @returns {void} No value.
 *
 * Side effects: writes viewport-constrained CSS position variables and marks the dialog as moved.
 */
function placeWindow(dlg, requestedLeft, requestedTop) {
    if (dlg.modal || compactWindow()) return;
    const rect = dlg.root.getBoundingClientRect();
    const bounds = viewportBounds();
    const titleHeight = dlg.titleBar.getBoundingClientRect().height || 36;
    const maxLeft = Math.max(bounds.left, bounds.right - rect.width);
    const maxTop = Math.max(bounds.top, bounds.bottom - titleHeight);
    const left = clamp(requestedLeft, bounds.left, maxLeft);
    const top = clamp(requestedTop, bounds.top, maxTop);
    dlg.root.style.setProperty("--ir-win-left", `${left}px`);
    dlg.root.style.setProperty("--ir-win-top", `${top}px`);
    dlg.root.classList.add("ir-moved");
    dlg.moved = true;
}

/**
 * Returns the dialog's current viewport-relative position.
 *
 * @param {object} dlg - The dialog controller whose root rectangle will be measured.
 * @returns {{left: number, top: number}} The root's current viewport coordinates.
 */
function currentWindowPosition(dlg) {
    const rect = dlg.root.getBoundingClientRect();
    return { left: rect.left, top: rect.top };
}

/**
 * Adds pointer and Alt+Arrow keyboard movement to a modeless dialog title bar.
 *
 * @param {object} dlg - The dialog controller whose title bar receives movement handlers.
 * @returns {void} No value.
 *
 * Side effects: registers title-bar listeners and mutates dialog position, pointer capture, and dragging classes during interaction.
 */
function makeDraggable(dlg) {
    let drag = null;
    const titleBar = dlg.titleBar;

    const endDrag = event => {
        if (!drag || (event.pointerId !== undefined && event.pointerId !== drag.pointerId)) return;
        if (titleBar.hasPointerCapture?.(drag.pointerId)) titleBar.releasePointerCapture(drag.pointerId);
        drag = null;
        titleBar.classList.remove("ir-dragging");
    };

    titleBar.addEventListener("pointerdown", event => {
        if (compactWindow() || event.button !== 0 || event.target.closest("button")) return;
        activateDialog(dlg);
        const rect = dlg.root.getBoundingClientRect();
        drag = {
            pointerId: event.pointerId,
            offsetX: event.clientX - rect.left,
            offsetY: event.clientY - rect.top,
        };
        placeWindow(dlg, rect.left, rect.top);
        titleBar.setPointerCapture?.(event.pointerId);
        titleBar.classList.add("ir-dragging");
        titleBar.focus({ preventScroll: true });
        event.preventDefault();
    });
    titleBar.addEventListener("pointermove", event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        placeWindow(dlg, event.clientX - drag.offsetX, event.clientY - drag.offsetY);
    });
    titleBar.addEventListener("pointerup", endDrag);
    titleBar.addEventListener("pointercancel", endDrag);
    titleBar.addEventListener("lostpointercapture", () => {
        drag = null;
        titleBar.classList.remove("ir-dragging");
    });
    titleBar.addEventListener("keydown", event => {
        if (compactWindow() || !event.altKey || !event.key.startsWith("Arrow")) return;
        const delta = event.shiftKey ? 1 : 10;
        const position = currentWindowPosition(dlg);
        const left = position.left + (event.key === "ArrowRight" ? delta : event.key === "ArrowLeft" ? -delta : 0);
        const top = position.top + (event.key === "ArrowDown" ? delta : event.key === "ArrowUp" ? -delta : 0);
        placeWindow(dlg, left, top);
        event.preventDefault();
        event.stopPropagation();
    });
}

/**
 * Closes every still-open dialog owned by one widget and removes its ownership set.
 *
 * @param {HTMLElement} host - The custom-element host being disconnected.
 * @returns {void} No value.
 *
 * Side effects: closes dialogs, aborts their listeners, removes their DOM, and restores prior focus where possible.
 */
export function closeDialogsOwnedBy(host) {
    for (const dialog of [...(dialogsByOwner.get(host) ?? [])]) dialog.close();
    dialogsByOwner.delete(host);
}

/**
 * Opens an editor or confirmation window. `build(body, dlg)` fills the content; `onApply(dlg)` runs on the
 * primary button. Return a promise and the window closes on success or shows the precise error and
 * stays open on failure. Omit onApply for a plain informational window. modal is reserved for short
 * confirmations.
 *
 * @param {{owner?: HTMLElement, title: string, width?: string, cls?: string, build: Function, applyLabel?: string, onApply?: Function, destructive?: boolean, modal?: boolean}} options - Ownership, content builder, apply behavior, sizing, and modality.
 * @returns {object} The mounted dialog controller, including `root`, `body`, `titleBar`, `close()`, and `setError()`.
 *
 * Side effects: closes popup menus, mounts a dialog, registers abortable global and local listeners, manages focus, and invokes the content builder.
 */
export function openDialog({
    owner,
    title,
    width,
    cls,
    build,
    applyLabel,
    onApply,
    destructive = false,
    modal = false,
}) {
    closePopups();
    const root = owner?.shadowRoot ?? document;
    const mount = root instanceof ShadowRoot ? root : document.body;
    const restoreFocus = root.activeElement ?? document.activeElement;
    const titleId = `ir-dialog-title-${++titleSequence}`;

    const errorBox = el("div", {
        class: "ir-dialog-error ir-banner-error", hidden: true, role: "alert", "aria-atomic": "true",
    });
    const body = el("div", { class: "ir-dialog-body" });
    let ownedDialogs = null;
    if (owner) {
        ownedDialogs = dialogsByOwner.get(owner) ?? new Set();
        dialogsByOwner.set(owner, ownedDialogs);
    }
    const controller = new AbortController();
    let closed = false;

    const dlg = {
        root: null,
        body,
        titleBar: null,
        modal,
        moved: false,
        /**
         * Closes this dialog once, aborts its listeners, removes ownership records, and restores prior focus.
         * @returns {void} No value.
         */
        close() {
            if (closed) return;
            closed = true;
            if (modal && dlg.root.open) dlg.root.close();
            else if (!modal) hidePopover(dlg.root);
            dlg.root.remove();
            controller.abort();
            removeOpenDialog(dlg);
            ownedDialogs?.delete(dlg);
            restorePreviousFocus(restoreFocus);
        },
        /**
         * Replaces the dialog error region with normalized messages and an optional trace reference.
         * @param {Error|string|object|null} err - The failure to display, or `null` to clear the region.
         * @returns {void} No value.
         */
        setError(err) {
            errorBox.replaceChildren();
            if (err == null) { errorBox.hidden = true; return; }
            const messages = errorLines(err, owner);
            if (err?.traceId) messages.push(errorReference(err.traceId, owner));
            errorBox.append(...messages.map(message => el("div", {}, message)));
            errorBox.hidden = false;
        },
    };

    let applying = false;
    // Runs at most one apply callback at a time, disables footer actions during the await,
    // closes on any result other than false, and renders thrown failures in the dialog.
    const runApply = async () => {
        if (!onApply || applying) return;
        applying = true;
        dlg.setError(null);
        const buttons = dlg.root.querySelectorAll(".ir-dialog-footer button");
        buttons.forEach(button => { button.disabled = true; });
        try {
            const applied = await onApply(dlg);
            if (applied !== false) dlg.close();
        } catch (err) {
            dlg.setError(err);
        } finally {
            applying = false;
            buttons.forEach(button => { button.disabled = false; });
        }
    };
    const applyBtn = onApply
        ? el("button", {
            type: "submit",
            class: "ir-btn ir-btn-primary" + (destructive ? " ir-btn-danger" : ""),
        }, applyLabel ?? translate(owner, "common.apply"))
        : null;

    const cancelBtn = el("button", {
        type: "button",
        class: "ir-btn",
        onclick: () => dlg.close(),
    }, translate(owner, onApply ? "common.cancel" : "common.close"));
    const closeBtn = el("button", {
        type: "button",
        class: "ir-dialog-x",
        "aria-label": translate(owner, "common.close"),
        onclick: () => dlg.close(),
    }, icon("close"));
    const titleBar = el("div", {
        class: "ir-dialog-title" + (modal ? "" : " ir-dialog-title-draggable"),
        tabIndex: modal ? undefined : 0,
        "aria-label": modal ? undefined : translate(owner, "dialog.moveWindow", { title }),
    }, el("span", { id: titleId, class: "ir-dialog-title-text" }, title), closeBtn);
    dlg.titleBar = titleBar;

    const dialogProps = {
        class: "ir-dialog" + (cls ? ` ${cls}` : "") + (modal ? " ir-dialog-modal" : ""),
        part: "dialog",
        "aria-labelledby": titleId,
    };
    if (modal) dialogProps["aria-modal"] = "true";
    else {
        dialogProps.role = "dialog";
        dialogProps.popover = "manual";
    }
    const form = el("form", {
        class: "ir-dialog-form",
        onsubmit: event => { event.preventDefault(); void runApply(); },
    }, body, el("div", { class: "ir-dialog-footer" }, cancelBtn, applyBtn));
    dlg.root = el(modal ? "dialog" : "div", dialogProps,
        titleBar,
        errorBox,
        form);
    if (width) dlg.root.style.setProperty("--ir-dialog-width", width);

    const onKey = event => {
        if (openDialogs[openDialogs.length - 1] !== dlg) return;
        if (event.key === "Escape") {
            event.preventDefault();
            event.stopImmediatePropagation();
            dlg.close();
            return;
        }
    };
    const onResize = () => {
        if (!dlg.moved || compactWindow()) return;
        const position = currentWindowPosition(dlg);
        placeWindow(dlg, position.left, position.top);
    };

    dlg.root.addEventListener("pointerdown", () => activateDialog(dlg), true);
    dlg.root.addEventListener("focusin", () => activateDialog(dlg));
    if (modal) dlg.root.addEventListener("cancel", event => {
        event.preventDefault();
        dlg.close();
    });
    else makeDraggable(dlg);
    document.addEventListener("keydown", onKey, { capture: true, signal: controller.signal });
    window.addEventListener("resize", onResize, { signal: controller.signal });
    window.visualViewport?.addEventListener("resize", onResize, { signal: controller.signal });

    build(body, dlg);
    mount.append(dlg.root);
    ownedDialogs?.add(dlg);
    openDialogs.push(dlg);
    dlg.root.style.zIndex = String(++fallbackZ);
    if (modal) dlg.root.showModal();
    else showPopover(dlg.root);
    (body.querySelector("input, select, textarea") ?? applyBtn ?? cancelBtn).focus();
    return dlg;
}

/**
 * Opens a destructive confirmation dialog and resolves whether the user confirmed it.
 *
 * @param {HTMLElement|object} owner - The widget or localization context that owns the confirmation.
 * @param {string} title - The title displayed by the confirmation dialog.
 * @param {string} message - The confirmation message.
 * @param {string|null} [confirmLabel=null] - The destructive action label, or the localized Delete label by default.
 * @returns {Promise<boolean>} Resolves `true` only when the apply action closed the dialog; cancellation resolves `false`.
 *
 * Side effects: opens a modal dialog, registers listeners, and restores focus when it closes.
 */
export function confirmDialog(owner, title, message, confirmLabel = null) {
    return new Promise(resolve => {
        let confirmed = false;
        const dlg = openDialog({
            owner,
            title,
            width: "26rem",
            applyLabel: confirmLabel ?? translate(owner, "common.delete"),
            destructive: true,
            modal: true,
            build: body => body.append(el("p", { class: "ir-confirm-text" }, message)),
            onApply: () => { confirmed = true; },
        });
        dlg.root.addEventListener("close", () => resolve(confirmed), { once: true });
    });
}
