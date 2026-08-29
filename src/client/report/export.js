// Export: POST the current state doc to the export endpoint and hand the
// answer to the browser as a file download.

import { download, saveBlob } from "../core/api.js";

export async function exportCsv(w) {
    try {
        const { blob, filename, truncated } = await download(
            `${w.reportUrl("export")}?format=csv`, w.serialize());
        saveBlob(blob, filename ?? `${w.reportName}.csv`);
        if (truncated) w.notify(w.t("report.exportTruncated"), "warn");
    } catch (err) {
        w.showError(err);
    }
}
