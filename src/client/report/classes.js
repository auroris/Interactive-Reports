// Custom per-column class tokens. Report state may select application-defined rules,
// but it cannot inject CSS and cannot opt into the component's reserved ir-* classes.

const TOKEN = /^[A-Za-z_][A-Za-z0-9_-]{0,63}$/;
const MAX_CLASSES = 20;

export function columnClasses(value, { strict = false } = {}) {
    const source = Array.isArray(value)
        ? value
        : typeof value === "string" ? value.trim().split(/\s+/).filter(Boolean) : [];
    const classes = [];

    for (const candidate of source) {
        const token = typeof candidate === "string" ? candidate.trim() : "";
        const valid = TOKEN.test(token) && !token.toLowerCase().startsWith("ir-");
        if (!valid) {
            if (strict) throw new Error(
                `CSS class "${String(candidate)}" is invalid or reserved; start with a letter or _, then use letters, digits, _ or -, and do not start with ir-`);
            continue;
        }
        if (!classes.includes(token)) classes.push(token);
    }

    if (classes.length > MAX_CLASSES) {
        if (strict) throw new Error(`Use at most ${MAX_CLASSES} CSS classes per column`);
        return classes.slice(0, MAX_CLASSES);
    }
    return classes;
}
