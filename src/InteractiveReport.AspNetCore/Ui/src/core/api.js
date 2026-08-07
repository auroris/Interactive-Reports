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

export class ApiError extends Error {
    constructor(problem, status) {
        const parts = [];
        if (problem.title) parts.push(problem.title);
        if (problem.detail) parts.push(problem.detail);
        for (const messages of Object.values(problem.errors ?? {}))
            for (const m of messages) parts.push(m);
        super(parts.join(" — ") || `HTTP ${status}`);
        this.name = "ApiError";
        this.status = status;
        this.problem = problem;
        this.errors = problem.errors ?? null;   // validation problems: { path: [messages] }
        this.traceId = problem.traceId ?? null; // sanitized server errors carry a correlation id
    }
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
    const res = await fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
    });
    if (!res.ok) throw await problemFrom(res);
    const disposition = res.headers.get("Content-Disposition") ?? "";
    const filename = /filename="?([^";]+)"?/.exec(disposition)?.[1] ?? null;
    return {
        blob: await res.blob(),
        filename,
        truncated: res.headers.get("X-IR-Truncated") === "true",
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
