// Shared widget mount and teardown. Each custom element gets an isolated shadow-root rendering
// boundary; disposal releases whatever transient overlay UI (menus, dialogs) the widget still
// owns when a host framework removes it.

import cssText from "../ir.css";
import { defaultApiBase, errorText } from "./api.js";
import { banner, el, transientBanner } from "./dom.js";
import { closeMenuOwnedBy } from "./menu.js";
import { closeDialogsOwnedBy } from "./dialog.js";
import { resolveLocale, translate } from "./localization.js";

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

/**
 * Gives one widget an isolated rendering boundary. Keeping both the DOM and the stylesheet
 * in the shadow root prevents host-page resets and utility classes from leaking in, and prevents the
 * widget's rules from leaking out.
 *
 * @param {HTMLElement} host - The custom-element host that does not yet have a shadow root.
 * @returns {{root: ShadowRoot, mount: HTMLDivElement}} The open shadow root and its report surface.
 *
 * Side effects: attaches a shadow root, installs packaged styles, and mounts the surface element.
 */
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

/**
 * Returns the bundled stylesheet node used by a new widget root.
 *
 * @returns {HTMLStyleElement} A detached style element containing the packaged CSS text.
 *
 * Side effects: creates a detached style element.
 */
function packagedStyleNode() {
    const style = el("style", { "data-ir-styles": "" });
    style.textContent = cssText;
    return style;
}

/**
 * Clones a stylesheet node into the widget root and records it for later replacement.
 *
 * @param {ShadowRoot} root - The widget root that will receive the packaged style node.
 * @returns {void} No value.
 *
 * Side effects: appends a new style element to the shadow root.
 */
function appendStyleNode(root) {
    root.append(packagedStyleNode());
}

// Invariant: adopted sheets cascade after every DOM sheet, so a custom stylesheet <link> could
// never out-tie an adopted packaged rule. A root that hosts a custom stylesheet therefore falls
// back to the style-node path, restoring the documented order: packaged styles first, the
// application's link after them. The demotion is one-way: a report switch that drops the
// custom stylesheet keeps the style node rather than thrash between the two mechanisms.
/**
 * Replaces the packaged adopted sheet with an equivalent style node so a later custom link can override it.
 *
 * @param {ShadowRoot} root - The widget root currently adopting the packaged sheet.
 * @returns {void} No value.
 *
 * Side effects: changes adopted sheets and inserts the packaged style node before custom styles and content.
 */
function demotePackagedStyles(root) {
    if (!packagedStyleSheet || !root.adoptedStyleSheets?.includes(packagedStyleSheet)) return;
    root.adoptedStyleSheets = root.adoptedStyleSheets.filter(sheet => sheet !== packagedStyleSheet);
    root.insertBefore(
        packagedStyleNode(),
        root.querySelector('link[data-ir-custom-styles], [part~="surface"]'));
}

// Invariant: a report definition may name one application-owned stylesheet. It belongs inside
// the shadow root so its rules share the component's styling boundary. Keeping the URL out of
// report state means saved/global reports cannot choose a CSS source.
/**
 * Loads, replaces, or removes the custom stylesheet owned by a widget host.
 *
 * @param {HTMLElement} host - The widget host whose shadow root owns the custom link.
 * @param {string|null|undefined} href - The application-owned stylesheet URL, or a falsy value to remove it.
 * @returns {void} No value.
 *
 * Side effects: may demote the adopted packaged sheet, then inserts, replaces, or removes the custom link.
 */
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

/**
 * Releases document-level listeners and transient UI owned by a removed widget host.
 *
 * @param {HTMLElement} host - The removed widget host whose transient UI should close.
 * @returns {void} No value.
 */
export function disposeWidget(host) {
    closeMenuOwnedBy(host);
    closeDialogsOwnedBy(host);
}

// Protocol contract: shared custom-element shell: shadow-root mount, API-base attributes,
// request sequencing, and transient-UI disposal. Concrete widgets retain their own connection
// and in-flight operation behavior.
export class WidgetElement extends HTMLElement {
    /**
     * Attaches the shared shadow-root shell and initializes the request sequence counter.
     *
     * Side effects: mutates the custom element by attaching its shadow root and initial DOM.
     */
    constructor() {
        super();
        const { root, mount } = createWidgetRoot(this);
        this._root = root;
        this._mount = mount;
        this._seq = 0;
    }

    /**
     * Returns the API base configured on the widget host.
     *
     * @returns {string} The explicit `api-base`, legacy `base`, or bundle-relative default.
     */
    get apiBase() { return this.getAttribute("api-base") ?? this.getAttribute("base") ?? BASE_DEFAULT; }
    /**
     * Sets or removes the API base attribute on the widget host.
     *
     * @param {unknown} value - The new API base; nullish values remove the explicit attribute.
     * @returns {void} No value.
     *
     * Side effects: sets or removes the host's `api-base` attribute.
     */
    set apiBase(value) {
        if (value === null || value === undefined) this.removeAttribute("api-base");
        else this.setAttribute("api-base", String(value));
    }
    /**
     * Returns the configured API base without trailing slashes.
     *
     * @returns {string} The configured API base without trailing slashes.
     */
    get base() { return this.apiBase.replace(/\/+$/, ""); }
    /**
     * Returns the locale resolved from the widget and its DOM ancestry.
     *
     * @returns {'en'|'fr-CA'} The supported locale resolved from this element and its page.
     */
    get locale() { return resolveLocale(this); }
    /**
     * Translates a message key with values using the widget's resolved locale.
     *
     * @param {string} key - The localization key to resolve.
     * @param {object} [values={}] - Named ICU interpolation values.
     * @returns {string} The localized, formatted message.
     */
    t(key, values = {}) { return translate(this, key, values); }

    /**
     * Renders a normalized error in the widget's error region.
     *
     * @param {Error|string|object} err - The error value to normalize for display.
     * @param {string|null} [message=null] - Optional text that replaces normalized error content.
     * @returns {void} No value.
     *
     * Side effects: replaces the error region with a dismissible banner when that region exists.
     */
    showError(err, message = null) {
        const slot = this.els?.errorSlot;
        if (!slot) return;
        slot.replaceChildren(
            banner("error", errorText(err, message, this), () => this.clearError(), this));
    }

    /**
     * Clears the widget's rendered error region.
     *
     * @returns {void} No value.
     *
     * Side effects: removes all children from the error region when it exists.
     */
    clearError() {
        this.els?.errorSlot?.replaceChildren();
    }

    /**
     * Shows a transient localized notification in the widget.
     *
     * @param {string} text - The already-localized notification text.
     * @param {'ok'|'warn'|'error'|string} [kind="ok"] - The banner status class suffix.
     * @returns {void} No value.
     *
     * Side effects: appends an auto-dismissing banner when the transient live region exists.
     */
    notify(text, kind = "ok") {
        if (this.els?.transientSlot)
            transientBanner(this.els.transientSlot, kind, text, 4000, this);
    }

    /**
     * Invalidates outstanding sequence checks and closes menus and dialogs when the element leaves the document.
     *
     * @returns {void} No value.
     */
    disconnectedCallback() {
        ++this._seq;
        disposeWidget(this);
    }
}
