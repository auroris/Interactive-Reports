// CSV presentation for the in-browser demo server. Mirrors the .NET file-download client
// (CsvReportPresentation): labels and format masks follow the same table-chain rules, link
// columns export their text, image columns export their URL, and everything renders through the
// client's exact format-code engine with the invariant English locale so the file reads the same
// as a download from the .NET server.

import { applyMask, formatValue, hasFraction } from "../client/report/render/format.js";

const LOCALE = "en";
const SHAPES = new Set(["group", "pivot", "chart"]);

const same = (a, b) => String(a ?? "").toUpperCase() === String(b ?? "").toUpperCase();
const lookup = (map, name) => {
    if (!map) return undefined;
    const key = Object.keys(map).find(k => same(k, name));
    return key === undefined ? undefined : map[key];
};
const kindOf = composable => String(composable?.kind ?? "").trim().toLowerCase();

/**
 * Walks the active table's ancestry back to the definition.
 *
 * @param {object|null|undefined} document - The resolved report document.
 * @returns {Array<object>} Tables from the definition edge to the active table.
 */
function tableChain(document) {
    const tables = document?.tables ?? {};
    const chain = [];
    const seen = new Set();
    let current = document?.activeTable;
    while (current && !same(current, "definition")) {
        const key = String(current).toUpperCase();
        if (seen.has(key)) break;
        seen.add(key);
        const table = lookup(tables, current);
        if (!table) break;
        chain.push(table);
        if (same(table.from, "definition")) break;
        current = table.from;
    }
    return chain.reverse();
}

/**
 * Finds the source column whose label a generated column inherits.
 *
 * @param {object} table - The table whose shape produced the column.
 * @param {object} column - The schema column.
 * @returns {string|null} The source column name, or `null` when there is none.
 */
function labelSource(table, column) {
    if (column.formatSource) return column.formatSource;
    const shape = [...(table.composables ?? [])].reverse().find(c => SHAPES.has(kindOf(c)));
    if (!shape) return column.name;
    const kind = kindOf(shape);
    if (kind === "group")
        return (shape.values ?? []).find(metric => same(metric.id, column.name))?.col ?? column.name;
    if (kind === "pivot" && column.pivotMetricId)
        return (shape.values ?? []).find(metric => same(metric.id, column.pivotMetricId))?.col ?? null;
    if (kind === "chart" && Array.isArray(table.schema) && table.schema.length > 1
        && same(column.name, table.schema[1].name))
        return shape.value ?? null;
    return column.name;
}

/** Substitutes the source label inside an aggregate label such as `sum(Amount)`. */
function replaceAggregateSource(label, sourceLabel) {
    const text = String(label ?? "");
    const open = text.lastIndexOf("(");
    const close = open < 0 ? -1 : text.indexOf(")", open + 1);
    return close > open ? `${text.slice(0, open + 1)}${sourceLabel}${text.slice(close)}` : text;
}

/** Carries inherited labels across a `from` edge onto the child table's schema. */
function projectLabels(inherited, table) {
    if (!Array.isArray(table.schema) || !table.schema.length) return { ...inherited };
    const result = {};
    for (const column of table.schema) {
        const own = lookup(inherited, column.name);
        if (own !== undefined) { result[column.name] = own; continue; }
        const source = labelSource(table, column);
        const sourceLabel = source ? lookup(inherited, source) : undefined;
        if (sourceLabel !== undefined) result[column.name] = replaceAggregateSource(column.label, sourceLabel);
    }
    return result;
}

/** Carries only scalar masks across a `from` edge, following each column's format lineage. */
function projectFormats(inherited, table) {
    const maskOnly = format => (typeof format?.mask === "string" && format.mask.trim() ? { mask: format.mask } : null);
    if (!Array.isArray(table.schema) || !table.schema.length) {
        const result = {};
        for (const [name, format] of Object.entries(inherited)) {
            const projected = maskOnly(format);
            if (projected) result[name] = projected;
        }
        return result;
    }
    const result = {};
    for (const column of table.schema) {
        const projected = maskOnly(lookup(inherited, column.formatSource ?? column.name));
        if (projected) result[column.name] = projected;
    }
    return result;
}

/**
 * Resolves the effective labels and formats for the active table.
 *
 * @param {object|null|undefined} document - The resolved report document.
 * @param {object} configuredLabels - The definition's column labels.
 * @returns {{labels: object, formats: object}} Effective presentation maps keyed by column name.
 */
function resolvePresentation(document, configuredLabels) {
    const chain = tableChain(document);
    let labels = { ...(configuredLabels ?? {}) };
    let formats = {};
    chain.forEach((table, index) => {
        labels = projectLabels(labels, table);
        if (index > 0) formats = projectFormats(formats, table);
        for (const composable of table.composables ?? []) {
            const kind = kindOf(composable);
            if (kind === "labels" && composable.labels && typeof composable.labels === "object") {
                if (!Object.keys(composable.labels).length) labels = {};
                for (const [name, label] of Object.entries(composable.labels)) setEntry(labels, name, label);
            } else if (kind === "formats" && composable.formats && typeof composable.formats === "object") {
                if (!Object.keys(composable.formats).length) formats = {};
                for (const [name, format] of Object.entries(composable.formats)) setEntry(formats, name, { ...format });
            }
        }
    });
    return { labels, formats };
}

/** Writes a map entry, replacing any key that differs only by case. */
function setEntry(map, name, value) {
    for (const key of Object.keys(map)) if (same(key, name)) delete map[key];
    map[name] = value;
}

/**
 * Decides whether text may be exported as a link or image address.
 *
 * @param {string} value - The trimmed candidate address.
 * @param {boolean} image - Whether the address is for an image.
 * @returns {boolean} `true` for http(s) addresses, mailto and tel links, and relative paths.
 */
function isAllowedUrl(value, image) {
    if (!value || [...value].some(ch => ch.charCodeAt(0) < 32 || ch.charCodeAt(0) === 127)) return false;
    const colon = value.indexOf(":");
    const delimiter = value.search(/[/?#]/);
    if (colon > 0 && (delimiter < 0 || colon < delimiter)) {
        const scheme = value.slice(0, colon);
        if (!/^[a-z][a-z0-9+\-.]*$/i.test(scheme)) return false;
        try { new URL(value); } catch { return false; }
        const lower = scheme.toLowerCase();
        return lower === "http" || lower === "https" || (!image && (lower === "mailto" || lower === "tel"));
    }
    return true;
}

const rawString = value => value === null || value === undefined ? ""
    : typeof value === "boolean" ? (value ? "true" : "false")
    : String(value);

const sourceValue = (row, requested, fallback) => {
    const name = typeof requested === "string" && requested.trim() ? requested.trim() : fallback;
    return lookup(row, name) ?? null;
};

/**
 * Renders one scalar the way a report cell shows it, in the invariant locale.
 *
 * @param {unknown} value - The raw row value.
 * @param {object} column - The column descriptor with its protocol type.
 * @param {boolean} decimalColumn - Whether another value in the column carries a fraction.
 * @param {string|null|undefined} mask - The effective format code.
 * @returns {string|null} The rendered text, or `null` for a null value.
 */
function renderText(value, column, decimalColumn, mask) {
    if (value === null || value === undefined) return null;
    if (column.type === "number" || column.type === "date")
        return formatValue(value, column.type, decimalColumn, mask ?? null, LOCALE);
    return rawString(value);
}

function renderValue(metadata, formats, row, column, value, format, decimalColumn) {
    const renderer = typeof format?.displayAs === "string" ? format.displayAs.trim().toLowerCase() : "";
    if (renderer === "action") return value;

    if (renderer === "image") {
        const text = rawString(sourceValue(row, format.urlColumn, column.name)).trim();
        return isAllowedUrl(text, true) ? text : renderText(value, column, decimalColumn, format.mask);
    }

    if (renderer === "link") {
        const url = rawString(sourceValue(row, format.urlColumn, column.name)).trim();
        const textName = typeof format.textColumn === "string" && format.textColumn.trim()
            ? format.textColumn.trim()
            : column.name;
        const textValue = sourceValue(row, textName, column.name);
        const textColumn = lookup(metadata, textName) ?? { name: textName, label: textName, type: "other" };
        const ownText = same(textColumn.name, column.name);
        const textFormat = ownText ? format : lookup(formats, textColumn.name);
        const text = renderText(textValue, textColumn, ownText ? decimalColumn : hasFraction(textValue), textFormat?.mask);
        if (!text) return isAllowedUrl(url, false) ? url : text;
        return text;
    }

    return typeof format?.mask === "string" && format.mask.trim()
        ? renderText(value, column, decimalColumn, format.mask)
        : value;
}

/**
 * Applies report-document presentation to a query result for CSV output.
 *
 * @param {object} result - The ReportResult produced by `executeReport`.
 * @returns {{columns: Array<object>, rows: Array<object>}} Relabelled visible columns and rendered rows.
 */
export function renderCsvTable(result) {
    const presentation = resolvePresentation(result.document, result.configuredLabels ?? {});
    const metadata = {};
    for (const column of [...(result.availableColumns ?? []), ...(result.columns ?? [])]) setEntry(metadata, column.name, column);
    const columns = (result.columns ?? []).map(column => {
        const label = lookup(presentation.labels, column.name);
        return label === undefined ? column : { ...column, label };
    });
    const decimalColumns = new Set(columns
        .filter(column => column.type === "number"
            && (result.rows ?? []).some(row => hasFraction(lookup(row, column.name))))
        .map(column => column.name.toUpperCase()));
    const rows = (result.rows ?? []).map(row => {
        const rendered = {};
        for (const column of columns) {
            rendered[column.name] = renderValue(
                metadata,
                presentation.formats,
                row,
                column,
                lookup(row, column.name) ?? null,
                lookup(presentation.formats, column.name),
                decimalColumns.has(column.name.toUpperCase()));
        }
        return rendered;
    });
    return { columns, rows };
}

/** Exposed for tests: the mask engine the export renders through, in the export locale. */
export const csvLocale = LOCALE;
export { applyMask as csvMask };
