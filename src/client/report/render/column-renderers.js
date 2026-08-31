// Per-column cell renderers. Renderer configuration is data, never markup: values
// become DOM properties through el(), and URL protocols pass through a small allowlist.

import { el } from "../../core/dom.js";
import { columnOf } from "../schema.js";
import { terminalTableColumns } from "../table.js";
import { composedFormatContext, lookupValue } from "../state.js";
import { formatValue, hasFraction } from "./format.js";

const LINK_PROTOCOLS = new Set(["http:", "https:", "mailto:", "tel:"]);
const IMAGE_PROTOCOLS = new Set(["http:", "https:"]);

/**
 * Accepts only renderer URLs whose resolved protocol is allowed for links or images.
 *
 * @param {unknown} value - The candidate absolute or document-relative URL.
 * @param {'link'|'image'} [kind="link"] - The renderer allowlist to apply.
 * @returns {string|null} The trimmed original URL when its resolved protocol is allowed; otherwise, `null`.
 */
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

/**
 * Returns the source-column name configured for a display-format property.
 *
 * @param {object} w - The report controller whose terminal columns define canonical casing.
 * @param {object|null} format - The active column format.
 * @param {string} property - The format property that may identify a source column.
 * @param {string|null} fallback - The source name used when the format property is blank.
 * @returns {string|null} The source name.
 */
const sourceName = (w, format, property, fallback) => {
    const value = format?.[property];
    if (typeof value !== "string" || !value.trim()) return fallback;
    const requested = value.trim();
    return terminalTableColumns(w).find(column => column.name.toLowerCase() === requested.toLowerCase())?.name ?? requested;
};

// Invariant: a column's effective format over the complete selected ancestry. A direct
// active-table entry wins in full. Synthetic shape outputs otherwise inherit only the safe
// scalar mask through formatSource; renderer and style state never crosses a column-lineage or
// named-table boundary.
/**
 * Returns the effective display format for a result column.
 *
 * @param {object} w - The report controller whose table ancestry supplies effective and imported formats.
 * @param {object} col - The result column, including logical name and optional `formatSource` lineage.
 * @returns {object|null} The active table's full format, an inherited mask-only format, or `null`.
 */
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

/**
 * Returns the source schema column configured for a display renderer.
 *
 * @param {object} w - The report controller whose terminal and base schemas are searched.
 * @param {string} name - The requested renderer source-column name.
 * @param {object} fallback - The visible column to reuse when names match.
 * @returns {object|null} The source column.
 */
function sourceColumn(w, name, fallback) {
    return terminalTableColumns(w).find(column => column.name.toLowerCase() === String(name).toLowerCase())
        ?? columnOf(w, name)
        ?? (name === fallback.name ? fallback : { name, type: "other" });
}

/**
 * Formats one row value through the column's type and effective scalar mask.
 *
 * @param {object} w - The report controller providing locale and format ancestry.
 * @param {object} row - The result row containing the value.
 * @param {object} col - The result column descriptor.
 * @param {boolean} [decimal=false] - Whether the numeric value should retain decimal precision.
 * @param {object|null} [format=null] - An already-resolved format, or `null` to resolve it here.
 * @returns {string} The display text.
 */
export function renderTextValue(w, row, col, decimal = false, format = null) {
    const effective = format ?? formatForColumn(w, col);
    return formatValue(row[col.name], col.type, decimal, effective?.mask, w);
}

/**
 * Resolves the row value used as a link's visible text.
 *
 * @param {object} w - The report controller providing terminal columns and format ancestry.
 * @param {object} row - The result row containing visible text and URL values.
 * @param {object} col - The visible result column.
 * @param {boolean} decimal - Whether the numeric value should retain decimal precision.
 * @param {object} format - The effective link format.
 * @returns {string} The link text value.
 */
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
    /**
     * Returns formatted text or a detached anchor when the configured row URL is safe.
     * @param {object} w - The report controller providing formats and event context.
     * @param {object} row - The result row containing URL and text values.
     * @param {object} col - The visible result column.
     * @param {boolean} decimal - Whether visible numeric text uses decimal formatting.
     * @param {object} format - The effective link renderer configuration.
     * @returns {string|HTMLAnchorElement} Renderable cell content.
     */
    link(w, row, col, decimal, format) {
        const urlName = sourceName(w, format, "urlColumn", col.name);
        const href = safeRendererUrl(row[urlName], "link");
        const text = linkTextValue(w, row, col, decimal, format);
        if (!href) return text;
        return el("a", { class: "ir-cell-link", href }, text || String(row[urlName]));
    },

    /**
     * Returns a detached lazy image when its URL is safe, otherwise the formatted cell text.
     * @param {object} w - The report controller providing formats and event context.
     * @param {object} row - The result row containing image URL and fallback values.
     * @param {object} col - The visible result column.
     * @param {boolean} decimal - Whether fallback numeric text uses decimal formatting.
     * @param {object} format - The effective image renderer configuration.
     * @returns {string|HTMLImageElement} Renderable cell content.
     */
    image(w, row, col, decimal, format) {
        const urlName = sourceName(w, format, "urlColumn", col.name);
        const src = safeRendererUrl(row[urlName], "image");
        if (!src) return renderTextValue(w, row, col, decimal, format);
        return el("img", {
            class: "ir-cell-image",
            src,
            // Invariant: the cell's only content is the image, so it is not decorative; the
            // column heading is the most honest description available.
            alt: String(col.label ?? col.name),
            loading: "lazy",
            decoding: "async",
        });
    },

    // A command button. The cell's own value is the label — a blank label renders as ordinary
    // (empty) text, which is how a definition withholds an action from individual rows.
    // Clicking dispatches ir-action from the host element; the row copy includes hidden
    // keyColumn values the host's handler needs.
    /**
     * Returns a detached command button, or blank formatted text when the row withholds the action.
     * @param {object} w - The report element that receives the action event.
     * @param {object} row - The result row copied into event detail.
     * @param {object} col - The visible result column.
     * @param {boolean} decimal - Whether the button label uses decimal formatting.
     * @param {object} format - The effective action configuration, including its command.
     * @returns {string|HTMLButtonElement} Renderable cell content.
     * Side effects: clicking the button dispatches a bubbling, composed `ir-action` event with a row copy.
     */
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

/**
 * Every cell enters here. Text is the base renderer and owns all scalar mask handling; link/image
 * optionally compose it instead of maintaining a parallel formatting path. Synthetic aggregate columns
 * set formatSource in their metadata and call this with display renderers disabled.
 *
 * @param {object} w - The report controller providing locale, formats, and the action event host.
 * @param {object} row - The result row containing visible and supporting values.
 * @param {object} col - The result column descriptor.
 * @param {boolean} [decimal=false] - Whether the numeric value should retain decimal precision.
 * @param {object|null} [format=null] - An already-resolved format, or `null` to resolve it here.
 * @param {boolean} [allowDisplayAs=true] - Whether column-specific display renderers may transform the value.
 * @returns {string|Node} The column value.
 *
 * Side effects: may create a detached anchor, image, or button; an action button dispatches only when clicked.
 */
export function renderColumnValue(w, row, col, decimal = false, format = null, allowDisplayAs = true) {
    const effective = format ?? formatForColumn(w, col);
    const name = typeof effective?.displayAs === "string" ? effective.displayAs.toLowerCase() : "";
    const renderer = allowDisplayAs ? renderers[name] : null;
    return renderer
        ? renderer(w, row, col, decimal, effective)
        : renderTextValue(w, row, col, decimal, effective);
}
