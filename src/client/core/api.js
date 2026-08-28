// Fetch layer for the report protocol: problem+json aware, JSON in/out, blob export.
// Shared by the report widget (ir.js) and the admin widget (ir-admin.js).

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

function problemLines(problem) {
    const lines = [];
    if (problem.title) lines.push(problem.title);
    if (problem.detail) lines.push(problem.detail);
    for (const messages of Object.values(problem.errors ?? {}))
        for (const message of messages) lines.push(message);
    return lines;
}

export class ApiError extends Error {
    constructor(problem, status) {
        super(problemLines(problem).join(" — ") || `HTTP ${status}`);
        this.name = "ApiError";
        this.status = status;
        this.problem = problem;
        this.traceId = problem.traceId ?? null; // sanitized server errors carry a correlation id
    }
}

/// The canonical content of an error, shared by banners and dialog error boxes:
/// the server's sanitized title, detail, and each validation message, in order.
/// Duck-typed on problem so it also covers plain Errors and strings.
export function errorLines(err) {
    if (typeof err === "string") return [err];
    const problem = err?.problem;
    if (!problem || typeof problem !== "object") return [err?.message || "Something went wrong."];
    const lines = problemLines(problem);
    if (!lines.length) lines.push(err.message || `HTTP ${err.status}`);
    return lines;
}

/// Compact banner text. Dialogs retain errorLines so each message can occupy its
/// own row; single-line presenters share this trace-reference convention.
export function errorText(err, message = null) {
    const text = message ?? errorLines(err).join(" — ");
    return err?.traceId ? `${text} (ref ${err.traceId})` : text;
}

async function problemFrom(res) {
    const problem = await res.json().catch(() => ({}));
    return new ApiError(problem, res.status);
}

export async function api(url, { method = "GET", body, signal } = {}) {
    const res = await fetch(url, {
        method,
        signal,
        headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
        body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) throw await problemFrom(res);
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
    if (!res.ok) throw await problemFrom(res);
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
