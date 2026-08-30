// Per-column cell renderers. Renderer configuration is data, never markup: values
// become DOM properties through el(), and URL protocols pass through a small allowlist.

import { el } from "../../core/dom.js";
import { columnOf } from "../schema.js";
import { terminalTableColumns } from "../table.js";
import { composedFormatContext, lookupValue } from "../state.js";
import { formatValue, hasFraction } from "./format.js";

const LINK_PROTOCOLS = new Set(["http:", "https:", "mailto:", "tel:"]);
const IMAGE_PROTOCOLS = new Set(["http:", "https:"]);

export function safeRendererUrl(value, kind = "link") {
    if (value === null || value === undefined) return null;
    const raw = String(value).trim();
    if (!raw) return null;

    try {
        const parsed = new URL(raw, document.baseURI);
        const allowed = kind === "image" ? IMAGE_PROTOCOLS : LINK_PROTOCOLS;
        return allowed.has(parsed.protocol) ? raw : null;
    } catch {
        return null;
    }
}

const sourceName = (w, format, property, fallback) => {
    const value = format?.[property];
    if (typeof value !== "string" || !value.trim()) return fallback;
    const requested = value.trim();
    return terminalTableColumns(w).find(column => column.name.toLowerCase() === requested.toLowerCase())?.name ?? requested;
};

/// A column's effective format over the complete selected ancestry. A direct
/// active-table entry wins in full. Synthetic shape outputs otherwise inherit
/// only the safe scalar mask through formatSource; renderer and style state never
/// crosses a column-lineage or named-table boundary.
export function formatForColumn(w, col) {
    const formats = composedFormatContext(w.doc);
    const own = lookupValue(formats.effective, col.name);
    if (own !== undefined) return own;
    const source = col.formatSource ?? col.name;
    const inherited = lookupValue(formats.imported, source);
    return typeof inherited?.mask === "string" && inherited.mask.trim()
        ? { mask: inherited.mask }
        : null;
}

function sourceColumn(w, name, fallback) {
    return terminalTableColumns(w).find(column => column.name.toLowerCase() === String(name).toLowerCase())
        ?? columnOf(w, name)
        ?? (name === fallback.name ? fallback : { name, type: "other" });
}

export function renderTextValue(w, row, col, decimal = false, format = null) {
    const effective = format ?? formatForColumn(w, col);
    return formatValue(row[col.name], col.type, decimal, effective?.mask, w);
}

function linkTextValue(w, row, col, decimal, format) {
    const name = sourceName(w, format, "textColumn", col.name);
    const source = sourceColumn(w, name, col);
    const sourceFormat = name.toLowerCase() === col.name.toLowerCase()
        ? format
        : formatForColumn(w, source);
    return renderTextValue(
        w,
        row,
        source,
        name.toLowerCase() === col.name.toLowerCase() ? decimal : hasFraction(row[name]),
        sourceFormat);
}

const renderers = {
    link(w, row, col, decimal, format) {
        const urlName = sourceName(w, format, "urlColumn", col.name);
        const href = safeRendererUrl(row[urlName], "link");
        const text = linkTextValue(w, row, col, decimal, format);
        if (!href) return text;
        return el("a", { class: "ir-cell-link", href }, text || String(row[urlName]));
    },

    image(w, row, col, decimal, format) {
        const urlName = sourceName(w, format, "urlColumn", col.name);
        const src = safeRendererUrl(row[urlName], "image");
        if (!src) return renderTextValue(w, row, col, decimal, format);
        return el("img", {
            class: "ir-cell-image",
            src,
            // The cell's only content is the image, so it is not decorative; the
            // column heading is the most honest description available.
            alt: String(col.label ?? col.name),
            loading: "lazy",
            decoding: "async",
        });
    },

    /// A command button. The cell's own value is the label — a blank label renders
    /// as ordinary (empty) text, which is how a definition withholds an action from
    /// individual rows. Clicking dispatches ir-action from the host element; the
    /// row copy includes hidden keyColumn values the host's handler needs.
    action(w, row, col, decimal, format) {
        const label = renderTextValue(w, row, col, decimal, format);
        if (!label) return label;
        return el("button", {
            type: "button",
            class: "ir-btn ir-cell-action",
            onclick: () => w.dispatchEvent(new CustomEvent("ir-action", {
                bubbles: true,
                composed: true,
                detail: { command: format?.command ?? "", row: { ...row }, column: col.name },
            })),
        }, label);
    },
};

/// Every cell enters here. Text is the base renderer and owns all scalar mask
/// handling; link/image optionally compose it instead of maintaining a parallel
/// formatting path. Synthetic aggregate columns set formatSource in their metadata
/// and call this with display renderers disabled.
export function renderColumnValue(w, row, col, decimal = false, format = null, allowDisplayAs = true) {
    const effective = format ?? formatForColumn(w, col);
    const name = typeof effective?.displayAs === "string" ? effective.displayAs.toLowerCase() : "";
    const renderer = allowDisplayAs ? renderers[name] : null;
    return renderer
        ? renderer(w, row, col, decimal, effective)
        : renderTextValue(w, row, col, decimal, effective);
}
