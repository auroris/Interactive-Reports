// Intercepts window.fetch calls to route /api/reports/* requests directly to InteractiveReportServer.

/**
 * Installs an in-browser fetch interceptor that routes report API requests to the provided server.
 * All non-report requests pass through to the original fetch function.
 *
 * @param {import("./server.js").InteractiveReportServer} server
 * @param {object} [options={}]
 * @param {string} [options.apiPrefix] - Default is server.apiPrefix
 * @returns {{ uninstall: () => void }} Function to restore the original fetch
 */
export function installFetchInterceptor(server, options = {}) {
    const apiPrefix = (options.apiPrefix || server.apiPrefix || "/api/reports").replace(/\/+$/, "");
    const originalFetch = globalThis.fetch;

    globalThis.fetch = async function interceptedFetch(input, init = {}) {
        let urlStr = typeof input === "string" ? input : (input?.url || "");

        // Determine if request targets apiPrefix
        let pathname = urlStr;
        try {
            const parsed = new URL(urlStr, globalThis.location?.href || "https://local.ir");
            pathname = parsed.pathname;
        } catch {
            pathname = urlStr.split("?")[0];
        }

        if (pathname.startsWith(apiPrefix)) {
            return await server.handleRequest(input, init);
        }

        if (typeof originalFetch === "function") {
            return await originalFetch.call(globalThis, input, init);
        }

        throw new Error(`Fetch is not supported for URL '${urlStr}' outside of ${apiPrefix}.`);
    };

    return {
        uninstall() {
            globalThis.fetch = originalFetch;
        },
    };
}
