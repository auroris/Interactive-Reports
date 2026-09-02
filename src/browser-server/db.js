// SQLite in-memory database wrapper for sql.js.
// Provides parameterized query execution, statement preparation, and type normalization.

import initSqlJs from "sql.js";

/**
 * Normalizes query parameters for SQLite:
 * - Booleans converted to 1 or 0
 * - Undefined converted to null
 * - Date instances converted to ISO strings
 * - Numbers and strings preserved
 *
 * @param {unknown} val
 * @returns {null|number|string}
 */
export function normalizeParam(val) {
    if (val === undefined || val === null) return null;
    if (typeof val === "boolean") return val ? 1 : 0;
    if (val instanceof Date) return val.toISOString();
    return val;
}

export class SqliteDatabase {
    /**
     * @param {import("sql.js").Database} db
     */
    constructor(db) {
        this.db = db;
    }

    /**
     * Executes a DDL or DML statement with optional parameters.
     *
     * @param {string} sql
     * @param {Array<unknown>} [params=[]]
     */
    run(sql, params = []) {
        const normalized = params.map(normalizeParam);
        this.db.run(sql, normalized);
    }

    /**
     * Executes raw SQL commands (can contain multiple statements).
     *
     * @param {string} sql
     * @returns {Array<{columns: string[], values: Array<Array<unknown>>}>}
     */
    exec(sql) {
        return this.db.exec(sql);
    }

    /**
     * Prepares and executes a SELECT query with positional bindings.
     *
     * @param {string} sql
     * @param {Array<unknown>} [params=[]]
     * @returns {{ columns: string[], rows: Array<Record<string, unknown>> }}
     */
    query(sql, params = []) {
        const normalized = params.map(normalizeParam);
        const stmt = this.db.prepare(sql);
        try {
            if (normalized.length > 0) {
                stmt.bind(normalized);
            }
            const columns = stmt.getColumnNames();
            const rows = [];
            while (stmt.step()) {
                rows.push(stmt.getAsObject());
            }
            return { columns, rows };
        } finally {
            stmt.free();
        }
    }

    /**
     * Executes a query and returns the first column of the first row.
     *
     * @param {string} sql
     * @param {Array<unknown>} [params=[]]
     * @returns {unknown}
     */
    queryScalar(sql, params = []) {
        const { rows, columns } = this.query(sql, params);
        if (rows.length === 0 || columns.length === 0) return null;
        return rows[0][columns[0]];
    }

    /**
     * Registers a custom user-defined function in SQLite.
     *
     * @param {string} name
     * @param {Function} fn
     */
    createFunction(name, fn) {
        if (typeof this.db.create_function === "function") {
            this.db.create_function(name, fn);
        }
    }

    /**
     * Closes the database.
     */
    close() {
        if (this.db) {
            this.db.close();
        }
    }
}

/**
 * Initializes sql.js and returns a new SqliteDatabase instance.
 * Supports both Node (imported initSqlJs) and Browser (<script src="./sql-wasm.js">).
 *
 * @param {object} [options={}] - Passed to initSqlJs({ locateFile, ... })
 * @returns {Promise<SqliteDatabase>}
 */
export async function createSqliteDb(options = {}) {
    const init = (typeof globalThis !== "undefined" && typeof globalThis.initSqlJs === "function")
        ? globalThis.initSqlJs
        : (typeof initSqlJs === "function" ? initSqlJs : initSqlJs?.default);

    if (typeof init !== "function") {
        throw new Error("sql.js is not loaded. Ensure sql.js is installed or sql-wasm.js is loaded via <script>.");
    }

    const isBrowser = typeof window !== "undefined" || typeof document !== "undefined";
    const finalOptions = { ...options };
    if (!finalOptions.locateFile && isBrowser) {
        finalOptions.locateFile = file => {
            try {
                return new URL(file, import.meta.url).href;
            } catch {
                return `./${file}`;
            }
        };
    }

    const SQL = await init(finalOptions);
    const db = new SQL.Database();
    return new SqliteDatabase(db);
}
