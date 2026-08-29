// Widget mount and teardown. Each custom element gets an isolated shadow-root
// rendering boundary; disposal releases whatever transient overlay UI (menus,
// dialogs) the widget still owns when a host framework removes it.

import cssText from "../ir.css";
import { defaultApiBase, errorText } from "./api.js";
import { banner, el, transientBanner } from "./dom.js";
import { closeMenuOwnedBy } from "./menu.js";
import { closeDialogsOwnedBy } from "./dialog.js";

const BASE_DEFAULT = defaultApiBase();

const packagedStyleSheet = (() => {
    try {
        const Sheet = globalThis.CSSStyleSheet ?? globalThis.window?.CSSStyleSheet;
        if (!Sheet) return null;
        const sheet = new Sheet();
        sheet.replaceSync(cssText);
        return sheet;
    } catch {
        return null;
    }
})();

/// Give each widget an isolated rendering boundary. Keeping both the DOM and the
/// stylesheet in the shadow root prevents host-page resets and utility classes
/// from leaking in, and prevents the widget's rules from leaking out.
export function createWidgetRoot(host) {
    const root = host.attachShadow({ mode: "open" });
    const mount = el("div", { part: "surface", "aria-busy": "false" });
    if (packagedStyleSheet && "adoptedStyleSheets" in root) {
        try { root.adoptedStyleSheets = [...root.adoptedStyleSheets, packagedStyleSheet]; }
        catch { appendStyleNode(root); }
    } else {
        appendStyleNode(root);
    }
    root.append(mount);
    return { root, mount };
}

function packagedStyleNode() {
    const style = el("style", { "data-ir-styles": "" });
    style.textContent = cssText;
    return style;
}

function appendStyleNode(root) {
    root.append(packagedStyleNode());
}

/// Adopted sheets cascade after every DOM sheet, so a custom stylesheet <link>
/// could never out-tie an adopted packaged rule. A root that hosts a custom
/// stylesheet therefore falls back to the style-node path, restoring the
/// documented order: packaged styles first, the application's link after them.
/// The demotion is one-way — a report switch that drops the custom stylesheet
/// keeps the style node rather than thrash between the two mechanisms.
function demotePackagedStyles(root) {
    if (!packagedStyleSheet || !root.adoptedStyleSheets?.includes(packagedStyleSheet)) return;
    root.adoptedStyleSheets = root.adoptedStyleSheets.filter(sheet => sheet !== packagedStyleSheet);
    root.insertBefore(
        packagedStyleNode(),
        root.querySelector('link[data-ir-custom-styles], [part~="surface"]'));
}

/// A report definition may name one application-owned stylesheet. It belongs inside
/// the shadow root so its rules share the component's styling boundary. Keeping the
/// URL out of report state means saved/global reports cannot choose a CSS source.
export function setCustomStyleSheet(host, href) {
    const root = host.shadowRoot;
    const current = root?.querySelector("link[data-ir-custom-styles]");
    if (!href) { current?.remove(); return; }
    if (!root || current?.getAttribute("href") === href) return;

    demotePackagedStyles(root);
    const link = el("link", {
        rel: "stylesheet",
        href,
        "data-ir-custom-styles": "",
    });
    if (current) current.replaceWith(link);
    else root.insertBefore(link, root.querySelector('[part~="surface"]'));
}

/// Release document-level listeners and transient UI when a host framework
/// removes a component from the page.
export function disposeWidget(host) {
    closeMenuOwnedBy(host);
    closeDialogsOwnedBy(host);
}

/// Shared custom-element shell: shadow-root mount, API-base attributes, request
/// sequencing, and transient-UI disposal. Concrete widgets retain their own
/// connection and in-flight operation behavior.
export class WidgetElement extends HTMLElement {
    constructor() {
        super();
        const { root, mount } = createWidgetRoot(this);
        this._root = root;
        this._mount = mount;
        this._seq = 0;
    }

    get apiBase() { return this.getAttribute("api-base") ?? this.getAttribute("base") ?? BASE_DEFAULT; }
    set apiBase(value) {
        if (value === null || value === undefined) this.removeAttribute("api-base");
        else this.setAttribute("api-base", String(value));
    }
    get base() { return this.apiBase.replace(/\/+$/, ""); }

    showError(err, message = null) {
        const slot = this.els?.errorSlot;
        if (!slot) return;
        slot.replaceChildren(
            banner("error", errorText(err, message, this), () => this.clearError()));
    }

    clearError() {
        this.els?.errorSlot?.replaceChildren();
    }

    notify(text, kind = "ok") {
        if (this.els?.transientSlot)
            transientBanner(this.els.transientSlot, kind, text);
    }

    disconnectedCallback() {
        ++this._seq;
        disposeWidget(this);
    }
}
