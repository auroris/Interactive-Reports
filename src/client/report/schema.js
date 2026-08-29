// Column metadata resolution over the widget's loaded schema and last result.
// Free functions over the widget instance `w`, like the render and dialog
// modules — the widget holds the data, these answer questions about it.
// Everything in this module speaks the SOURCE table's terms; stage-scoped
// universes live in stage.js.

import { lookupValue, sourceLayer } from "./state.js";

/// Server column metadata with the report's own display labels applied. Labels
/// are client-side presentation: the server sends real names and neutral labels;
/// the source layer's labels (seeded from the definition via the default report)
/// win here.
export function pickable(w) {
    const columns = w.lastResult?.availableColumns ?? w.schema?.columns ?? [];
    const labels = w.doc ? sourceLayer(w.doc).labels : null;
    if (!labels) return columns;
    return columns.map(c => {
        const label = lookupValue(labels, c.name);
        return label ? { ...c, label } : c;
    });
}

export function columnOf(w, name) {
    const requested = String(name ?? "").toLowerCase();
    return pickable(w).find(c => c.name.toLowerCase() === requested) ?? null;
}

export function typeOf(w, name) { return columnOf(w, name)?.type ?? "other"; }
export function labelOf(w, name) { return columnOf(w, name)?.label ?? name; }

export function fnsFor(w, type) {
    const catalog = w.schema?.capabilities?.aggregateFunctions ?? {};
    return catalog[type] ?? catalog.other ?? [];
}

/// Chart metrics must come out numeric, so the server advertises a stricter set.
export function chartFnsFor(w, type) {
    const catalog = w.schema?.capabilities?.chartAggregateFunctions ?? {};
    return catalog[type] ?? catalog.other ?? [];
}

export function expressionFunctions(w) { return w.schema?.capabilities?.expressionFunctions ?? []; }

export function visibleColumnNames(w) {
    const columns = w.doc ? sourceLayer(w.doc).columns : null;
    if (columns?.length) return [...columns];
    return pickable(w).map(c => c.name);
}

/// The definition's feature whitelist, resolved server-side and delivered on the
/// schema payload. A missing list (schema not loaded yet, or an older server that
/// predates feature configuration) means everything is on.
export function featureEnabled(w, feature) {
    const features = w.schema?.features;
    return !features || features.includes(feature);
}

/// The definition's per-column overrides, delivered on the schema payload keyed
/// by canonical definition-column name (behavior flags only — labels ride the default
/// report's labels channel). A missing map or entry means unrestricted, which
/// also covers computed columns and stage synthetics: their names never appear
/// in the map because the server filters it to live definition-schema columns.
export function columnOverride(w, name) {
    return lookupValue(w.schema?.columnOverrides, name) ?? null;
}

export function columnSortable(w, name) { return columnOverride(w, name)?.sortable !== false; }
export function columnFilterable(w, name) { return columnOverride(w, name)?.filterable !== false; }
export function columnHelp(w, name) { return columnOverride(w, name)?.helpText ?? null; }

/// The header cell renders no visible text; the accessible name and every menu,
/// dialog, and picker keep the real label.
export function headerLabelHidden(w, name) { return columnOverride(w, name)?.hideLabel === true; }

export function sortableColumns(w) { return pickable(w).filter(c => columnSortable(w, c.name)); }
export function filterableColumns(w) { return pickable(w).filter(c => columnFilterable(w, c.name)); }

/// Whether the working document can diverge at all. Download is the one feature
/// that never mutates the doc; anything else makes Reset worth offering.
export function anyMutableFeature(w) {
    const features = w.schema?.features;
    return !features || features.some(f => f !== "download");
}
