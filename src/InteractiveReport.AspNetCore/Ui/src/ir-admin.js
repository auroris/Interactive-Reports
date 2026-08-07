// <interactive-report-admin></interactive-report-admin>
//
// Bundle entry for the saved-report administration widget. The implementation
// lives in admin/element.js over the shared core/ primitives; this entry only
// registers the element.
//
// Attributes:
//   api-base — API prefix; defaults to the prefix this script was served from
//   base     — compatibility alias for api-base

import { InteractiveReportAdminElement } from "./admin/element.js";

if (!customElements.get("interactive-report-admin"))
    customElements.define("interactive-report-admin", InteractiveReportAdminElement);
