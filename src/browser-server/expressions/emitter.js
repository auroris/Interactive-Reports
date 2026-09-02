// Lowers portable expression AST into SQLite SQL syntax and positional parameter bindings.
// Ported and specialized for SQLite from InteractiveReport.Core.Expressions.ExprEmitter.

import { ExprError } from "./parser.js";

/**
 * Escapes characters with special meaning in SQLite LIKE patterns (%, _, \).
 *
 * @param {string} text
 * @returns {string}
 */
export function escapeLikePattern(text) {
    return String(text)
        .replace(/\\/g, "\\\\")
        .replace(/%/g, "\\%")
        .replace(/_/g, "\\_");
}

/**
 * Converts a portable date-format mask to a SQLite strftime mask.
 * E.g., YYYY-MM-DD -> %Y-%m-%d
 *
 * @param {string} format
 * @returns {string}
 */
export function convertDateFormat(format) {
    if (!format) return "%Y-%m-%d";
    return format
        .replace(/YYYY/g, "%Y")
        .replace(/YY/g, "%y")
        .replace(/MM/g, "%m")
        .replace(/DD/g, "%d")
        .replace(/HH24/g, "%H")
        .replace(/HH/g, "%H")
        .replace(/MI/g, "%M")
        .replace(/SS/g, "%S");
}

export class SqliteExprEmitter {
    /**
     * @param {Record<string, string>} [physicalColumns=null] - Mapping of uppercase logical column names to physical SQL aliases
     */
    constructor(physicalColumns = null) {
        this.physical = physicalColumns || {};
        this.bindings = [];
    }

    quoteIdent(name) {
        // Resolve mapped physical identifier or quote original column name
        const upper = name.toUpperCase();
        const physical = this.physical[upper] ?? this.physical[name] ?? name;
        return `"${physical.replace(/"/g, '""')}"`;
    }

    emit(ast) {
        if (!ast) return "NULL";

        switch (ast.type) {
            case "number": {
                this.bindings.push(ast.value);
                return "?";
            }
            case "string": {
                this.bindings.push(ast.value);
                return "?";
            }
            case "null": {
                return "NULL";
            }
            case "col": {
                return this.quoteIdent(ast.name);
            }
            case "unary": {
                if (ast.op === "NOT") {
                    return `(NOT (${this.emit(ast.operand)}))`;
                }
                if (ast.op === "-") {
                    return `(-(${this.emit(ast.operand)}))`;
                }
                throw new ExprError(`unsupported unary operator '${ast.op}'`);
            }
            case "binary": {
                const left = this.emit(ast.left);
                const right = this.emit(ast.right);
                const op = ast.op.toUpperCase();
                return `(${left} ${op} ${right})`;
            }
            case "null_test": {
                const operand = this.emit(ast.operand);
                return ast.negated
                    ? `(${operand} IS NOT NULL)`
                    : `(${operand} IS NULL)`;
            }
            case "between": {
                const operand = this.emit(ast.operand);
                const lower = this.emit(ast.lower);
                const upper = this.emit(ast.upper);
                return `(${operand} BETWEEN ${lower} AND ${upper})`;
            }
            case "case": {
                const parts = ["(CASE"];
                if (ast.operand) {
                    parts.push(this.emit(ast.operand));
                }
                for (const when of ast.whens) {
                    parts.push(`WHEN ${this.emit(when.when)} THEN ${this.emit(when.then)}`);
                }
                if (ast.else) {
                    parts.push(`ELSE ${this.emit(ast.else)}`);
                }
                parts.push("END)");
                return parts.join(" ");
            }
            case "call": {
                return this.emitFunction(ast.name, ast.args);
            }
            default:
                throw new ExprError(`unsupported AST node type '${ast.type}'`);
        }
    }

    emitFunction(name, args) {
        const fn = name.toUpperCase();

        switch (fn) {
            case "UPPER":
            case "LOWER":
            case "TRIM":
            case "LENGTH":
            case "ABS": {
                if (args.length !== 1) throw new ExprError(`${fn} takes 1 argument, got ${args.length}`);
                return `${fn}(${this.emit(args[0])})`;
            }
            case "SUBSTR": {
                if (args.length < 2 || args.length > 3) throw new ExprError(`SUBSTR takes 2 or 3 arguments, got ${args.length}`);
                const sqlArgs = args.map(a => this.emit(a));
                return `SUBSTR(${sqlArgs.join(", ")})`;
            }
            case "ROUND": {
                if (args.length < 1 || args.length > 2) throw new ExprError(`ROUND takes 1 or 2 arguments, got ${args.length}`);
                const sqlArgs = args.map(a => this.emit(a));
                return `ROUND(${sqlArgs.join(", ")})`;
            }
            case "CONCAT": {
                if (args.length < 2) throw new ExprError(`CONCAT takes at least 2 arguments, got ${args.length}`);
                // Treat NULL as empty string
                const items = args.map(a => `COALESCE(${this.emit(a)}, '')`);
                return `(${items.join(" || ")})`;
            }
            case "COALESCE": {
                if (args.length < 2) throw new ExprError(`COALESCE takes at least 2 arguments, got ${args.length}`);
                return `COALESCE(${args.map(a => this.emit(a)).join(", ")})`;
            }
            case "CONTAINS": {
                if (args.length !== 2) throw new ExprError("CONTAINS takes 2 arguments");
                return this.emitTextMatch(args[0], args[1], true, true);
            }
            case "STARTS_WITH": {
                if (args.length !== 2) throw new ExprError("STARTS_WITH takes 2 arguments");
                return this.emitTextMatch(args[0], args[1], false, true);
            }
            case "ENDS_WITH": {
                if (args.length !== 2) throw new ExprError("ENDS_WITH takes 2 arguments");
                return this.emitTextMatch(args[0], args[1], true, false);
            }
            case "WILDCARD_MATCH": {
                if (args.length !== 2) throw new ExprError("WILDCARD_MATCH takes 2 arguments");
                const colSql = this.emit(args[0]);
                if (args[1].type !== "string") {
                    throw new ExprError("WILDCARD_MATCH argument 2 must be a text literal");
                }
                const pattern = escapeLikePattern(args[1].value)
                    .replace(/\\\*/g, "%")
                    .replace(/\\\?/g, "_");
                this.bindings.push(pattern);
                return `(${colSql} LIKE ? ESCAPE '\\')`;
            }
            case "IN_LIST": {
                if (args.length < 2) throw new ExprError("IN_LIST takes at least 2 arguments");
                const colSql = this.emit(args[0]);
                const valueSqls = args.slice(1).map(a => this.emit(a));
                return `(${colSql} IN (${valueSqls.join(", ")}))`;
            }
            case "YEAR": {
                if (args.length !== 1) throw new ExprError("YEAR takes 1 argument");
                return `CAST(strftime('%Y', ${this.emit(args[0])}) AS INTEGER)`;
            }
            case "MONTH": {
                if (args.length !== 1) throw new ExprError("MONTH takes 1 argument");
                return `CAST(strftime('%m', ${this.emit(args[0])}) AS INTEGER)`;
            }
            case "DAY": {
                if (args.length !== 1) throw new ExprError("DAY takes 1 argument");
                return `CAST(strftime('%d', ${this.emit(args[0])}) AS INTEGER)`;
            }
            case "NOW": {
                if (args.length !== 0) throw new ExprError("NOW takes 0 arguments");
                return "strftime('%Y-%m-%d %H:%M:%S', 'now')";
            }
            case "TO_DATE": {
                if (args.length !== 1) throw new ExprError("TO_DATE takes 1 argument");
                return `strftime('%Y-%m-%d', ${this.emit(args[0])})`;
            }
            case "DATE_TRUNC": {
                if (args.length !== 2) throw new ExprError("DATE_TRUNC takes 2 arguments");
                const unit = args[0].type === "string" ? args[0].value.toLowerCase() : "";
                const dateSql = this.emit(args[1]);
                if (unit === "year") {
                    return `strftime('%Y-01-01', ${dateSql})`;
                }
                if (unit === "month") {
                    return `strftime('%Y-%m-01', ${dateSql})`;
                }
                if (unit === "day") {
                    return `strftime('%Y-%m-%d', ${dateSql})`;
                }
                return `strftime('%Y-%m-%d', ${dateSql})`;
            }
            case "TO_STRING": {
                if (args.length < 1 || args.length > 2) throw new ExprError("TO_STRING takes 1 or 2 arguments");
                const dateSql = this.emit(args[0]);
                const format = args.length === 2 && args[1].type === "string"
                    ? convertDateFormat(args[1].value)
                    : "%Y-%m-%d";
                return `strftime('${format}', ${dateSql})`;
            }
            default:
                throw new ExprError(`unknown function '${fn}'`);
        }
    }

    emitTextMatch(colNode, patternNode, leading, trailing) {
        const colSql = this.emit(colNode);
        if (patternNode.type === "string") {
            const escaped = escapeLikePattern(patternNode.value).toLowerCase();
            const fullPattern = (leading ? "%" : "") + escaped + (trailing ? "%" : "");
            this.bindings.push(fullPattern);
            return `(LOWER(${colSql}) LIKE ? ESCAPE '\\')`;
        }
        // Expression pattern: concatenate wildcards
        const exprSql = this.emit(patternNode);
        const prefix = leading ? "'%' || " : "";
        const suffix = trailing ? " || '%'" : "";
        return `(LOWER(${colSql}) LIKE (${prefix}LOWER(${exprSql})${suffix}))`;
    }
}

/**
 * Emits a parsed expression AST as a SQLite SQL fragment and positional parameter bindings.
 *
 * @param {object} ast
 * @param {Record<string, string>} [physicalColumns=null]
 * @returns {{ sql: string, bindings: Array<unknown> }}
 */
export function emitSqlite(ast, physicalColumns = null) {
    const emitter = new SqliteExprEmitter(physicalColumns);
    const sql = emitter.emit(ast);
    return { sql, bindings: emitter.bindings };
}
