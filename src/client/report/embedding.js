// Public embedding configuration. Everything here is presentation or an untrusted
// initial query document; it never changes server authorization or a definition.
import { canonicalControlName } from "./schema.js";

export function controlList(value) {
    if (value == null) return null;
    const values = typeof value === "string" ? value.trim().split(/\s+/).filter(Boolean) : value;
    if (!Array.isArray(values)) throw new TypeError("controls must be a list of control names or null.");
    return [...new Set(values.map(name => {
        const canonical = canonicalControlName(name);
        if (!canonical) throw new TypeError(`Unknown report control: ${String(name)}`);
        return canonical;
    }))];
}

export function pageSize(value) {
    if (value == null) return null;
    if (!/^[1-9]\d*$/.test(String(value)) || !Number.isSafeInteger(Number(value)))
        throw new TypeError("initial-page-size must be a positive safe integer.");
    return Number(value);
}

export function columnPresentation(value) {
    if (value == null) return null;
    if (typeof value !== "object" || Array.isArray(value)) throw new TypeError("columnPresentation must be an object.");
    const entries = Object.entries(value).map(([name, settings]) => {
        if (!name.trim() || !settings || typeof settings !== "object" || Array.isArray(settings))
            throw new TypeError("Each column requires a name and presentation settings.");
        const result = {};
        for (const [key, item] of Object.entries(settings)) {
            if (!["label", "helpText", "hideLabel"].includes(key)) throw new TypeError(`Unknown column presentation setting: ${key}`);
            if (item !== null && typeof item !== (key === "hideLabel" ? "boolean" : "string"))
                throw new TypeError(`Invalid column presentation setting: ${key}`);
            result[key] = item;
        }
        return [name, result];
    });
    if (new Set(entries.map(([name]) => name.toLowerCase())).size !== entries.length)
        throw new TypeError("Column names must be unique ignoring case.");
    return Object.fromEntries(entries);
}

export function linkSettings(value, edit = false) {
    if (value == null) return value;
    if (typeof value !== "object" || Array.isArray(value)) throw new TypeError("A link must be an object, null, or undefined.");
    const allowed = [edit ? "urlTemplate" : "url", "label", "target", "mode"];
    for (const [key, item] of Object.entries(value))
        if (!allowed.includes(key) || (item != null && typeof item !== "string")) throw new TypeError(`Invalid link setting: ${key}`);
    const copy = { ...value };
    if (copy.mode != null && !["navigate", "event"].includes(copy.mode)) throw new TypeError("Link mode must be navigate or event.");
    const url = edit ? copy.urlTemplate : copy.url;
    if ((edit || copy.mode !== "event") && !url?.trim()) throw new TypeError("A navigation URL is required.");
    if (url) {
        const normalized = url.replace(/\{[^{}]+\}/g, "key");
        const parsed = new URL(normalized, "https://embedding.invalid/");
        if (!/^https?:$/.test(parsed.protocol) || /[\u0000-\u0020\\]/.test(url))
            throw new TypeError("Component links must be relative or HTTP(S) URLs without control characters.");
    }
    if (edit && !/\{[^{}]+\}/.test(url)) throw new TypeError("An edit link must contain a column placeholder.");
    if (!edit && /[{}]/.test(url ?? "")) throw new TypeError("A create link cannot contain column placeholders.");
    return copy;
}

export function presentColumn(w, column) {
    const entry = Object.entries(w._columnPresentation ?? {}).find(([key]) => key.toLowerCase() === column.name.toLowerCase())?.[1];
    return entry?.label != null ? { ...column, label: entry.label } : column;
}

export function validateEditProjection(w) {
    const override = w._editLink;
    if (!override) return;
    const declared = new Set([...(w.schema?.editLink?.urlTemplate ?? "").matchAll(/\{([^{}]+)\}/g)].map(match => match[1].toLowerCase()));
    for (const match of override.urlTemplate.matchAll(/\{([^{}]+)\}/g))
        if (!declared.has(match[1].toLowerCase()))
            throw new TypeError(`Edit column '${match[1]}' must be declared by the report's server editLink so it remains available when hidden.`);
}
