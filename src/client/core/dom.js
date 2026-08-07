// DOM primitives shared by every widget: element builder, inline icons, notice
// banners, and small form helpers. Everything renders through createElement and
// textContent — report data never passes through innerHTML (the only innerHTML
// below is our own static icon markup).

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
};

export function icon(name) {
    const span = el("span", { class: "ir-icon", "aria-hidden": "true" });
    span.innerHTML = ICONS[name] ?? "";
    return span;
}

/// Notice banner: kinds error | warn | ok. Pass onDismiss for a close button.
export function banner(kind, text, onDismiss) {
    return el("div", { class: `ir-banner ir-banner-${kind}` },
        el("span", { class: "ir-banner-text" }, text),
        onDismiss ? el("button", {
            type: "button", class: "ir-banner-x", "aria-label": "Dismiss", onclick: onDismiss,
        }, icon("close")) : null);
}

export function labeled(text, control, opts = {}) {
    return el("label", { class: "ir-field" + (opts.inline ? " ir-field-inline" : "") },
        el("span", { class: "ir-field-label" }, text), control);
}

/// options: array of strings or {value, label}; groups: [{label, options}] also allowed.
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
