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
    const retainedIds = new Set(Object.keys(next).map(id => id.toLowerCase()));
    for (const id of Object.keys(before?.tables ?? {}))
        if (!retainedIds.has(id.toLowerCase())) changed.add(id.toLowerCase());
    for (const [id, table] of Object.entries(next)) {
        const old = tableEntry(before, id)?.table;
        const oldDefinition = old
            ? JSON.stringify({ from: old.from, composables: old.composables ?? [] })
            : null;
        const nextDefinition = JSON.stringify({ from: table?.from, composables: table?.composables ?? [] });
        if (oldDefinition !== nextDefinition) changed.add(id.toLowerCase());
    }

    // Search can change a Pivot's data-dependent columns, but Group, Chart, and
    // ordinary table schemas are structural. Seed only Pivot owners here; the
    // descendant walk below carries invalidation through their completed outputs.
    // Paging and switching the active table never affect a schema cache.
    if ((before?.search ?? null) !== (after?.search ?? null))
        for (const [id, table] of Object.entries(next))
            if ((table?.composables ?? []).some(item => kindOf(item) === "pivot"))
                changed.add(id.toLowerCase());

    let grew = true;
    while (grew) {
        grew = false;
        for (const [id, table] of Object.entries(next)) {
            const key = id.toLowerCase();
            if (!changed.has(key) && changed.has(token(table?.from))) {
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
const ordinaryKindSet = new Set([
    "select", "labels", "formats", "compute", "filter", "sort", "highlight",
    "break", "aggregate",
]);
const inheritedKindSet = new Set(["compute", "filter", "labels", "formats"]);
const token = value => String(value ?? "").trim().toLowerCase();
const kindOf = composable => token(composable?.kind);
const isKind = (composable, kind) => kindOf(composable) === token(kind);
const modeKind = mode => mode === "groupBy" ? "group" : mode;
const kindMode = kind => token(kind) === "group" ? "groupBy" : token(kind);

export const tableEntry = (doc, requested) => {
    if (!requested || !doc?.tables) return null;
    const wanted = token(requested);
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
        if (token(entry.table?.from) === "definition") break;
        entry = tableEntry(doc, entry.table?.from);
    }
    return result.length && token(result[0].table?.from) === "definition"
        ? result
        : [];
};

/// Every composable along the selected table's chain, retaining its exact owner and
/// array position. `participates` mirrors the server fold: parent tables contribute
/// relational rules and metadata, while their terminal presentation/control state
/// remains local to those tables. Consumers use these locations instead of
/// flattening repeated nodes into a synthetic settings object. `authorable` is deliberately
/// narrower than `owned`: the packaged editors can safely write only the last node
/// of each kind in the active table's terminal segment. Earlier/repeated/foreign
/// nodes stay preserved and read-only.
export const composableLocations = (doc, requested = doc?.activeTable) => {
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
                if (shapeKinds.has(kindOf(composables[index]))) lastShape = index;
        }

        for (let index = 0; index < composables.length; index++) {
            const composable = composables[index];
            const kind = kindOf(composable);
            const shape = shapeKinds.has(kind);
            const participates = shape
                || (ordinaryKindSet.has(kind)
                    && (owned || inheritedKindSet.has(kind)));
            const terminal = owned && !shape && index > lastShape;
            const laterSameKind = terminal && composables
                .slice(index + 1)
                .some(item => kindOf(item) === kind);
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
                authorable: terminal && ordinaryKindSet.has(kind) && !laterSameKind,
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
            if (shapeKinds.has(kindOf(composable))) {
                afterShape = true;
                continue;
            }
            if (!isKind(composable, kind)) continue;
            const values = composable[field];
            if (!values || typeof values !== "object" || Array.isArray(values)) continue;
            const side = afterShape ? "output" : "input";
            result[side] ??= {};
            if (Object.keys(values).length === 0) {
                // An empty presentation map is a reset at this point in the
                // composition, not merely an empty override for the current side.
                // Once a shape exists, source metadata would otherwise leak back
                // through a generated column's formatSource provenance.
                if (afterShape) result.input = {};
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
/// and an explicit empty map clears everything accumulated before that point,
/// matching the server's fold.
export const composedLabels = doc => composedMap(doc, "labels", "labels");
export const composedFormats = doc => composedMap(doc, "formats", "formats");

const ownRange = (doc, requested, { terminal = false, input = false } = {}) => {
    const entry = tableEntry(doc, requested);
    if (!entry) return null;
    const composables = entry.table?.composables ?? [];
    let firstShape = composables.findIndex(item => shapeKinds.has(kindOf(item)));
    if (firstShape < 0) firstShape = composables.length;
    let lastShape = -1;
    for (let index = 0; index < composables.length; index++)
        if (shapeKinds.has(kindOf(composables[index]))) lastShape = index;
    return {
        entry,
        start: terminal ? lastShape + 1 : 0,
        end: input ? firstShape : composables.length,
    };
};

const locationInRange = (range, kind) => {
    if (!range) return null;
    const composables = range.entry.table?.composables ?? [];
    for (let index = range.end - 1; index >= range.start; index--) {
        if (isKind(composables[index], kind))
            return {
                tableId: range.entry.id,
                table: range.entry.table,
                composable: composables[index],
                composableIndex: index,
            };
    }
    return null;
};

const locationsInRange = (range, kind) => {
    if (!range) return [];
    const composables = range.entry.table?.composables ?? [];
    const result = [];
    for (let index = range.start; index < range.end; index++) {
        if (!isKind(composables[index], kind)) continue;
        result.push({
            tableId: range.entry.id,
            table: range.entry.table,
            composable: composables[index],
            composableIndex: index,
        });
    }
    return result;
};

const editInRange = (range, kind, mutate) => {
    if (!range) throw new Error("The target table is no longer available.");
    const canonicalKind = token(kind);
    let location = locationInRange(range, canonicalKind);
    if (!location) {
        const composable = { kind: canonicalKind };
        range.entry.table.composables ??= [];
        range.entry.table.composables.splice(range.end, 0, composable);
        location = {
            tableId: range.entry.id,
            table: range.entry.table,
            composable,
            composableIndex: range.end,
        };
    } else location.composable.kind = canonicalKind;
    mutate(location.composable, location);
    return location;
};

/// The last exact composable of `kind` in a table's terminal segment. A table
/// that owns a shape starts its terminal segment after the final owned shape; a
/// table that inherits a shape has its complete own composition as the segment.
export function terminalComposableLocation(doc, kind, requested = doc?.activeTable) {
    return locationInRange(ownRange(doc, requested, { terminal: true }), kind);
}

export function editTerminalComposable(doc, kind, mutate, requested = doc?.activeTable) {
    return editInRange(ownRange(doc, requested, { terminal: true }), kind, mutate);
}

const definitionInputEntry = doc => {
    const chain = activeChain(doc);
    if (chain.length) return chain[0];
    const roots = Object.entries(doc?.tables ?? {})
        .filter(([, table]) => token(table?.from) === "definition")
        .map(([id, table]) => ({ id, table }));
    return roots.length === 1 ? roots[0] : null;
};

/// Input-scoped editors (currently scoped search and definition-table cleanup)
/// target the final same-kind node before the first shape of the selected root.
export function inputComposableLocation(doc, kind) {
    const entry = definitionInputEntry(doc);
    return entry
        ? locationInRange(ownRange(doc, entry.id, { input: true }), kind)
        : null;
}

export function editInputComposable(doc, kind, mutate) {
    const entry = definitionInputEntry(doc);
    if (!entry) throw new Error("The definition-input table is ambiguous or unavailable.");
    return editInRange(ownRange(doc, entry.id, { input: true }), kind, mutate);
}

/// Server-populated, non-authoritative schema cache for the active table. This
/// is the client column universe; query validation still happens on the server.
export function activeTableSchema(doc) {
    const schema = tableEntry(doc, doc?.activeTable)?.table?.schema;
    return Array.isArray(schema) ? schema : null;
}

export const ownShapeLocations = (doc, requested = doc?.activeTable) => {
    const entry = tableEntry(doc, requested);
    return entry ? (entry.table?.composables ?? []).flatMap((composable, composableIndex) =>
        shapeKinds.has(kindOf(composable))
            ? [{ tableId: entry.id, table: entry.table, composable, composableIndex }]
            : []) : [];
};

export const shapeLocations = (doc, requested = doc?.activeTable) => activeChain(doc, requested)
    .flatMap(entry => ownShapeLocations(doc, entry.id));

export const activeShapeLocation = (doc, kind = null) => ownShapeLocations(doc)
    .find(location => kind === null || isKind(location.composable, kind)) ?? null;

export function replaceComposable(doc, location, replacement) {
    const entry = tableEntry(doc, location?.tableId);
    const index = location?.composableIndex;
    if (!entry || !Number.isInteger(index)
        || kindOf(entry.table?.composables?.[index]) !== kindOf(location?.composable))
        throw new Error("The composable changed while it was being edited.");
    entry.table.composables[index] = {
        ...structuredClone(replacement),
        kind: kindOf(replacement),
    };
    return {
        tableId: entry.id,
        table: entry.table,
        composable: entry.table.composables[index],
        composableIndex: index,
    };
}

/// The built-in UI mode is a predicate over the active composition. Documents
/// with several shape composables are preserved without assigning a lossy toolbar
/// mode; the server remains responsible for deciding whether they are executable.
export function modeOf(doc) {
    const kinds = ownShapeLocations(doc).map(location => kindOf(location.composable));
    if (kinds.length === 0) {
        const active = tableEntry(doc, doc?.activeTable);
        return token(active?.table?.from) === "definition"
            ? "grid"
            : "custom";
    }
    if (kinds.length !== 1) return "custom";
    return kinds[0] === "group" ? "groupBy"
        : kinds[0] === "pivot" ? "pivot"
            : kinds[0] === "chart" ? "chart"
                : "custom";
}

const isViewCandidate = (doc, id, mode) => {
    const entry = tableEntry(doc, id);
    const chain = activeChain(doc, id);
    if (!entry || !chain.length) return false;
    const shapes = ownShapeLocations(doc, id);
    if (mode === "grid")
        return shapes.length === 0
            && token(entry.table?.from) === "definition";
    const expected = modeKind(mode);
    return shapes.length === 1 && isKind(shapes[0].composable, expected);
};

export const viewCandidates = (doc, mode) => Object.keys(doc?.tables ?? {})
    .filter(id => isViewCandidate(doc, id, mode))
    .map(id => {
        const entry = tableEntry(doc, id);
        return {
            tableId: entry.id,
            table: entry.table,
            shapeLocation: mode === "grid" ? null : ownShapeLocations(doc, entry.id)[0],
        };
    });

/// Resolve a built-in view without consulting map order. Ambiguity is data the
/// caller must surface; it is never reinterpreted as an absent view.
export function resolveView(doc, mode) {
    const candidates = viewCandidates(doc, mode);
    const active = candidates.find(candidate => sameColumn(candidate.tableId, doc?.activeTable));
    if (active) return { status: "active", candidate: active, candidates };
    if (candidates.length === 0) return { status: "absent", candidate: null, candidates };
    if (candidates.length === 1) return { status: "available", candidate: candidates[0], candidates };
    return { status: "ambiguous", candidate: null, candidates };
}

/// Resolve the base input for a newly authored shaped view. The selected
/// ancestry's base wins; otherwise the same explicit unique/ambiguous result as
/// toolbar view selection applies.
export function resolveCreationBase(doc) {
    const root = activeChain(doc)[0];
    const isDefinitionBase = candidate => candidate
        && ownShapeLocations(doc, candidate.id).length === 0
        && token(candidate.table?.from) === "definition";
    if (isDefinitionBase(root))
        return { status: "active", candidate: { tableId: root.id, table: root.table, shapeLocation: null }, candidates: [] };
    const candidates = Object.entries(doc?.tables ?? {})
        .map(([id]) => tableEntry(doc, id))
        .filter(isDefinitionBase)
        .map(entry => ({ tableId: entry.id, table: entry.table, shapeLocation: null }));
    if (candidates.length === 0) return { status: "absent", candidate: null, candidates };
    if (candidates.length === 1) return { status: "available", candidate: candidates[0], candidates };
    return { status: "ambiguous", candidate: null, candidates };
}

const nextTableId = (doc, prefix) => {
    const used = new Set(Object.keys(doc.tables ?? {}).map(id => id.toLowerCase()));
    if (!used.has(prefix.toLowerCase())) return prefix;
    let suffix = 2;
    while (used.has(`${prefix}${suffix}`.toLowerCase())) suffix++;
    return `${prefix}${suffix}`;
};

export function selectView(doc, mode, tableId = null) {
    const candidate = tableId
        ? viewCandidates(doc, mode).find(item => sameColumn(item.tableId, tableId))
        : resolveView(doc, mode).candidate;
    if (!candidate) return false;
    doc.activeTable = candidate.tableId;
    return true;
}

export function createView(doc, mode, shape, fromTableId) {
    if (mode === "grid") throw new Error("A base table cannot be created as a shaped view.");
    const source = tableEntry(doc, fromTableId);
    if (!source
        || ownShapeLocations(doc, source.id).length !== 0
        || token(source.table?.from) !== "definition")
        throw new Error("The selected base table is unavailable.");
    const kind = modeKind(mode);
    if (!isKind(shape, kind)) throw new Error(`Expected a ${kind} composable.`);
    doc.tables ??= {};
    const id = nextTableId(doc, mode);
    doc.tables[id] = {
        from: source.id,
        composables: [{ ...structuredClone(shape), kind }],
    };
    doc.activeTable = id;
    return {
        tableId: id,
        table: doc.tables[id],
        composable: doc.tables[id].composables[0],
        composableIndex: 0,
    };
}

export const shapeMode = location => kindMode(location?.composable?.kind);

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
            while (end < source.length && /[A-Za-z0-9_$#]/.test(source[end])) end++;
            if (matches(source.slice(index, end))) return true;
            index = end;
            continue;
        }
        index++;
    }
    return false;
}

// --- coarse dependency invalidation (T0) -------------------------------------

const shapesReferenceColumn = (locations, column) => locations.some(location => {
    const shape = location.composable ?? {};
    return (shape.by ?? []).some(name => sameColumn(name, column))
        || (shape.rows ?? []).some(name => sameColumn(name, column))
        || (shape.cols ?? []).some(name => sameColumn(name, column))
        || (shape.values ?? []).some(value => sameColumn(value?.col, column))
        || sameColumn(shape.label, column)
        || sameColumn(shape.value, column);
});

/// Remove a column and every computed column that depends on it from all
/// same-kind nodes in one input or terminal segment. Packaged editors author the
/// final node of a kind, but foreign documents may contain earlier nodes which
/// remain executable and therefore must not retain dangling references.
const cleanupColumnReferences = (range, column, { pivotFamily = false } = {}) => {
    const retired = [];
    const visited = new Set();
    const nodes = kind => locationsInRange(range, kind).map(location => location.composable);

    const remove = current => {
        if (typeof current !== "string" || visited.has(current.toLowerCase())) return;
        visited.add(current.toLowerCase());
        retired.push(current);
        const matches = name => sameColumn(name, current)
            || (pivotFamily && typeof name === "string"
                && name.toLowerCase().startsWith(`${current.toLowerCase()}@`));
        const expressionMatches = expression => expressionReferencesColumn(
            expression,
            current,
            { pivotFamily });

        const removedComputed = [];
        for (const compute of nodes("compute")) {
            const removed = (compute.computed ?? []).filter(rule =>
                matches(rule.id) || expressionMatches(rule.expr));
            compute.computed = (compute.computed ?? []).filter(rule => !removed.includes(rule));
            removedComputed.push(...removed);
        }
        for (const filter of nodes("filter"))
            filter.filters = (filter.filters ?? []).filter(rule => !expressionMatches(rule.expr));
        for (const sort of nodes("sort"))
            sort.sorts = (sort.sorts ?? []).filter(rule => !matches(rule.col));
        for (const breaks of nodes("break"))
            breaks.breaks = (breaks.breaks ?? []).filter(name => !matches(name));
        for (const aggregate of nodes("aggregate"))
            aggregate.aggregates = (aggregate.aggregates ?? []).filter(rule => !matches(rule.col));
        for (const highlight of nodes("highlight"))
            highlight.highlights = (highlight.highlights ?? []).filter(rule =>
                !matches(rule.col) && !expressionMatches(rule.expr));
        for (const select of nodes("select"))
            if (Array.isArray(select.columns)) select.columns = select.columns.filter(name => !matches(name));
        for (const labels of nodes("labels")) mapDeleteWhere(labels.labels, matches);
        for (const formats of nodes("formats")) {
            for (const [name, format] of Object.entries(formats.formats ?? {})) {
                if (matches(name)) {
                    delete formats.formats[name];
                    continue;
                }
                if (matches(format?.keyColumn)) {
                    if (String(format?.displayAs ?? "").trim().toLowerCase() === "action") {
                        delete format.displayAs;
                        delete format.command;
                    }
                    delete format.keyColumn;
                }
                if (matches(format?.urlColumn) || matches(format?.textColumn)) {
                    delete format.displayAs;
                    delete format.urlColumn;
                    delete format.textColumn;
                }
            }
        }

        // A removed computed rule has an output identity of its own. Its
        // dependants and terminal presentation must retire transitively.
        for (const rule of removedComputed)
            if (!sameColumn(rule.id, current)) remove(rule.id);
    };

    remove(column);
    return retired;
};

/// Delete one definition-input computed column and everything that depends on it.
/// Within the input table, references are stripped precisely. A descendant
/// table whose shape consumes the column is removed with its descendants (T0
/// coarse invalidation). Unrelated roots in an externally-authored document are
/// untouched. Returns the built-in modes that were dropped so callers can say so.
export function removeInputComputedColumn(state, column) {
    const source = definitionInputEntry(state);
    if (!source) return [];
    const retired = cleanupColumnReferences(
        ownRange(state, source.id, { input: true }),
        column);

    const descendants = new Set([source.id.toLowerCase()]);
    let changed = true;
    while (changed) {
        changed = false;
        for (const [id, table] of Object.entries(state.tables ?? {})) {
            const key = id.toLowerCase();
            if (descendants.has(key)) continue;
            if (descendants.has(token(table?.from))) {
                descendants.add(key);
                changed = true;
            }
        }
    }

    const remove = new Set();
    for (const [id] of Object.entries(state.tables ?? {})) {
        if (id.toLowerCase() === source.id.toLowerCase() || !descendants.has(id.toLowerCase())) continue;
        if (retired.some(name => shapesReferenceColumn(shapeLocations(state, id), name)))
            remove.add(id.toLowerCase());
    }
    changed = true;
    while (changed) {
        changed = false;
        for (const [id, table] of Object.entries(state.tables ?? {})) {
            if (!remove.has(id.toLowerCase()) && remove.has(token(table?.from))) {
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
    if (remove.has(token(state.activeTable))) state.activeTable = source.id;
    return dropped;
}

/// Delete one terminal computed column and its references in the exact active
/// table: filters, sorts, highlights, selection, and presentation maps.
export function removeTerminalComputedColumn(state, column, tableId = state?.activeTable) {
    const pivotFamily = shapeLocations(state, tableId)
        .some(location => isKind(location.composable, "pivot"));
    cleanupColumnReferences(
        ownRange(state, tableId, { terminal: true }),
        column,
        { pivotFamily });
    return state;
}

/// After a Group By / Pivot edit retires metric ids, drop terminal state that
/// referenced them (the same coarse rule as computed-column removal).
export function pruneRetiredMetrics(state, tableId, retiredIds) {
    for (const id of retiredIds) removeTerminalComputedColumn(state, id, tableId);
    return state;
}

/// Pivot cell names encode the complete ordered column-dimension key. If that
/// dimension sequence changes, every old count and metric cell family retires,
/// even when the metric ids themselves remain stable.
export function pruneRetiredPivotOutputs(
    state,
    tableId,
    previous,
    replacement,
    retiredMetricIds = []) {
    const oldColumns = previous?.cols ?? [];
    const nextColumns = replacement?.cols ?? [];
    const columnsChanged = oldColumns.length !== nextColumns.length
        || oldColumns.some((name, index) => !sameColumn(name, nextColumns[index]));
    const retired = [...retiredMetricIds];
    if (columnsChanged)
        retired.push("__count", ...(previous?.values ?? []).map(value => value.id));
    const unique = retired.filter((name, index) => typeof name === "string"
        && retired.findIndex(candidate => sameColumn(candidate, name)) === index);
    pruneRetiredMetrics(state, tableId, unique);
    for (const row of previous?.rows ?? [])
        if (!(replacement?.rows ?? []).some(candidate => sameColumn(candidate, row)))
            removeTerminalComputedColumn(state, row, tableId);
    return state;
}

const chartOutputColumns = shape => {
    const label = shape?.label;
    const metricBase = !shape?.value ? "__count" : shape.fn ? "v0" : shape.value;
    const metric = sameColumn(label, metricBase) ? `${metricBase}_metric` : metricBase;
    return [label, metric].filter(name => typeof name === "string" && name.length > 0);
};

/// Chart output names are stable when an edit changes only presentation or keeps
/// the same label/metric identities. Retire only names which disappear.
export function pruneRetiredChartOutputs(state, tableId, previous, replacement) {
    const retained = chartOutputColumns(replacement);
    for (const name of chartOutputColumns(previous))
        if (!retained.some(candidate => sameColumn(candidate, name)))
            removeTerminalComputedColumn(state, name, tableId);
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
