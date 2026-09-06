// Administration account picker shared by the owner-reassignment, administrator-list, and
// report-access dialogs. It is a bounded, server-backed list of values over
// GET …/admin/users?search=: the application's user directory merged with the identities the
// server already knows. The search box doubles as free-form identity entry, so an account the
// directory does not list can still be named exactly.

import { api, apiUrl } from "../core/api.js";
import { el } from "../core/dom.js";

const USER_SEARCH_DEBOUNCE_MS = 200;

/**
 * Looks up account choices for administration controls.
 *
 * @param {string} base - The API base.
 * @param {string} [search=""] - Optional case-insensitive partial-match text.
 * @param {{signal?: AbortSignal}} [options={}] - Optional request cancellation.
 * @returns {Promise<{items: Array<{display: string, value: string}>, truncated: boolean}>} Bounded matches in server order.
 * @throws {ApiError} When the lookup fails.
 *
 * Side effects: performs a fetch.
 */
export async function searchUsers(base, search = "", { signal } = {}) {
    const text = String(search ?? "").trim();
    const url = apiUrl(base, "admin", "users") + (text ? `?search=${encodeURIComponent(text)}` : "");
    const result = await api(url, { signal });
    return { items: result.items ?? [], truncated: result.truncated === true };
}

/**
 * Builds a searchable account picker. `input` is the search box and free-form identity field;
 * `results` holds the status line and the bounded result list. They are separate nodes so the
 * input can sit inside a label or an add row while the results render beneath it.
 *
 * @param {object} w - The widget providing `base` and `t()`.
 * @param {{onPick: (user: {display: string, value: string}) => void, onError?: (error: Error) => void, onResults?: (items: Array<{display: string, value: string}>) => void, placeholder?: string}} options - Selection, failure, and result callbacks.
 * @returns {{input: HTMLInputElement, results: HTMLElement, value: string, load: () => Promise<void>, dispose: () => void}} The picker controller; `value` is the trimmed input text.
 *
 * Side effects: starts the initial lookup and debounces one lookup per keystroke burst.
 */
export function userPicker(w, { onPick, onError, onResults, placeholder } = {}) {
    const input = el("input", {
        class: "ir-input ir-input-wide",
        type: "text",
        autocomplete: "off",
        spellcheck: false,
        maxLength: 200,
        placeholder: placeholder ?? w.t("users.searchPlaceholder"),
    });
    const status = el("p", { class: "ir-dialog-note ir-users-status", "aria-live": "polite" });
    const list = el("div", { class: "ir-users-items", role: "listbox", "aria-label": w.t("users.results") });
    const results = el("div", { class: "ir-users-results" }, status, list);
    let timer = null;
    let request = null;
    let sequence = 0;

    const load = async () => {
        const current = ++sequence;
        request?.abort();
        request = new AbortController();
        status.textContent = w.t("users.loading");
        list.replaceChildren();
        try {
            const result = await searchUsers(w.base, input.value, { signal: request.signal });
            if (current !== sequence) return;
            const items = result.items ?? [];
            list.replaceChildren(...items.map(user => el("button", {
                type: "button",
                class: "ir-users-item",
                role: "option",
                onclick: () => onPick?.(user),
            },
            el("span", { class: "ir-users-display" }, user.display ?? user.value),
            user.display && user.display !== user.value
                ? el("span", { class: "ir-users-value" }, user.value)
                : null)));
            status.textContent = items.length === 0
                ? w.t("users.empty")
                : result.truncated
                    ? w.t("users.truncated", { count: items.length })
                    : "";
            onResults?.(items);
        } catch (error) {
            if (error?.name === "AbortError" || current !== sequence) return;
            status.textContent = "";
            onError?.(error);
        }
    };

    input.addEventListener("input", () => {
        clearTimeout(timer);
        timer = setTimeout(() => void load(), USER_SEARCH_DEBOUNCE_MS);
    });
    void load();

    return {
        input,
        results,
        get value() { return input.value.trim(); },
        set value(text) { input.value = text ?? ""; },
        load,
        dispose() {
            clearTimeout(timer);
            request?.abort();
        },
    };
}
