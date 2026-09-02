// Intercepts window.fetch calls to route report and file-download requests directly to InteractiveReportServer.

function matchesPrefix(pathname, prefix) {
    return pathname === prefix || pathname.startsWith(`${prefix}/`);
}

/**
 * Installs an in-browser fetch interceptor that routes report API requests to the provided server.
 * All unrelated requests pass through to the original fetch function.
 *
 * @param {import("./server.js").InteractiveReportServer} server
 * @param {object} [options={}]
 * @param {string} [options.apiPrefix] - Default is server.apiPrefix
 * @param {string} [options.downloadPrefix] - Default is server.downloadPrefix
 * @returns {{ uninstall: () => void }} Function to restore the original fetch
 */
export function installFetchInterceptor(server, options = {}) {
    const apiPrefix = (options.apiPrefix || server.apiPrefix || "/api/reports").replace(/\/+$/, "");
    const downloadPrefix = (options.downloadPrefix || server.downloadPrefix || "/api/download").replace(/\/+$/, "");
    const originalFetch = globalThis.fetch;

    globalThis.fetch = async function interceptedFetch(input, init = {}) {
        let urlStr = typeof input === "string" ? input : (input?.url || "");

        // Determine whether the request targets either in-process endpoint prefix.
        let pathname = urlStr;
        try {
            const parsed = new URL(urlStr, globalThis.location?.href || "https://local.ir");
            pathname = parsed.pathname;
        } catch {
            pathname = urlStr.split("?")[0];
        }

        if (matchesPrefix(pathname, apiPrefix) || matchesPrefix(pathname, downloadPrefix)) {
            return await server.handleRequest(input, init);
        }

        if (typeof originalFetch === "function") {
            return await originalFetch.call(globalThis, input, init);
        }

        throw new Error(`Fetch is not supported for URL '${urlStr}' outside of ${apiPrefix} or ${downloadPrefix}.`);
    };

    return {
        uninstall() {
            globalThis.fetch = originalFetch;
        },
    };
}
