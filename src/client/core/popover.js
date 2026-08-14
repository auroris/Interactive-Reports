// Native popover operations with the shared attribute fallback used by hosts
// that do not expose the Popover API. Keeping the fallback marker here gives
// menus and modeless dialogs one display protocol.

export function popoverIsOpen(node) {
    try { return node.matches(":popover-open"); }
    catch { return node.hasAttribute("data-ir-popover-open"); }
}

export function showPopover(node, { source } = {}) {
    if (typeof node.showPopover === "function") {
        if (source) {
            try { node.showPopover({ source }); return; }
            catch { /* retry without the newer source option */ }
        }
        node.showPopover();
        return;
    }
    node.setAttribute("data-ir-popover-open", "");
}

export function hidePopover(node) {
    if (typeof node.hidePopover === "function" && popoverIsOpen(node)) {
        try { node.hidePopover(); }
        catch { /* attribute removal and caller cleanup are the fallback */ }
    }
    node.removeAttribute("data-ir-popover-open");
}
