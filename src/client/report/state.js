// Pure report-state transformations over the composable table document. A
// document owns an unordered map of named tables. Each table explicitly names
// its input with `from` and folds its ordered composables over that input. Table
// names are opaque; the only reserved input is `definition`.

import { resolveLocale, translate } from "../core/localization.js";

export function normalizeReportState(raw, defaultPageSize = 50, defaults = null) {
    const state = defaults ? structuredClone(defaults) : {};
    for (const [key, value] of Object.entries(raw ? structuredClone(raw) : {}))
        if (value !== null && value !== undefined) state[key] = value;

    // The retired schema-snapshot key: server documents are authoritative and
    // the server validates on query, so the client neither checks nor carries it.
    delete state.schema;
    delete state.v;
    if (!state.tables || typeof state.tables !== "object" || Array.isArray(state.tables))
        state.tables = {};
    if (Object.keys(state.tables).length === 0) {
        state.tables.base = { from: "definition", composables: [] };
        state.activeTable = "base";
    } else if (!tableEntry(state, state.activeTable) && Object.keys(state.tables).length === 1) {
        state.activeTable = Object.keys(state.tables)[0];
    }
    for (const table of Object.values(state.tables))
        if (table && !Array.isArray(table.composables)) table.composables = [];
    state.page = { index: 1, size: state.page?.size ?? defaultPageSize };
    return state;
}

export function serializeReportState(source) {
    const walk = value => {
        if (Array.isArray(value)) return value.map(walk);
        if (value && typeof value === "object") {
            const result = {};
            for (const [key, child] of Object.entries(value)) {
                if (key.startsWith("_") || child === undefined) continue;
                result[key] = walk(child);
            }
            return result;
        }
        return value;
    };

    const result = walk(source);
    delete result.v;
    return result;
}

/// Null cached schemas for changed table definitions and every table delegating
/// from them. The server replaces null caches on the next document submission.
export function invalidateChangedSchemas(before, after) {
    const next = after?.tables ?? {};
    const changed = new Set();
    for (const [id, table] of Object.entries(next)) {
        const old = tableEntry(before, id)?.table;
        const oldDefinition = old
            ? JSON.stringify({ from: old.from, composables: old.composables ?? [] })
            : null;
        const nextDefinition = JSON.stringify({ from: table?.from, composables: table?.composables ?? [] });
        if (oldDefinition !== nextDefinition) changed.add(id.toLowerCase());
    }

    // Search can change a pivot's data-dependent columns. Paging and switching the
    // active table cannot, so those operations retain every cache.
    if ((before?.search ?? null) !== (after?.search ?? null))
        for (const id of Object.keys(next)) changed.add(id.toLowerCase());

    let grew = true;
    while (grew) {
        grew = false;
        for (const [id, table] of Object.entries(next)) {
            const key = id.toLowerCase();
            if (!changed.has(key) && changed.has(String(table?.from ?? "").toLowerCase())) {
                changed.add(key);
                grew = true;
            }
        }
    }

    for (const [id, table] of Object.entries(next))
        if (changed.has(id.toLowerCase())) table.schema = null;
    return after;
}

// --- table/composable access -------------------------------------------------

const shapeKinds = new Set(["group", "pivot", "chart"]);
const layerFields = {
    columns: ["select", "columns"],
    labels: ["labels", "labels"],
    formats: ["formats", "formats"],
    computed: ["compute", "computed"],
    filters: ["filter", "filters"],
    sorts: ["sort", "sorts"],
    highlights: ["highlight", "highlights"],
    breaks: ["break", "breaks"],
    aggregates: ["aggregate", "aggregates"],
};
const layerKindSet = new Set(Object.values(layerFields).map(([kind]) => kind));
const inheritedKindSet = new Set(["compute", "filter", "labels", "formats"]);

export const tableEntry = (doc, requested) => {
    if (!requested || !doc?.tables) return null;
    const wanted = String(requested).toLowerCase();
    const matches = Object.entries(doc.tables).filter(([id]) => id.toLowerCase() === wanted);
    return matches.length === 1 ? { id: matches[0][0], table: matches[0][1] } : null;
};

export const activeChain = (doc, requested = doc?.activeTable) => {
    const result = [];
    const seen = new Set();
    let entry = tableEntry(doc, requested);
    while (entry) {
        const key = entry.id.toLowerCase();
        if (seen.has(key)) return [];
        seen.add(key);
        result.unshift(entry);
        if (String(entry.table?.from ?? "").toLowerCase() === "definition") break;
        entry = tableEntry(doc, entry.table?.from);
    }
    return result.length && String(result[0].table?.from ?? "").toLowerCase() === "definition"
        ? result
        : [];
};

/// Every composable along the selected table's chain, retaining its exact owner and
/// array position. `participates` mirrors the server fold: parent tables contribute
/// relational rules and metadata, while their terminal presentation/control state
/// remains local to those tables. Consumers use these locations instead of
/// flattening repeated nodes into a synthetic layer. `authorable` is deliberately
/// narrower than `owned`: the packaged editors can safely write only the last node
/// of each kind in the active table's terminal segment. Earlier/repeated/foreign
/// nodes stay preserved and read-only.
export const locatedComposables = (doc, requested = doc?.activeTable) => {
    const chain = activeChain(doc, requested);
    if (!chain.length) return [];
    const activeId = chain.at(-1).id.toLowerCase();
    const rootId = chain[0].id.toLowerCase();
    let afterShape = false;
    const result = [];

    for (const entry of chain) {
        const composables = entry.table?.composables ?? [];
        const owned = entry.id.toLowerCase() === activeId;
        let lastShape = -1;
        if (owned) {
            for (let index = 0; index < composables.length; index++)
                if (shapeKinds.has(composables[index]?.kind)) lastShape = index;
        }

        for (let index = 0; index < composables.length; index++) {
            const composable = composables[index];
            const shape = shapeKinds.has(composable?.kind);
            const participates = shape
                || (layerKindSet.has(composable?.kind)
                    && (owned || inheritedKindSet.has(composable.kind)));
            const terminal = owned && !shape && index > lastShape;
            const laterSameKind = terminal && composables
                .slice(index + 1)
                .some(item => item?.kind === composable?.kind);
            result.push({
                tableId: entry.id,
                table: entry.table,
                composable,
                composableIndex: index,
                owned,
                inherited: !owned,
                participates,
                afterShape,
                source: entry.id.toLowerCase() === rootId && !afterShape,
                terminal,
                authorable: terminal && layerKindSet.has(composable?.kind) && !laterSameKind,
            });
            if (shape) afterShape = true;
        }
    }
    return result;
};

const composedMap = (doc, kind, field) => {
    const result = { input: undefined, output: undefined };
    let afterShape = false;
    for (const entry of activeChain(doc)) {
        for (const composable of entry.table?.composables ?? []) {
            if (shapeKinds.has(composable?.kind)) {
                afterShape = true;
                continue;
            }
            if (composable?.kind !== kind) continue;
            const values = composable[field];
            if (!values || typeof values !== "object" || Array.isArray(values)) continue;
            const side = afterShape ? "output" : "input";
            result[side] ??= {};
            if (Object.keys(values).length === 0) {
                result[side] = {};
                continue;
            }
            for (const [name, value] of Object.entries(values)) {
                if (!String(name).trim() || value === null || value === undefined) continue;
                if (kind === "labels") {
                    if (typeof value !== "string" || !value.trim()) continue;
                    setMapEntry(result[side], name, value.trim());
                    continue;
                }
                if (typeof value !== "object" || Array.isArray(value)) continue;
                setMapEntry(result[side], name, value);
            }
        }
    }
    return result;
};

/// Effective presentation metadata over the complete selected ancestry, split at
/// the optional shape boundary. Later entries on either side win case-insensitively
/// and an explicit empty map clears that side, matching the server's fold.
export const composedLabels = doc => composedMap(doc, "labels", "labels");
export const composedFormats = doc => composedMap(doc, "formats", "formats");

const rangeNodes = (table, start, end, kind) => {
    const composables = table?.composables ?? [];
    return composables.slice(start, end).filter(item => item?.kind === kind);
};

const layerAdapter = (table, start = 0, end = table?.composables?.length ?? 0) => {
    const layer = {};
    for (const [property, [kind, field]] of Object.entries(layerFields)) {
        Object.defineProperty(layer, property, {
            enumerable: true,
            get() {
                const nodes = rangeNodes(table, start, end, kind);
                if (!nodes.length) return undefined;
                // An editor always owns one exact composable. Earlier repeated
                // nodes may have been authored by another UI and remain part of
                // the composition, but are never flattened into a synthetic
                // value that a later assignment could accidentally write back.
                return nodes.at(-1)[field];
            },
            set(value) {
                let node = rangeNodes(table, start, end, kind).at(-1);
                if (!node) {
                    node = { kind };
                    table.composables ??= [];
                    table.composables.splice(end, 0, node);
                    end++;
                }
                node[field] = value;
            },
        });
    }
    return layer;
};

/// The active table's terminal ordinary-composable segment. If the active table
/// owns a shape, terminal operations begin immediately after its last shape. If
/// it inherits a shape from `from`, every composable it owns is terminal. Reads
/// and writes target the last exact node of each kind within that segment.
export function activeTableLayer(doc) {
    const entry = tableEntry(doc, doc?.activeTable);
    if (!entry) return {};
    const composables = entry.table?.composables ?? [];
    let lastShape = -1;
    for (let index = 0; index < composables.length; index++)
        if (shapeKinds.has(composables[index]?.kind)) lastShape = index;
    return layerAdapter(entry.table, lastShape + 1, composables.length);
}

/// Server-populated, non-authoritative schema cache for the active table. This
/// is the client column universe; query validation still happens on the server.
export function activeTableSchema(doc) {
    const schema = tableEntry(doc, doc?.activeTable)?.table?.schema;
    return Array.isArray(schema) ? schema : null;
}

const stageRecord = (entry, index) => {
    const shape = entry.table.composables[index];
    return {
        shape,
        layer: layerAdapter(entry.table, index + 1, entry.table.composables.length),
        _tableId: entry.id,
        _table: entry.table,
        _shapeIndex: index,
    };
};

const stagesFor = (doc, tableId = doc?.activeTable) => activeChain(doc, tableId).flatMap(entry =>
    (entry.table?.composables ?? []).flatMap((item, index) =>
        shapeKinds.has(item?.kind) ? [stageRecord(entry, index)] : []));

const sourceEntry = doc => {
    const active = activeChain(doc)[0];
    if (active) return active;
    const roots = Object.entries(doc?.tables ?? {})
        .filter(([, table]) => String(table?.from ?? "").toLowerCase() === "definition")
        .map(([id, table]) => ({ id, table }));
    return roots.length === 1 ? roots[0] : null;
};

export function sourceLayer(doc) {
    const entry = sourceEntry(doc);
    if (!entry) return {};
    const firstShape = (entry.table.composables ?? []).findIndex(item => shapeKinds.has(item?.kind));
    return layerAdapter(entry.table, 0, firstShape < 0 ? entry.table.composables.length : firstShape);
}

export function stageOf(doc, kind) {
    return stagesFor(doc).find(stage => stage.shape?.kind === kind) ?? null;
}

export function stageLayer(stage) {
    if (!stage) return {};
    return stage.layer ??= {};
}

export function tailOf(doc) {
    return stagesFor(doc);
}

/// The built-in UI mode is a predicate over the active composition. Documents
/// with several shape composables are preserved without assigning a lossy toolbar
/// mode; the server remains responsible for deciding whether they are executable.
export function modeOf(doc) {
    const kinds = stagesFor(doc).map(stage => stage.shape?.kind);
    if (kinds.length === 0) return "grid";
    if (kinds.length !== 1) return "custom";
    return kinds[0] === "group" ? "groupBy"
        : kinds[0] === "pivot" ? "pivot"
            : kinds[0] === "chart" ? "chart"
                : "custom";
}

const isUiCandidate = (doc, id, mode) => {
    if (modeOf({ ...doc, activeTable: id }) !== mode) return false;
    const entry = tableEntry(doc, id);
    if (!entry) return false;
    const own = entry.table.composables ?? [];
    if (mode === "grid")
        return String(entry.table.from ?? "").toLowerCase() === "definition"
            && own.every(item => layerKindSet.has(item?.kind));
    const expected = mode === "groupBy" ? "group" : mode;
    return own[0]?.kind === expected
        && own.slice(1).every(item => layerKindSet.has(item?.kind));
};

const candidatesForMode = (doc, mode) => Object.keys(doc?.tables ?? {})
    .filter(id => isUiCandidate(doc, id, mode));

const uniqueCandidate = (doc, mode) => {
    if (isUiCandidate(doc, doc?.activeTable, mode)) return tableEntry(doc, doc.activeTable);
    const ids = candidatesForMode(doc, mode);
    return ids.length === 1 ? tableEntry(doc, ids[0]) : null;
};

const plainLayer = layer => {
    const result = {};
    for (const property of Object.keys(layerFields)) {
        const value = layer?.[property];
        if (value !== undefined) result[property] = structuredClone(value);
    }
    return result;
};

const plainStage = stage => ({
    shape: structuredClone(stage.shape),
    layer: plainLayer(stage.layer),
    // Working-copy coordinates let a shape editor replace only the exact shape
    // node it opened. serializeReportState strips them before submission.
    _tableId: stage._tableId,
    _shapeIndex: stage._shapeIndex,
});

/// Return the uniquely identifiable table configured for a built-in mode in
/// the legacy stage-shaped editor form. Map order is never used to break ties.
export function configuredTail(doc, mode) {
    const entry = uniqueCandidate(doc, mode);
    if (!entry || mode === "grid") return null;
    const stages = stagesFor(doc, entry.id);
    return stages.length === 1 ? stages.map(plainStage) : null;
}

const composablesFromLayer = layer => Object.entries(layerFields).flatMap(([property, [kind, field]]) => {
    const value = layer?.[property];
    return value === undefined ? [] : [{ kind, [field]: structuredClone(value) }];
});

const composablesFromTail = tail => (tail ?? []).flatMap(stage => [
    structuredClone(stage.shape ?? {}),
    ...composablesFromLayer(stage.layer ?? {}),
]);

const nextTableId = (doc, prefix) => {
    const used = new Set(Object.keys(doc.tables ?? {}).map(id => id.toLowerCase()));
    if (!used.has(prefix.toLowerCase())) return prefix;
    let suffix = 2;
    while (used.has(`${prefix}${suffix}`.toLowerCase())) suffix++;
    return `${prefix}${suffix}`;
};

/// Activate or author a built-in UI table. Existing tables stay in the map;
/// switching views changes only activeTable. Editing an existing shape replaces
/// that exact composable and preserves every ordinary or unfamiliar sibling.
export function activateTail(doc, mode, tail = null) {
    doc.tables ??= {};
    if (mode === "grid") {
        const grid = uniqueCandidate(doc, "grid") ?? sourceEntry(doc);
        if (grid) doc.activeTable = grid.id;
        return doc;
    }

    let entry = uniqueCandidate(doc, mode);
    if (tail) {
        const source = uniqueCandidate(doc, "grid") ?? sourceEntry(doc);
        if (!source) return doc;
        if (!entry) {
            const id = nextTableId(doc, mode);
            doc.tables[id] = { from: source.id, composables: [] };
            entry = { id, table: doc.tables[id] };
            entry.table.composables = composablesFromTail(tail);
        } else {
            const edited = tail.length === 1 ? tail[0] : null;
            const index = Number.isInteger(edited?._shapeIndex)
                && sameColumn(edited?._tableId, entry.id)
                ? edited._shapeIndex
                : (entry.table.composables ?? []).findIndex(item => item?.kind === edited?.shape?.kind);
            if (index >= 0 && shapeKinds.has(entry.table.composables[index]?.kind))
                entry.table.composables[index] = structuredClone(edited.shape);
        }
        entry.table.from = entry.table.from ?? source.id;
    }
    if (entry) doc.activeTable = entry.id;
    return doc;
}

/// The row dimensions of an active pivot.
export function pivotRowDims(doc) {
    return stageOf(doc, "pivot")?.shape?.rows ?? [];
}

// --- shared helpers ----------------------------------------------------------

export const sameColumn = (left, right) => typeof left === "string" && typeof right === "string"
    && left.toLowerCase() === right.toLowerCase();

export function lookupValue(map, name) {
    if (!map) return undefined;
    const requested = String(name).toLowerCase();
    const key = Object.keys(map).find(candidate => candidate.toLowerCase() === requested);
    return key === undefined ? undefined : map[key];
}

/// Write or clear a map entry by case-insensitive key. Every case-variant of
/// name is removed first, so lookupValue can never resolve a stale duplicate
/// left under different casing. Pass undefined to clear the entry.
export function setMapEntry(map, name, value) {
    for (const key of Object.keys(map))
        if (sameColumn(key, name)) delete map[key];
    if (value !== undefined) map[name] = value;
}

export function nextFreeId(usedLowercase, prefix) {
    let next = 1;
    while (usedLowercase.has(`${prefix}${next}`)) next++;
    return `${prefix}${next}`;
}

const mapDeleteWhere = (map, predicate) => {
    if (!map) return;
    for (const key of Object.keys(map))
        if (predicate(key)) delete map[key];
};

/// True when an expression contains the column as an identifier, excluding quoted
/// string contents and longer identifiers (c1 does not match c10 or 'c1').
export function expressionReferencesColumn(expression, column, { pivotFamily = false } = {}) {
    const source = String(expression ?? "");
    const target = String(column ?? "").toLowerCase();
    const matches = name => {
        const candidate = name.toLowerCase();
        return candidate === target || (pivotFamily && candidate.startsWith(`${target}@`));
    };
    let quoted = false;
    for (let index = 0; index < source.length;) {
        if (source[index] === "'") {
            if (quoted && source[index + 1] === "'") { index += 2; continue; }
            quoted = !quoted;
            index++;
            continue;
        }
        if (!quoted && source[index] === "`") {
            index++;
            let identifier = "";
            while (index < source.length) {
                if (source[index] === "`" && source[index + 1] === "`") {
                    identifier += "`";
                    index += 2;
                    continue;
                }
                if (source[index] === "`") { index++; break; }
                identifier += source[index++];
            }
            if (matches(identifier)) return true;
            continue;
        }
        if (!quoted && /[A-Za-z_]/.test(source[index])) {
            let end = index + 1;
            while (end < source.length && /[A-Za-z0-9_]/.test(source[end])) end++;
            if (matches(source.slice(index, end))) return true;
            index = end;
            continue;
        }
        index++;
    }
    return false;
}

// --- coarse dependency invalidation (T0) -------------------------------------

const tailReferencesColumn = (stages, column) => stages.some(stage => {
    const shape = stage.shape ?? {};
    return (shape.by ?? []).some(name => sameColumn(name, column))
        || (shape.rows ?? []).some(name => sameColumn(name, column))
        || (shape.cols ?? []).some(name => sameColumn(name, column))
        || (shape.values ?? []).some(value => sameColumn(value?.col, column))
        || sameColumn(shape.label, column)
        || sameColumn(shape.value, column);
});

/// Delete one source-table computed column and everything that depends on it.
/// Within the source table, references are stripped precisely. A descendant
/// table whose shape consumes the column is removed with its descendants (T0
/// coarse invalidation). Unrelated roots in an externally-authored document are
/// untouched. Returns the built-in modes that were dropped so callers can say so.
export function removeSourceComputedColumn(state, column) {
    const source = sourceEntry(state);
    const layer = sourceLayer(state);
    const withoutName = values => Array.isArray(values)
        ? values.filter(value => !sameColumn(value, column))
        : values;
    const withoutColumnRule = values => Array.isArray(values)
        ? values.filter(value => !sameColumn(value?.col, column))
        : values;

    layer.computed = (layer.computed ?? []).filter(rule => !sameColumn(rule.id, column));
    layer.columns = withoutName(layer.columns);
    layer.sorts = withoutColumnRule(layer.sorts ?? []);
    layer.breaks = withoutName(layer.breaks);
    layer.aggregates = withoutColumnRule(layer.aggregates);
    layer.filters = (layer.filters ?? []).filter(rule => !expressionReferencesColumn(rule.expr, column));
    layer.highlights = (layer.highlights ?? []).filter(rule =>
        !sameColumn(rule.col, column) && !expressionReferencesColumn(rule.expr, column));
    mapDeleteWhere(layer.labels, name => sameColumn(name, column));
    if (layer.formats) {
        for (const [name, format] of Object.entries(layer.formats)) {
            if (sameColumn(name, column)) {
                delete layer.formats[name];
                continue;
            }
            if (sameColumn(format?.urlColumn, column) || sameColumn(format?.textColumn, column)) {
                delete format.displayAs;
                delete format.urlColumn;
                delete format.textColumn;
            }
        }
    }

    if (!source) return [];

    const descendants = new Set([source.id.toLowerCase()]);
    let changed = true;
    while (changed) {
        changed = false;
        for (const [id, table] of Object.entries(state.tables ?? {})) {
            const key = id.toLowerCase();
            if (descendants.has(key)) continue;
            if (descendants.has(String(table?.from ?? "").toLowerCase())) {
                descendants.add(key);
                changed = true;
            }
        }
    }

    const remove = new Set();
    for (const [id] of Object.entries(state.tables ?? {})) {
        if (id.toLowerCase() === source.id.toLowerCase() || !descendants.has(id.toLowerCase())) continue;
        if (tailReferencesColumn(stagesFor(state, id), column)) remove.add(id.toLowerCase());
    }
    changed = true;
    while (changed) {
        changed = false;
        for (const [id, table] of Object.entries(state.tables ?? {})) {
            if (!remove.has(id.toLowerCase()) && remove.has(String(table?.from ?? "").toLowerCase())) {
                remove.add(id.toLowerCase());
                changed = true;
            }
        }
    }

    const dropped = [];
    for (const [id] of Object.entries(state.tables ?? {})) {
        if (!remove.has(id.toLowerCase())) continue;
        const mode = modeOf({ ...state, activeTable: id });
        if (mode !== "custom" && mode !== "grid" && !dropped.includes(mode)) dropped.push(mode);
        delete state.tables[id];
    }
    if (remove.has(String(state.activeTable ?? "").toLowerCase())) state.activeTable = source.id;
    return dropped;
}

/// Delete one derived-layer computed column and its references within that stage:
/// filters, sorts, highlights, column selection, and presentation maps.
export function removeStageComputedColumn(state, stage, column) {
    const layer = stageLayer(stage);
    const pivotFamily = stage?.shape?.kind === "pivot";
    const matches = name => sameColumn(name, column)
        || (pivotFamily && typeof name === "string"
            && name.toLowerCase().startsWith(`${String(column).toLowerCase()}@`));
    const expressionMatches = expression => expressionReferencesColumn(
        expression,
        column,
        { pivotFamily });
    const removedComputed = (layer.computed ?? []).filter(rule =>
        sameColumn(rule.id, column) || expressionMatches(rule.expr));
    layer.computed = (layer.computed ?? []).filter(rule => !removedComputed.includes(rule));
    layer.filters = (layer.filters ?? []).filter(rule =>
        !expressionMatches(rule.expr));
    layer.sorts = (layer.sorts ?? []).filter(rule => !matches(rule.col));
    layer.breaks = (layer.breaks ?? []).filter(name => !matches(name));
    layer.aggregates = (layer.aggregates ?? []).filter(rule => !matches(rule.col));
    layer.highlights = (layer.highlights ?? []).filter(rule =>
        !matches(rule.col) && !expressionMatches(rule.expr));
    if (Array.isArray(layer.columns))
        layer.columns = layer.columns.filter(name => !matches(name));
    mapDeleteWhere(layer.labels, matches);
    mapDeleteWhere(layer.formats, matches);

    // A computed definition removed because it consumed the retired column has
    // an identity of its own. Strip that identity from the rest of the layer too.
    for (const rule of removedComputed)
        if (!sameColumn(rule.id, column)) removeStageComputedColumn(state, stage, rule.id);

    return state;
}

/// After a Group By / Pivot dialog edit retires metric ids, drop the stage-layer
/// state that referenced them (the same coarse rule as computed-column removal).
export function pruneRetiredMetrics(state, stage, retiredIds) {
    for (const id of retiredIds) removeStageComputedColumn(state, stage, id);
    return state;
}

// --- scoped search -----------------------------------------------------------

export function scopedSearchExpression(column, type, rawValue, context = null) {
    const value = rawValue.trim();
    if (!value) throw new Error(translate(context, "search.enterValue"));
    const reference = expressionIdentifier(column);

    switch (type) {
        case "text":
            return `CONTAINS(${reference}, ${quote(value)})`;
        case "number": {
            const locale = resolveLocale(context);
            const normalized = locale === "fr-CA"
                ? value.replace(/[\s\u00a0\u202f]/g, "").replace(",", ".")
                : /^(?:[+-])?(?:\d{1,3}(?:,\d{3})+)(?:\.\d+)?$/.test(value)
                    ? value.replaceAll(",", "")
                    : value;
            if (!/^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$/.test(normalized))
                throw new Error(translate(context, "search.notNumber", { value }));
            return `${reference} = ${normalized}`;
        }
        case "date":
            if (!/^\d{4}-\d{2}-\d{2}$/.test(value))
                throw new Error(translate(context, "search.notDate", { value }));
            return `${reference} = TO_DATE(${quote(value)})`;
        case "bool": {
            const normalized = value.toLowerCase();
            const french = resolveLocale(context) === "fr-CA";
            if (normalized === "true" || normalized === "1" || (french && normalized === "vrai")) return reference;
            if (normalized === "false" || normalized === "0" || (french && normalized === "faux")) return `NOT ${reference}`;
            throw new Error(translate(context, "search.notBoolean", { value }));
        }
        default:
            throw new Error(translate(context, "search.unsupportedColumn", { column }));
    }
}

function quote(value) {
    return `'${value.replaceAll("'", "''")}'`;
}

function expressionIdentifier(name) {
    const ordinary = /^[A-Za-z_][A-Za-z0-9_$#]*$/.test(name);
    const keyword = /^(CASE|WHEN|THEN|ELSE|END|AND|OR|NOT|IS|NULL|BETWEEN)$/i.test(name);
    return ordinary && !keyword ? name : `\`${name.replaceAll("`", "``")}\``;
}
