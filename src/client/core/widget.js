// Widget mount and teardown. Each custom element gets an isolated shadow-root
// rendering boundary; disposal releases whatever transient overlay UI (menus,
// dialogs) the widget still owns when a host framework removes it.

import cssText from "../ir.css";
import { el } from "./dom.js";
import { closeMenuOwnedBy } from "./menu.js";
import { closeDialogsOwnedBy } from "./dialog.js";

/// Give each widget an isolated rendering boundary. Keeping both the DOM and the
/// stylesheet in the shadow root prevents host-page resets and utility classes
/// from leaking in, and prevents the widget's rules from leaking out.
export function createWidgetRoot(host) {
    const root = host.attachShadow({ mode: "open" });
    const style = el("style", { "data-ir-styles": "" });
    style.textContent = cssText;
    const mount = el("div", { part: "surface" });
    root.append(style, mount);
    return { root, mount };
}

/// A report definition may name one application-owned stylesheet. It belongs inside
/// the shadow root so its rules share the component's styling boundary. Keeping the
/// URL out of report state means saved/global reports cannot choose a CSS source.
export function setCustomStyleSheet(host, href) {
    const current = host.shadowRoot?.querySelector("link[data-ir-custom-styles]");
    if (!href) { current?.remove(); return; }
    if (current?.getAttribute("href") === href) return;

    const link = el("link", {
        rel: "stylesheet",
        href,
        "data-ir-custom-styles": "",
    });
    if (current) current.replaceWith(link);
    else {
        const root = host.shadowRoot;
        if (root) root.insertBefore(link, root.querySelector('[part~="surface"]'));
    }
}

/// Release document-level listeners and transient UI when a host framework
/// removes a component from the page.
export function disposeWidget(host) {
    closeMenuOwnedBy(host);
    closeDialogsOwnedBy(host);
}
