// The stage context: one object answering "what table is the user looking at,
// which columns does it have, and which layer does each menu edit". Menus,
// dialogs, chips, and renderers consume this instead of hard-coding the source
// layer, so the same Columns/Compute/Sort/Highlight/Settings surfaces operate
// on whichever table the pipeline's tail produces.
//
// Layer routing per mode:
//   grid    → everything edits the source layer.
//   groupBy → columns/labels/formats/computed/sorts/highlights edit the group
//             stage's layer; Filter always edits the source layer (stage 1).
//   pivot   → labels/formats edit the spread layer (keys are response column
//             names, including stable cell names); Compute and Sort edit the
//             GROUP layer — pre-spread computed metrics and row-dim ordering.
//   chart   → no table; only source-level features apply.

import { columnOf, columnSortable, labelOf, pickable, sortableColumns, typeOf, featureEnabled } from "./schema.js";
import { FN_LABELS } from "./render/format.js";
import {
    lookupValue,
    modeOf,
    pivotRowDims,
    sameColumn,
    sourceLayer,
    stageLayer,
    stageOf,
} from "./state.js";

export const stageLabelOf = (layer, name) => lookupValue(layer?.labels, name);

const countColumn = layer => ({
    name: "__count",
    label: stageLabelOf(layer, "__count") ?? "Count",
    type: "number",
    computed: false,
    metric: true,
});

/// The group stage's output columns, statically derived from its shape: dims
/// (source labels unless the layer overrides), __count, metrics by stable id,
/// and — unless excluded — the layer's computed columns. This is the dialog
/// universe for group-terminal mode and the compute/sort universe for pivot.
export function groupStageColumns(w, { includeComputed = true } = {}) {
    const stage = stageOf(w.doc, "group");
    if (!stage) return [];
    const shape = stage.shape ?? {};
    const layer = stage.layer ?? {};
    const columns = [];

    for (const dim of shape.by ?? []) {
        const base = columnOf(w, dim);
        columns.push({
            name: base?.name ?? dim,
            label: stageLabelOf(layer, dim) ?? base?.label ?? dim,
            type: base?.type ?? "other",
            computed: !!base?.computed,
            dim: true,
        });
    }
    columns.push(countColumn(layer));
    for (const value of shape.values ?? []) {
        const minMax = value.fn === "min" || value.fn === "max";
        columns.push({
            name: value.id,
            label: stageLabelOf(layer, value.id)
                ?? `${FN_LABELS[value.fn] ?? value.fn} of ${labelOf(w, value.col)}`,
            type: minMax ? typeOf(w, value.col) : "number",
            computed: false,
            metric: true,
            formatSource: value.fn === "count" || value.fn === "countDistinct" ? null : value.col,
        });
    }
    if (includeComputed) {
        for (const rule of layer.computed ?? []) {
            columns.push({
                name: rule.id,
                label: rule.label ?? rule.id,
                type: resultColumnType(w, rule.id) ?? "number",
                computed: true,
            });
        }
    }
    return columns;
}

/// A stage computed column's type is only known after execution; read it off the
/// last response when available.
function resultColumnType(w, name) {
    const requested = String(name).toLowerCase();
    return w.lastResult?.columns?.find(c => c.name.toLowerCase() === requested)?.type ?? null;
}

/// The spread output's columns as last reported: row dims plus stable cell
/// columns. Data-dependent by nature, so the last response is the universe.
function spreadColumns(w) {
    const spread = stageOf(w.doc, "spread");
    const layer = spread?.layer ?? {};
    return (w.lastResult?.columns ?? []).map(c => ({
        name: c.name,
        label: stageLabelOf(layer, c.name) ?? c.label,
        type: c.type,
        computed: !!c.computed,
        formatSource: c.formatSource ?? null,
    }));
}

/// The pivot's row dimensions with display labels (sortable universe).
function pivotRowColumns(w) {
    const spread = stageOf(w.doc, "spread");
    const layer = spread?.layer ?? {};
    return pivotRowDims(w.doc).map(dim => {
        const base = columnOf(w, dim);
        return {
            name: base?.name ?? dim,
            label: stageLabelOf(layer, dim) ?? base?.label ?? dim,
            type: base?.type ?? "other",
            computed: !!base?.computed,
            dim: true,
        };
    });
}

export function stageContext(w) {
    const mode = modeOf(w.doc);

    if (mode === "groupBy") {
        const columns = groupStageColumns(w);
        const groupLayer = d => stageLayer(stageOf(d, "group"));
        return {
            mode,
            columns,
            dims: (stageOf(w.doc, "group")?.shape?.by ?? []).slice(),
            columnsLayer: groupLayer,
            labelsLayer: groupLayer,
            formatsLayer: groupLayer,
            computeLayer: groupLayer,
            sortLayer: groupLayer,
            highlightLayer: groupLayer,
            computeTokens: groupStageColumns(w, { includeComputed: false }),
            // Dims are pass-through base columns, so definition sort restrictions
            // reach them; stage synthetics (__count, metrics, computed) never
            // match an override and stay sortable.
            sortColumns: columns.filter(c => columnSortable(w, c.name)),
            caps: caps(w, {
                columns: true, columnSettings: true, rename: true, compute: true,
                highlight: true, sort: true, visibility: true, displayAs: false,
                break: false, aggregate: false, pagination: true,
            }),
        };
    }

    if (mode === "pivot") {
        const groupLayer = d => stageLayer(stageOf(d, "group"));
        const spreadLayer = d => stageLayer(stageOf(d, "spread"));
        return {
            mode,
            columns: spreadColumns(w),
            dims: pivotRowDims(w.doc),
            labelsLayer: spreadLayer,
            formatsLayer: spreadLayer,
            computeLayer: groupLayer,
            sortLayer: groupLayer,
            computeTokens: groupStageColumns(w, { includeComputed: false }),
            sortColumns: pivotRowColumns(w).filter(c => columnSortable(w, c.name)),
            caps: caps(w, {
                columns: false, columnSettings: true, rename: true, compute: true,
                highlight: false, sort: true, visibility: false, displayAs: false,
                break: false, aggregate: false, pagination: false,
            }),
        };
    }

    if (mode === "chart") {
        return {
            mode,
            columns: [],
            caps: caps(w, {
                columns: false, columnSettings: false, rename: false, compute: false,
                highlight: false, sort: false, visibility: false, displayAs: false,
                break: false, aggregate: false, pagination: false,
            }),
        };
    }

    const source = d => sourceLayer(d);
    return {
        mode: "grid",
        columns: pickable(w),
        columnsLayer: source,
        labelsLayer: source,
        formatsLayer: source,
        computeLayer: source,
        sortLayer: source,
        highlightLayer: source,
        computeTokens: pickable(w).filter(c => !c.computed),
        sortColumns: sortableColumns(w),
        caps: caps(w, {
            columns: true, columnSettings: true, rename: true, compute: true,
            highlight: true, sort: true, visibility: true, displayAs: true,
            break: true, aggregate: true, pagination: true,
        }),
    };
}

/// The terminal table's currently visible, still-valid column names. An explicit
/// list can outlive a removed column or record a different casing in a saved
/// report, so every caller gets the same self-healing view — stale names dropped,
/// survivors resolved to the stage universe's canonical casing — before it edits
/// visibility.
export function visibleStageColumnNames(ctx, w) {
    const explicit = ctx.columnsLayer?.(w.doc)?.columns;
    if (explicit?.length) {
        return explicit
            .map(name => ctx.columns.find(column => sameColumn(column.name, name))?.name)
            .filter(name => name !== undefined);
    }
    return ctx.columns.map(column => column.name);
}

/// Mode capabilities intersected with the definition's feature whitelist. A menu
/// entry exists when the feature is whitelisted; it is enabled when the current
/// stage supports it.
function caps(w, byMode) {
    const gate = (feature, supported) => featureEnabled(w, feature) && supported;
    return {
        columns: gate("columns", byMode.columns),
        columnSettings: gate("columnSettings", byMode.columnSettings),
        rename: gate("rename", byMode.rename),
        compute: gate("compute", byMode.compute),
        highlight: gate("highlight", byMode.highlight),
        sort: gate("sort", byMode.sort),
        filter: featureEnabled(w, "filter"),      // always the source layer
        break: gate("controlBreak", byMode.break),
        aggregate: gate("aggregate", byMode.aggregate),
        pagination: gate("pagination", byMode.pagination),
        visibility: byMode.visibility,
        displayAs: byMode.displayAs,
    };
}
