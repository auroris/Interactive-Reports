// Generic active-table context. Shapes are ordinary composable locations in a
// table ancestry; every terminal editor reads and writes exact composable nodes
// owned by the active table.

import {
    columnFilterable,
    columnSortable,
    featureEnabled,
} from "./schema.js";
import {
    activeChain,
    activeShapeLocation,
    activeTableSchema,
    editTerminalComposable,
    lookupValue,
    modeOf,
    sameColumn,
    setMapEntry,
    tableEntry,
    terminalComposableLocation,
} from "./state.js";

const copyColumns = columns => (columns ?? []).map(column => ({ ...column }));
const kindOf = composable => String(composable?.kind ?? "").trim().toLowerCase();

const replaceLabel = (label, original, replacement) => {
    if (!original || !replacement) return label;
    const at = String(label ?? "").toLowerCase().indexOf(String(original).toLowerCase());
    return at < 0
        ? label
        : `${label.slice(0, at)}${replacement}${label.slice(at + String(original).length)}`;
};

/// Neutral columns for one completed table, before the client layers label
/// composables over them. Keeping this surface separate is important to editors:
/// an effective heading is not the structural default against which an override
/// should be cleared.
export function structuralTableColumns(w, requested = w.doc?.activeTable) {
    const entry = tableEntry(w.doc, requested);
    if (Array.isArray(entry?.table?.schema)) return copyColumns(entry.table.schema);
    if (entry && sameColumn(entry.id, w.doc?.activeTable)) {
        const response = w.lastResult?.availableColumns ?? w.lastResult?.columns;
        if (Array.isArray(response)) return copyColumns(response);
    }
    if (String(entry?.table?.from ?? "").trim().toLowerCase() === "definition")
        return copyColumns(w.schema?.columns);
    return [];
}

const columnFrom = (columns, name) => (columns ?? [])
    .find(column => sameColumn(column.name, name));

const aggregateNames = new Map([
    ["sum", "sum"],
    ["avg", "avg"],
    ["median", "median"],
    ["min", "min"],
    ["max", "max"],
    ["count", "count"],
    ["countdistinct", "countDistinct"],
]);
const aggregateName = value => {
    const raw = String(value ?? "").trim();
    return aggregateNames.get(raw.toLowerCase()) ?? raw;
};
const aggregateLabel = (fn, label) => `${aggregateName(fn)}(${label})`;

const clearMap = map => {
    for (const key of Object.keys(map)) delete map[key];
};

const mergeLabels = (target, values, clear) => {
    if (clear) clearMap(target);
    if (!values || typeof values !== "object" || Array.isArray(values)) return;
    for (const [name, label] of Object.entries(values)) {
        if (!String(name).trim() || typeof label !== "string" || !label.trim()) continue;
        setMapEntry(target, name, label.trim());
    }
};

const uniqueName = (columns, candidate) => {
    const used = new Set((columns ?? []).map(column => String(column.name).toLowerCase()));
    while (used.has(candidate.toLowerCase())) candidate = `_${candidate}`;
    return candidate;
};

const applyGroupLabels = (shape, input, labels) => {
    const dimensions = (shape.by ?? []).map(name => columnFrom(input, name)).filter(Boolean);
    const metrics = [];
    for (const metric of shape.values ?? []) {
        const source = columnFrom(input, metric?.col);
        const id = String(metric?.id ?? "").trim();
        if (!source || !id) continue;
        const display = lookupValue(labels, source.name) ?? source.label;
        setMapEntry(labels, id, aggregateLabel(metric.fn, display));
        metrics.push({
            name: id,
            label: aggregateLabel(metric.fn, source.label),
            type: "number",
        });
    }
    const countName = uniqueName([...dimensions, ...metrics], "__count");
    return [
        ...dimensions,
        { name: countName, label: "Count", type: "number" },
        ...metrics,
    ];
};

const applyChartLabels = (shape, input, labels) => {
    const label = columnFrom(input, shape.label);
    const value = columnFrom(input, shape.value);
    if (!label) return input;

    let metric;
    if (!value) {
        metric = { name: "__count", label: "Count", type: "number" };
    } else if (shape.fn === null || shape.fn === undefined || String(shape.fn).trim() === "") {
        metric = { ...value };
    } else {
        metric = {
            name: "v0",
            label: aggregateLabel(shape.fn, value.label),
            type: "number",
        };
    }
    if (sameColumn(label.name, metric.name)) metric.name = `${metric.name}_metric`;

    const display = value ? lookupValue(labels, value.name) ?? value.label : "Count";
    setMapEntry(labels, metric.name, value && shape.fn !== null && shape.fn !== undefined
        ? aggregateLabel(shape.fn, display)
        : display);
    return [label, metric];
};

const replaceAggregateSource = (label, fn, structuralSource, displaySource) => {
    const structural = aggregateLabel(fn, structuralSource);
    const source = String(label ?? "");
    const at = source.toLowerCase().lastIndexOf(structural.toLowerCase());
    return at < 0
        ? source
        : `${source.slice(0, at)}${aggregateLabel(fn, displaySource)}${source.slice(at + structural.length)}`;
};

const applyPivotLabels = (shape, input, output, labels) => {
    const metrics = (shape.values ?? []).flatMap(metric => {
        const source = columnFrom(input, metric?.col);
        const id = String(metric?.id ?? "").trim();
        return source && id ? [{ metric, source, id }] : [];
    });
    for (const column of output) {
        const match = metrics.find(({ id }) =>
            String(column.name).toLowerCase().startsWith(`${id.toLowerCase()}@`));
        if (!match) continue;
        const display = lookupValue(labels, match.source.name) ?? match.source.label;
        const label = metrics.length === 1
            ? column.label
            : replaceAggregateSource(column.label, match.metric.fn, match.source.label, display);
        setMapEntry(labels, column.name, label);
    }
    return output.length
        ? output
        : (shape.rows ?? []).map(name => columnFrom(input, name)).filter(Boolean);
};

const applyComputedSchema = (composable, input) => {
    const result = copyColumns(input);
    for (const rule of composable.computed ?? []) {
        const name = String(rule?.id ?? "").trim();
        if (!name || columnFrom(result, name)) continue;
        result.push({
            name,
            label: typeof rule.label === "string" && rule.label.trim() ? rule.label.trim() : name,
            type: "other",
            computed: true,
        });
    }
    return result;
};

/// Fold labels in the server's natural semantic order for each selected table.
/// Shape always precedes same-table Compute and Labels regardless of array
/// position. Generated labels therefore see completed parent metadata, while a
/// same-table label cannot leak backward and rewrite an already-built metric.
/// Cached schemas close each named-table boundary. Within unfamiliar foreign
/// compositions, the synthesized schema is deliberately best-effort.
function foldedLabels(w) {
    const labels = {};
    let columns = copyColumns(w.schema?.columns);
    for (const entry of activeChain(w.doc)) {
        const composables = entry.table?.composables ?? [];
        const completed = structuralTableColumns(w, entry.id);
        const shapes = composables.filter(composable =>
            ["group", "pivot", "chart"].includes(kindOf(composable)));

        for (let index = 0; index < shapes.length; index++) {
            const composable = shapes[index];
            const kind = kindOf(composable);
            switch (kind) {
                case "group":
                    columns = applyGroupLabels(composable, columns, labels);
                    break;
                case "chart":
                    columns = applyChartLabels(composable, columns, labels);
                    break;
                case "pivot": {
                    columns = applyPivotLabels(
                        composable,
                        columns,
                        index + 1 < shapes.length ? [] : completed,
                        labels);
                    break;
                }
            }
        }

        for (const composable of composables)
            if (kindOf(composable) === "compute")
                columns = applyComputedSchema(composable, columns);

        const labelMaps = composables.flatMap(composable =>
            kindOf(composable) === "labels"
                && composable.labels
                && typeof composable.labels === "object"
                && !Array.isArray(composable.labels)
                ? [composable.labels]
                : []);
        if (labelMaps.some(values => Object.keys(values).length === 0))
            clearMap(labels);
        for (const values of labelMaps) {
            if (Object.keys(values).length === 0) continue;
            mergeLabels(labels, values, false);
        }
        if (completed.length) columns = completed;
    }
    return labels;
}

/// Complete active-table schema, independent of the current projection. Newer
/// servers return it in the selected table's cache; response and definition
/// columns remain compatibility fallbacks for an older server or first request.
export function terminalTableColumns(w) {
    const columns = structuralTableColumns(w);
    if (columns.length) {
        const labels = foldedLabels(w);
        return columns.map(column => {
            const label = lookupValue(labels, column.name);
            return label ? { ...column, label } : column;
        });
    }
    return copyColumns(activeTableSchema(w.doc)
        ?? w.lastResult?.availableColumns
        ?? w.lastResult?.columns
        ?? w.schema?.columns);
}

/// A packaged shape editor can replace its exact node independent of storage
/// position. Natural ordering guarantees that its input is the completed parent
/// table, so siblings written before it in JSON do not obscure that schema.
export const shapeEditable = location => !!location;

/// Schema before a UI-authored shape, or the base schema selected for a new view.
/// A shape is naturally first in its table, so its `from` table's cache is exact
/// even when the shape is stored elsewhere in the composables array.
export function shapeInputColumns(w, location = null, baseTableId = null) {
    const owner = tableEntry(w.doc, location?.tableId);
    const inputId = owner?.table?.from ?? baseTableId;
    const input = tableEntry(w.doc, inputId);
    if (Array.isArray(input?.table?.schema)) return copyColumns(input.table.schema);
    if (String(inputId ?? "").trim().toLowerCase() === "definition")
        return copyColumns(w.schema?.columns);
    if (input && String(input.table?.from ?? "").trim().toLowerCase() === "definition")
        return copyColumns(input.table.schema ?? w.schema?.columns);
    return [];
}

const shapeDimensions = doc => {
    const shape = activeShapeLocation(doc)?.composable;
    if (kindOf(shape) === "group") return [...(shape.by ?? [])];
    return kindOf(shape) === "pivot" ? [...(shape.rows ?? [])] : [];
};

export function tableContext(w) {
    const columns = terminalTableColumns(w);
    const tableId = w.doc?.activeTable;
    return {
        mode: modeOf(w.doc),
        tableId,
        columns,
        dims: shapeDimensions(w.doc),
        node(doc, kind) {
            return terminalComposableLocation(doc, kind, tableId)?.composable ?? null;
        },
        edit(doc, kind, mutate) {
            return editTerminalComposable(doc, kind, mutate, tableId);
        },
        // The server dependency-orders computed columns. Existing local outputs are
        // therefore valid inputs to a new rule; an editor removes only its own id.
        computeTokens: columns,
        sortColumns: columns.filter(column => columnSortable(w, column.name)),
        filterColumns: columns.filter(column => columnFilterable(w, column.name)),
        caps: capabilities(w),
    };
}

/// The active table's currently visible, still-valid column names. An explicit
/// select can outlive a removed column or use different casing, so callers get
/// canonical surviving names before editing it.
export function visibleTableColumnNames(ctx, w) {
    const explicit = ctx.node(w.doc, "select")?.columns;
    if (Array.isArray(explicit)) {
        return explicit
            .map(name => ctx.columns.find(column => sameColumn(column.name, name))?.name)
            .filter(name => name !== undefined);
    }
    return ctx.columns.map(column => column.name);
}

function capabilities(w) {
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
