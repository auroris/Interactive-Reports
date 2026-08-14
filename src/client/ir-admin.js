// <interactive-report-admin></interactive-report-admin>
//
// Bundle entry for the saved-report administration widget. The admin element
// embeds an <interactive-report> pointed at the built-in "__saved-reports"
// listing, so this bundle registers the report element too — an admin page
// needs no second script tag.
//
// Attributes:
//   api-base — API prefix; defaults to the prefix this script was served from
//   base     — compatibility alias for api-base

import { InteractiveReportAdminElement } from "./admin/element.js";
import { InteractiveReportElement } from "./report/element.js";

if (!customElements.get("interactive-report"))
    customElements.define("interactive-report", InteractiveReportElement);
if (!customElements.get("interactive-report-admin"))
    customElements.define("interactive-report-admin", InteractiveReportAdminElement);
