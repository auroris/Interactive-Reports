// Pure report-state transformations over the v3 pipeline document. Keeping these
// outside the custom element makes shape guarantees, tail switching, snapshot
// comparison, and dependency cleanup independently testable.
//
// A document is: { v, schema?, search?, page, pipeline: [stage...], shelf }.
// pipeline[0] is always the source stage; the tail (later stages) IS the view —
// [] grid, [group] groupBy, [group, spread] pivot, [chart] chart. The shelf holds
// the parked tails of inactive modes so the toolbar can switch back losslessly.

export function normalizeReportState(raw, defaultPageSize = 50, defaults = null) {
    const state = defaults ? structuredClone(defaults) : {};
    for (const [key, value] of Object.entries(raw ? structuredClone(raw) : {}))
        if (value !== null && value !== undefined) state[key] = value;

    if (!Array.isArray(state.pipeline) || state.pipeline.length === 0)
        state.pipeline = [{}];
    const head = state.pipeline[0];
    head.shape = { ...(head.shape ?? {}), kind: "source" };
    head.layer ??= {};
    head.layer.filters ??= [];
    head.layer.sorts ??= [];
    if (!state.shelf || typeof state.shelf !== "object" || Array.isArray(state.shelf)) state.shelf = {};
    state.page = { index: 1, size: state.page?.size ?? defaultPageSize };
    return state;
}

export function serializeReportState(source, stateVersion) {
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

    return { ...walk(source), v: stateVersion };
}

// --- pipeline access ---------------------------------------------------------

export function sourceLayer(doc) {
    const head = doc.pipeline?.[0];
    if (!head) return {};
    return head.layer ??= {};
}

export function stageOf(doc, kind) {
    return (doc?.pipeline ?? []).find(s => (s.shape?.kind ?? "source") === kind) ?? null;
}

export function stageLayer(stage) {
    if (!stage) return {};
    return stage.layer ??= {};
}

export function tailOf(doc) {
    return (doc?.pipeline ?? []).slice(1);
}

/// The view mode is derived from the tail, never stored.
export function modeOf(doc) {
    const kinds = tailOf(doc).map(s => s.shape?.kind);
    if (kinds.includes("chart")) return "chart";
    if (kinds.includes("spread")) return "pivot";
    if (kinds.includes("group")) return "groupBy";
    return "grid";
}

/// The tail configured for a mode: the live pipeline tail when that mode is
/// active, else the shelved copy. Null when the mode was never configured.
export function configuredTail(doc, mode) {
    if (modeOf(doc) === mode) {
        const tail = tailOf(doc);
        return tail.length ? tail : null;
    }
    const shelved = doc?.shelf?.[mode];
    return Array.isArray(shelved) && shelved.length ? shelved : null;
}

/// Switch the pipeline's tail. The current tail is parked on the shelf under its
/// derived mode; the new tail comes from the argument (a dialog authored it) or
/// from the shelf. Shelf entries hold complete stages — shape AND layer — so
/// switching away and back loses nothing.
export function activateTail(doc, mode, tail = null) {
    const current = modeOf(doc);
    if (current !== "grid" && current !== mode)
        (doc.shelf ??= {})[current] = tailOf(doc);

    doc.pipeline = [doc.pipeline[0]];
    if (mode !== "grid") {
        const stages = tail ?? doc.shelf?.[mode];
        if (Array.isArray(stages) && stages.length) {
            doc.pipeline.push(...structuredClone(stages));
            if (doc.shelf) delete doc.shelf[mode];
        }
    }
    return doc;
}

/// The row dimensions of an active pivot: group.by minus spread.cols, in by order.
export function pivotRowDims(doc) {
    const by = stageOf(doc, "group")?.shape?.by ?? [];
    const cols = (stageOf(doc, "spread")?.shape?.cols ?? []).map(c => String(c).toLowerCase());
    return by.filter(name => !cols.includes(String(name).toLowerCase()));
}

// --- schema snapshot ---------------------------------------------------------

/// The recorded discovered-schema map a document is stamped with on save.
export function schemaSnapshot(columns) {
    const map = {};
    for (const column of columns ?? []) map[column.name] = column.type;
    return map;
}

/// Compare a document's recorded snapshot against the live schema columns.
/// Mismatch = a recorded column is missing or retyped; pure additions pass.
/// Returns a list of human-readable differences, or null when the check passes
/// (including when the document never recorded a snapshot).
export function schemaMismatch(recorded, liveColumns) {
    if (!recorded || typeof recorded !== "object" || Array.isArray(recorded)) return null;
    const live = new Map((liveColumns ?? []).map(c => [String(c.name).toLowerCase(), String(c.type)]));
    const problems = [];
    for (const [name, type] of Object.entries(recorded)) {
        const liveType = live.get(String(name).toLowerCase());
        if (liveType === undefined) problems.push(`${name} was removed`);
        else if (liveType.toLowerCase() !== String(type).toLowerCase())
            problems.push(`${name} changed from ${type} to ${liveType}`);
    }
    return problems.length ? problems : null;
}

// --- shared helpers ----------------------------------------------------------

export const sameColumn = (left, right) => typeof left === "string" && typeof right === "string"
    && left.toLowerCase() === right.toLowerCase();

const mapDeleteWhere = (map, predicate) => {
    if (!map) return;
    for (const key of Object.keys(map))
        if (predicate(key)) delete map[key];
};

/// True when an expression contains the column as an identifier, excluding quoted
/// string contents and longer identifiers (c1 does not match c10 or 'c1').
export function expressionReferencesColumn(expression, column) {
    const source = String(expression ?? "");
    const target = String(column ?? "").toLowerCase();
    let quoted = false;
    for (let index = 0; index < source.length;) {
        if (source[index] === "'") {
            if (quoted && source[index + 1] === "'") { index += 2; continue; }
            quoted = !quoted;
            index++;
            continue;
        }
        if (!quoted && /[A-Za-z_]/.test(source[index])) {
            let end = index + 1;
            while (end < source.length && /[A-Za-z0-9_]/.test(source[end])) end++;
            if (source.slice(index, end).toLowerCase() === target) return true;
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
        || (shape.values ?? []).some(value => sameColumn(value?.col, column))
        || sameColumn(shape.label, column)
        || sameColumn(shape.value, column);
});

/// Delete one SOURCE computed column and everything that depends on it. Within
/// the source layer, references are stripped precisely (today's behavior); any
/// pipeline tail or shelved tail that consumes the column — as a dim, a metric
/// source, or a chart column — is deleted whole (T0 coarse invalidation).
/// Returns the modes whose configurations were dropped so callers can say so.
export function removeSourceComputedColumn(state, column) {
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

    const dropped = [];
    const tail = tailOf(state);
    if (tail.length && tailReferencesColumn(tail, column)) {
        dropped.push(modeOf(state));
        state.pipeline = [state.pipeline[0]];
    }
    for (const [mode, stages] of Object.entries(state.shelf ?? {})) {
        if (Array.isArray(stages) && tailReferencesColumn(stages, column)) {
            dropped.push(mode);
            delete state.shelf[mode];
        }
    }
    return dropped;
}

/// Delete one GROUP-layer computed column and its references within that stage —
/// sorts, highlights, column selection, presentation maps — plus any spread-cell
/// presentation keyed to its cell family ("{id}@…").
export function removeStageComputedColumn(state, stage, column) {
    const layer = stageLayer(stage);
    layer.computed = (layer.computed ?? []).filter(rule =>
        !sameColumn(rule.id, column) && !expressionReferencesColumn(rule.expr, column));
    layer.sorts = (layer.sorts ?? []).filter(rule => !sameColumn(rule.col, column));
    layer.highlights = (layer.highlights ?? []).filter(rule =>
        !sameColumn(rule.col, column) && !expressionReferencesColumn(rule.expr, column));
    if (Array.isArray(layer.columns))
        layer.columns = layer.columns.filter(name => !sameColumn(name, column));
    mapDeleteWhere(layer.labels, name => sameColumn(name, column));
    mapDeleteWhere(layer.formats, name => sameColumn(name, column));

    const prefix = `${String(column).toLowerCase()}@`;
    const spread = stageOf(state, "spread");
    if (spread?.layer) {
        mapDeleteWhere(spread.layer.labels, name => name.toLowerCase().startsWith(prefix));
        mapDeleteWhere(spread.layer.formats, name => name.toLowerCase().startsWith(prefix));
    }
    return state;
}

/// After a Group By / Pivot dialog edit retires metric ids, drop the stage-layer
/// state that referenced them (the same coarse rule as computed-column removal).
export function pruneRetiredMetrics(state, stage, retiredIds) {
    for (const id of retiredIds) removeStageComputedColumn(state, stage, id);
    return state;
}

// --- scoped search -----------------------------------------------------------

export function scopedSearchExpression(column, type, rawValue) {
    const value = rawValue.trim();
    if (!value) throw new Error("Enter a search value");

    switch (type) {
        case "text":
            return `CONTAINS(${column}, ${quote(value)})`;
        case "number":
            if (!/^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$/.test(value))
                throw new Error(`'${value}' is not a number`);
            return `${column} = ${value}`;
        case "date":
            if (!/^\d{4}-\d{2}-\d{2}$/.test(value))
                throw new Error(`'${value}' is not an ISO date (YYYY-MM-DD)`);
            return `${column} = TO_DATE(${quote(value)})`;
        case "bool": {
            const normalized = value.toLowerCase();
            if (normalized === "true" || normalized === "1") return column;
            if (normalized === "false" || normalized === "0") return `NOT ${column}`;
            throw new Error(`'${value}' is not true or false`);
        }
        default:
            throw new Error(`Column '${column}' does not support scoped search`);
    }
}

function quote(value) {
    return `'${value.replaceAll("'", "''")}'`;
}
