// Pure report-state transformations over the composable table document. A
// document owns an unordered map of named tables. Each table explicitly names
// its input with `from`; composable kinds determine their semantic phase, while
// their array positions are retained only as document locations for exact edits.
// Table names are opaque; the only reserved input is `definition`.

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

const stableValue = value => {
    if (Array.isArray(value)) return value.map(stableValue);
    if (!value || typeof value !== "object") return value;
    return Object.fromEntries(Object.keys(value)
        .sort()
        .map(key => [key, stableValue(value[key])]));
};

const exportedRelationKinds = new Set(["group", "pivot", "chart", "compute", "filter"]);

/// A table signature for schema-cache invalidation. Only `from` and exported
/// relation operations can change this table's or a descendant's public schema.
/// Metadata and owner-local response instructions are interpreted live and do not
/// cross that boundary. Composable array position is not executable semantics, so
/// a storage-only permutation also leaves the signature unchanged.
const semanticTableSignature = table => {
    const composables = (table?.composables ?? []).flatMap(composable => {
        if (!exportedRelationKinds.has(token(composable?.kind))) return [];
        const normalized = stableValue(composable);
        if (normalized && typeof normalized === "object") normalized.kind = token(normalized.kind);
        return [normalized];
    });
    composables.sort((left, right) =>
        String(JSON.stringify(left)).localeCompare(String(JSON.stringify(right))));
    return JSON.stringify(stableValue({
        from: token(table?.from),
        composables,
    }));
};

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
        const oldDefinition = old ? semanticTableSignature(old) : null;
        const nextDefinition = semanticTableSignature(table);
        if (oldDefinition !== nextDefinition) changed.add(id.toLowerCase());
    }

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
/// storage position. `participates` mirrors table export: parent tables contribute
/// relational rules and metadata, while their local presentation/control state
/// remains local to those tables. Shape position never defines a segment. A shape
/// is naturally first in its owning table, and every other owned composable is
/// interpreted against its completed shape. `authorable` is deliberately narrower
/// than `owned`: packaged editors safely write only the last node of each kind in
/// the active table. Earlier/repeated/foreign nodes stay preserved and read-only.
export const composableLocations = (doc, requested = doc?.activeTable) => {
    const chain = activeChain(doc, requested);
    if (!chain.length) return [];
    const activeId = chain.at(-1).id.toLowerCase();
    const rootId = chain[0].id.toLowerCase();
    let inheritedShape = false;
    const result = [];

    for (const entry of chain) {
        const composables = entry.table?.composables ?? [];
        const owned = entry.id.toLowerCase() === activeId;
        const ownsShape = composables.some(composable => shapeKinds.has(kindOf(composable)));

        for (let index = 0; index < composables.length; index++) {
            const composable = composables[index];
            const kind = kindOf(composable);
            const shape = shapeKinds.has(kind);
            const afterShape = inheritedShape || (ownsShape && !shape);
            const participates = shape
                || (ordinaryKindSet.has(kind)
                    && (owned || inheritedKindSet.has(kind)));
            const terminal = owned && !shape;
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
                source: entry.id.toLowerCase() === rootId && !ownsShape && !shape,
                terminal,
                authorable: terminal && ordinaryKindSet.has(kind) && !laterSameKind,
            });
        }
        if (ownsShape) inheritedShape = true;
    }
    return result;
};

const composedMap = (doc, kind, field) => {
    const result = { input: undefined, output: undefined };
    let afterShape = false;
    for (const entry of activeChain(doc)) {
        const composables = entry.table?.composables ?? [];
        if (composables.some(composable => shapeKinds.has(kindOf(composable))))
            afterShape = true;

        const maps = composables.flatMap(composable => {
            if (!isKind(composable, kind)) return [];
            const values = composable[field];
            return values && typeof values === "object" && !Array.isArray(values)
                ? [values]
                : [];
        });
        if (!maps.length) continue;

        const side = afterShape ? "output" : "input";
        result[side] ??= {};
        if (maps.some(values => Object.keys(values).length === 0)) {
            // A same-table empty map resets inherited metadata once before its
            // sibling overlays merge. Its storage position cannot erase a
            // non-empty overlay owned by the same table.
            if (afterShape) result.input = {};
            result[side] = {};
        }

        for (const values of maps) {
            if (Object.keys(values).length === 0) continue;
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

/// Effective display labels over the complete selected ancestry, split at the
/// natural shape boundary. Labels owned by a shaped table apply to that shape's
/// output regardless of storage position. Same-table overlays merge
/// case-insensitively; an explicit empty map first clears everything inherited by
/// that table, wherever the empty map is stored in the array.
export const composedLabels = doc => composedMap(doc, "labels", "labels");

const copyFormat = value => ({
    ...value,
    ...(Array.isArray(value?.classes) ? { classes: [...value.classes] } : {}),
});

const formatMapsOwnedBy = entry => (entry?.table?.composables ?? []).flatMap(composable => {
    if (!isKind(composable, "formats")) return [];
    const values = composable.formats;
    return values && typeof values === "object" && !Array.isArray(values)
        ? [values]
        : [];
});

const overlayFormatMaps = (inherited, maps) => {
    const effective = {};
    for (const [name, value] of Object.entries(inherited ?? {}))
        if (value && typeof value === "object" && !Array.isArray(value))
            setMapEntry(effective, name, copyFormat(value));

    // Empty is a table-boundary reset. Apply it once before every non-empty
    // same-table overlay so serialization order cannot change the result.
    if (maps.some(values => Object.keys(values).length === 0))
        for (const name of Object.keys(effective)) delete effective[name];

    for (const values of maps) {
        if (Object.keys(values).length === 0) continue;
        for (const [name, value] of Object.entries(values)) {
            if (!String(name).trim()
                || !value
                || typeof value !== "object"
                || Array.isArray(value)) continue;
            setMapEntry(effective, name, copyFormat(value));
        }
    }
    return effective;
};

const maskOnlyFormats = formats => {
    const result = {};
    for (const [name, value] of Object.entries(formats ?? {})) {
        if (typeof value?.mask !== "string" || !value.mask.trim()) continue;
        setMapEntry(result, name, { mask: value.mask });
    }
    return result;
};

/// A named table exports presentation metadata only for columns in its completed
/// public schema. Generated columns inherit through their structural formatSource,
/// but a same-table format for a source column removed by a Shape is not allowed to
/// hitch a ride across the next `from` edge.
const exportedMaskFormats = (entry, imported, effective, owned) => {
    const columns = entry?.table?.schema;
    if (!Array.isArray(columns)) return maskOnlyFormats(effective);

    const result = {};
    for (const column of columns) {
        const name = typeof column === "string" ? column : column?.name;
        if (typeof name !== "string" || !name.trim()) continue;
        const source = typeof column?.formatSource === "string" && column.formatSource.trim()
            ? column.formatSource
            : name;
        const direct = lookupValue(owned, name);
        const inherited = direct === undefined
            ? lookupValue(effective, name) ?? lookupValue(imported, source)
            : undefined;
        const selected = direct ?? inherited;
        if (typeof selected?.mask !== "string" || !selected.mask.trim()) continue;

        const exported = { mask: selected.mask };
        setMapEntry(result, name, exported);
    }
    return result;
};

/// Effective formats for the selected table plus the mask-only contract imported
/// by that table. Keeping those maps distinct prevents an active-table assignment
/// to a pre-Shape source name from leaking backward into a generated column.
export const composedFormatContext = doc => {
    const chain = activeChain(doc);
    let inherited = {};
    for (let index = 0; index < chain.length; index++) {
        const entry = chain[index];
        const maps = formatMapsOwnedBy(entry);
        const clearsInherited = maps.some(values => Object.keys(values).length === 0);
        const imported = clearsInherited ? {} : inherited;
        const effective = overlayFormatMaps(inherited, maps);
        const owned = overlayFormatMaps({}, maps);
        if (index === chain.length - 1) return { effective, imported };

        inherited = exportedMaskFormats(entry, imported, effective, owned);
    }
    return { effective: inherited, imported: inherited };
};

/// Effective formats for the selected table. A `from` edge carries only a
/// column's safe scalar mask. Alignment, styles, classes, renderers, commands,
/// and renderer source columns remain owner-local. The active table's direct
/// entries stay complete; a later boundary reduces them to mask metadata again.
export const composedFormats = doc => composedFormatContext(doc).effective;

const ownRange = (doc, requested) => {
    const entry = tableEntry(doc, requested);
    if (!entry) return null;
    const composables = entry.table?.composables ?? [];
    return {
        entry,
        start: 0,
        end: composables.length,
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

/// The last exact composable of `kind` owned by a table. Shape position does not
/// partition the table because natural semantic ordering is inferred by kind.
export function terminalComposableLocation(doc, kind, requested = doc?.activeTable) {
    return locationInRange(ownRange(doc, requested), kind);
}

export function editTerminalComposable(doc, kind, mutate, requested = doc?.activeTable) {
    return editInRange(ownRange(doc, requested), kind, mutate);
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
/// target the final same-kind node owned by the selected definition root. A root
/// shape, when present in an external document, is naturally evaluated before
/// those ordinary composables regardless of storage position.
export function inputComposableLocation(doc, kind) {
    const entry = definitionInputEntry(doc);
    return entry
        ? locationInRange(ownRange(doc, entry.id), kind)
        : null;
}

export function editInputComposable(doc, kind, mutate) {
    const entry = definitionInputEntry(doc);
    if (!entry) throw new Error("The definition-input table is ambiguous or unavailable.");
    return editInRange(ownRange(doc, entry.id), kind, mutate);
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

const stableTextCompare = (left, right) => {
    const a = String(left ?? "");
    const b = String(right ?? "");
    const lowerA = a.toLowerCase();
    const lowerB = b.toLowerCase();
    return lowerA < lowerB ? -1
        : lowerA > lowerB ? 1
            : a < b ? -1
                : a > b ? 1
                    : 0;
};

const highlightScopeOrder = rule =>
    String(rule?.scope ?? "row").trim().toLowerCase() === "cell" ? 1 : 0;

/// Canonical client view of one table's highlight priority set. Missing
/// sequences are assigned by stable id into the first unused ten-step slots,
/// matching server normalization; document/list position is retained only as a
/// mutation address in `index`. Disabled declarations deliberately participate
/// in this normalization so toggling a rule cannot reorder the remaining rules.
/// Row rules precede cell rules, and sequence establishes precedence within each
/// scope. Consumers which execute rules filter disabled entries only afterward.
export function normalizedHighlightRules(rules) {
    const entries = (rules ?? []).map((rule, index) => ({
        rule,
        index,
        sequence: Number.isInteger(rule?.sequence) ? rule.sequence : null,
    }));
    const used = new Set(entries
        .filter(entry => entry.sequence !== null)
        .map(entry => entry.sequence));
    const missing = entries
        .filter(entry => entry.sequence === null)
        .sort((left, right) => stableTextCompare(left.rule?.id, right.rule?.id));
    let sequence = 10;
    for (const entry of missing) {
        while (used.has(sequence)) sequence += 10;
        entry.sequence = sequence;
        used.add(sequence);
        sequence += 10;
    }
    return entries.sort((left, right) => highlightScopeOrder(left.rule) - highlightScopeOrder(right.rule)
        || left.sequence - right.sequence
        || stableTextCompare(left.rule?.id, right.rule?.id));
}

const addUsedColumnId = (ids, candidate) => {
    const value = typeof candidate === "string" ? candidate : candidate?.name;
    if (typeof value === "string" && value.trim()) ids.add(value.trim().toLowerCase());
};

/// The shared public-column namespace used by client-authored computed columns
/// and Group/Pivot metrics. Table schema caches reserve source and generated
/// column names; composable scans keep allocation safe even before a refreshed
/// cache arrives. Callers may add the definition or dialog input columns which
/// live outside the report document.
export function usedColumnIds(doc, additionalColumns = []) {
    const ids = new Set();
    for (const column of additionalColumns ?? []) addUsedColumnId(ids, column);
    for (const table of Object.values(doc?.tables ?? {})) {
        for (const column of table?.schema ?? []) addUsedColumnId(ids, column);
        for (const composable of table?.composables ?? []) {
            const kind = kindOf(composable);
            if (kind === "compute")
                for (const rule of composable?.computed ?? []) addUsedColumnId(ids, rule?.id);
            if (kind === "group" || kind === "pivot")
                for (const metric of composable?.values ?? []) addUsedColumnId(ids, metric?.id);
        }
    }
    return ids;
}

export const nextSyntheticColumnId = (doc, additionalColumns = []) =>
    nextFreeId(usedColumnIds(doc, additionalColumns), "ir");

/// Preserve metric identities by (column, function) while allocating every new
/// metric from the same document-wide `irN` namespace as computed columns.
/// Removed identities stay reserved during the edit because they are still in
/// `doc`; this prevents a replacement metric from silently inheriting state.
export function assignShapeMetricIds(doc, rows, previous, additionalColumns = []) {
    const remaining = [...(previous ?? [])];
    const used = usedColumnIds(doc, additionalColumns);
    const fresh = () => {
        const id = nextFreeId(used, "ir");
        used.add(id);
        return id;
    };
    const values = (rows ?? []).map(row => {
        const index = remaining.findIndex(value =>
            sameColumn(value?.col, row?.col) && value?.fn === row?.fn);
        if (index >= 0) {
            const [kept] = remaining.splice(index, 1);
            return { id: kept.id, col: row.col, fn: row.fn };
        }
        return { id: fresh(), col: row.col, fn: row.fn };
    });
    return { values, retired: remaining.map(value => value.id) };
}

const mapDeleteWhere = (map, predicate) => {
    if (!map) return;
    for (const key of Object.keys(map))
        if (predicate(key)) delete map[key];
};

/// True when an expression contains the column as an identifier, excluding quoted
/// string contents and longer identifiers (ir1 does not match ir10 or 'ir1').
const expressionKeywords = new Set([
    "case", "when", "then", "else", "end", "and", "or", "not", "is", "null", "between",
]);
const expressionIdentifierStart = value => /[\p{L}_]/u.test(value);
const expressionIdentifierPart = value => /[\p{L}\p{Nd}_$#]/u.test(value);

export function expressionReferencesColumn(expression, column) {
    const source = String(expression ?? "");
    const target = String(column ?? "").toLowerCase();
    const matches = name => {
        const candidate = name.toLowerCase();
        return candidate === target;
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
        if (!quoted && expressionIdentifierStart(source[index])) {
            let end = index + 1;
            while (end < source.length && expressionIdentifierPart(source[end])) end++;
            const identifier = source.slice(index, end);
            let next = end;
            while (next < source.length && /\s/u.test(source[next])) next++;
            // Bare language keywords and call heads are syntax, not column
            // references. Quoted names above remain columns even before `(`.
            if (!expressionKeywords.has(identifier.toLowerCase())
                && source[next] !== "("
                && matches(identifier)) return true;
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
/// same-kind nodes owned by one table. Packaged editors author the final node of
/// a kind, but foreign documents may contain earlier nodes which remain
/// executable and therefore must not retain dangling references.
const cleanupColumnReferences = (range, column) => {
    const retired = [];
    const visited = new Set();
    const nodes = kind => locationsInRange(range, kind).map(location => location.composable);

    const remove = current => {
        if (typeof current !== "string" || visited.has(current.toLowerCase())) return;
        visited.add(current.toLowerCase());
        retired.push(current);
        const matches = name => sameColumn(name, current);
        const expressionMatches = expression => expressionReferencesColumn(expression, current);

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

const schemaColumnNames = table => (table?.schema ?? [])
    .map(column => typeof column === "string" ? column : column?.name)
    .filter(name => typeof name === "string" && name.length > 0);

/// Remove exported columns from one table and propagate that loss through every
/// descendant. Ordinary descendant rules are pruned exactly. A descendant whose
/// Shape consumes a retired column is removed with its subtree because its output
/// contract can no longer be inferred locally.
function pruneExportedColumns(state, tableId, columns) {
    const root = tableEntry(state, tableId);
    if (!root || columns.length === 0) return [];

    const retiredByTable = new Map();
    const rootRetired = [];
    for (const column of columns)
        rootRetired.push(...cleanupColumnReferences(ownRange(state, root.id), column));
    retiredByTable.set(root.id.toLowerCase(), rootRetired);

    const removed = new Set();
    let changed = true;
    while (changed) {
        changed = false;
        for (const [id, table] of Object.entries(state.tables ?? {})) {
            const key = id.toLowerCase();
            if (key === root.id.toLowerCase() || removed.has(key) || retiredByTable.has(key)) continue;
            const parent = token(table?.from);
            if (removed.has(parent)) {
                removed.add(key);
                changed = true;
                continue;
            }
            const inherited = retiredByTable.get(parent);
            if (!inherited) continue;
            const shapes = ownShapeLocations(state, id);
            if (inherited.some(column => shapesReferenceColumn(shapes, column))) {
                removed.add(key);
                changed = true;
                continue;
            }

            // A Shape which does not consume the retired input is a schema
            // boundary: that input is already absent from its completed export.
            if (shapes.length > 0) {
                retiredByTable.set(key, []);
                changed = true;
                continue;
            }

            const retired = [];
            for (const column of inherited)
                retired.push(...cleanupColumnReferences(ownRange(state, id), column));
            retiredByTable.set(key, retired);
            changed = true;
        }
    }

    const dropped = [];
    for (const [id] of Object.entries(state.tables ?? {})) {
        if (!removed.has(id.toLowerCase())) continue;
        const mode = modeOf({ ...state, activeTable: id });
        if (mode !== "custom" && mode !== "grid" && !dropped.includes(mode)) dropped.push(mode);
        delete state.tables[id];
    }
    if (removed.has(token(state.activeTable))) state.activeTable = root.id;
    return dropped;
}

/// Delete one definition-input computed column and everything exported from it.
/// Ordinary descendants lose exact references; a descendant Shape which consumes
/// a retired column is removed with its subtree. Unrelated roots remain untouched.
export function removeInputComputedColumn(state, column) {
    const source = definitionInputEntry(state);
    return source ? pruneExportedColumns(state, source.id, [column]) : [];
}

/// Delete one computed/public output and propagate its loss through every named
/// table which imports it. Returns built-in descendant modes removed by coarse
/// Shape invalidation so UI callers can report the loss.
export function removeTerminalComputedColumn(state, column, tableId = state?.activeTable) {
    return pruneExportedColumns(state, tableId, [column]);
}

/// After a Group/Pivot edit retires metric ids, drop owner and descendant state
/// which referenced their exported columns.
export function pruneRetiredMetrics(state, tableId, retiredIds) {
    pruneExportedColumns(state, tableId, retiredIds);
    return state;
}

const authoredComputedIds = table => new Set((table?.composables ?? [])
    .filter(composable => isKind(composable, "compute"))
    .flatMap(composable => composable?.computed ?? [])
    .map(rule => typeof rule?.id === "string" ? rule.id.toLowerCase() : null)
    .filter(Boolean));

/// Dynamic Pivot cell ids are intentionally opaque. The previous live schema is
/// therefore the authority for the cells owned by a Pivot: every output other than
/// its row dimensions is a generated cell (or a dependent same-table computation).
/// A column-key change retires all of those ids. Removing a metric does the same
/// coarse cleanup because the public id deliberately does not encode its family.
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
    const retired = (previous?.rows ?? [])
        .filter(row => !(replacement?.rows ?? []).some(candidate => sameColumn(candidate, row)));
    if (columnsChanged || retiredMetricIds.length > 0) {
        const table = tableEntry(state, tableId)?.table;
        const rows = previous?.rows ?? [];
        const computed = authoredComputedIds(table);
        const generated = schemaColumnNames(table)
            .filter(name => !rows.some(row => sameColumn(row, name))
                && !computed.has(name.toLowerCase()));
        retired.push(...generated);
    }
    const unique = retired.filter((name, index) => typeof name === "string"
        && retired.findIndex(candidate => sameColumn(candidate, name)) === index);
    pruneExportedColumns(state, tableId, unique);
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
    const retired = chartOutputColumns(previous)
        .filter(name => !retained.some(candidate => sameColumn(candidate, name)));
    pruneExportedColumns(state, tableId, retired);
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
