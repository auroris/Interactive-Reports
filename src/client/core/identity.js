// Shared access to the optional who-am-I endpoint. Both widgets use the same absence policy, and
// an admin page initializes its shell and embedded report together, so concurrent requests for
// the same API base share one fetch.

import { api, apiUrl } from "./api.js";

const inFlight = new Map();

/**
 * Loads the current identity and coalesces concurrent requests to the same resolved endpoint URL.
 *
 * @param {string} base - The API base used to resolve the `whoami` endpoint.
 * @returns {Promise<{whoami: object|null, error: Error|null}>} The identity and any actionable failure. HTTP 401 and 404 are treated as an absent optional identity.
 *
 * Side effects: performs at most one in-flight network request per endpoint URL and removes the cached promise when it settles.
 */
export function loadWhoami(base) {
    const url = apiUrl(base, "whoami");
    const existing = inFlight.get(url);
    if (existing) return existing;

    const request = api(url)
        .then(whoami => ({ whoami, error: null }))
        .catch(error => ({
            whoami: null,
            // 404 means the endpoint is disabled; 401 means no signed-in identity is available.
            // Every other failure remains actionable.
            error: error.status === 404 || error.status === 401 ? null : error,
        }));
    inFlight.set(url, request);
    void request.finally(() => {
        if (inFlight.get(url) === request) inFlight.delete(url);
    });
    return request;
}
