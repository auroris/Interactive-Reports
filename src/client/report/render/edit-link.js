// The definition's per-row edit pencil: a leading synthetic grid column built
// from the schema payload's editLink (urlTemplate/label/target). Definition
// chrome, not a ColumnFormat renderer — it exists independent of the document's
// column selection, using template columns the server projects as hidden row
// data. Substituted values are URL-encoded and the result still passes the
// renderer protocol allowlist, so row data can never smuggle a scheme.

import { el, icon } from "../../core/dom.js";
import { translate } from "../../core/localization.js";
import { safeRendererUrl } from "./column-renderers.js";

/// The active edit link, or null. The pencil is a grid-row affordance: grouped,
/// pivoted, and charted rows have no single source row to edit.
export function activeEditLink(w, mode) {
    return mode === "grid" ? (w.schema?.editLink ?? null) : null;
}

/// Substitutes {COLUMN} placeholders with the row's URL-encoded values. Returns
/// null when any referenced value is null or missing — the row renders no
/// pencil, mirroring the action renderer's blank-label convention. Placeholders
/// arrive canonical-cased from the server; the case-insensitive fallback keeps
/// hand-written hosts working.
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

/// The cell content for one row: an anchor with the pencil icon, or "" when the
/// row withholds its link. A real href with no click handler, so middle-click,
/// ctrl-click, and open-in-new-tab behave natively.
export function renderEditCell(editLink, row, context = null) {
    const url = substituteEditUrl(editLink.urlTemplate, row);
    const href = url === null ? null : safeRendererUrl(url, "link");
    if (!href) return "";
    const label = editLink.label ?? translate(context, "grid.edit");
    const blank = editLink.target === "_blank";
    return el("a", {
        class: "ir-cell-edit",
        href,
        "aria-label": label,
        title: label,
        target: blank ? "_blank" : undefined,
        rel: blank ? "noopener" : undefined,
    }, icon("pencil"));
}
