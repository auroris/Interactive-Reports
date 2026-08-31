// Browser bundle entrypoint: registers the report custom element. Feature modules remain
// separate so registration does not duplicate report-state, rendering, or transport semantics.

// Protocol contract: this bundle registers <interactive-report> and delegates implementation to
// report/ modules built over the shared core/ primitives. The `report` attribute is required;
// `saved-report` selects an initial saved title; `api-base` selects the API prefix, with `base`
// retained as its compatibility alias. Hosts may call getExport(format, { signal }) to retrieve
// blob metadata without initiating a browser download.

import { InteractiveReportElement } from "./report/element.js";

if (!customElements.get("interactive-report"))
    customElements.define("interactive-report", InteractiveReportElement);
