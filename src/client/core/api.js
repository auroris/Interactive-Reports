// Fetch layer for the report protocol: coded-error aware, JSON in/out, blob export.
// Shared by the report widget (ir.js) and the admin widget (ir-admin.js).

import { errorReference, localizedError } from "./localization.js";

/// Default API prefix for a widget with no api-base attribute: the prefix this
/// script was served from. …/api/reports/ui/ir.js → …/api/reports
export function defaultApiBase() {
    return new URL("..", import.meta.url).pathname.replace(/\/$/, "");
}

export function apiUrl(base, ...segments) {
    const prefix = String(base).replace(/\/+$/, "");
    if (!segments.length) return prefix;
    return `${prefix}/${segments.map(segment => encodeURIComponent(String(segment))).join("/")}`;
}

function serverErrorLines(error, locale = null) {
    const lines = [];
    const localized = error.code ? localizedError(error.code, locale) : null;
    const title = localized?.title ?? error.title;
    if (title) lines.push(title);
    const description = localized?.description
        ?? error.description
        ?? error.detail; // detail supports pre-code servers
    if (description)
        lines.push(...String(description).split(/\r?\n/).filter(Boolean));
    if (error.details)
        lines.push(...String(error.details).split(/\r?\n/).filter(Boolean));
    // ValidationProblemDetails compatibility for clients and servers upgraded at
    // different times. New servers flatten these entries into details.
    for (const messages of Object.values(error.errors ?? {}))
        for (const message of messages) lines.push(message);
    return lines;
}

export class ApiError extends Error {
    constructor(error, status) {
        error = error && typeof error === "object" ? error : {};
        super(serverErrorLines(error).join(" — ") || `HTTP ${status}`);
        this.name = "ApiError";
        this.status = status;
        this.error = error;
        this.problem = error; // compatibility for host code written against the former contract
        this.code = error.code ?? null;
        this.traceId = error.traceId ?? null; // sanitized server errors carry a correlation id
    }
}

/// The canonical content of an error, shared by banners and dialog error boxes:
/// the server's sanitized title and description, in order. Duck-typed on error so
/// it also covers plain Errors, strings, and the former problem-details contract.
export function errorLines(err, locale = null) {
    if (typeof err === "string") return [err];
    const error = err?.error ?? err?.problem;
    if (!error || typeof error !== "object") return [err?.message || "Something went wrong."];
    const lines = serverErrorLines(error, locale);
    if (!lines.length) lines.push(err.message || `HTTP ${err.status}`);
    return lines;
}

/// Compact banner text. Dialogs retain errorLines so each message can occupy its
/// own row; single-line presenters share this trace-reference convention.
export function errorText(err, message = null, locale = null) {
    const text = message ?? errorLines(err, locale).join(" — ");
    return err?.traceId ? `${text} ${errorReference(err.traceId, locale, true)}` : text;
}

async function errorFrom(res) {
    const error = await res.json().catch(() => ({}));
    return new ApiError(error, res.status);
}

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

/// POST that answers with a file. Returns { blob, filename, truncated }.
export async function download(url, body) {
    const result = await downloadFile(url, { method: "POST", body });
    return {
        ...result,
        truncated: result.response.headers.get("X-IR-Truncated") === "true",
    };
}

/// Request an arbitrary attachment. Used by the admin report-document export as
/// well as the report-specific POST export above.
export async function downloadFile(url, { method = "GET", body } = {}) {
    const res = await fetch(url, {
        method,
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

/// Trigger a browser download of a blob.
export function saveBlob(blob, filename) {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = filename;
    a.click();
    URL.revokeObjectURL(a.href);
}
