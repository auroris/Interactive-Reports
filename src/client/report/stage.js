// Generic terminal-table context. A table's shape is one composable in its
// ancestry; it does not choose a separate editor implementation. Every ordinary
// editor works against the active table's own terminal segment and uses that
// table's server-populated schema cache as its column universe.

import {
    columnFilterable,
    columnSortable,
    featureEnabled,
} from "./schema.js";
import {
    activeTableLayer,
    activeTableSchema,
    composedLabels,
    lookupValue,
    modeOf,
    sameColumn,
    stageOf,
    tableEntry,
} from "./state.js";

export const stageLabelOf = (layer, name) => lookupValue(layer?.labels, name);

const copyColumns = columns => (columns ?? []).map(column => ({ ...column }));

const replaceLabel = (label, original, replacement) => {
    if (!original || !replacement) return label;
    const at = String(label ?? "").toLowerCase().indexOf(String(original).toLowerCase());
    return at < 0
        ? label
        : `${label.slice(0, at)}${replacement}${label.slice(at + String(original).length)}`;
};

/// Complete terminal schema, independent of the current projection. Newer
/// servers return it in the active table's cache. The response and definition
/// schema remain compatibility fallbacks while a new document makes its first
/// round trip or when connected to an older server.
export function terminalTableColumns(w) {
    const columns = activeTableSchema(w.doc)
        ?? w.lastResult?.availableColumns
        ?? w.lastResult?.columns
        ?? w.schema?.columns
        ?? [];
    const labels = composedLabels(w.doc);
    return copyColumns(columns).map(column => {
        const direct = stageLabelOf({ labels: labels.output }, column.name)
            ?? stageLabelOf({ labels: labels.input }, column.name);
        if (direct) return { ...column, label: direct };
        const inherited = stageLabelOf({ labels: labels.input }, column.formatSource);
        if (!inherited) return column;
        const original = w.schema?.columns?.find(candidate =>
            sameColumn(candidate.name, column.formatSource))?.label ?? column.formatSource;
        return { ...column, label: replaceLabel(column.label, original, inherited) };
    });
}

/// Schema immediately before a UI-authored shape. Built-in shaped tables put
/// the shape first, so their `from` table's cache is exact. For a foreign table
/// with ordinary nodes before the shape, the parent cache is the closest safe
/// column universe; those intermediate nodes remain preserved but are not
/// rewritten by the built-in shape editor.
export function shapeInputColumns(w, stage = null) {
    const owner = tableEntry(w.doc, stage?._tableId);
    if (owner) {
        const parent = tableEntry(w.doc, owner.table?.from);
        if (Array.isArray(parent?.table?.schema)) return copyColumns(parent.table.schema);
        if (String(owner.table?.from ?? "").toLowerCase() === "definition")
            return copyColumns(w.schema?.columns);
    }

    const roots = Object.entries(w.doc?.tables ?? {})
        .filter(([, table]) => String(table?.from ?? "").toLowerCase() === "definition");
    if (roots.length === 1 && Array.isArray(roots[0][1]?.schema))
        return copyColumns(roots[0][1].schema);
    return copyColumns(w.schema?.columns);
}

// Kept as a compatibility export for code that used the old stage helper. Its
// answer is now the same generic terminal schema, including computed columns.
export function groupStageColumns(w, { includeComputed = true } = {}) {
    const columns = terminalTableColumns(w);
    return includeComputed ? columns : columns.filter(column => !column.computed);
}

const shapeDimensions = doc => {
    const group = stageOf(doc, "group")?.shape;
    if (group) return [...(group.by ?? [])];
    const pivot = stageOf(doc, "pivot")?.shape;
    return pivot ? [...(pivot.rows ?? [])] : [];
};

export function stageContext(w) {
    const columns = terminalTableColumns(w);
    const layerOf = d => activeTableLayer(d);
    return {
        mode: modeOf(w.doc),
        columns,
        dims: shapeDimensions(w.doc),
        layer: layerOf,
        columnsLayer: layerOf,
        labelsLayer: layerOf,
        formatsLayer: layerOf,
        computeLayer: layerOf,
        filterLayer: layerOf,
        sortLayer: layerOf,
        highlightLayer: layerOf,
        computeTokens: columns.filter(column => !column.computed),
        sortColumns: columns.filter(column => columnSortable(w, column.name)),
        filterColumns: columns.filter(column => columnFilterable(w, column.name)),
        caps: caps(w),
    };
}

/// The terminal table's currently visible, still-valid column names. An explicit
/// list can outlive a removed column or record a different casing in a saved
/// report, so callers receive canonical surviving names before editing it.
export function visibleStageColumnNames(ctx, w) {
    const explicit = ctx.columnsLayer?.(w.doc)?.columns;
    if (Array.isArray(explicit)) {
        return explicit
            .map(name => ctx.columns.find(column => sameColumn(column.name, name))?.name)
            .filter(name => name !== undefined);
    }
    return ctx.columns.map(column => column.name);
}

/// Ordinary composables have one capability model. The definition feature
/// whitelist still decides whether the built-in UI exposes each editor.
function caps(w) {
    const gate = feature => featureEnabled(w, feature);
    return {
        columns: gate("columns"),
        columnSettings: gate("columnSettings"),
        rename: gate("rename"),
        compute: gate("compute"),
        highlight: gate("highlight"),
        sort: gate("sort"),
        filter: gate("filter"),
        break: gate("controlBreak"),
        aggregate: gate("aggregate"),
        pagination: gate("pagination"),
        visibility: true,
        displayAs: true,
    };
}
