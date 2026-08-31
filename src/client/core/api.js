// Report protocol transport: a coded-error-aware fetch layer for JSON requests and blob
// export. Shared by the report widget (ir.js) and the admin widget (ir-admin.js).

import { errorReference, localizedError, translate } from "./localization.js";

/**
 * Default API prefix for a widget with no api-base attribute: the prefix this script was served from.
 * …/api/reports/ui/ir.js → …/api/reports.
 *
 * @returns {string} The normalized report API prefix.
 */
export function defaultApiBase() {
    return new URL("..", import.meta.url).pathname.replace(/\/$/, "");
}

/**
 * Builds a normalized report API endpoint from the configured base and path segments.
 *
 * @param {string} base - The API prefix, with or without trailing slashes.
 * @param {Array<string>} ...segments - The URL path segments appended to the configured API base.
 * @returns {string} The encoded endpoint URL.
 */
export function apiUrl(base, ...segments) {
    const prefix = String(base).replace(/\/+$/, "");
    if (!segments.length) return prefix;
    return `${prefix}/${segments.map(segment => encodeURIComponent(String(segment))).join("/")}`;
}

/**
 * Extracts displayable messages from a server problem response.
 *
 * @param {Error|string|object} error - The error value to normalize for display.
 * @param {string|Element|object|null} [locale=null] - The locale or DOM context used to localize a recognized error code.
 * @returns {Array<string>} Sanitized title, description, details, and legacy validation messages in display order.
 */
function serverErrorLines(error, locale = null) {
    const lines = [];
    const localized = error.code ? localizedError(error.code, locale) : null;
    const title = localized?.title ?? error.title;
    if (title) lines.push(title);
    const description = localized?.description
        ?? error.description
        ?? error.detail; // Detail supports pre-code servers.
    if (description)
        lines.push(...String(description).split(/\r?\n/).filter(Boolean));
    if (error.details)
        lines.push(...String(error.details).split(/\r?\n/).filter(Boolean));
    // ValidationProblemDetails compatibility for clients and servers upgraded at different
    // times. New servers flatten these entries into details.
    for (const messages of Object.values(error.errors ?? {}))
        for (const message of messages) lines.push(message);
    return lines;
}

export class ApiError extends Error {
    /**
     * Creates a typed HTTP error from a decoded server problem document.
     *
     * @param {object|null} error - The decoded problem document; non-object values are treated as empty problems.
     * @param {number} status - The unsuccessful HTTP status.
     *
     * Side effects: retains the problem, status, stable error code, and trace identifier on the error instance.
     */
    constructor(error, status) {
        error = error && typeof error === "object" ? error : {};
        super(serverErrorLines(error).join(" — ") || translate(null, "error.http", { status }));
        this.name = "ApiError";
        this.status = status;
        this.error = error;
        this.problem = error; // Compatibility for host code written against the former contract.
        this.code = error.code ?? null;
        this.traceId = error.traceId ?? null; // Protocol contract: sanitized server errors carry a correlation id.
    }
}

// Protocol contract: the canonical content of an error, shared by banners and dialog error
// boxes: the server's sanitized title and description, in order. Duck-typed on error so it also
// covers plain Errors, strings, and the former problem-details contract.
/**
 * Normalizes an arbitrary failure into user-facing diagnostic lines.
 *
 * @param {Error|string|object} err - The error value to normalize for display.
 * @param {string} [locale=null] - The locale used for translation and value formatting.
 * @returns {Array<string>} The displayable error messages.
 */
export function errorLines(err, locale = null) {
    if (typeof err === "string") return [err];
    const error = err?.error ?? err?.problem;
    if (!error || typeof error !== "object") return [err?.message || translate(locale, "error.generic")];
    const lines = serverErrorLines(error, locale);
    if (!lines.length) lines.push(err.message || translate(locale, "error.http", { status: err.status }));
    return lines;
}

/**
 * Formats an arbitrary error as compact user-facing text for the test banner.
 *
 * @param {Error|string|object} err - The error value to normalize for display.
 * @param {string|null} [message=null] - An optional display message that replaces the normalized error lines.
 * @param {string|Element|object|null} [locale=null] - The locale or DOM context used for fallback text.
 * @returns {string} The compact error text, including a trace reference when present.
 */
export function errorText(err, message = null, locale = null) {
    const text = message ?? errorLines(err, locale).join(" — ");
    return err?.traceId ? `${text} ${errorReference(err.traceId, locale, true)}` : text;
}

/**
 * Builds an API error from an unsuccessful HTTP response.
 *
 * @param {Response} res - The unsuccessful response whose JSON problem body may be decoded.
 * @returns {Promise<ApiError>} An API error using an empty problem when the body is absent or invalid JSON.
 */
async function errorFrom(res) {
    const error = await res.json().catch(() => ({}));
    return new ApiError(error, res.status);
}

/**
 * Sends an API request and returns its decoded JSON response.
 *
 * @param {string} url - The endpoint URL to request.
 * @param {{method?: string, body?: unknown, signal?: AbortSignal}} [options={}] - The HTTP method, optional JSON body, and cancellation signal.
 * @returns {Promise<unknown|null>} Decoded JSON, or `null` for HTTP 204.
 * @throws {ApiError} When the response status is unsuccessful.
 *
 * Side effects: performs a fetch and serializes a provided body as JSON.
 */
export async function api(url, { method = "GET", body, signal } = {}) {
    const res = await fetch(url, {
        method,
        signal,
        headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
        body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) throw await errorFrom(res);
    if (res.status === 204) return null;
    return res.json();
}

// Protocol contract: POST that answers with a file. Returns { blob, filename, response,
// truncated }.
/**
 * Posts an export request and returns downloadable file metadata.
 *
 * @param {string} url - The report export endpoint.
 * @param {object|string|null} body - The report state to serialize as JSON.
 * @param {{signal?: AbortSignal}} [options={}] - Optional request cancellation.
 * @returns {Promise<{blob: Blob, filename: string|null, response: Response, truncated: boolean}>} The file body and response metadata.
 *
 * Side effects: performs a POST request.
 */
export async function download(url, body, { signal } = {}) {
    const result = await downloadFile(url, { method: "POST", body, signal });
    return {
        ...result,
        truncated: result.response.headers.get("X-IR-Truncated") === "true",
    };
}

// Protocol contract: request an arbitrary attachment. Used by the admin report-document export
// as well as the report-specific POST export above.
/**
 * Requests a file and returns its blob with response metadata.
 *
 * @param {string} url - The attachment endpoint URL.
 * @param {{method?: string, body?: unknown, signal?: AbortSignal}} [options={}] - The HTTP method, optional JSON body, and cancellation signal.
 * @returns {Promise<{blob: Blob, filename: string|null, response: Response}>} The file body, parsed content-disposition filename, and original response.
 * @throws {ApiError} When the response status is unsuccessful.
 *
 * Side effects: performs a fetch and consumes its body as a blob.
 */
export async function downloadFile(url, { method = "GET", body, signal } = {}) {
    const res = await fetch(url, {
        method,
        signal,
        headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
        body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) throw await errorFrom(res);
    const disposition = res.headers.get("Content-Disposition") ?? "";
    const filename = /filename="?([^";]+)"?/.exec(disposition)?.[1] ?? null;
    return {
        blob: await res.blob(),
        filename,
        response: res,
    };
}

/**
 * Trigger a browser download of a blob.
 *
 * @param {Blob} blob - The file content to expose as a browser download.
 * @param {string} filename - The suggested filename presented by the browser download.
 * @returns {void} No value.
 *
 * Side effects: starts a browser download.
 */
export function saveBlob(blob, filename) {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = filename;
    a.click();
    URL.revokeObjectURL(a.href);
}
