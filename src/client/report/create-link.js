// Definition-owned create button: toolbar chrome built from the schema payload's createLink
// (url/label/target/mode). Like the edit pencil it is neither report state nor a feature token —
// configuring it is what shows it — and it is independent of the active view, because a new
// record is created the same way whichever shape the report is showing. Every activation
// dispatches `ir-create` from the host; navigate mode then follows the anchor unless the event
// was prevented, and event mode renders a button that never navigates.

import { el, icon } from "../core/dom.js";
import { anchorClickHandler, dispatchLinkEvent, eventMode } from "./link-events.js";
import { safeRendererUrl } from "./render/column-renderers.js";

/**
 * Builds the toolbar create control for the current schema, or null when the definition has none
 * or its URL fails the renderer protocol allowlist.
 *
 * @param {object} w - The report controller: schema, localization, and event target.
 * @returns {HTMLAnchorElement|HTMLButtonElement|null} A detached control, or null when nothing should show.
 *
 * Side effects: creates a detached control; activating it dispatches `ir-create`.
 */
export function renderCreateButton(w) {
    const createLink = w.schema?.createLink;
    if (!createLink) return null;
    const label = createLink.label ?? w.t("toolbar.create");
    const url = createLink.url == null ? null : safeRendererUrl(createLink.url, "link");
    const detail = () => ({ url });
    if (eventMode(createLink)) {
        return el("button", {
            type: "button",
            class: "ir-btn ir-btn-primary ir-createbtn",
            title: label,
            onclick: () => dispatchLinkEvent(w, "ir-create", detail()),
        }, icon("plus"), label);
    }
    if (!url) return null;
    const blank = createLink.target === "_blank";
    return el("a", {
        class: "ir-btn ir-btn-primary ir-createbtn",
        href: url,
        title: label,
        target: blank ? "_blank" : undefined,
        rel: blank ? "noopener" : undefined,
        onclick: anchorClickHandler(w, "ir-create", detail),
    }, icon("plus"), label);
}

/**
 * Fits the toolbar's create slot to the current schema: one control, or nothing.
 *
 * @param {object} w - The report controller whose `els.createSlot` receives the control.
 * @returns {void} No value.
 *
 * Side effects: replaces the slot's children and toggles its visibility.
 */
export function applyCreateButton(w) {
    const slot = w.els?.createSlot;
    if (!slot) return;
    const control = renderCreateButton(w);
    slot.replaceChildren(...(control ? [control] : []));
    slot.hidden = !control;
}
