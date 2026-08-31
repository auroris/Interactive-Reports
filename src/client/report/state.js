// State transformations operate purely over the composable table document. A document owns an
// unordered map of named tables. Each table explicitly names its input with `from`; composable
// kinds determine their semantic phase, while their array positions are retained only as
// document locations for exact edits. Table names are opaque; the only reserved input is
// `definition`.

import { resolveLocale, translate } from "../core/localization.js";

/**
 * Normalizes report state into the stable form required by the browser report controller.
 *
 * @param {object|null|undefined} raw - The persisted or caller-supplied state; nullish input starts from the defaults.
 * @param {number} [defaultPageSize=50] - The page size used when the incoming report state does not specify one.
 * @param {object|null} [defaults=null] - Baseline state values copied before defined fields from `raw` are overlaid.
 * @returns {object} The normalized report-state document.
 *
 * Side effects: none; both supplied objects are cloned before normalization.
 */
export function normalizeReportState(raw, defaultPageSize = 50, defaults = null) {
    const state = defaults ? structuredClone(defaults) : {};
    for (const [key, value] of Object.entries(raw ? structuredClone(raw) : {}))
        if (value !== null && value !== undefined) state[key] = value;

    // Legacy schema snapshots are discarded because the server document is authoritative and
    // every query is validated server-side.
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

/**
 * Serializes report state after removing client-only and obsolete transport fields.
 *
 * @param {object} source - The report-state document to prepare for persistence or transport.
 * @returns {object} A detached document without underscore-prefixed properties, undefined values, or the legacy version field.
 */
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

/**
 * Recursively canonicalizes a value so semantic state comparisons are deterministic.
 *
 * @param {unknown} value - The JSON-compatible value to canonicalize.
 * @returns {unknown} A recursively key-sorted, JSON-compatible value.
 */
const stableValue = value => {
    if (Array.isArray(value)) return value.map(stableValue);
    if (!value || typeof value !== "object") return value;
    return Object.fromEntries(Object.keys(value)
        .sort()
        .map(key => [key, stableValue(value[key])]));
};

const exportedRelationKinds = new Set(["group", "pivot", "chart", "compute", "filter"]);

// Cache policy: only `from` and exported
// relation operations can change this table's or a descendant's public schema. Metadata and
// owner-local response instructions are interpreted live and do not cross that boundary.
// Composable array position is not executable semantics, so a storage-only permutation also
// leaves the signature unchanged.
/**
 * Builds the table signature used to detect schema-affecting state changes.
 *
 * @param {object|null|undefined} table - The table definition whose schema-producing operations are included.
 * @returns {string} A deterministic JSON signature of the table's public-schema inputs.
 */
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

// Protocol contract: null cached schemas for changed table definitions and every table
// delegating from them. The server replaces null caches on the next document submission.
/**
 * Invalidates schema caches for changed tables and their dependents.
 *
 * @param {object} before - The previous report-state document used to detect semantic table changes.
 * @param {object} after - The next report-state document whose dependent schema caches may be cleared.
 * @returns {object} The same `after` document after invalidation.
 *
 * Side effects: sets `schema` to null on changed tables and every transitive dependent in `after`.
 */
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

// Table traversal keeps storage locations distinct from semantic participation. This allows the
// editors to update their terminal nodes without reordering or discarding unfamiliar document data.

const shapeKinds = new Set(["group", "pivot", "chart"]);
const ordinaryKindSet = new Set([
    "select", "labels", "formats", "compute", "filter", "sort", "highlight",
    "break", "aggregate",
]);
const inheritedKindSet = new Set(["compute", "filter", "labels", "formats"]);
/**
 * Normalizes a value into a lowercase comparison token.
 *
 * @param {unknown} value - The value to trim and case-fold; nullish values become an empty token.
 * @returns {string} The trimmed lowercase comparison token.
 */
const token = value => String(value ?? "").trim().toLowerCase();
/**
 * Returns the normalized kind of a composable operation.
 *
 * @param {object|null|undefined} composable - The composable whose kind is read.
 * @returns {string} The normalized composable kind.
 */
const kindOf = composable => token(composable?.kind);
/**
 * Determines whether a composable's normalized kind matches the requested kind.
 *
 * @param {object|null|undefined} composable - The composable whose kind is tested.
 * @param {string} kind - The kind to compare after normalization.
 * @returns {boolean} Whether the composable has the requested kind.
 */
const isKind = (composable, kind) => kindOf(composable) === token(kind);
/**
 * Maps a public view mode to its composable kind.
 *
 * @param {string} mode - The report view mode to select.
 * @returns {string} The composable kind corresponding to the view mode.
 */
const modeKind = mode => mode === "groupBy" ? "group" : mode;
/**
 * Maps a composable kind to its public view mode.
 *
 * @param {string} kind - The composable kind to expose as a view mode.
 * @returns {string} The view mode corresponding to the composable kind.
 */
const kindMode = kind => token(kind) === "group" ? "groupBy" : token(kind);

/**
 * Returns one table and its ancestry metadata from the selected composable chain.
 *
 * @param {object} doc - The report-state document containing the table map.
 * @param {string} requested - The case-insensitive table identifier to resolve.
 * @returns {object|null} The matching table entry, or null when it cannot be resolved.
 */
export const tableEntry = (doc, requested) => {
    if (!requested || !doc?.tables) return null;
    const wanted = token(requested);
    const matches = Object.entries(doc.tables).filter(([id]) => id.toLowerCase() === wanted);
    return matches.length === 1 ? { id: matches[0][0], table: matches[0][1] } : null;
};

/**
 * Returns the selected composable-table ancestry from definition input to active table.
 *
 * @param {object} doc - The report-state document containing the selected table graph.
 * @param {string} [requested=doc?.activeTable] - The requested table identifier; when omitted, the active table is used.
 * @returns {Array<object>} Table entries ordered from the definition root to the requested table, or an empty array for missing, ambiguous, or cyclic chains.
 */
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

// Invariant: every composable along the selected table's chain, retaining its exact owner and
// storage position. `participates` mirrors table export: parent tables contribute relational
// rules and metadata, while their local presentation/control state remains local to those
// tables. Shape position never defines a segment. A shape is naturally first in its owning
// table, and every other owned composable is interpreted against its completed shape.
// `authorable` is deliberately narrower than `owned`: packaged editors safely write only the
// last node of each kind in the active table. Earlier/repeated/foreign nodes stay preserved and
// read-only.
/**
 * Returns every composable with its owning table, index, and sequence position.
 *
 * @param {object} doc - The report-state document containing the selected table graph.
 * @param {string} [requested=doc?.activeTable] - The requested table identifier; when omitted, the active table is used.
 * @returns {Array<object>} Location records with ownership, participation, shape-phase, and authorability metadata.
 */
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

/**
 * Composes an owner-local map across the active table chain.
 *
 * @param {object} doc - The report-state document whose selected ancestry supplies the maps.
 * @param {string} kind - The composable kind that owns the requested map.
 * @param {string} field - The composable property containing the map to merge.
 * @returns {{input: object|undefined, output: object|undefined}} Maps composed on each side of the selected chain's shape boundary.
 */
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
            // Invariant: a same-table empty map resets inherited metadata once before its
            // sibling overlays merge. Its storage position cannot erase a non-empty overlay
            // owned by the same table.
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

/**
 * Effective display labels over the complete selected ancestry, split at the natural shape boundary.
 * Labels owned by a shaped table apply to that shape's output regardless of storage position.
 * Same-table overlays merge case-insensitively; an explicit empty map first clears everything
 * inherited by that table, wherever the empty map is stored in the array.
 *
 * @param {object} doc - The report-state document whose selected ancestry supplies label declarations.
 * @returns {{input: object|undefined, output: object|undefined}} Effective label maps on each side of the shape boundary.
 */
export const composedLabels = doc => composedMap(doc, "labels", "labels");

/**
 * Returns a defensive copy of a column-format declaration.
 *
 * @param {object} value - The format declaration to copy.
 * @returns {object} A detached column-format object.
 */
const copyFormat = value => ({
    ...value,
    ...(Array.isArray(value?.classes) ? { classes: [...value.classes] } : {}),
});

/**
 * Returns the format maps owned by one selected-chain entry.
 *
 * @param {object} entry - The selected-chain entry whose owner-local values are being inspected.
 * @returns {Array<object>} Every valid format map declared by the entry, in document order.
 */
const formatMapsOwnedBy = entry => (entry?.table?.composables ?? []).flatMap(composable => {
    if (!isKind(composable, "formats")) return [];
    const values = composable.formats;
    return values && typeof values === "object" && !Array.isArray(values)
        ? [values]
        : [];
});

/**
 * Overlays format maps while preserving inherited properties not explicitly replaced.
 *
 * @param {object} inherited - The values inherited from the selected parent chain.
 * @param {Array<object>} maps - The owner-local maps to merge over the inherited values.
 * @returns {object} A new case-insensitive effective format map.
 */
const overlayFormatMaps = (inherited, maps) => {
    const effective = {};
    for (const [name, value] of Object.entries(inherited ?? {}))
        if (value && typeof value === "object" && !Array.isArray(value))
            setMapEntry(effective, name, copyFormat(value));

    // Invariant: empty is a table-boundary reset. Apply it once before every non-empty
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

/**
 * Returns only the display-mask metadata from a format map.
 *
 * @param {object} formats - The column-format map from which mask-only entries are extracted.
 * @returns {object} A new map containing only entries with nonblank masks.
 */
const maskOnlyFormats = formats => {
    const result = {};
    for (const [name, value] of Object.entries(formats ?? {})) {
        if (typeof value?.mask !== "string" || !value.mask.trim()) continue;
        setMapEntry(result, name, { mask: value.mask });
    }
    return result;
};

// Invariant: a named table exports presentation metadata only for columns in its completed
// public schema. Generated columns inherit through their structural formatSource, but a
// same-table format for a source column removed by a Shape is not allowed to hitch a ride
// across the next `from` edge.
/**
 * Returns the mask formats exported across a composable-table boundary.
 *
 * @param {object} entry - The selected-chain entry whose owner-local values are being inspected.
 * @param {object} imported - The mask formats imported across the parent table edge.
 * @param {object} effective - Imported and owner-local formats after overlay.
 * @param {object} owned - Formats declared by the current table alone.
 * @returns {object} The mask-only formats safe to expose to a child table.
 */
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

// Invariant: effective formats for the selected table plus the mask-only contract imported by
// that table. Keeping those maps distinct prevents an active-table assignment to a pre-Shape
// source name from leaking backward into a generated column.
/**
 * Builds inherited and owner-local format maps for the active table chain.
 *
 * @param {object} doc - The report-state document whose active chain supplies format declarations.
 * @returns {{effective: object, imported: object}} Effective formats and the mask-only values imported by the active table.
 */
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

// Invariant: effective formats for the selected table. A `from` edge carries only a column's
// safe scalar mask. Alignment, styles, classes, renderers, commands, and renderer source
// columns remain owner-local. The active table's direct entries stay complete; a later boundary
// reduces them to mask metadata again.
/**
 * Returns the effective column formats for the selected composable table.
 *
 * @param {object} doc - The report-state document whose active chain supplies format declarations.
 * @returns {object} The effective output format map.
 */
export const composedFormats = doc => composedFormatContext(doc).effective;

/**
 * Returns the sequence range owned directly by one composable table.
 *
 * @param {object} doc - The report-state document containing the target table.
 * @param {string} requested - The case-insensitive table identifier to resolve.
 * @returns {{entry: object, start: number, end: number}|null} The table entry and its complete composable index range, or null when unresolved.
 */
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

/**
 * Returns the last composable location of a kind within the supplied range.
 *
 * @param {object} range - The contiguous composable range to inspect or update.
 * @param {string} kind - The composable kind to locate.
 * @returns {object|null} The last matching location in the range, or null when none exists.
 */
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

/**
 * Returns all composable locations within the supplied range.
 *
 * @param {object} range - The contiguous composable range to inspect or update.
 * @param {string} kind - The composable kind to locate.
 * @returns {Array<object>} Matching table and array locations in storage order.
 */
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

/**
 * Updates the last matching composable in a sequence range or inserts one at the range end.
 *
 * @param {object} range - The contiguous composable range to inspect or update.
 * @param {string} kind - The composable kind to edit.
 * @param {(composable: object, location: object) => void} mutate - Callback that edits the located or newly inserted composable.
 * @returns {object} The edited composable's table and array location.
 * @throws {Error} When the target range no longer exists.
 *
 * Side effects: may insert a composable and passes the document-owned object to `mutate`.
 */
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

/**
 * Rationale: the last exact composable of `kind` owned by a table. Shape position does not partition
 * the table because natural semantic ordering is inferred by kind.
 *
 * @param {object} doc - The report-state document containing the target table.
 * @param {string} kind - The composable kind to locate.
 * @param {string} [requested=doc?.activeTable] - The requested table identifier; when omitted, the active table is used.
 * @returns {object|null} The terminal composable location.
 */
export function terminalComposableLocation(doc, kind, requested = doc?.activeTable) {
    return locationInRange(ownRange(doc, requested), kind);
}

/**
 * Updates the last composable of a kind owned by the requested table, inserting it if absent.
 *
 * @param {object} doc - The report-state document containing the target table.
 * @param {string} kind - The composable kind to edit.
 * @param {(composable: object, location: object) => void} mutate - Callback that edits the document-owned composable.
 * @param {string} [requested=doc?.activeTable] - The requested table identifier; when omitted, the active table is used.
 * @returns {object} The edited composable's table and array location.
 *
 * Side effects: mutates `doc` through insertion, kind normalization, and the supplied callback.
 */
export function editTerminalComposable(doc, kind, mutate, requested = doc?.activeTable) {
    return editInRange(ownRange(doc, requested), kind, mutate);
}

/**
 * Returns the selected-chain entry that owns the definition input.
 *
 * @param {object} doc - The report-state document whose definition root is required.
 * @returns {object|null} The selected definition-root entry, or the sole available root when no valid chain is selected.
 */
const definitionInputEntry = doc => {
    const chain = activeChain(doc);
    if (chain.length) return chain[0];
    const roots = Object.entries(doc?.tables ?? {})
        .filter(([, table]) => token(table?.from) === "definition")
        .map(([id, table]) => ({ id, table }));
    return roots.length === 1 ? roots[0] : null;
};

/**
 * Input-scoped editors (currently scoped search and definition-table cleanup) target the final
 * same-kind node owned by the selected definition root. A root shape, when present in an external
 * document, is naturally evaluated before those ordinary composables regardless of storage position.
 *
 * @param {object} doc - The report-state document whose definition root is inspected.
 * @param {string} kind - The input-scoped composable kind to locate.
 * @returns {object|null} The input composable location.
 */
export function inputComposableLocation(doc, kind) {
    const entry = definitionInputEntry(doc);
    return entry
        ? locationInRange(ownRange(doc, entry.id), kind)
        : null;
}

/**
 * Updates a composable in the active table's definition-input range.
 *
 * @param {object} doc - The report-state document whose definition root is edited.
 * @param {string} kind - The input-scoped composable kind to edit.
 * @param {(composable: object, location: object) => void} mutate - Callback that edits the document-owned composable.
 * @returns {object} The edited composable's table and array location.
 * @throws {Error} When a unique definition-input table cannot be resolved.
 *
 * Side effects: mutates `doc` through insertion, kind normalization, and the supplied callback.
 */
export function editInputComposable(doc, kind, mutate) {
    const entry = definitionInputEntry(doc);
    if (!entry) throw new Error("The definition-input table is ambiguous or unavailable.");
    return editInRange(ownRange(doc, entry.id), kind, mutate);
}

// Cache policy: server-populated, non-authoritative schema cache for the active table. This is
// the client column universe; query validation still happens on the server.
/**
 * Returns the effective schema columns for the active composable table.
 *
 * @param {object} doc - The report-state document whose active table supplies the cache.
 * @returns {Array<object>|null} The active table's advisory schema, or null when it has not been populated.
 */
export function activeTableSchema(doc) {
    const schema = tableEntry(doc, doc?.activeTable)?.table?.schema;
    return Array.isArray(schema) ? schema : null;
}

/**
 * Returns shape locations authored directly by the requested table.
 *
 * @param {object} doc - The report-state document containing the requested table.
 * @param {string} [requested=doc?.activeTable] - The requested table identifier; when omitted, the active table is used.
 * @returns {Array<object>} The own shape locations.
 */
export const ownShapeLocations = (doc, requested = doc?.activeTable) => {
    const entry = tableEntry(doc, requested);
    return entry ? (entry.table?.composables ?? []).flatMap((composable, composableIndex) =>
        shapeKinds.has(kindOf(composable))
            ? [{ tableId: entry.id, table: entry.table, composable, composableIndex }]
            : []) : [];
};

/**
 * Returns all participating shape locations in the active table chain.
 *
 * @param {object} doc - The report-state document containing the requested table chain.
 * @param {string} [requested=doc?.activeTable] - The requested table identifier; when omitted, the active table is used.
 * @returns {Array<object>} The shape locations.
 */
export const shapeLocations = (doc, requested = doc?.activeTable) => activeChain(doc, requested)
    .flatMap(entry => ownShapeLocations(doc, entry.id));

/**
 * Returns the shape location selected as the active report view.
 *
 * @param {object} doc - The report-state document containing the active table.
 * @param {string|null} [kind=null] - Optional shape kind used to filter the active table's shapes.
 * @returns {object|null} The active shape location.
 */
export const activeShapeLocation = (doc, kind = null) => ownShapeLocations(doc)
    .find(location => kind === null || isKind(location.composable, kind)) ?? null;

/**
 * Replaces one located composable operation in the report document.
 *
 * @param {object} doc - The report-state document containing the located operation.
 * @param {object} location - The table and array position of the composable operation to update.
 * @param {object} replacement - The replacement composable operation.
 * @returns {object} The replacement composable's refreshed table and array location.
 * @throws {Error} When the table, index, or original composable kind no longer matches the location.
 *
 * Side effects: replaces the located composable in `doc` with a cloned, kind-normalized definition.
 */
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

// Protocol contract: the built-in UI mode is a predicate over the active composition. Documents
// with several shape composables are preserved without assigning a lossy toolbar mode; the
// server remains responsible for deciding whether they are executable.
/**
 * Returns the unambiguous built-in view mode of the active table.
 *
 * @param {object} doc - The report-state document whose active table is classified.
 * @returns {string} `grid`, `groupBy`, `pivot`, `chart`, or `custom` when no built-in mode is unambiguous.
 */
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

/**
 * Determines whether a composable may represent the requested report view mode.
 *
 * @param {object} doc - The report-state document containing the candidate table.
 * @param {string} id - The candidate table identifier.
 * @param {string} mode - The built-in view mode the table must represent.
 * @returns {boolean} Whether the table has a valid chain and exactly represents the requested mode.
 */
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

/**
 * Returns the shape locations eligible to represent a requested view mode.
 *
 * @param {object} doc - The report-state document whose tables are inspected.
 * @param {string} mode - The built-in view mode to match.
 * @returns {Array<object>} Candidate table records, each with its table and optional shape location.
 */
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

// Invariant: resolve a built-in view without consulting map order. Ambiguity is data the caller
// must surface; it is never reinterpreted as an absent view.
/**
 * Resolves the active table's selected view and any ambiguity among shape candidates.
 *
 * @param {object} doc - The report-state document whose view candidates are resolved.
 * @param {string} mode - The built-in view mode to resolve.
 * @returns {{status: string, candidate: object|null, candidates: Array<object>}} Resolution state and all matching candidates.
 */
export function resolveView(doc, mode) {
    const candidates = viewCandidates(doc, mode);
    const active = candidates.find(candidate => sameColumn(candidate.tableId, doc?.activeTable));
    if (active) return { status: "active", candidate: active, candidates };
    if (candidates.length === 0) return { status: "absent", candidate: null, candidates };
    if (candidates.length === 1) return { status: "available", candidate: candidates[0], candidates };
    return { status: "ambiguous", candidate: null, candidates };
}

/**
 * Rationale: resolve the base input for a newly authored shaped view. The selected ancestry's base
 * wins; otherwise the same explicit unique/ambiguous result as toolbar view selection applies.
 *
 * @param {object} doc - The report-state document whose unshaped definition roots are inspected.
 * @returns {{status: string, candidate: object|null, candidates: Array<object>}} The selected, unique, absent, or ambiguous creation base.
 */
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

/**
 * Allocates the next unused composable-table identifier.
 *
 * @param {object} doc - The report-state document whose table identifiers are reserved.
 * @param {string} prefix - The preferred table identifier and prefix for numeric fallbacks.
 * @returns {string} A case-insensitively unused table identifier.
 */
const nextTableId = (doc, prefix) => {
    const used = new Set(Object.keys(doc.tables ?? {}).map(id => id.toLowerCase()));
    if (!used.has(prefix.toLowerCase())) return prefix;
    let suffix = 2;
    while (used.has(`${prefix}${suffix}`.toLowerCase())) suffix++;
    return `${prefix}${suffix}`;
};

/**
 * Marks one existing shape as the active view and disables conflicting candidates.
 *
 * @param {object} doc - The report-state document whose active table is changed.
 * @param {string} mode - The built-in view mode to activate.
 * @param {string} [tableId=null] - The case-insensitive composable-table identifier.
 * @returns {boolean} True when a matching candidate was found and activated.
 *
 * Side effects: sets `doc.activeTable` on success.
 */
export function selectView(doc, mode, tableId = null) {
    const candidate = tableId
        ? viewCandidates(doc, mode).find(item => sameColumn(item.tableId, tableId))
        : resolveView(doc, mode).candidate;
    if (!candidate) return false;
    doc.activeTable = candidate.tableId;
    return true;
}

/**
 * Creates a shape for a view mode and makes it the active selection.
 *
 * @param {object} doc - The report-state document to extend with a shaped table.
 * @param {string} mode - The non-grid built-in view mode to create.
 * @param {object} shape - The matching group, chart, or pivot composable definition.
 * @param {string} fromTableId - The unshaped definition-root table used as the new table's input.
 * @returns {object} The new shape's table and array location.
 * @throws {Error} When the mode is grid, the source is not an unshaped definition root, or the shape kind does not match the mode.
 *
 * Side effects: inserts a table into `doc.tables` and makes it active.
 */
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

/**
 * Returns the public view mode represented by a shape composable.
 *
 * @param {object|null|undefined} location - A composable location whose shape kind is read.
 * @returns {string} The public view mode, or an empty token when the location has no kind.
 */
export const shapeMode = location => kindMode(location?.composable?.kind);

// Shared identity helpers enforce case-insensitive column and map behavior while retaining the
// original spelling that must be sent back to the server.

/**
 * Determines whether two column identifiers are equal under case-insensitive matching.
 *
 * @param {unknown} left - The left value in the equality or ordering comparison.
 * @param {unknown} right - The right value in the equality or ordering comparison.
 * @returns {boolean} Whether both values are strings equal under case-insensitive comparison.
 */
export const sameColumn = (left, right) => typeof left === "string" && typeof right === "string"
    && left.toLowerCase() === right.toLowerCase();

/**
 * Returns a case-insensitive map value while preserving the stored key spelling.
 *
 * @param {object|null|undefined} map - The keyed object to search.
 * @param {string} name - The key to match case-insensitively.
 * @returns {unknown} The stored value, or undefined when the map has no matching key.
 */
export function lookupValue(map, name) {
    if (!map) return undefined;
    const requested = String(name).toLowerCase();
    const key = Object.keys(map).find(candidate => candidate.toLowerCase() === requested);
    return key === undefined ? undefined : map[key];
}

// Invariant: write or clear a map entry by case-insensitive key. Every case-variant of name is
// removed first, so lookupValue can never resolve a stale duplicate left under different
// casing. Pass undefined to clear the entry.
/**
 * Sets a case-insensitive map entry while preserving an existing key's spelling.
 *
 * @param {object} map - The keyed object to mutate.
 * @param {string} name - The key whose case variants are replaced or removed.
 * @param {unknown} value - The value to store; undefined clears the entry.
 * @returns {void} No value.
 *
 * Side effects: mutates the supplied map.
 */
export function setMapEntry(map, name, value) {
    for (const key of Object.keys(map))
        if (sameColumn(key, name)) delete map[key];
    if (value !== undefined) map[name] = value;
}

/**
 * Returns the next unused identifier with the requested prefix.
 *
 * @param {Set<string>} usedLowercase - The case-folded identifiers that are already reserved.
 * @param {string} prefix - The identifier prefix to combine with a positive integer suffix.
 * @returns {string} The first `${prefix}N` identifier absent from the supplied set.
 */
export function nextFreeId(usedLowercase, prefix) {
    let next = 1;
    while (usedLowercase.has(`${prefix}${next}`)) next++;
    return `${prefix}${next}`;
}

/**
 * Compares text deterministically with a case-insensitive primary ordering.
 *
 * @param {unknown} left - The left value in the equality or ordering comparison.
 * @param {unknown} right - The right value in the equality or ordering comparison.
 * @returns {number} The stable text compare.
 */
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

/**
 * Returns the display order assigned to a highlight scope.
 *
 * @param {object} rule - The highlight rule whose row or cell scope is ranked.
 * @returns {number} The highlight scope order.
 */
const highlightScopeOrder = rule =>
    String(rule?.scope ?? "row").trim().toLowerCase() === "cell" ? 1 : 0;

// Protocol contract: canonical client view of one table's highlight priority set. Missing
// sequences are assigned by stable id into the first unused ten-step slots, matching server
// normalization; document/list position is retained only as a mutation address in `index`.
// Disabled declarations deliberately participate in this normalization so toggling a rule
// cannot reorder the remaining rules. Row rules precede cell rules, and sequence establishes
// precedence within each scope. Consumers which execute rules filter disabled entries only
// afterward.
/**
 * Normalizes highlight rules and assigns their effective priority order.
 *
 * @param {Array<unknown>} rules - Highlight declarations in their stored order.
 * @returns {Array<object>} Records containing each original rule, its stored index, and its explicit or assigned sequence, sorted by execution priority.
 */
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

/**
 * Adds a valid, unused column identifier to the reserved identifier set.
 *
 * @param {Set<string>} ids - The case-folded identifier set to mutate.
 * @param {string|object|null|undefined} candidate - A column name or an object whose `name` may be reserved.
 * @returns {void} No value.
 *
 * Side effects: adds a nonblank candidate to `ids` in lowercase.
 */
const addUsedColumnId = (ids, candidate) => {
    const value = typeof candidate === "string" ? candidate : candidate?.name;
    if (typeof value === "string" && value.trim()) ids.add(value.trim().toLowerCase());
};

// Cache policy: the shared public-column namespace used by client-authored computed columns and
// Group/Pivot metrics. Table schema caches reserve source and generated column names;
// composable scans keep allocation safe even before a refreshed cache arrives. Callers may add
// the definition or dialog input columns which live outside the report document.
/**
 * Collects source and generated column identifiers already used by the report state.
 *
 * @param {object} doc - The report-state document whose schemas and generated declarations are scanned.
 * @param {Array<unknown>} [additionalColumns=[]] - Extra column names or definitions to reserve.
 * @returns {Set<string>} Case-folded identifiers already used by source or generated columns.
 */
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

/**
 * Allocates the next unused identifier for a generated schema column.
 *
 * @param {object} doc - The report-state document whose column namespace is inspected.
 * @param {Array<unknown>} [additionalColumns=[]] - Extra column names or definitions to reserve.
 * @returns {string} The next synthetic column id.
 */
export const nextSyntheticColumnId = (doc, additionalColumns = []) =>
    nextFreeId(usedColumnIds(doc, additionalColumns), "ir");

// Invariant: preserve metric identities by (column, function) while allocating every new metric
// from the same document-wide `irN` namespace as computed columns. Removed identities stay
// reserved during the edit because they are still in `doc`; this prevents a replacement metric
// from silently inheriting state.
/**
 * Assigns stable, collision-free identifiers to generated shape metrics.
 *
 * @param {object} doc - The report-state document whose generated-column namespace is reserved.
 * @param {Array<object>} rows - Metric drafts containing `col` and `fn` but no stable identifier.
 * @param {Array<object>} previous - Previously assigned metric definitions eligible for identity reuse.
 * @param {Array<unknown>} [additionalColumns=[]] - Extra column names or definitions to reserve.
 * @returns {{values: Array<object>, retired: Array<string>}} Assigned metrics and the identifiers of previous metrics no longer represented.
 */
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

/**
 * Deletes map entries accepted by the supplied predicate and reports whether anything changed.
 *
 * @param {object|null|undefined} map - The keyed object whose matching properties are removed.
 * @param {(key: string) => boolean} predicate - Selects keys to remove.
 * @returns {void} No value.
 *
 * Side effects: mutates the supplied map.
 */
const mapDeleteWhere = (map, predicate) => {
    if (!map) return;
    for (const key of Object.keys(map))
        if (predicate(key)) delete map[key];
};

// True when an expression contains the column as an identifier, excluding quoted string
// contents and longer identifiers (ir1 does not match ir10 or 'ir1').
const expressionKeywords = new Set([
    "case", "when", "then", "else", "end", "and", "or", "not", "is", "null", "between",
]);
/**
 * Determines whether a character may begin an expression identifier.
 *
 * @param {string} value - The single character to classify.
 * @returns {boolean} Whether the character may begin an unquoted identifier.
 */
const expressionIdentifierStart = value => /[\p{L}_]/u.test(value);
/**
 * Determines whether a character may continue an expression identifier.
 *
 * @param {string} value - The single character to classify.
 * @returns {boolean} Whether the character may continue an unquoted identifier.
 */
const expressionIdentifierPart = value => /[\p{L}\p{Nd}_$#]/u.test(value);

/**
 * Determines whether an expression references the supplied column identifier.
 *
 * @param {string} expression - The expression text to scan for a column reference.
 * @param {string} column - The column identifier to find case-insensitively.
 * @returns {boolean} Whether the expression contains the column as a bare or backtick-quoted identifier.
 */
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
            // Bare language keywords and call heads are syntax, not column references. Quoted
            // names above remain columns even before `(`.
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

// Dependency invalidation is intentionally conservative. Exact ordinary-rule references are
// pruned, while shaped descendants are removed when their output schema cannot be derived locally.

/**
 * Determines whether any report shape references the supplied column.
 *
 * @param {Array<object>} locations - Shape locations to inspect.
 * @param {string} column - The column identifier to match across dimensions, metrics, and chart axes.
 * @returns {boolean} Whether any supplied shape consumes the column.
 */
const shapesReferenceColumn = (locations, column) => locations.some(location => {
    const shape = location.composable ?? {};
    return (shape.by ?? []).some(name => sameColumn(name, column))
        || (shape.rows ?? []).some(name => sameColumn(name, column))
        || (shape.cols ?? []).some(name => sameColumn(name, column))
        || (shape.values ?? []).some(value => sameColumn(value?.col, column))
        || sameColumn(shape.label, column)
        || sameColumn(shape.value, column);
});

// Invariant: remove a column and every computed column that depends on it from all same-kind
// nodes owned by one table. Packaged editors author the final node of a kind, but foreign
// documents may contain earlier nodes which remain executable and therefore must not retain
// dangling references.
/**
 * Removes report-state references to a retired column and returns affected view modes.
 *
 * @param {object} range - The contiguous composable range to inspect or update.
 * @param {string} column - The retired column whose direct and transitive references must be removed.
 * @returns {Array<string>} The requested column plus computed outputs retired transitively.
 *
 * Side effects: removes references from every supported composable in `range` and clears dependent format properties.
 */
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

        // Invariant: a removed computed rule has an output identity of its own. Its dependants
        // and terminal presentation must retire transitively.
        for (const rule of removedComputed)
            if (!sameColumn(rule.id, current)) remove(rule.id);
    };

    remove(column);
    return retired;
};

/**
 * Returns the case-folded schema column names used for collision checks.
 *
 * @param {object} table - The table whose cached schema is read.
 * @returns {Array<string>} The schema column names.
 */
const schemaColumnNames = table => (table?.schema ?? [])
    .map(column => typeof column === "string" ? column : column?.name)
    .filter(name => typeof name === "string" && name.length > 0);

/**
 * Rationale: remove exported columns from one table and propagate that loss through every descendant.
 * Ordinary descendant rules are pruned exactly. A descendant whose Shape consumes a retired column is
 * removed with its subtree because its output contract can no longer be inferred locally.
 *
 * @param {object} state - The report-state document to prune.
 * @param {string} tableId - The table that originally exported the retired columns.
 * @param {Array<string>} columns - Exported column identifiers to retire.
 * @returns {Array<string>} Built-in view modes removed with invalid descendant shape tables.
 *
 * Side effects: prunes references, deletes invalid descendant tables, and may reset `activeTable` to the source table.
 */
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

            // A Shape which does not consume the retired input is a schema boundary: that input
            // is already absent from its completed export.
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

/**
 * Delete one definition-input computed column and everything exported from it. Ordinary descendants
 * lose exact references; a descendant Shape which consumes a retired column is removed with its
 * subtree. Unrelated roots remain untouched.
 *
 * @param {object} state - The report-state document to mutate.
 * @param {string} column - The definition-input computed column to retire.
 * @returns {Array<string>} Built-in descendant view modes removed because they depended on the column.
 */
export function removeInputComputedColumn(state, column) {
    const source = definitionInputEntry(state);
    return source ? pruneExportedColumns(state, source.id, [column]) : [];
}

/**
 * Delete one computed/public output and propagate its loss through every named table which imports it.
 * Returns built-in descendant modes removed by coarse Shape invalidation so UI callers can report the
 * loss.
 *
 * @param {object} state - The report-state document to mutate.
 * @param {string} column - The computed or public output column to retire.
 * @param {string} [tableId=state?.activeTable] - The case-insensitive composable-table identifier.
 * @returns {Array<string>} Built-in descendant view modes removed because they depended on the column.
 */
export function removeTerminalComputedColumn(state, column, tableId = state?.activeTable) {
    return pruneExportedColumns(state, tableId, [column]);
}

/**
 * After a Group/Pivot edit retires metric ids, drop owner and descendant state which referenced their
 * exported columns.
 *
 * @param {object} state - The report-state document to mutate.
 * @param {string} tableId - The table whose metric outputs changed.
 * @param {Array<string>} retiredIds - The computed-column identifiers removed from the prior definition.
 * @returns {object} The same `state` document after dependency pruning.
 */
export function pruneRetiredMetrics(state, tableId, retiredIds) {
    pruneExportedColumns(state, tableId, retiredIds);
    return state;
}

/**
 * Returns the identifiers of computed columns authored in the report document.
 *
 * @param {object} table - The table whose compute composables are scanned.
 * @returns {Set<string>} Case-folded identifiers authored by compute composables in the table.
 */
const authoredComputedIds = table => new Set((table?.composables ?? [])
    .filter(composable => isKind(composable, "compute"))
    .flatMap(composable => composable?.computed ?? [])
    .map(rule => typeof rule?.id === "string" ? rule.id.toLowerCase() : null)
    .filter(Boolean));

/**
 * Rationale: dynamic Pivot cell ids are intentionally opaque. The previous live schema is therefore
 * the authority for the cells owned by a Pivot: every output other than its row dimensions is a
 * generated cell (or a dependent same-table computation). A column-key change retires all of those
 * ids. Removing a metric does the same coarse cleanup because the public id deliberately does not
 * encode its family.
 *
 * @param {object} state - The report-state document to mutate.
 * @param {string} tableId - The pivot table whose output schema changed.
 * @param {object} previous - The pivot definition before the edit.
 * @param {object} replacement - The replacement pivot definition.
 * @param {Array<string>} [retiredMetricIds=[]] - The metric identifiers removed from the prior shape.
 * @returns {object} The same `state` document after dependency pruning.
 */
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

/**
 * Derives the generated schema columns produced by a chart shape.
 *
 * @param {object} shape - The chart definition whose label and metric output names are derived.
 * @returns {Array<string>} Nonblank generated output column names for the chart.
 */
const chartOutputColumns = shape => {
    const label = shape?.label;
    const metricBase = !shape?.value ? "__count" : shape.fn ? "v0" : shape.value;
    const metric = sameColumn(label, metricBase) ? `${metricBase}_metric` : metricBase;
    return [label, metric].filter(name => typeof name === "string" && name.length > 0);
};

// Invariant: chart output names are stable when an edit changes only presentation or keeps the
// same label/metric identities. Retire only names which disappear.
/**
 * Removes chart-generated column references retired by a shape change.
 *
 * @param {object} state - The report-state document to mutate.
 * @param {string} tableId - The chart table whose output schema changed.
 * @param {object} previous - The chart definition before the edit.
 * @param {object} replacement - The replacement chart definition.
 * @returns {object} The same `state` document after dependency pruning.
 */
export function pruneRetiredChartOutputs(state, tableId, previous, replacement) {
    const retained = chartOutputColumns(replacement);
    const retired = chartOutputColumns(previous)
        .filter(name => !retained.some(candidate => sameColumn(candidate, name)));
    pruneExportedColumns(state, tableId, retired);
    return state;
}

// Scoped search translates a user-entered scalar into the expression language understood by
// the server. Parsing is deliberately limited by column type so invalid locale input is rejected
// before a filter composable is written.

/**
 * Builds a typed filter expression from a column-scoped search value.
 *
 * @param {string} column - The column identifier referenced by the generated expression.
 * @param {string} type - Supported column type: `text`, `number`, `date`, or `bool`.
 * @param {string} rawValue - User-entered value to validate and encode.
 * @param {Element|object|string|null} [context=null] - Locale context used for number, Boolean, and error-message handling.
 * @returns {string} A server expression representing the scoped search.
 * @throws {Error} When the value is blank, invalid for its type, or the type is unsupported.
 */
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

/**
 * Quotes an expression identifier, escaping embedded quote characters.
 *
 * @param {string} value - The string literal value to quote.
 * @returns {string} A single-quoted literal with embedded quotes doubled.
 */
function quote(value) {
    return `'${value.replaceAll("'", "''")}'`;
}

/**
 * Returns a safely quoted identifier for use in a report expression.
 *
 * @param {string} name - The column identifier to encode.
 * @returns {string} The bare identifier when legal and non-keyword, otherwise a backtick-quoted identifier.
 */
function expressionIdentifier(name) {
    const ordinary = /^[A-Za-z_][A-Za-z0-9_$#]*$/.test(name);
    const keyword = /^(CASE|WHEN|THEN|ELSE|END|AND|OR|NOT|IS|NULL|BETWEEN)$/i.test(name);
    return ordinary && !keyword ? name : `\`${name.replaceAll("`", "``")}\``;
}
