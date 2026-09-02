// Host-observable activation events shared by the definition's edit pencil and create button.
// Both affordances dispatch a cancelable, composed CustomEvent from the host element on every
// activation. In navigate mode the anchor's default action follows unless a listener calls
// preventDefault(); in event mode there is no navigation to suppress, so the event is the whole
// behavior and the host application owns what happens next (a dialog, a router push, ...).

/**
 * Dispatches one link-activation event from the host element.
 *
 * @param {object} w - The report controller or host element that dispatches events.
 * @param {string} type - The event type, `ir-edit` or `ir-create`.
 * @param {object} detail - The detached event detail.
 * @returns {boolean} False when a listener called `preventDefault()`.
 *
 * Side effects: dispatches a bubbling, composed, cancelable event.
 */
export function dispatchLinkEvent(w, type, detail) {
    // The host's own window supplies the constructor, as the query events do, so the event is
    // native to whichever document the element lives in.
    const node = w?.host ?? w;
    const EventType = node?.ownerDocument?.defaultView?.CustomEvent ?? globalThis.CustomEvent;
    return w.dispatchEvent(new EventType(type, {
        bubbles: true,
        composed: true,
        cancelable: true,
        detail,
    }));
}

/**
 * Builds the click handler for a navigate-mode anchor: the event fires first and a prevented
 * event cancels the navigation. Modifier clicks and middle clicks still reach the anchor when
 * nobody prevents them, so open-in-new-tab keeps working.
 *
 * @param {object} w - The report controller or host element that dispatches events.
 * @param {string} type - The event type, `ir-edit` or `ir-create`.
 * @param {() => object} detail - Produces the detached event detail per click.
 * @returns {(event: MouseEvent) => void} The anchor click handler.
 */
export function anchorClickHandler(w, type, detail) {
    return event => {
        if (!dispatchLinkEvent(w, type, detail())) event.preventDefault();
    };
}

/**
 * Whether a link definition asks for the event-only affordance (a button, no navigation).
 *
 * @param {object|null|undefined} link - The edit-link or create-link definition.
 * @returns {boolean} True for `mode: "event"`.
 */
export function eventMode(link) {
    return String(link?.mode ?? "").toLowerCase() === "event";
}
