// Per-column cell renderers. Renderer configuration is data, never markup: values
// become DOM properties through el(), and URL protocols pass through a small allowlist.

import { el } from "../../core/dom.js";
import { columnOf, pickable } from "../schema.js";
import { modeOf, sourceLayer, stageOf } from "../state.js";
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
    return pickable(w).find(column => column.name.toLowerCase() === requested.toLowerCase())?.name ?? requested;
};

const lookupFormat = (formats, name) => {
    if (!formats) return null;
    const requested = String(name).toLowerCase();
    const key = Object.keys(formats).find(candidate => candidate.toLowerCase() === requested);
    return key ? formats[key] : null;
};

/// A column's effective format: the terminal stage's own entry first, then the
/// source layer keyed by the column's formatSource (a metric or cell inherits
/// its source column's mask and style until the view overrides it) or by the
/// pass-through name itself.
export function formatForColumn(w, col) {
    const mode = modeOf(w.doc);
    const stage = mode === "groupBy" ? stageOf(w.doc, "group")
        : mode === "pivot" ? stageOf(w.doc, "spread")
        : null;
    const own = stage ? lookupFormat(stage.layer?.formats, col.name) : null;
    if (own) return own;
    return lookupFormat(sourceLayer(w.doc).formats, col.formatSource ?? col.name);
}

function sourceColumn(w, name, fallback) {
    return columnOf(w, name) ?? (name === fallback.name ? fallback : { name, type: "other" });
}

export function renderTextValue(w, row, col, decimal = false, format = null) {
    const effective = format ?? formatForColumn(w, col);
    return formatValue(row[col.name], col.type, decimal, effective?.mask);
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
            alt: "",
            loading: "lazy",
            decoding: "async",
        });
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
