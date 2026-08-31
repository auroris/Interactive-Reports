// Column-class validation module. Report state may select application-defined class tokens,
// but it cannot inject CSS and cannot opt into the component's reserved ir-* classes.

import { translate } from "../core/localization.js";

const TOKEN = /^[A-Za-z_][A-Za-z0-9_-]{0,63}$/;
const MAX_CLASSES = 20;

/**
 * Normalizes a class array or whitespace-separated string and rejects unsafe or reserved tokens.
 *
 * @param {unknown} value - The candidate class array or whitespace-separated class string; other values produce an empty list.
 * @param {{strict?: boolean, context?: object|null}} [options={}] - Strict mode throws on the first invalid token or excessive count; context supplies localization.
 * @returns {Array<string>} At most 20 unique, valid, non-`ir-*` class tokens in source order.
 * @throws {Error} When strict mode encounters an invalid token or more than 20 accepted classes.
 */
export function columnClasses(value, { strict = false, context = null } = {}) {
    const source = Array.isArray(value)
        ? value
        : typeof value === "string" ? value.trim().split(/\s+/).filter(Boolean) : [];
    const classes = [];

    for (const candidate of source) {
        const token = typeof candidate === "string" ? candidate.trim() : "";
        const valid = TOKEN.test(token) && !token.toLowerCase().startsWith("ir-");
        if (!valid) {
            if (strict) throw new Error(translate(context, "columns.invalidCssClass", {
                name: String(candidate),
            }));
            continue;
        }
        if (!classes.includes(token)) classes.push(token);
    }

    if (classes.length > MAX_CLASSES) {
        if (strict) throw new Error(translate(context, "columns.tooManyCssClasses", { maximum: MAX_CLASSES }));
        return classes.slice(0, MAX_CLASSES);
    }
    return classes;
}
