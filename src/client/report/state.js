// Pure report-state transformations. Keeping these outside the custom element makes
// default/clear semantics and expression construction independently testable.

export function normalizeReportState(raw, defaultPageSize = 50, defaults = null) {
    const state = defaults ? structuredClone(defaults) : {};
    for (const [key, value] of Object.entries(raw ? structuredClone(raw) : {}))
        if (value !== null && value !== undefined) state[key] = value;
    state.filters ??= [];
    state.sorts ??= [];
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

const sameColumn = (left, right) => typeof left === "string" && typeof right === "string"
    && left.toLowerCase() === right.toLowerCase();

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

/// Delete one computed column and every state instruction that depends on it. A view
/// that loses a required dimension or chart metric falls back to Grid; retaining a
/// malformed alternate view would make the next query fail validation.
export function removeComputedColumnReferences(state, column) {
    const withoutName = values => Array.isArray(values)
        ? values.filter(value => !sameColumn(value, column))
        : values;
    const withoutColumnRule = values => Array.isArray(values)
        ? values.filter(value => !sameColumn(value?.col, column))
        : values;

    state.computed = (state.computed ?? []).filter(rule => !sameColumn(rule.id, column));
    state.columns = withoutName(state.columns);
    state.sorts = withoutColumnRule(state.sorts);
    state.breaks = withoutName(state.breaks);
    state.aggregates = withoutColumnRule(state.aggregates);
    state.filters = (state.filters ?? []).filter(rule => !expressionReferencesColumn(rule.expr, column));
    state.highlights = (state.highlights ?? []).filter(rule =>
        !sameColumn(rule.col, column) && !expressionReferencesColumn(rule.expr, column));

    if (state.labels) {
        for (const name of Object.keys(state.labels))
            if (sameColumn(name, column)) delete state.labels[name];
    }

    if (state.formats) {
        for (const [name, format] of Object.entries(state.formats)) {
            if (sameColumn(name, column)) {
                delete state.formats[name];
                continue;
            }
            if (sameColumn(format?.urlColumn, column) || sameColumn(format?.textColumn, column)) {
                delete format.displayAs;
                delete format.urlColumn;
                delete format.textColumn;
            }
        }
    }

    const view = state.view;
    if (!view || view.mode === "grid") return state;
    view.values = withoutColumnRule(view.values);
    if (view.mode === "groupBy") {
        view.groupBy = withoutName(view.groupBy);
        if (!view.groupBy?.length) state.view = { mode: "grid" };
    } else if (view.mode === "pivot") {
        view.rows = withoutName(view.rows);
        view.cols = withoutName(view.cols);
        if (!view.rows?.length || !view.cols?.length) state.view = { mode: "grid" };
    } else if (view.mode === "chart"
        && (sameColumn(view.label, column) || sameColumn(view.value, column))) {
        state.view = { mode: "grid" };
    }
    return state;
}

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
