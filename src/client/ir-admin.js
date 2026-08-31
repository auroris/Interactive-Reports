// Administration bundle entrypoint: registers the saved-report administration element and the
// report element it embeds. It shares core widget infrastructure while keeping administration
// workflows out of the ordinary report-viewer entrypoint.

// Protocol contract: <interactive-report-admin> embeds <interactive-report> against the built-in
// "__saved-reports" listing, so this entry registers both elements and requires only one script.
// `api-base` selects the API prefix, with `base` retained as its compatibility alias.

import { InteractiveReportAdminElement } from "./admin/element.js";
import { InteractiveReportElement } from "./report/element.js";

if (!customElements.get("interactive-report"))
    customElements.define("interactive-report", InteractiveReportElement);
if (!customElements.get("interactive-report-admin"))
    customElements.define("interactive-report-admin", InteractiveReportAdminElement);
