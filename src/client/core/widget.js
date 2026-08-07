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

/// Release document-level listeners and transient UI when a host framework
/// removes a component from the page.
export function disposeWidget(host) {
    closeMenuOwnedBy(host);
    closeDialogsOwnedBy(host);
}
