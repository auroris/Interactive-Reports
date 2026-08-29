// Export: retrieve the current report artifact as data. The public custom-
// element method hands the result to its host; the Actions-menu command is a
// thin browser-download wrapper over the same operation.

import { download, saveBlob } from "../core/api.js";

function invalidState(message) {
    const error = new Error(message);
    error.name = "InvalidStateError";
    return error;
}

/**
 * Retrieve one server-generated representation of the current working report.
 * No browser navigation or download is initiated.
 *
 * Returns { blob, filename, contentType, truncated }.
 */
export async function retrieveExport(w, format = "csv", { signal } = {}) {
    format = String(format ?? "").trim().toLowerCase();
    if (!format) throw new TypeError("Export format must not be empty.");
    if (!w.reportName || !w.schema || !w.doc || !w.lastResult)
        throw invalidState("The report must finish loading before it can be exported.");

    const { blob, filename, truncated, response } = await download(
        `${w.reportUrl("export")}?format=${encodeURIComponent(format)}`,
        w.serialize(),
        { signal });
    return {
        blob,
        filename: filename ?? `${w.reportName}.${format}`,
        contentType: response.headers.get("Content-Type") || blob.type || "application/octet-stream",
        truncated,
    };
}

export async function downloadExport(w, format = "csv") {
    try {
        const { blob, filename, truncated } = await retrieveExport(w, format);
        saveBlob(blob, filename);
        if (truncated) w.notify(w.t("report.exportTruncated"), "warn");
    } catch (err) {
        w.showError(err);
    }
}
