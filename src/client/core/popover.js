// Popover compatibility wraps native operations with the shared attribute fallback used by
// hosts that do not expose the Popover API. Keeping the fallback marker here gives menus and
// modeless dialogs one display protocol.

/**
 * Determines whether a native or fallback popover is currently open.
 *
 * @param {Element} node - The element whose native or fallback open state is queried.
 * @returns {boolean} Whether the element is currently open.
 */
export function popoverIsOpen(node) {
    try { return node.matches(":popover-open"); }
    catch { return node.hasAttribute("data-ir-popover-open"); }
}

/**
 * Opens a popover with native support when available and a compatibility fallback otherwise.
 *
 * @param {Element} node - The element to open.
 * @param {{source?: Element}} [options={}] - An optional invoking element for browsers that support the Popover API source option.
 * @returns {void} No value.
 *
 * Side effects: opens the native popover or adds the fallback open-state attribute.
 */
export function showPopover(node, { source } = {}) {
    if (typeof node.showPopover === "function") {
        if (source) {
            try { node.showPopover({ source }); return; }
            catch { /* Retry without the newer source option. */ }
        }
        node.showPopover();
        return;
    }
    node.setAttribute("data-ir-popover-open", "");
}

/**
 * Closes an open native or fallback popover.
 *
 * @param {Element} node - The element to close.
 * @returns {void} No value.
 *
 * Side effects: closes the native popover when possible and always removes the fallback attribute.
 */
export function hidePopover(node) {
    if (typeof node.hidePopover === "function" && popoverIsOpen(node)) {
        try { node.hidePopover(); }
        catch { /* Attribute removal and caller cleanup remain the compatibility fallback. */ }
    }
    node.removeAttribute("data-ir-popover-open");
}
