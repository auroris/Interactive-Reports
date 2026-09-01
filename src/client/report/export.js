// Export transport retrieves the current report artifact as data. The public custom-element
// method hands the result to its host; the Actions-menu command is a browser-download wrapper
// over the same request.

import { apiUrl, download, saveBlob } from "../core/api.js";

/**
 * Creates the error used when an operation requires a valid report state.
 *
 * @param {string} message - The reason the export cannot start.
 * @returns {Error} An error named `InvalidStateError` for the public element API.
 */
function invalidState(message) {
    const error = new Error(message);
    error.name = "InvalidStateError";
    return error;
}

/**
 * Retrieves one server-generated representation of the current report without initiating a browser download.
 *
 * @param {object} w - The loaded report controller, which supplies the active state and report URL.
 * @param {string} [format="csv"] - The server export format, normalized to trimmed lowercase.
 * @param {{signal?: AbortSignal}} [options={}] - Optional cancellation for the export request.
 * @returns {Promise<{blob: Blob, filename: string, contentType: string, truncated: boolean}>} The file data and response metadata.
 * @throws {TypeError} When `format` is empty.
 * @throws {Error} When the report has not finished loading or the server request fails.
 *
 * Side effects: posts the serialized report state to the export endpoint.
 */
export async function retrieveExport(w, format = "csv", { signal } = {}) {
    format = String(format ?? "").trim().toLowerCase();
    if (!format) throw new TypeError("Export format must not be empty.");
    if (!w.reportId || !w.definitionName || !w.schema || !w.doc || !w.lastResult)
        throw invalidState("The report must finish loading before it can be exported.");

    const { blob, filename, truncated, response } = await download(
        apiUrl(w.downloadBase, w.definitionName, format),
        w.serialize(),
        { signal });
    return {
        blob,
        filename: filename ?? `${w.definitionName}.${format}`,
        contentType: response.headers.get("Content-Type") || blob.type || "application/octet-stream",
        truncated,
    };
}

/**
 * Requests the selected report export and hands the returned file to the browser.
 *
 * @param {object} w - The report controller that supplies export state, download services, and notifications.
 * @param {string} [format="csv"] - The server export format.
 * @returns {Promise<void>} Resolves after the download starts or the failure has been shown.
 *
 * Side effects: performs a network request, initiates a browser download, and may show a warning or error.
 */
export async function downloadExport(w, format = "csv") {
    try {
        const { blob, filename, truncated } = await retrieveExport(w, format);
        saveBlob(blob, filename);
        if (truncated) w.notify(w.t("report.exportTruncated"), "warn");
    } catch (err) {
        w.showError(err);
    }
}
