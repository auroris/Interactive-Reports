// Definition-input column metadata resolution over the widget's loaded schema and working state.
// Like the render and dialog modules, these are free functions over widget instance `w`: the widget holds the data,
// these answer questions about it. Everything in this module speaks the selected
// definition-input table's terms; terminal-table universes live in table.js.

import { activeChain, inputComposableLocation, lookupValue } from "./state.js";

// Public control names mirror ReportFeatures on the server. The schema list is a server-authored
// suggestion for the packaged UI, not an authorization boundary: an embedding application may
// override any name locally. Keeping the catalog here gives every renderer and the public element
// API one canonical spelling and validation path.
export const reportControlNames = Object.freeze([
    "search", "columns", "rename", "columnSettings", "filter", "sort", "pagination",
    "controlBreak", "highlight", "aggregate", "compute", "groupBy", "pivot", "chart",
    "savedReports", "download",
]);

const controlNamesByToken = new Map(reportControlNames.map(name => [name.toLowerCase(), name]));

/**
 * Resolves a caller-supplied control name to its canonical protocol spelling.
 *
 * @param {unknown} name - The requested report control name.
 * @returns {string|null} The canonical name, or `null` when the token is unknown.
 */
export function canonicalControlName(name) {
    return typeof name === "string"
        ? (controlNamesByToken.get(name.trim().toLowerCase()) ?? null)
        : null;
}

// Protocol contract: server column metadata with the report's own display labels applied.
// Labels are client-side presentation: the server sends real names and neutral labels; the
// input labels composable (seeded from the definition via the default report) win here.
/**
 * Returns input columns with labels from the participating input labels composable applied.
 *
 * @param {object} w - The report controller containing the working document and loaded definition schema.
 * @returns {Array<object>} Input-table schema columns in source order, copying only entries whose labels are overridden.
 */
export function pickable(w) {
    const input = w.doc ? activeChain(w.doc)[0]?.table : null;
    const columns = input?.schema ?? w.schema?.columns ?? [];
    const labels = w.doc ? inputComposableLocation(w.doc, "labels")?.composable?.labels : null;
    if (!labels) return columns;
    return columns.map(c => {
        const label = lookupValue(labels, c.name);
        return label ? { ...c, label } : c;
    });
}

/**
 * Returns the schema column matching the supplied identifier.
 *
 * @param {object} w - The report controller whose input columns will be searched.
 * @param {string} name - The logical column identifier to match without case sensitivity.
 * @returns {object|null} The matching input column, or `null`.
 */
export function columnOf(w, name) {
    const requested = String(name ?? "").toLowerCase();
    return pickable(w).find(c => c.name.toLowerCase() === requested) ?? null;
}

/**
 * Returns the normalized type of the supplied schema column.
 *
 * @param {object} w - The report controller whose input schema will be searched.
 * @param {string} name - The logical column identifier.
 * @returns {string} The normalized portable type of the supplied column.
 */
export function typeOf(w, name) { return columnOf(w, name)?.type ?? "other"; }
/**
 * Returns the user-facing label for the supplied column.
 *
 * @param {object} w - The report controller whose input labels will be searched.
 * @param {string} name - The logical column identifier and fallback label.
 * @returns {string} The supplied column's user-facing label.
 */
export function labelOf(w, name) { return columnOf(w, name)?.label ?? name; }

/**
 * Returns the aggregate functions allowed for a column type.
 *
 * @param {object} w - The report controller containing the server capability catalog.
 * @param {string} type - The protocol column type.
 * @returns {Array<string>} The aggregate-function names allowed for the supplied column type.
 */
export function fnsFor(w, type) {
    const catalog = w.schema?.capabilities?.aggregateFunctions ?? {};
    return catalog[type] ?? catalog.other ?? [];
}

// Protocol contract: chart metrics must come out numeric, so the server advertises a stricter
// set.
/**
 * Returns the chart aggregate functions allowed for a column.
 *
 * @param {object} w - The report controller containing the server chart capability catalog.
 * @param {string} type - The protocol source-column type.
 * @returns {Array<string>} The aggregate-function names allowed for the supplied chart column.
 */
export function chartFnsFor(w, type) {
    const catalog = w.schema?.capabilities?.chartAggregateFunctions ?? {};
    return catalog[type] ?? catalog.other ?? [];
}

/**
 * Returns the expression functions advertised by the active schema.
 *
 * @param {object} w - The report controller containing server-advertised expression capabilities.
 * @returns {Array<object>} Function descriptors, or an empty array before schema load.
 */
export function expressionFunctions(w) { return w.schema?.capabilities?.expressionFunctions ?? []; }

/**
 * Returns schema-column identifiers not hidden by report selection state.
 *
 * @param {object} w - The report controller containing the input selection and schema.
 * @returns {Array<string>} An authored selection copy when present, otherwise every pickable input name.
 */
export function visibleColumnNames(w) {
    const columns = w.doc ? inputComposableLocation(w.doc, "select")?.composable?.columns : null;
    if (columns?.length) return [...columns];
    return pickable(w).map(c => c.name);
}

// Protocol contract: the definition's feature suggestion, resolved server-side and delivered on
// the schema payload. A missing list (schema not loaded yet, or an older server that predates
// feature configuration) suggests that everything is on. Client overrides remain authoritative
// for packaged control presentation; endpoint authorization and validation remain server-owned.
/**
 * Determines whether the server suggests that a schema feature be available.
 *
 * @param {object} w - The report controller containing the definition feature suggestion.
 * @param {string} feature - The schema feature flag to evaluate.
 * @returns {boolean} Whether the feature is listed, or whether no suggestion has been loaded.
 */
export function serverFeatureEnabled(w, feature) {
    const features = w.schema?.features;
    return !features || features.includes(feature);
}

/**
 * Determines whether the packaged client controls for one report feature are enabled.
 *
 * @param {object} w - The report controller containing server suggestions and client overrides.
 * @param {string} feature - The canonical report control name.
 * @returns {boolean} The client override when present; otherwise the server suggestion.
 */
export function featureEnabled(w, feature) {
    if (w._controlOverrides?.has(feature)) return w._controlOverrides.get(feature);
    return serverFeatureEnabled(w, feature);
}

// Protocol contract: the definition's per-column overrides, delivered on the schema payload
// keyed by canonical definition-column name. Behavior flags live here; labels ride the default
// report's labels channel). A missing map or entry means unrestricted, which also covers
// computed and derived columns: their names never appear in the map because the server filters
// it to live definition-schema columns.
/**
 * Returns the report-specific capability overrides for a column.
 *
 * @param {object} w - The report controller containing definition-column overrides.
 * @param {string} name - The definition-column name to look up without case sensitivity.
 * @returns {object|null} The override, or `null` for unrestricted and derived columns.
 */
export function columnOverride(w, name) {
    return lookupValue(w.schema?.columnOverrides, name) ?? null;
}

/**
 * Determines whether a column may participate in report sorting.
 *
 * @param {object} w - The report controller containing column overrides.
 * @param {string} name - The logical column name.
 * @returns {boolean} `false` only when the definition explicitly disables sorting.
 */
export function columnSortable(w, name) { return columnOverride(w, name)?.sortable !== false; }
/**
 * Determines whether a column may participate in report filtering.
 *
 * @param {object} w - The report controller containing column overrides.
 * @param {string} name - The logical column name.
 * @returns {boolean} `false` only when the definition explicitly disables filtering.
 */
export function columnFilterable(w, name) { return columnOverride(w, name)?.filterable !== false; }
/**
 * Returns the schema-provided help text for a column when available.
 *
 * @param {object} w - The report controller containing column overrides.
 * @param {string} name - The logical column name.
 * @returns {string|null} The column help.
 */
export function columnHelp(w, name) { return columnOverride(w, name)?.helpText ?? null; }

/**
 * The header cell renders no visible text; the accessible name and every menu, dialog, and picker keep
 * the real label.
 *
 * @param {object} w - The report controller containing column overrides.
 * @param {string} name - The logical column name.
 * @returns {boolean} Whether the definition explicitly hides visible header text.
 */
export function headerLabelHidden(w, name) { return columnOverride(w, name)?.hideLabel === true; }

/**
 * Returns the visible columns eligible for sort rules.
 *
 * @param {object} w - The report controller whose input columns and overrides will be evaluated.
 * @returns {Array<object>} Pickable input column descriptors not explicitly restricted from sorting.
 */
export function sortableColumns(w) { return pickable(w).filter(c => columnSortable(w, c.name)); }
/**
 * Returns the visible columns eligible for filter rules.
 *
 * @param {object} w - The report controller whose input columns and overrides will be evaluated.
 * @returns {Array<object>} Pickable input column descriptors not explicitly restricted from filtering.
 */
export function filterableColumns(w) { return pickable(w).filter(c => columnFilterable(w, c.name)); }

// Invariant: whether the working document can diverge at all. Download is the one feature that
// never mutates the doc; anything else makes Reset worth offering.
/**
 * Determines whether the schema exposes any feature that can change report state.
 *
 * @param {object} w - The report controller containing the effective control policy.
 * @returns {boolean} Whether any effectively enabled feature can mutate report state.
 */
export function anyMutableFeature(w) {
    return reportControlNames.some(feature => feature !== "download" && featureEnabled(w, feature));
}
