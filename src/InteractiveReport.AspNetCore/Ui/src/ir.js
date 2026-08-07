// <interactive-report report="orders"></interactive-report>
//
// Bundle entry for the packaged Interactive Report widget: an APEX-style
// consumer of the report protocol. The implementation lives under report/
// (element, state, schema lookups, skeleton, search, menus, saved reports,
// render/, dialogs/) over the shared core/ primitives; this entry only
// registers the element.
//
// Attributes:
//   report   — preferred report definition; falls back to the first report visible
//              to the caller when missing or unavailable
//   api-base — API prefix; defaults to the prefix this script was served from
//   base     — compatibility alias for api-base

import { InteractiveReportElement } from "./report/element.js";

if (!customElements.get("interactive-report"))
    customElements.define("interactive-report", InteractiveReportElement);
