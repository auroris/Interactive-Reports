// Active-table schema and capability context. Shapes are ordinary composable locations in a
// table ancestry; every terminal editor reads and writes exact nodes owned by the active table.

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

/**
 * Returns defensive copies of the supplied schema columns.
 *
 * @param {Array<object>|null|undefined} columns - Schema column descriptors to detach.
 * @returns {Array<object>} Shallow copies in source order, or an empty array.
 */
const copyColumns = columns => (columns ?? []).map(column => ({ ...column }));
/**
 * Returns the normalized kind of a composable operation.
 *
 * @param {object|null|undefined} composable - The composable whose kind token will be normalized.
 * @returns {string} The normalized composable kind.
 */
const kindOf = composable => String(composable?.kind ?? "").trim().toLowerCase();

/**
 * Replaces the source-name portion of a generated label while preserving its surrounding text.
 *
 * @param {string} label - The generated label to inspect.
 * @param {string} original - The original source label to replace.
 * @param {string} replacement - The display text that replaces the matched source label.
 * @returns {string} The label with its source portion replaced when present.
 */
const replaceLabel = (label, original, replacement) => {
    if (!original || !replacement) return label;
    const at = String(label ?? "").toLowerCase().indexOf(String(original).toLowerCase());
    return at < 0
        ? label
        : `${label.slice(0, at)}${replacement}${label.slice(at + String(original).length)}`;
};

// Protocol contract: neutral columns for one completed table, before the client layers label
// composables over them. Keeping this surface separate is important to editors: an effective
// heading is not the structural default against which an override should be cleared.
/**
 * Returns the schema columns produced by the requested table before label overrides.
 *
 * @param {object} w - The report controller containing document caches, the latest response, and base schema.
 * @param {string} [requested=w.doc?.activeTable] - The requested table identifier; when omitted, the active table is used.
 * @returns {Array<object>} The structural table columns.
 */
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

/**
 * Returns the schema column whose name matches the supplied identifier.
 *
 * @param {Array<object>} columns - The schema columns to search.
 * @param {string} name - The logical column name to compare case-insensitively.
 * @returns {object|undefined} The matching schema column, or `undefined` when absent.
 */
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
/**
 * Normalizes an aggregate-function name to its canonical spelling.
 *
 * @param {unknown} value - The authored aggregate token.
 * @returns {string} Canonical `countDistinct` casing for recognized functions, otherwise trimmed source text.
 */
const aggregateName = value => {
    const raw = String(value ?? "").trim();
    return aggregateNames.get(raw.toLowerCase()) ?? raw;
};
/**
 * Builds the generated label for an aggregate function and source column.
 *
 * @param {string} fn - The authored aggregate token.
 * @param {string} label - The source column label.
 * @returns {string} The aggregate label.
 */
const aggregateLabel = (fn, label) => `${aggregateName(fn)}(${label})`;

/**
 * Deletes every own entry from the supplied map object.
 *
 * @param {object} map - The mutable plain-object map to empty.
 * @returns {void} No value.
 *
 * Side effects: mutates the supplied map.
 */
const clearMap = map => {
    for (const key of Object.keys(map)) delete map[key];
};

/**
 * Optionally clears a label map, then merges its valid non-empty label entries.
 *
 * @param {object} target - The case-insensitive destination label map.
 * @param {object|null|undefined} values - Authored column-to-label entries; arrays are ignored.
 * @param {boolean} clear - Whether to delete existing entries before merging.
 * @returns {void} No value.
 *
 * Side effects: mutates the supplied label map.
 */
const mergeLabels = (target, values, clear) => {
    if (clear) clearMap(target);
    if (!values || typeof values !== "object" || Array.isArray(values)) return;
    for (const [name, label] of Object.entries(values)) {
        if (!String(name).trim() || typeof label !== "string" || !label.trim()) continue;
        setMapEntry(target, name, label.trim());
    }
};

/**
 * Returns a column name made unique against the supplied schema.
 *
 * @param {Array<object>} columns - Existing schema columns whose names are reserved.
 * @param {string} candidate - The proposed identifier or name to make unique.
 * @returns {string} The unique name.
 */
const uniqueName = (columns, candidate) => {
    const used = new Set((columns ?? []).map(column => String(column.name).toLowerCase()));
    while (used.has(candidate.toLowerCase())) candidate = `_${candidate}`;
    return candidate;
};

/**
 * Builds grouped-result columns and records labels for generated metrics.
 *
 * @param {object} shape - The group declaration containing dimensions and metric rules.
 * @param {Array<object>} input - The pre-group schema columns.
 * @param {object} labels - The mutable effective-label map to extend for generated metric ids.
 * @returns {Array<object>} The grouped result-schema columns.
 *
 * Side effects: mutates the supplied label map.
 */
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

/**
 * Builds chart-result columns and records the generated metric label.
 *
 * @param {object} shape - The chart declaration containing label, value, and optional aggregation.
 * @param {Array<object>} input - The pre-chart schema columns.
 * @param {object} labels - The mutable effective-label map to extend for the generated metric.
 * @returns {Array<object>} The chart result-schema columns.
 *
 * Side effects: mutates the supplied label map.
 */
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

/**
 * Replaces the structural source suffix in a generated pivot metric label.
 *
 * @param {string} label - The structural generated pivot label.
 * @param {string} fn - The metric aggregate token.
 * @param {string} structuralSource - The schema-derived source name for the pivot metric.
 * @param {string} displaySource - The display label currently shown for the pivot metric's source.
 * @returns {string} The pivot label with its display source updated.
 */
const replacePivotMetricSource = (label, fn, structuralSource, displaySource) => {
    const structural = ` · ${aggregateLabel(fn, structuralSource)}`;
    const source = String(label ?? "");
    return !source.toLowerCase().endsWith(structural.toLowerCase())
        ? source
        : `${source.slice(0, -structural.length)} · ${aggregateLabel(fn, displaySource)}`;
};

/**
 * Applies display labels to generated pivot metrics while preserving structural identities.
 *
 * @param {object} shape - The pivot declaration containing row dimensions and metric rules.
 * @param {Array<object>} input - The pre-pivot schema columns.
 * @param {Array<object>} output - The pivot output columns whose labels are being resolved.
 * @param {object} labels - The mutable effective-label map to extend for generated pivot cells.
 * @returns {Array<object>} The relabeled pivot result-schema columns.
 *
 * Side effects: mutates the supplied label map.
 */
const applyPivotLabels = (shape, input, output, labels) => {
    const metrics = (shape.values ?? []).flatMap(metric => {
        const source = columnFrom(input, metric?.col);
        return source ? [{ metric, source }] : [];
    });
    if (metrics.length < 2) return output.length
        ? output
        : (shape.rows ?? []).map(name => columnFrom(input, name)).filter(Boolean);

    for (const column of output) {
        const match = metrics.find(({ metric }) =>
            sameColumn(metric?.id, column.pivotMetricId));
        if (!match) continue;
        const display = lookupValue(labels, match.source.name) ?? match.source.label;
        const label = replacePivotMetricSource(
            column.label,
            match.metric.fn,
            match.source.label,
            display);
        setMapEntry(labels, column.name, label);
    }
    return output.length
        ? output
        : (shape.rows ?? []).map(name => columnFrom(input, name)).filter(Boolean);
};

/**
 * Adds missing computed columns to a defensive copy of the input schema.
 *
 * @param {object} composable - The compute declaration containing authored synthetic columns.
 * @param {Array<object>} input - The schema columns available before computation.
 * @returns {Array<object>} A copied schema containing any missing computed columns.
 */
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

// Protocol contract: fold labels in the server's natural semantic order for each selected
// table. Shape always precedes same-table Compute and Labels regardless of array position.
// Generated labels therefore see completed parent metadata, while a same-table label cannot
// leak backward and rewrite an already-built metric. Cached schemas close each named-table
// boundary. Within unfamiliar foreign compositions, the synthesized schema is deliberately
// best-effort.
/**
 * Folds inherited and owner-local label composables across the active table chain.
 *
 * @param {object} w - The report controller containing the active table chain, schema caches, and label composables.
 * @returns {object} A case-insensitive effective-label map after semantic-order folding.
 */
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
                case "pivot":
                    columns = applyPivotLabels(
                        composable,
                        columns,
                        index + 1 < shapes.length ? [] : completed,
                        labels);
                    break;
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

// Cache policy: complete active-table schema, independent of the current projection. Newer
// servers return it in the selected table's cache; response and definition columns remain
// compatibility fallbacks for an older server or first request.
/**
 * Returns the effective columns produced at the active table's terminal boundary.
 *
 * @param {object} w - The report controller containing active-table caches and effective labels.
 * @returns {Array<object>} The terminal table columns.
 */
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

/**
 * A packaged shape editor can replace its exact node independent of storage position. Natural ordering
 * guarantees that its input is the completed parent table, so siblings written before it in JSON do
 * not obscure that schema.
 *
 * @param {object|null|undefined} location - The resolved shape location.
 * @returns {boolean} Whether an existing shape node is available for exact replacement.
 */
export const shapeEditable = location => !!location;

// Cache policy: schema before a UI-authored shape, or the base schema selected for a new view.
// A shape is naturally first in its table, so its `from` table's cache is exact even when the
// shape is stored elsewhere in the composables array.
/**
 * Returns the effective input columns available to a report shape.
 *
 * @param {object} w - The report controller containing named-table schema caches and base schema.
 * @param {object|null} [location=null] - The existing shape's owning table location.
 * @param {string|null} [baseTableId=null] - The selected base table for a new shape.
 * @returns {Array<object>} The shape input columns.
 */
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

/**
 * Returns the dimension-column identifiers used by the supplied shape.
 *
 * @param {object} doc - The report state whose active owned shape will be inspected.
 * @returns {Array<string>} The shape dimensions.
 */
const shapeDimensions = doc => {
    const shape = activeShapeLocation(doc)?.composable;
    if (kindOf(shape) === "group") return [...(shape.by ?? [])];
    return kindOf(shape) === "pivot" ? [...(shape.rows ?? [])] : [];
};

/**
 * Builds the active table context used by report editors and renderers.
 *
 * @param {object} w - The report controller whose active table, columns, features, and editing helpers are exposed.
 * @returns {object} The active table context and its editing helpers.
 */
export function tableContext(w) {
    const columns = terminalTableColumns(w);
    const tableId = w.doc?.activeTable;
    return {
        mode: modeOf(w.doc),
        tableId,
        columns,
        dims: shapeDimensions(w.doc),
        /**
         * Resolves one terminal composable owned by this context's active table.
         * @param {object} doc - The report state to inspect.
         * @param {string} kind - The terminal composable kind.
         * @returns {object|null} The composable, or `null` when absent.
         */
        node(doc, kind) {
            return terminalComposableLocation(doc, kind, tableId)?.composable ?? null;
        },
        /**
         * Creates or edits one terminal composable owned by this context's active table.
         * @param {object} doc - The mutable report state clone.
         * @param {string} kind - The terminal composable kind.
         * @param {Function} mutate - The callback that updates the resolved composable.
         * @returns {object} The edited composable location returned by `editTerminalComposable`.
         * Side effects: mutates `doc`.
         */
        edit(doc, kind, mutate) {
            return editTerminalComposable(doc, kind, mutate, tableId);
        },
        // Protocol contract: the server dependency-orders computed columns. Existing local
        // outputs are therefore valid inputs to a new rule; an editor removes only its own id.
        computeTokens: columns,
        sortColumns: columns.filter(column => columnSortable(w, column.name)),
        filterColumns: columns.filter(column => columnFilterable(w, column.name)),
        caps: capabilities(w),
    };
}

/**
 * The active table's currently visible, still-valid column names. An explicit select can outlive a
 * removed column or use different casing, so callers get canonical surviving names before editing it.
 *
 * @param {object} ctx - The active table context containing canonical terminal columns and a select-node resolver.
 * @param {object} w - The report controller whose state contains the explicit selection.
 * @returns {Array<string>} The visible table column names.
 */
export function visibleTableColumnNames(ctx, w) {
    const explicit = ctx.node(w.doc, "select")?.columns;
    if (Array.isArray(explicit)) {
        return explicit
            .map(name => ctx.columns.find(column => sameColumn(column.name, name))?.name)
            .filter(name => name !== undefined);
    }
    return ctx.columns.map(column => column.name);
}

/**
 * Returns the active schema capability contract with safe empty defaults.
 *
 * @param {object} w - The report controller containing server suggestions and client overrides.
 * @returns {object} Boolean editor capabilities, with visibility and display renderers always enabled inside an available column-settings surface.
 */
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
