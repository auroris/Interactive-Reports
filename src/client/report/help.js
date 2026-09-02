// The Help window: a modeless dialog showing the packaged user guide. The guide is a
// standalone HTML page generated at build time (scripts/build-help.mjs) and served beside the
// bundles as help.<locale>.html; this module fetches it, lifts its scoped stylesheet and
// article into the dialog, and keeps the page's own table-of-contents links working inside
// the shadow root, where fragment navigation cannot reach.

import { el } from "../core/dom.js";
import { openDialog } from "../core/dialog.js";
import { resolveLocale } from "../core/localization.js";

const pages = new Map();

/**
 * Resolves the packaged help page for a locale, falling back to English when the locale has no page.
 *
 * @param {string} locale - The widget's resolved locale.
 * @returns {Promise<string>} The help page markup.
 *
 * Side effects: fetches the page once per locale and caches the pending or resolved result.
 */
function loadHelpPage(locale) {
    const candidates = [...new Set([locale, "en"])];
    const attempt = async () => {
        for (const candidate of candidates) {
            const response = await fetch(new URL(`./help.${candidate}.html`, import.meta.url).href, {
                headers: { Accept: "text/html" },
            });
            if (response.ok) return response.text();
        }
        throw new Error("help.loadFailed");
    };
    const key = candidates.join("|");
    if (!pages.has(key)) {
        const pending = attempt().catch(error => { pages.delete(key); throw error; });
        pages.set(key, pending);
    }
    return pages.get(key);
}

/**
 * Converts help page markup into nodes that belong inside the dialog: its scoped style sheets and body content.
 *
 * @param {string} html - The complete help document markup.
 * @returns {Array<Node>} Style elements followed by the page's article content.
 */
function helpNodes(html) {
    // Packaged static markup (our own build output), never report data.
    const template = document.createElement("template");
    template.innerHTML = html;
    return [...template.content.childNodes].filter(node =>
        node.nodeType !== 1
        || !["TITLE", "META", "LINK", "SCRIPT", "HEAD", "BODY"].includes(node.tagName));
}

/**
 * Opens the Help window and fills it with the localized user guide.
 *
 * @param {object} w - The report controller providing localization and dialog ownership.
 * @returns {object} The dialog controller.
 *
 * Side effects: opens a dialog, fetches the help page, and renders it or an error inside the window.
 */
export function openHelpDialog(w) {
    const container = el("div", { class: "ir-help-content", tabIndex: -1 });
    const dlg = openDialog({
        owner: w,
        title: w.t("help.title"),
        width: "52rem",
        cls: "ir-help-window",
        build: body => body.append(container),
    });
    container.addEventListener("click", event => {
        const anchor = event.target.closest?.("a[href^='#']");
        if (!anchor) return;
        const id = decodeURIComponent(anchor.getAttribute("href").slice(1));
        const target = [...container.querySelectorAll("[id]")].find(node => node.id === id);
        if (!target) return;
        event.preventDefault();
        target.scrollIntoView({ block: "start" });
    });
    loadHelpPage(resolveLocale(w.host)).then(html => {
        if (!container.isConnected) return;
        container.replaceChildren(...helpNodes(html));
        container.focus({ preventScroll: true });
    }, () => {
        if (container.isConnected) dlg.setError(new Error(w.t("help.loadFailed")));
    });
    return dlg;
}
