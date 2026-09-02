// Definition-owned per-row edit links: a leading synthetic grid column
// built from the schema payload's editLink (urlTemplate/label/target/mode). Definition chrome, not a
// ColumnFormat renderer. It exists independent of the document's column selection and uses
// template columns the server projects as hidden row data. Substituted values are URL-encoded
// and the result still passes the renderer protocol allowlist, so row data can never smuggle a
// scheme. Every activation dispatches `ir-edit` from the host; navigate mode then follows the
// anchor unless the event was prevented, and event mode renders a button that never navigates.

import { el, icon } from "../../core/dom.js";
import { translate } from "../../core/localization.js";
import { anchorClickHandler, dispatchLinkEvent, eventMode } from "../link-events.js";
import { safeRendererUrl } from "./column-renderers.js";

/**
 * The active edit link, or null. The pencil is a grid-row affordance: grouped, pivoted, and charted
 * rows have no single source row to edit.
 *
 * @param {object} w - The report controller whose schema may declare an edit link.
 * @param {string} mode - The active terminal mode.
 * @returns {object|null} The active grid edit-link definition, or null outside grid mode.
 */
export function activeEditLink(w, mode) {
    return mode === "grid" ? (w.schema?.editLink ?? null) : null;
}

// Protocol contract: substitutes {COLUMN} placeholders with the row's URL-encoded values.
// Returns null when any referenced value is null or missing; the row renders no pencil,
// mirroring the action renderer's blank-label convention. Placeholders arrive canonical-cased
// from the server; the case-insensitive fallback keeps hand-written hosts working.
/**
 * Replaces edit-link placeholders with encoded values from the result row.
 *
 * @param {string} template - The edit URL template containing row-value placeholders.
 * @param {object} row - The result row supplying placeholder values, matched with a case-insensitive fallback.
 * @returns {string|null} The substituted URL, or null when a required row value is absent.
 */
export function substituteEditUrl(template, row) {
    let missing = false;
    const url = String(template).replace(/\{([^{}]+)\}/g, (_, name) => {
        let value = row[name];
        if (value === undefined && !(name in row)) {
            const requested = name.toLowerCase();
            const key = Object.keys(row).find(candidate => candidate.toLowerCase() === requested);
            value = key === undefined ? undefined : row[key];
        }
        if (value === null || value === undefined) {
            missing = true;
            return "";
        }
        return encodeURIComponent(String(value));
    });
    return missing ? null : url;
}

/**
 * The cell content for one row: a pencil anchor or button, or "" when the row withholds its link.
 * Navigate mode is a real href whose click first dispatches `ir-edit`; middle-click, ctrl-click,
 * and open-in-new-tab behave natively unless a listener prevents the event. Event mode is a
 * button whose only behavior is that event, for hosts that open their own editor.
 *
 * @param {object} editLink - The edit-link definition used to build the row-specific control.
 * @param {object} row - The result row supplying URL-template values and the event's row copy.
 * @param {object} w - The report controller or host element: localization context and event target.
 * @returns {string|HTMLAnchorElement|HTMLButtonElement} A detached pencil control, or an empty string when substitution or protocol validation fails.
 *
 * Side effects: creates a detached control and icon when a safe link is available; activating it dispatches `ir-edit`.
 */
export function renderEditCell(editLink, row, w) {
    const url = substituteEditUrl(editLink.urlTemplate, row);
    // The allowlist applies in both modes: the URL reaches the host's handler in event mode, and
    // a definition that cannot navigate should not hand out a scheme it could not follow itself.
    const href = url === null ? null : safeRendererUrl(url, "link");
    if (!href) return "";
    const label = editLink.label ?? translate(w, "grid.edit");
    const detail = () => ({ url: href, row: { ...row } });
    if (eventMode(editLink)) {
        return el("button", {
            type: "button",
            class: "ir-cell-edit",
            "aria-label": label,
            title: label,
            onclick: () => dispatchLinkEvent(w, "ir-edit", detail()),
        }, icon("pencil"));
    }
    const blank = editLink.target === "_blank";
    return el("a", {
        class: "ir-cell-edit",
        href,
        "aria-label": label,
        title: label,
        target: blank ? "_blank" : undefined,
        rel: blank ? "noopener" : undefined,
        onclick: anchorClickHandler(w, "ir-edit", detail),
    }, icon("pencil"));
}
