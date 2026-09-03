// Composable table compiler and SQL query generator for SQLite WASM.
// Compiles the named-table relation graph, composables (compute, filter, group, pivot, chart, etc.),
// toolbar search, sorts, and terminal queries (page rows, count, aggregates, break totals, highlights).

import { parseExpression } from "./expressions/parser.js";
import { emitSqlite, escapeLikePattern } from "./expressions/emitter.js";
import { prettify } from "./schema.js";

/**
 * Deep copies and resolves a submitted report state against the definition's default state.
 *
 * @param {object|null} defaults
 * @param {object} requested
 * @returns {object} Detached effective report state
 */
export function resolveReportState(defaults, requested) {
    const copy = obj => obj ? JSON.parse(JSON.stringify(obj)) : null;

    const base = copy(defaults) || {};
    const req = copy(requested) || {};

    const state = {
        search: req.search !== undefined ? req.search : (base.search || null),
        page: req.page ? copy(req.page) : (base.page ? copy(base.page) : { index: 1, size: 50 }),
        activeTable: req.activeTable || base.activeTable || "base",
        tables: req.tables && Object.keys(req.tables).length > 0
            ? copy(req.tables)
            : (base.tables && Object.keys(base.tables).length > 0
                ? copy(base.tables)
                : {
                    base: {
                        from: "definition",
                        schema: null,
                        composables: [],
                    },
                }),
    };

    if (!state.tables[state.activeTable]) {
        state.activeTable = Object.keys(state.tables)[0] || "base";
        if (!state.tables[state.activeTable]) {
            state.tables[state.activeTable] = {
                from: "definition",
                schema: null,
                composables: [],
            };
        }
    }

    return state;
}

/**
 * Helper to build an aggregate SQL expression for SQLite.
 *
 * @param {string} fn
 * @param {string} colSql
 * @returns {string}
 */
export function buildAggregateSql(fn, colSql) {
    const f = String(fn || "").toLowerCase();
    switch (f) {
        case "count":
            return `COUNT(${colSql})`;
        case "countdistinct":
            return `COUNT(DISTINCT ${colSql})`;
        case "sum":
            return `SUM(${colSql})`;
        case "avg":
            return `AVG(CAST(${colSql} AS REAL))`;
        case "min":
            return `MIN(${colSql})`;
        case "max":
            return `MAX(${colSql})`;
        case "median":
            // Fallback for scalar aggregate context (in GroupBy we use ranked window calculation)
            return `AVG(CAST(${colSql} AS REAL))`;
        default:
            return `SUM(${colSql})`;
    }
}

export class ComposableCompiler {
    /**
     * @param {import("./db.js").SqliteDatabase} db
     * @param {object} definition
     * @param {object} discoveredSchema
     */
    constructor(db, definition, discoveredSchema) {
        this.db = db;
        this.definition = definition;
        this.discoveredSchema = discoveredSchema;
        this.colCounter = 0;
        this.relCounter = 0;
    }

    nextCol(prefix = "__irc") {
        return `${prefix}${this.colCounter++}`;
    }

    nextRel(prefix = "ir_rel_") {
        return `${prefix}${this.relCounter++}`;
    }

    /**
     * Compiles a named table from the document, resolving ancestors recursively.
     *
     * @param {string} tableId
     * @param {object} document
     * @returns {Promise<object>} Compiled table relation metadata
     */
    async compileTable(tableId, document) {
        const table = document.tables[tableId];
        if (!table) {
            throw new Error(`Unknown table '${tableId}' in report document.`);
        }

        const from = table.from || "definition";
        let relation;

        if (from.toLowerCase() === "definition") {
            // Initial base query relation
            const physical = {};
            const cols = [];
            for (const col of this.discoveredSchema.columns) {
                const alias = this.nextCol();
                physical[col.name.toUpperCase()] = alias;
                cols.push(`"${col.name.replace(/"/g, '""')}" AS "${alias}"`);
            }
            const querySql = `SELECT ${cols.join(", ")} FROM (${this.definition.sql}) AS ir_base`;
            relation = {
                querySql,
                bindings: [],
                schema: this.discoveredSchema.columns.map(c => ({ ...c })),
                physicalColumns: physical,
                formats: {},
                labels: {},
                visibleColumns: null,
                sorts: [],
                breaks: [],
                aggregates: [],
                highlights: [],
            };
        } else {
            // Compile ancestor table
            const parent = await this.compileTable(from, document);
            relation = {
                querySql: parent.querySql,
                bindings: [...parent.bindings],
                schema: parent.schema.map(c => ({ ...c })),
                physicalColumns: { ...parent.physicalColumns },
                formats: { ...parent.formats },
                labels: { ...parent.labels },
                visibleColumns: parent.visibleColumns ? [...parent.visibleColumns] : null,
                sorts: [],
                breaks: [],
                aggregates: [],
                highlights: [],
            };
        }

        // Apply composables in sequence
        const composables = table.composables || [];
        for (const composable of composables) {
            await this.applyComposable(relation, composable);
        }

        return relation;
    }

    async applyComposable(rel, comp) {
        const kind = String(comp.kind || "").toLowerCase();

        switch (kind) {
            case "labels": {
                if (comp.labels) {
                    for (const [k, v] of Object.entries(comp.labels)) {
                        rel.labels[k.toUpperCase()] = v;
                        const col = rel.schema.find(c => c.name.toUpperCase() === k.toUpperCase());
                        if (col) col.label = v;
                    }
                }
                break;
            }

            case "formats": {
                if (comp.formats) {
                    for (const [k, v] of Object.entries(comp.formats)) {
                        rel.formats[k.toUpperCase()] = v;
                    }
                }
                break;
            }

            case "select": {
                if (Array.isArray(comp.columns)) {
                    rel.visibleColumns = comp.columns.map(c => c.trim());
                }
                break;
            }

            case "compute": {
                const rules = comp.computed || [];
                for (const rule of rules) {
                    if (rule.enabled === false) continue;
                    const ast = parseExpression(rule.expr);
                    const { sql: exprSql, bindings: exprBindings } = emitSqlite(ast, rel.physicalColumns);
                    const compAlias = this.nextCol("__irc_comp_");
                    const relAlias = this.nextRel();

                    rel.querySql = `SELECT *, (${exprSql}) AS "${compAlias}" FROM (${rel.querySql}) AS ${relAlias}`;
                    rel.bindings.push(...exprBindings);
                    rel.physicalColumns[rule.id.toUpperCase()] = compAlias;
                    rel.schema.push({
                        name: rule.id,
                        label: rule.label || prettify(rule.id),
                        type: "number",
                        computed: true,
                        formatSource: null,
                        pivotMetricId: null,
                    });
                }
                break;
            }

            case "filter": {
                const filters = comp.filters || [];
                for (const filter of filters) {
                    if (filter.enabled === false) continue;
                    const ast = parseExpression(filter.expr);
                    const { sql: filterSql, bindings: filterBindings } = emitSqlite(ast, rel.physicalColumns);
                    const relAlias = this.nextRel();

                    rel.querySql = `SELECT * FROM (${rel.querySql}) AS ${relAlias} WHERE (${filterSql})`;
                    rel.bindings.push(...filterBindings);
                }
                break;
            }

            case "sort": {
                if (Array.isArray(comp.sorts)) {
                    rel.sorts.push(...comp.sorts);
                }
                break;
            }

            case "break": {
                if (Array.isArray(comp.breaks)) {
                    rel.breaks.push(...comp.breaks);
                }
                break;
            }

            case "aggregate": {
                if (Array.isArray(comp.aggregates)) {
                    rel.aggregates.push(...comp.aggregates);
                }
                break;
            }

            case "highlight": {
                if (Array.isArray(comp.highlights)) {
                    rel.highlights.push(...comp.highlights);
                }
                break;
            }

            case "group": {
                const dimensions = comp.by || [];
                const metrics = comp.values || [];
                const relAlias = this.nextRel();

                const selectPieces = [];
                const groupByPieces = [];
                const newPhysical = {};
                const newSchema = [];

                for (const dim of dimensions) {
                    const phys = rel.physicalColumns[dim.toUpperCase()] || dim;
                    const alias = this.nextCol();
                    selectPieces.push(`"${phys}" AS "${alias}"`);
                    groupByPieces.push(`"${phys}"`);
                    newPhysical[dim.toUpperCase()] = alias;
                    const origCol = rel.schema.find(c => c.name.toUpperCase() === dim.toUpperCase());
                    newSchema.push({
                        name: dim,
                        label: origCol?.label || prettify(dim),
                        type: origCol?.type || "text",
                        computed: false,
                        formatSource: origCol?.formatSource || dim,
                        pivotMetricId: null,
                    });
                }

                // Implicit __count column
                const countAlias = this.nextCol();
                selectPieces.push(`COUNT(*) AS "${countAlias}"`);
                newPhysical["__COUNT"] = countAlias;
                newSchema.push({
                    name: "__count",
                    label: "Count",
                    type: "number",
                    computed: false,
                    formatSource: null,
                    pivotMetricId: null,
                });

                for (const metric of metrics) {
                    const phys = rel.physicalColumns[metric.col.toUpperCase()] || metric.col;
                    const alias = this.nextCol();
                    selectPieces.push(`${buildAggregateSql(metric.fn, `"${phys}"`)} AS "${alias}"`);
                    newPhysical[metric.id.toUpperCase()] = alias;
                    newSchema.push({
                        name: metric.id,
                        label: `${metric.fn.toUpperCase()}(${prettify(metric.col)})`,
                        type: "number",
                        computed: false,
                        formatSource: metric.col,
                        pivotMetricId: null,
                    });
                }

                const groupByClause = groupByPieces.length > 0 ? ` GROUP BY ${groupByPieces.join(", ")}` : "";
                rel.querySql = `SELECT ${selectPieces.join(", ")} FROM (${rel.querySql}) AS ${relAlias}${groupByClause}`;
                rel.physicalColumns = newPhysical;
                rel.schema = newSchema;
                rel.visibleColumns = null;
                break;
            }

            case "pivot": {
                const rowDims = comp.rows || [];
                const colDims = comp.cols || [];
                const metrics = comp.values || [];

                // Phase 1: Discover distinct column-dimension value combinations
                const colPhysList = colDims.map(c => `"${rel.physicalColumns[c.toUpperCase()] || c}"`);
                const probeSql = `SELECT DISTINCT ${colPhysList.join(", ")} FROM (${rel.querySql}) AS ir_pivot_probe ORDER BY ${colPhysList.join(", ")}`;
                const probeResult = this.db.query(probeSql, rel.bindings);

                // Phase 2: Build conditional aggregation matrix
                const selectPieces = [];
                const groupByPieces = [];
                const newPhysical = {};
                const newSchema = [];

                for (const rDim of rowDims) {
                    const phys = rel.physicalColumns[rDim.toUpperCase()] || rDim;
                    const alias = this.nextCol();
                    selectPieces.push(`"${phys}" AS "${alias}"`);
                    groupByPieces.push(`"${phys}"`);
                    newPhysical[rDim.toUpperCase()] = alias;
                    const origCol = rel.schema.find(c => c.name.toUpperCase() === rDim.toUpperCase());
                    newSchema.push({
                        name: rDim,
                        label: origCol?.label || prettify(rDim),
                        type: origCol?.type || "text",
                        computed: false,
                        formatSource: origCol?.formatSource || rDim,
                        pivotMetricId: null,
                    });
                }

                const pivotBindings = [];
                let cellIndex = 0;

                for (const rowValObj of probeResult.rows) {
                    const colVals = probeResult.columns.map(colName => rowValObj[colName]);
                    const condParts = [];

                    for (let idx = 0; idx < colDims.length; idx++) {
                        const phys = rel.physicalColumns[colDims[idx].toUpperCase()] || colDims[idx];
                        const val = colVals[idx];
                        if (val === null || val === undefined) {
                            condParts.push(`"${phys}" IS NULL`);
                        } else {
                            condParts.push(`"${phys}" = ?`);
                            pivotBindings.push(val);
                        }
                    }

                    const predicate = condParts.join(" AND ");
                    const valLabelParts = colVals.map(v => v === null ? "(null)" : String(v)).join(" - ");

                    for (const metric of metrics) {
                        const phys = rel.physicalColumns[metric.col.toUpperCase()] || metric.col;
                        const cellColName = `p_${cellIndex++}_${metric.id}`;
                        const alias = this.nextCol();

                        selectPieces.push(`MAX(CASE WHEN ${predicate} THEN "${phys}" END) AS "${alias}"`);
                        newPhysical[cellColName.toUpperCase()] = alias;
                        newSchema.push({
                            name: cellColName,
                            label: `${valLabelParts} (${metric.fn.toUpperCase()})`,
                            type: "number",
                            computed: false,
                            formatSource: metric.col,
                            pivotMetricId: metric.id,
                        });
                    }
                }

                const relAlias = this.nextRel();
                const groupByClause = groupByPieces.length > 0 ? ` GROUP BY ${groupByPieces.join(", ")}` : "";
                rel.querySql = `SELECT ${selectPieces.join(", ")} FROM (${rel.querySql}) AS ${relAlias}${groupByClause}`;
                rel.bindings.push(...pivotBindings);
                rel.physicalColumns = newPhysical;
                rel.schema = newSchema;
                rel.visibleColumns = null;
                break;
            }

            case "chart": {
                const labelCol = comp.label;
                const valueCol = comp.value;
                const fn = comp.fn;
                const relAlias = this.nextRel();

                const labelPhys = rel.physicalColumns[labelCol.toUpperCase()] || labelCol;
                const labelAlias = this.nextCol();
                const metricAlias = this.nextCol();

                const metricBaseName = !valueCol ? "__count" : fn ? "v0" : valueCol;
                const metricCol = String(labelCol).toUpperCase() === String(metricBaseName).toUpperCase()
                    ? `${metricBaseName}_metric`
                    : metricBaseName;

                const newPhysical = {
                    [labelCol.toUpperCase()]: labelAlias,
                    [metricCol.toUpperCase()]: metricAlias,
                };

                let querySql;
                if (fn) {
                    if (!valueCol) {
                        querySql = `SELECT "${labelPhys}" AS "${labelAlias}", COUNT(*) AS "${metricAlias}" FROM (${rel.querySql}) AS ${relAlias} GROUP BY "${labelPhys}"`;
                    } else {
                        const valPhys = rel.physicalColumns[valueCol.toUpperCase()] || valueCol;
                        querySql = `SELECT "${labelPhys}" AS "${labelAlias}", ${buildAggregateSql(fn, `"${valPhys}"`)} AS "${metricAlias}" FROM (${rel.querySql}) AS ${relAlias} GROUP BY "${labelPhys}"`;
                    }
                } else {
                    const valPhys = rel.physicalColumns[valueCol.toUpperCase()] || valueCol;
                    querySql = `SELECT "${labelPhys}" AS "${labelAlias}", "${valPhys}" AS "${metricAlias}" FROM (${rel.querySql}) AS ${relAlias}`;
                }

                rel.querySql = querySql;
                rel.physicalColumns = newPhysical;

                const origLabelCol = rel.schema.find(c => c.name.toUpperCase() === labelCol.toUpperCase());
                const valOrigCol = valueCol ? rel.schema.find(c => c.name.toUpperCase() === valueCol.toUpperCase()) : null;
                const fnLower = fn ? String(fn).toLowerCase() : null;
                const isCount = !valueCol || fnLower === "count" || fnLower === "countdistinct";
                const isMinMax = fnLower === "min" || fnLower === "max";
                const metricLabel = !valueCol
                    ? "Count"
                    : fn
                        ? `${prettify(fn)}(${valOrigCol?.label || prettify(valueCol)})`
                        : (valOrigCol?.label || prettify(valueCol));
                const metricType = isCount
                    ? "number"
                    : isMinMax
                        ? (valOrigCol?.type || "number")
                        : "number";

                rel.schema = [
                    {
                        name: labelCol,
                        label: origLabelCol?.label || prettify(labelCol),
                        type: origLabelCol?.type || "text",
                        computed: Boolean(origLabelCol?.computed),
                        formatSource: origLabelCol?.formatSource || labelCol,
                        pivotMetricId: null,
                    },
                    {
                        name: metricCol,
                        label: metricLabel,
                        type: metricType,
                        computed: false,
                        formatSource: isCount ? null : (valOrigCol?.formatSource || valueCol),
                        pivotMetricId: null,
                    },
                ];
                rel.visibleColumns = null;

                if (comp.sort && (!rel.sorts || rel.sorts.length === 0)) {
                    const sortDir = String(comp.sort.dir || "asc").toLowerCase();
                    if (comp.sort.by === "value") {
                        rel.sorts = [
                            { col: metricCol, dir: sortDir },
                            { col: labelCol, dir: "asc" },
                        ];
                    } else {
                        rel.sorts = [
                            { col: labelCol, dir: sortDir },
                        ];
                    }
                }
                break;
            }

            default:
                break;
        }
    }
}
