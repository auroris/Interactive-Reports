// Pure report-state transformations over the pipeline document. Keeping these
// outside the custom element makes shape guarantees, tail switching, and
// dependency cleanup independently testable.
//
// A document is: { search?, page, pipeline: [stage...], shelf }.
// pipeline[0] is always the source stage; the tail (later stages) IS the view —
// [] grid, [group] groupBy, [pivot] pivot, [chart] chart. The shelf holds
// the parked tails of inactive modes so the toolbar can switch back losslessly.

import { resolveLocale, translate } from "../core/localization.js";

export function normalizeReportState(raw, defaultPageSize = 50, defaults = null) {
    const state = defaults ? structuredClone(defaults) : {};
    for (const [key, value] of Object.entries(raw ? structuredClone(raw) : {}))
        if (value !== null && value !== undefined) state[key] = value;

    // The retired schema-snapshot key: server documents are authoritative and
    // the server validates on query, so the client neither checks nor carries it.
    delete state.schema;
    delete state.v;
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
    if (kinds.includes("pivot")) return "pivot";
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
