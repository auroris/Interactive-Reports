// Schema discovery and metadata generation for Interactive Reports using SQLite WASM.

/**
 * Converts a technical identifier into a readable display label (e.g., "ORDER_ID" -> "Order Id").
 * Matches C# ColumnModel.Prettify.
 *
 * @param {string} name
 * @returns {string}
 */
export function prettify(name) {
    if (!name) return "";
    const words = name.replace(/_/g, " ").trim().split(/\s+/).filter(Boolean);
    return words
        .map(w => w.length === 0 ? w : w.charAt(0).toUpperCase() + w.slice(1).toLowerCase())
        .join(" ");
}

/**
 * Maps SQLite column type declarations and sample values to the protocol ColumnKind.
 * Allowed values: "number", "text", "date", "bool", "other".
 *
 * @param {string|null} declaredType
 * @param {unknown} [sampleValue]
 * @param {string} [columnName=""]
 * @returns {"number"|"text"|"date"|"bool"|"other"}
 */
export function mapSqliteType(declaredType, sampleValue, columnName = "") {
    const type = String(declaredType || "").toUpperCase();
    if (type.includes("INT") || type.includes("NUMERIC") || type.includes("DECIMAL")
        || type.includes("REAL") || type.includes("FLOAT") || type.includes("DOUBLE")) {
        return "number";
    }
    if (type.includes("BOOL")) {
        return "bool";
    }
    if (type.includes("DATE") || type.includes("TIME")) {
        return "date";
    }
    if (type.includes("CHAR") || type.includes("CLOB") || type.includes("TEXT") || type.includes("STR")) {
        return "text";
    }
    if (type.includes("BLOB")) {
        return "other";
    }

    // Inspect sample value if declared type was uninformative
    if (sampleValue !== undefined && sampleValue !== null) {
        if (typeof sampleValue === "number") return "number";
        if (typeof sampleValue === "boolean") return "bool";
        if (typeof sampleValue === "string") {
            if (/^\d{4}-\d{2}-\d{2}/.test(sampleValue)) return "date";
            return "text";
        }
    }

    // Heuristic by column name if type is unknown
    const upperName = columnName.toUpperCase();
    if (upperName.endsWith("_ID") || upperName === "ID" || upperName.includes("AMOUNT")
        || upperName.includes("PRICE") || upperName.includes("COUNT") || upperName.includes("QTY")) {
        return "number";
    }
    if (upperName.includes("DATE") || upperName.endsWith("_AT") || upperName.endsWith("_ON")) {
        return "date";
    }

    return "text";
}

export const EXPRESSION_FUNCTIONS = [
    "ABS", "COALESCE", "CONCAT", "CONTAINS", "DATE_TRUNC", "DAY", "ENDS_WITH",
    "IN_LIST", "LENGTH", "LOWER", "MONTH", "NOW", "ROUND", "STARTS_WITH",
    "SUBSTR", "TO_DATE", "TO_STRING", "TRIM", "UPPER", "WILDCARD_MATCH", "YEAR",
];

export const AGGREGATE_FUNCTIONS = {
    text: ["min", "max", "count", "countDistinct"],
    number: ["sum", "avg", "median", "min", "max", "count", "countDistinct"],
    date: ["min", "max", "count", "countDistinct"],
    bool: ["count", "countDistinct"],
    other: ["count", "countDistinct"],
};

export const CHART_AGGREGATE_FUNCTIONS = {
    text: ["count", "countDistinct"],
    number: ["sum", "avg", "median", "min", "max", "count", "countDistinct"],
    date: ["count", "countDistinct"],
    bool: ["count", "countDistinct"],
    other: ["count", "countDistinct"],
};

export const ALL_FEATURES = [
    "search", "columns", "rename", "columnSettings", "filter", "sort",
    "pagination", "controlBreak", "highlight", "aggregate", "compute",
    "groupBy", "pivot", "chart", "savedReports", "download",
];

/**
 * Discovers the report schema by probing the definition SQL against SQLite.
 *
 * @param {import("./db.js").SqliteDatabase} db
 * @param {object} definition - The report definition
 * @returns {object} Discovered InteractiveReportSchema
 */
export function discoverSchema(db, definition) {
    if (!definition || !definition.sql) {
        throw new Error(`Report definition '${definition?.name}' requires a 'sql' query.`);
    }

    // Probe base query columns using WHERE 1 = 0
    const probeSql = `SELECT * FROM (${definition.sql}) AS ir_probe WHERE 1 = 0`;
    const probe = db.query(probeSql);
    const colNames = probe.columns;
    if (!colNames || colNames.length === 0) {
        throw new Error(`Report '${definition.name}': base query returned no columns.`);
    }

    // Try reading one sample row to accurately infer untyped expression column kinds
    let sampleRow = {};
    try {
        const sampleRes = db.query(`SELECT * FROM (${definition.sql}) AS ir_sample LIMIT 1`);
        if (sampleRes.rows.length > 0) {
            sampleRow = sampleRes.rows[0];
        }
    } catch {
        // Sample query failure is non-fatal
    }

    const columns = colNames.map(name => {
        const val = sampleRow[name];
        const kind = mapSqliteType(null, val, name);
        const configuredLabel = definition.columnLabels?.[name];
        return {
            name,
            label: configuredLabel || prettify(name),
            type: kind,
            computed: false,
            formatSource: null,
            pivotMetricId: null,
        };
    });

    // Build default state
    let defaultState = definition.defaultState ? JSON.parse(JSON.stringify(definition.defaultState)) : null;
    if (!defaultState || !defaultState.tables || Object.keys(defaultState.tables).length === 0) {
        const composables = [];
        if (definition.columnLabels && Object.keys(definition.columnLabels).length > 0) {
            composables.push({
                kind: "labels",
                labels: { ...definition.columnLabels },
            });
        }
        defaultState = {
            activeTable: "base",
            tables: {
                base: {
                    from: "definition",
                    schema: null,
                    composables,
                },
            },
        };
    }

    return {
        name: definition.name,
        title: definition.title || prettify(definition.name),
        columns,
        editLink: definition.editLink || null,
        createLink: definition.createLink || null,
        columnOverrides: definition.columns || null,
        defaultState,
        capabilities: {
            expressionFunctions: EXPRESSION_FUNCTIONS,
            aggregateFunctions: AGGREGATE_FUNCTIONS,
            chartAggregateFunctions: CHART_AGGREGATE_FUNCTIONS,
        },
        features: definition.features || ALL_FEATURES,
        limits: {
            defaultPageSize: definition.defaultPageSize || 50,
            maxPageSize: definition.maxPageSize || 1000,
            maxRows: definition.maxRows || 10000,
            maxChartPoints: definition.maxChartPoints || 1000,
        },
        authorization: {
            mayRequestAdministration: false,
        },
    };
}
