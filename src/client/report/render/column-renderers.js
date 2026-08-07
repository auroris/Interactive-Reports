// Per-column cell renderers. Renderer configuration is data, never markup: values
// become DOM properties through el(), and URL protocols pass through a small allowlist.

import { el } from "../../core/dom.js";
import { pickable, typeOf } from "../schema.js";
import { formatValue } from "./format.js";

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

function textValue(w, row, col, decimal, format) {
    const name = sourceName(w, format, "textColumn", col.name);
    const mask = name === col.name ? format?.mask : null;
    return formatValue(row[name], typeOf(w, name), name === col.name && decimal, mask);
}

const renderers = {
    link(w, row, col, decimal, format) {
        const urlName = sourceName(w, format, "urlColumn", col.name);
        const href = safeRendererUrl(row[urlName], "link");
        const text = textValue(w, row, col, decimal, format);
        if (!href) return text;
        return el("a", { class: "ir-cell-link", href }, text || String(row[urlName]));
    },

    image(w, row, col, decimal, format) {
        const urlName = sourceName(w, format, "urlColumn", col.name);
        const src = safeRendererUrl(row[urlName], "image");
        if (!src) return formatValue(row[col.name], col.type, decimal, format?.mask);
        return el("img", {
            class: "ir-cell-image",
            src,
            alt: "",
            loading: "lazy",
            decoding: "async",
        });
    },
};

export function renderColumnValue(w, row, col, decimal = false, format = null) {
    const effective = format ?? w.doc.formats?.[col.name];
    const name = typeof effective?.displayAs === "string" ? effective.displayAs.toLowerCase() : "";
    const renderer = renderers[name];
    return renderer
        ? renderer(w, row, col, decimal, effective)
        : formatValue(row[col.name], col.type, decimal, effective?.mask);
}
