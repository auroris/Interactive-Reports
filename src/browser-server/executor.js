// Executes compiled report queries on SQLite WASM, evaluates highlights,
// computes subtotals and aggregates, shapes ReportResult, and handles LOV / CSV export.

import { ComposableCompiler, resolveReportState, buildAggregateSql } from "./compiler.js";
import { escapeLikePattern, emitSqlite } from "./expressions/emitter.js";
import { parseExpression } from "./expressions/parser.js";

/**
 * Executes a full interactive report query against SQLite WASM.
 *
 * @param {import("./db.js").SqliteDatabase} db
 * @param {object} definition
 * @param {object} requestedState
 * @param {object} discoveredSchema
 * @returns {Promise<object>} ReportResult matching the protocol contract
 */
export async function executeReport(db, definition, requestedState, discoveredSchema) {
    const startTime = Date.now();
    const document = resolveReportState(definition.defaultState, requestedState);
    const activeTable = document.activeTable || "base";

    const compiler = new ComposableCompiler(db, definition, discoveredSchema);
    const relation = await compiler.compileTable(activeTable, document);

    // 1. Toolbar Search
    const searched = applyToolbarSearch(relation, document.search);
    const searchableSql = searched.querySql;
    const searchBindings = searched.searchBindings;
    const currentBindings = searched.bindings;

    // 2. Visible vs Available Columns
    const availableColumns = relation.schema.map(c => ({
        name: c.name,
        label: relation.labels[c.name.toUpperCase()] || c.label,
        type: c.type,
        computed: Boolean(c.computed),
        formatSource: c.formatSource || null,
        pivotMetricId: c.pivotMetricId || null,
    }));

    let visibleColumnNames = relation.visibleColumns;
    if (!visibleColumnNames || visibleColumnNames.length === 0) {
        visibleColumnNames = availableColumns.map(c => c.name);
    }
    const columns = availableColumns.filter(c =>
        visibleColumnNames.some(vc => vc.toUpperCase() === c.name.toUpperCase())
    );

    // 3. Highlight markers
    const highlightMarkers = [];
    const highlightBindings = [];
    const activeHighlights = relation.highlights.filter(h => h.enabled !== false && h.expr);

    activeHighlights.forEach((hl, idx) => {
        try {
            const ast = parseExpression(hl.expr);
            const { sql, bindings } = emitSqlite(ast, relation.physicalColumns);
            const markerName = `__irh_${idx}`;
            highlightMarkers.push(`(CASE WHEN ${sql} THEN 1 ELSE 0 END) AS "${markerName}"`);
            highlightBindings.push(...bindings);
        } catch {
            // Non-fatal highlight parse failure
        }
    });

    // 4. Sorts
    let orderClause = "";
    if (relation.sorts && relation.sorts.length > 0) {
        const sortPieces = relation.sorts.map(s => {
            const phys = relation.physicalColumns[s.col.toUpperCase()] || s.col;
            const dir = String(s.dir || "asc").toUpperCase();
            const nulls = s.nulls ? ` NULLS ${String(s.nulls).toUpperCase()}` : "";
            return `"${phys}" ${dir}${nulls}`;
        });
        orderClause = ` ORDER BY ${sortPieces.join(", ")}`;
    }

    // 5. Paging
    const pageIndex = Math.max(1, document.page?.index || 1);
    const pageSize = document.page?.size !== undefined ? document.page.size : 50;
    const pageAll = pageSize === 0;

    let limitClause = "";
    const hasBreaks = relation.breaks && relation.breaks.length > 0;
    const fetchLimit = !pageAll && hasBreaks ? pageSize + 1 : pageSize;

    if (pageAll) {
        const maxRows = definition.maxRows || 10000;
        limitClause = ` LIMIT ${maxRows}`;
    } else {
        const offset = (pageIndex - 1) * pageSize;
        limitClause = ` LIMIT ${fetchLimit} OFFSET ${offset}`;
    }

    // 6. Projections for main page rows. Link and image renderer inputs (URL and text columns)
    // ride along as hidden values so the grid and the CSV export can render them, matching the
    // .NET server's hidden projections.
    const projected = [...columns];
    for (const c of columns) {
        const format = relation.formats[c.name.toUpperCase()];
        const renderer = String(format?.displayAs ?? "").trim().toLowerCase();
        if (renderer !== "link" && renderer !== "image") continue;
        for (const source of [format.urlColumn, renderer === "link" ? format.textColumn : null]) {
            if (typeof source !== "string" || !source.trim()) continue;
            const column = availableColumns.find(a => a.name.toUpperCase() === source.trim().toUpperCase());
            if (column && !projected.some(p => p.name.toUpperCase() === column.name.toUpperCase()))
                projected.push(column);
        }
    }
    const selectColPieces = projected.map(c => {
        const phys = relation.physicalColumns[c.name.toUpperCase()] || c.name;
        return `"${phys}" AS "${c.name}"`;
    });

    const allSelectPieces = [...selectColPieces, ...highlightMarkers];
    const pageSql = `SELECT ${allSelectPieces.join(", ")} FROM (${searchableSql}) AS ir_page${orderClause}${limitClause}`;
    const pageParams = [...highlightBindings, ...currentBindings];

    const pageResult = db.query(pageSql, pageParams);
    let rows = pageResult.rows;

    let breakContinues = false;
    if (!pageAll && hasBreaks && rows.length > pageSize) {
        breakContinues = true;
        rows = rows.slice(0, pageSize);
    }

    // 7. Total Rows Count
    const countSql = `SELECT COUNT(*) AS total_count FROM (${searchableSql}) AS ir_count`;
    const countResult = db.query(countSql, currentBindings);
    const totalRows = Number(countResult.rows[0]?.total_count ?? rows.length);

    // 8. Footer Aggregates
    const aggregates = {};
    if (relation.aggregates && relation.aggregates.length > 0) {
        const aggSelects = [];
        const aggMetadata = [];

        for (const agg of relation.aggregates) {
            const col = relation.schema.find(c => c.name.toUpperCase() === agg.col.toUpperCase());
            if (!col) continue;
            const phys = relation.physicalColumns[col.name.toUpperCase()] || col.name;
            const alias = `agg_${aggMetadata.length}`;
            aggSelects.push(`${buildAggregateSql(agg.fn, `"${phys}"`)} AS "${alias}"`);
            aggMetadata.push({ col: col.name, fn: String(agg.fn).toLowerCase(), alias });
        }

        if (aggSelects.length > 0) {
            const aggSql = `SELECT ${aggSelects.join(", ")} FROM (${searchableSql}) AS ir_footer`;
            const aggResult = db.query(aggSql, currentBindings);
            if (aggResult.rows.length > 0) {
                const aggRow = aggResult.rows[0];
                for (const meta of aggMetadata) {
                    aggregates[meta.col] ??= {};
                    aggregates[meta.col][meta.fn] = aggRow[meta.alias];
                }
            }
        }
    }

    // 9. Control Break Subtotals
    const breakTotals = [];
    if (hasBreaks) {
        const breakCols = relation.breaks.map(b => {
            const col = relation.schema.find(c => c.name.toUpperCase() === b.toUpperCase());
            return {
                name: col ? col.name : b,
                phys: relation.physicalColumns[(col ? col.name : b).toUpperCase()] || b,
            };
        });

        const breakSelects = breakCols.map(b => `"${b.phys}" AS "${b.name}"`);
        breakSelects.push('COUNT(*) AS "__break_rows"');

        const breakAggMeta = [];
        if (relation.aggregates) {
            for (const agg of relation.aggregates) {
                const col = relation.schema.find(c => c.name.toUpperCase() === agg.col.toUpperCase());
                if (!col) continue;
                const phys = relation.physicalColumns[col.name.toUpperCase()] || col.name;
                const alias = `brk_agg_${breakAggMeta.length}`;
                breakSelects.push(`${buildAggregateSql(agg.fn, `"${phys}"`)} AS "${alias}"`);
                breakAggMeta.push({ col: col.name, fn: String(agg.fn).toLowerCase(), alias });
            }
        }

        const groupPieces = breakCols.map(b => `"${b.phys}"`);
        const breakSql = `SELECT ${breakSelects.join(", ")} FROM (${searchableSql}) AS ir_breaks GROUP BY ${groupPieces.join(", ")}`;
        const breakResult = db.query(breakSql, currentBindings);

        for (const row of breakResult.rows) {
            const key = {};
            for (const b of breakCols) {
                key[b.name] = row[b.name];
            }
            const groupAggs = {};
            for (const meta of breakAggMeta) {
                groupAggs[meta.col] ??= {};
                groupAggs[meta.col][meta.fn] = row[meta.alias];
            }
            breakTotals.push({
                key,
                rows: Number(row.__break_rows || 0),
                aggregates: groupAggs,
            });
        }
    }

    // 10. Highlights evaluation on returned rows
    const highlights = [];
    const orderedHighlights = [...activeHighlights].sort((a, b) => {
        const scopeA = a.scope === "cell" ? 1 : 0;
        const scopeB = b.scope === "cell" ? 1 : 0;
        if (scopeA !== scopeB) return scopeA - scopeB;
        return (a.sequence || 0) - (b.sequence || 0);
    });

    for (let r = 0; r < rows.length; r++) {
        const row = rows[r];
        for (const hl of orderedHighlights) {
            const idx = activeHighlights.indexOf(hl);
            const markerName = `__irh_${idx}`;
            if (row[markerName] === 1 || row[markerName] === true) {
                highlights.push({
                    row: r,
                    id: hl.id,
                    col: hl.scope === "cell" ? hl.col : null,
                });
            }
        }
        // Clean up private highlight markers from the returned public row object
        for (let idx = 0; idx < activeHighlights.length; idx++) {
            delete row[`__irh_${idx}`];
        }
    }

    // 11. Refresh cached schema in document
    if (document.tables && document.tables[activeTable]) {
        document.tables[activeTable].schema = availableColumns;
    }

    const elapsedMs = Date.now() - startTime;

    return {
        document,
        columns,
        availableColumns,
        configuredLabels: definition.columnLabels || {},
        rows,
        page: { index: pageIndex, size: pageSize },
        totalRows,
        aggregates,
        breakTotals,
        breakContinues,
        highlights,
        ignored: [],
        elapsedMs,
    };
}

/**
 * Wraps a compiled relation in the toolbar text search, as the .NET compiler does when it
 * completes the active table for a request. A blank search, or a relation without text
 * columns, leaves the relation unchanged.
 *
 * @param {{querySql: string, bindings: Array<unknown>, schema: Array<object>, physicalColumns: object}} relation
 * @param {string|null|undefined} search - The document's toolbar search text.
 * @returns {{querySql: string, bindings: Array<unknown>, searchBindings: Array<unknown>}} The searchable SQL, its complete bindings, and the search-only bindings.
 */
export function applyToolbarSearch(relation, search) {
    const searchBindings = [];
    let querySql = relation.querySql;
    if (typeof search === "string" && search.trim()) {
        const pattern = `%${escapeLikePattern(search.trim()).toLowerCase()}%`;
        const textCols = relation.schema.filter(c => c.type === "text");
        if (textCols.length > 0) {
            const orParts = [];
            for (const c of textCols) {
                const phys = relation.physicalColumns[c.name.toUpperCase()] || c.name;
                orParts.push(`(LOWER("${phys}") LIKE ? ESCAPE '\\')`);
                searchBindings.push(pattern);
            }
            querySql = `SELECT * FROM (${querySql}) AS ir_search WHERE (${orParts.join(" OR ")})`;
        }
    }
    return { querySql, bindings: [...relation.bindings, ...searchBindings], searchBindings };
}

/** The hard upper bound on distinct values one LOV response carries; mirrors ReportExecutor.MaxLovItems. */
export const MAX_LOV_ITEMS = 50;

/** The longest LOV search text accepted; mirrors the .NET request validation. */
const MAX_LOV_SEARCH_LENGTH = 200;

/**
 * Executes a List of Values (LOV) distinct query for one column of the active table.
 *
 * Mirrors ReportExecutor.Lov: the complete current document is compiled first so its filters,
 * computed columns, toolbar search, and table ancestry participate; `table` must name that
 * document's active table; NULL is an ordinary distinct value; the optional search is a
 * case-insensitive substring of the value's text form; and at most MAX_LOV_ITEMS values are
 * returned together with a truncation flag.
 *
 * @param {import("./db.js").SqliteDatabase} db
 * @param {object} definition
 * @param {object} request - { document, table, column, search }
 * @param {object} discoveredSchema
 * @returns {Promise<{table: string, column: string, type: string, items: Array<unknown>, truncated: boolean}>} ReportLovResult
 */
export async function executeLov(db, definition, request, discoveredSchema) {
    if (!request || !request.document) {
        throw new Error("The current report document is required for LOV.");
    }

    const document = resolveReportState(definition.defaultState, request.document);
    const activeTable = document.activeTable;

    const requestedTable = typeof request.table === "string" ? request.table.trim() : "";
    if (!requestedTable) {
        throw new Error("The current active table is required for LOV.");
    }
    if (requestedTable.toUpperCase() !== String(activeTable).toUpperCase()) {
        throw new Error("The LOV table must identify the submitted document's active table.");
    }

    const reqCol = typeof request.column === "string" ? request.column.trim() : "";
    if (!reqCol) {
        throw new Error("One current-table column is required for LOV.");
    }

    const search = request.search === null || request.search === undefined ? "" : String(request.search);
    if (search.length > MAX_LOV_SEARCH_LENGTH) {
        throw new Error(`LOV search cannot exceed ${MAX_LOV_SEARCH_LENGTH} characters.`);
    }

    const compiler = new ComposableCompiler(db, definition, discoveredSchema);
    const relation = await compiler.compileTable(activeTable, document);

    const targetCol = relation.schema.find(c => c.name.toUpperCase() === reqCol.toUpperCase());
    if (!targetCol) {
        throw new Error(`Unknown active-table column '${reqCol}'.`);
    }

    const phys = relation.physicalColumns[targetCol.name.toUpperCase()] || targetCol.name;
    const searched = applyToolbarSearch(relation, document.search);
    const lovBindings = [...searched.bindings];

    // NULL is a legitimate distinct value (the client renders it as its own choice), so there is
    // no IS NOT NULL clause. The search compares the text form, as the .NET dialects do.
    let whereClause = "";
    if (search) {
        whereClause = `WHERE LOWER(CAST("${phys}" AS TEXT)) LIKE ? ESCAPE '\\'`;
        lovBindings.push(`%${escapeLikePattern(search).toLowerCase()}%`);
    }

    const lovSql = `SELECT DISTINCT "${phys}" AS val FROM (${searched.querySql}) AS ir_lov ${whereClause} ORDER BY "${phys}" LIMIT ${MAX_LOV_ITEMS + 1}`;
    const result = db.query(lovSql, lovBindings);

    const items = result.rows.map(r => r.val === undefined ? null : r.val);
    const truncated = items.length > MAX_LOV_ITEMS;

    return {
        table: activeTable,
        column: targetCol.name,
        type: targetCol.type,
        items: truncated ? items.slice(0, MAX_LOV_ITEMS) : items,
        truncated,
    };
}

/**
 * Generates CSV content with a UTF-8 BOM from rows and columns.
 *
 * @param {Array<object>} rows
 * @param {Array<object>} columns
 * @returns {string}
 */
export function exportCsv(rows, columns) {
    const BOM = "\uFEFF";
    const escapeCsv = val => {
        if (val === null || val === undefined) return "";
        const s = String(val);
        if (s.includes(",") || s.includes('"') || s.includes("\n") || s.includes("\r")) {
            return `"${s.replace(/"/g, '""')}"`;
        }
        return s;
    };

    const header = columns.map(c => escapeCsv(c.label || c.name)).join(",");
    const lines = [header];

    for (const row of rows) {
        const line = columns.map(c => escapeCsv(row[c.name])).join(",");
        lines.push(line);
    }

    return BOM + lines.join("\r\n") + "\r\n";
}
