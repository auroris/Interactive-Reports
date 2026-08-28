// Shared access to the optional identity endpoint. Both widgets use the same
// absence policy, and an admin page initializes its shell and embedded report
// together, so concurrent requests for the same API base share one fetch.

import { api, apiUrl } from "./api.js";

const inFlight = new Map();

export function loadWhoami(base) {
    const url = apiUrl(base, "whoami");
    const existing = inFlight.get(url);
    if (existing) return existing;

    const request = api(url)
        .then(whoami => ({ whoami, error: null }))
        .catch(error => ({
            whoami: null,
            // 404 means the endpoint is disabled; 401 means no signed-in
            // identity is available. Every other failure remains actionable.
            error: error.status === 404 || error.status === 401 ? null : error,
        }));
    inFlight.set(url, request);
    void request.finally(() => {
        if (inFlight.get(url) === request) inFlight.delete(url);
    });
    return request;
}
