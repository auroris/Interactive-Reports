// DOM primitives shared by every widget: element construction, bundled icons, notice
// banners, and small form helpers. Everything renders through createElement and textContent;
// report data never passes through innerHTML (the only innerHTML below is our own static icon
// markup).

import { translate } from "./localization.js";

/**
 * Creates a DOM element, applies its properties, and appends its children.
 *
 * @param {string} tag - The HTML tag name of the element to create.
 * @param {object} [props={}] - Properties, attributes, dataset entries, styles, and `on*` handlers to assign.
 * @param {...(Node|string|number|Array<Node|string|number>|null|undefined|false)} children - Nested child values; nullish values and `false` are omitted.
 * @returns {HTMLElement} A detached element containing the supplied children.
 *
 * Side effects: creates DOM nodes and attaches supplied event handlers; it does not mount the result.
 */
export function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
        if (v === undefined || v === null) continue;
        if (k === "class") node.className = v;
        else if (k === "part") node.setAttribute("part", v);
        else if (k === "for") node.htmlFor = v;
        else if (k === "dataset") Object.assign(node.dataset, v);
        else if (k === "style") Object.assign(node.style, v);
        else if (k.startsWith("on")) node[k] = v;
        else if (k in node) node[k] = v;
        else node.setAttribute(k, v);
    }
    node.append(...children.flat(Infinity).filter(c => c !== null && c !== undefined && c !== false));
    return node;
}

const ICONS = {
    search: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><circle cx="6.5" cy="6.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.6"/><line x1="10" y1="10" x2="14" y2="14" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',
    caret: '<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 5.5 8 11l5-5.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
    grid: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M1.5 9.5h13 M6 2.5v11 M11 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>',
    group: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="1.5" y="2" width="6" height="3" fill="currentColor" opacity=".55"/><rect x="1.5" y="6.5" width="10" height="3" fill="currentColor" opacity=".8"/><rect x="1.5" y="11" width="13" height="3" fill="currentColor"/></svg>',
    pivot: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M6 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/><circle cx="10.5" cy="10" r="1.4" fill="currentColor"/></svg>',
    chart: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="2" y="8" width="3" height="6" fill="currentColor" opacity=".65"/><rect x="6.5" y="3.5" width="3" height="10.5" fill="currentColor"/><rect x="11" y="6" width="3" height="8" fill="currentColor" opacity=".8"/></svg>',
    close: '<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 3l10 10M13 3L3 13" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
    help: '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><circle cx="8" cy="8" r="6.4" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M6.1 6.4a1.95 1.95 0 1 1 2.8 1.75c-.6.3-.9.65-.9 1.25v.2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/><circle cx="8" cy="11.7" r=".95" fill="currentColor"/></svg>',
    pencil: '<svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true"><path d="M11.2 2.1 13.9 4.8 5.6 13.1 2.2 13.8 2.9 10.4z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/><path d="M9.9 3.4 12.6 6.1" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>',
};

/**
 * Creates the decorative icon element for a named bundled symbol.
 *
 * @param {string} name - A key in the bundled icon catalog.
 * @returns {HTMLSpanElement} A detached, aria-hidden icon container; unknown names produce an empty container.
 *
 * Side effects: creates a detached element and assigns trusted static SVG markup.
 */
export function icon(name) {
    const span = el("span", { class: "ir-icon", "aria-hidden": "true" });
    span.innerHTML = ICONS[name] ?? "";
    return span;
}

/**
 * Creates a notice banner and adds a localized close control when dismissal is supported.
 *
 * @param {'error'|'warn'|'ok'|string} kind - The visual status appended to the banner class name.
 * @param {string} text - The already-safe display text.
 * @param {Function|null} onDismiss - The optional close-button callback.
 * @param {Element|object|string|null} [context=null] - The localization context for the dismiss label.
 * @returns {HTMLDivElement} A detached banner element.
 */
export function banner(kind, text, onDismiss, context = null) {
    return el("div", { class: `ir-banner ir-banner-${kind}` },
        el("span", { class: "ir-banner-text" }, text),
        onDismiss ? el("button", {
            type: "button", class: "ir-banner-x", "aria-label": translate(context, "common.dismiss"), onclick: onDismiss,
        }, icon("close")) : null);
}

/**
 * Append a short-lived status message to a persistent live region. Keeping the role on the slot
 * (rather than on a node inserted with its text already filled) gives assistive technology a stable
 * region to observe.
 *
 * @param {Element} slot - The persistent live region that receives the message.
 * @param {string} kind - The banner status class suffix.
 * @param {string} text - The already-safe display text.
 * @param {number} [timeout=4000] - The number of milliseconds before the transient banner is dismissed.
 * @param {Element|object|string|null} [context=null] - The localization context for banner controls.
 * @returns {Function} A dismissal function that clears the timer and removes the message immediately.
 *
 * Side effects: appends a banner, schedules its removal, and removes it when the returned callback runs.
 */
export function transientBanner(slot, kind, text, timeout = 4000, context = null) {
    const node = banner(kind, text, null, context);
    slot.append(node);
    const timer = setTimeout(() => node.remove(), timeout);
    return () => { clearTimeout(timer); node.remove(); };
}

/**
 * Wraps a form control with its visible and accessible label.
 *
 * @param {string} text - The visible field label.
 * @param {Element} control - The form control nested inside the label.
 * @param {{inline?: boolean}} [opts={}] - Whether to use the inline field layout.
 * @returns {HTMLLabelElement} A detached label containing its caption and control.
 */
export function labeled(text, control, opts = {}) {
    return el("label", { class: "ir-field" + (opts.inline ? " ir-field-inline" : "") },
        el("span", { class: "ir-field-label" }, text), control);
}

/**
 * Options: array of strings or {value, label}; groups: [{label, options}] also allowed.
 *
 * @param {Array<string|{value: string, label: string}|{label: string, options: Array<string|{value: string, label: string}>}>} options - Flat options or labeled option groups.
 * @param {unknown} value - The initial selected value; nullish values leave browser selection unchanged.
 * @returns {HTMLSelectElement} A detached select populated in source order.
 *
 * Side effects: creates option and optgroup DOM nodes; it does not mount the select.
 */
export function sel(options, value) {
    const node = el("select", { class: "ir-select" });
    const opt = o => typeof o === "string" ? new Option(o, o) : new Option(o.label, o.value);
    for (const o of options) {
        if (o.options) {
            const g = el("optgroup", { label: o.label });
            g.append(...o.options.map(opt));
            node.append(g);
        } else node.append(opt(o));
    }
    if (value !== undefined && value !== null) node.value = value;
    return node;
}
