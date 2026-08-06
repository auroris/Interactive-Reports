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
