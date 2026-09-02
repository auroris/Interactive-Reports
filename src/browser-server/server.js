// In-browser Interactive Reports server engine.
// Coordinates report definitions, schema discovery, relational compilation,
// query execution, ephemeral saved-report storage, and Web API / Fetch request handling.

import { discoverSchema } from "./schema.js";
import { executeReport, executeLov, exportCsv } from "./executor.js";
import { EphemeralSavedReportStore } from "./saved-reports.js";

function jsonResponse(data, status = 200) {
    return new Response(JSON.stringify(data), {
        status,
        headers: {
            "Content-Type": "application/json; charset=utf-8",
            "Cache-Control": "no-store",
        },
    });
}

function errorResponse(status, code, title, description, details = null) {
    return jsonResponse({
        code,
        title,
        description,
        details,
        traceId: `trace-${Date.now()}`,
    }, status);
}

export class InteractiveReportServer {
    /**
     * @param {import("./db.js").SqliteDatabase} db
     * @param {object} [options={}]
     * @param {string} [options.apiPrefix="/api/reports"]
     */
    constructor(db, options = {}) {
        this.db = db;
        this.apiPrefix = (options.apiPrefix || "/api/reports").replace(/\/+$/, "");
        /** @type {Map<string, object>} */
        this.definitions = new Map();
        /** @type {Map<string, object>} */
        this.schemaCache = new Map();
        this.savedReports = new EphemeralSavedReportStore();
    }

    /**
     * Registers an in-browser report definition.
     *
     * @param {object} def
     * @param {string} def.name - Unique report identifier (e.g. "orders")
     * @param {string} def.sql - SQLite base query (e.g. "SELECT * FROM ORDERS")
     * @param {string} [def.title] - Human-readable report title
     * @param {Record<string, string>} [def.columnLabels] - Friendly display names for columns
     * @param {Array<string>} [def.features] - Feature whitelist
     * @param {object} [def.defaultState] - Optional initial default report state document
     * @param {object} [def.editLink] - Optional edit link template
     * @param {object} [def.columns] - Optional column overrides
     */
    registerReport(def) {
        if (!def || !def.name) {
            throw new Error("Report definition requires a 'name' property.");
        }
        const key = def.name.toLowerCase();
        this.definitions.set(key, { ...def });
        this.schemaCache.delete(key);
        this.savedReports.ensureDefault(def.name, def.defaultState);
    }

    /**
     * Retrieves the discovered schema for a registered report.
     *
     * @param {string} reportName
     * @returns {object} Discovered InteractiveReportSchema
     */
    getSchema(reportName) {
        const key = String(reportName || "").toLowerCase();
        const def = this.definitions.get(key);
        if (!def) {
            throw new Error(`Report '${reportName}' was not found.`);
        }

        if (this.schemaCache.has(key)) {
            return this.schemaCache.get(key);
        }

        const schema = discoverSchema(this.db, def);
        this.schemaCache.set(key, schema);
        return schema;
    }

    /**
     * Executes a report query with the provided state document.
     *
     * @param {string} reportName
     * @param {object} reportState
     * @returns {Promise<object>} ReportResult
     */
    async query(reportName, reportState = {}) {
        const key = String(reportName || "").toLowerCase();
        const def = this.definitions.get(key);
        if (!def) {
            throw new Error(`Report '${reportName}' was not found.`);
        }

        const schema = this.getSchema(reportName);
        return await executeReport(this.db, def, reportState, schema);
    }

    /**
     * Queries List of Values (LOV) distinct choices for one column.
     *
     * @param {string} reportName
     * @param {object} lovRequest - { document, table, column, search }
     * @returns {Promise<object>} ReportLovResult
     */
    async lov(reportName, lovRequest) {
        const key = String(reportName || "").toLowerCase();
        const def = this.definitions.get(key);
        if (!def) {
            throw new Error(`Report '${reportName}' was not found.`);
        }

        const schema = this.getSchema(reportName);
        return await executeLov(this.db, def, lovRequest, schema);
    }

    /**
     * Exports all rows matching the report state as CSV.
     *
     * @param {string} reportName
     * @param {object} reportState
     * @returns {Promise<string>} CSV string with UTF-8 BOM
     */
    async export(reportName, reportState = {}) {
        const unpagedState = {
            ...reportState,
            page: { index: 1, size: 0 },
        };
        const result = await this.query(reportName, unpagedState);
        return exportCsv(result.rows, result.columns);
    }

    /**
     * Handles an incoming Fetch Request or URL + options and returns a Web API Response.
     *
     * @param {Request|string} input - URL string or Request object
     * @param {object} [init={}] - Fetch init options (method, body, headers)
     * @returns {Promise<Response>}
     */
    async handleRequest(input, init = {}) {
        let urlStr = typeof input === "string" ? input : input.url;
        const method = (init.method || (typeof input === "object" && input.method) || "GET").toUpperCase();

        // Extract path relative to root or apiPrefix
        let pathname = urlStr;
        try {
            const parsed = new URL(urlStr, "https://local.ir");
            pathname = parsed.pathname;
        } catch {
            pathname = urlStr.split("?")[0];
        }

        // Normalize prefix
        if (pathname.startsWith(this.apiPrefix)) {
            pathname = pathname.slice(this.apiPrefix.length);
        }
        if (!pathname.startsWith("/")) {
            pathname = "/" + pathname;
        }

        // Parse JSON body helper
        const readBody = async () => {
            if (init.body !== undefined && init.body !== null) {
                if (typeof init.body === "string") return JSON.parse(init.body);
                return init.body;
            }
            if (typeof input === "object" && typeof input.json === "function") {
                return await input.json().catch(() => ({}));
            }
            return {};
        };

        try {
            // 1. /whoami
            if (pathname === "/whoami" && method === "GET") {
                return jsonResponse({
                    authenticated: true,
                    identity: "demo-user",
                    isAdministrator: true,
                    configuredAdministrator: true,
                    databaseAdministrator: false,
                    administratorListConfigured: false,
                    applicationAuthorizationConfigured: false,
                    name: "Demo User",
                    authenticationType: "ephemeral",
                    claims: [],
                });
            }

            // 2. Listing registered report configurations: GET / or GET ""
            if ((pathname === "" || pathname === "/") && method === "GET") {
                const list = [];
                for (const def of this.definitions.values()) {
                    list.push({
                        name: def.name,
                        title: def.title || def.name,
                    });
                }
                return jsonResponse(list);
            }

            // Match /{name}/schema
            const schemaMatch = /^\/([^/?#]+)\/schema\/?$/.exec(pathname);
            if (schemaMatch && method === "GET") {
                const name = decodeURIComponent(schemaMatch[1]);
                const schema = this.getSchema(name);
                return jsonResponse(schema);
            }

            // Match /{name}/query
            const queryMatch = /^\/([^/?#]+)\/query\/?$/.exec(pathname);
            if (queryMatch && method === "POST") {
                const name = decodeURIComponent(queryMatch[1]);
                const state = await readBody();
                const result = await this.query(name, state);
                return jsonResponse(result);
            }

            // Match /{name}/lov
            const lovMatch = /^\/([^/?#]+)\/lov\/?$/.exec(pathname);
            if (lovMatch && method === "POST") {
                const name = decodeURIComponent(lovMatch[1]);
                const req = await readBody();
                const result = await this.lov(name, req);
                return jsonResponse(result);
            }

            // Match /{name}/export or /{name}/csv (POST or GET)
            const exportMatch = /^\/([^/?#]+)\/(export|csv)\/?$/.exec(pathname);
            if (exportMatch && (method === "POST" || method === "GET")) {
                const name = decodeURIComponent(exportMatch[1]);
                const state = method === "POST" ? await readBody() : {};
                const csv = await this.export(name, state);
                return new Response(csv, {
                    status: 200,
                    headers: {
                        "Content-Type": "text/csv; charset=utf-8",
                        "Content-Disposition": `attachment; filename="${name}.csv"`,
                    },
                });
            }

            // Match saved reports:
            // POST /{id}/saved or POST /{name}/saved
            const saveMatch = /^\/([^/?#]+)\/saved\/?$/.exec(pathname);
            if (saveMatch && method === "POST") {
                const segment = decodeURIComponent(saveMatch[1]);
                const req = await readBody();
                const reportName = isNaN(Number(segment))
                    ? segment
                    : (this.savedReports.reports.get(Number(segment))?.reportName || "orders");
                const summary = this.savedReports.save(reportName, req);
                return jsonResponse(summary, 201);
            }

            // GET /{name}/{id} (Load saved report document)
            const loadMatch = /^\/([^/?#]+)\/(\d+)\/?$/.exec(pathname);
            if (loadMatch && method === "GET") {
                const name = decodeURIComponent(loadMatch[1]);
                const id = Number(loadMatch[2]);
                const doc = this.savedReports.load(name, id);
                if (!doc) {
                    return errorResponse(404, "IR-1404", "Saved report not found", `Saved report #${id} was not found for report '${name}'.`);
                }
                return jsonResponse(doc);
            }

            // PUT /{id} (Update saved report)
            const updateMatch = /^\/(\d+)\/?$/.exec(pathname);
            if (updateMatch && method === "PUT") {
                const id = Number(updateMatch[1]);
                const req = await readBody();
                const updated = this.savedReports.update(id, req);
                if (!updated) {
                    return errorResponse(404, "IR-1404", "Saved report not found", `Saved report #${id} was not found.`);
                }
                return jsonResponse(updated);
            }

            // DELETE /{id} (Delete saved report)
            const deleteMatch = /^\/(\d+)\/?$/.exec(pathname);
            if (deleteMatch && method === "DELETE") {
                const id = Number(deleteMatch[1]);
                const deleted = this.savedReports.delete(id);
                if (!deleted) {
                    return errorResponse(400, "IR-1400", "Cannot delete report", `Saved report #${id} could not be deleted.`);
                }
                return new Response(null, { status: 204 });
            }

            // GET /{name} (List saved reports for a family)
            const listMatch = /^\/([^/?#]+)\/?$/.exec(pathname);
            if (listMatch && method === "GET") {
                const name = decodeURIComponent(listMatch[1]);
                // If name is a registered definition or recognized family:
                if (this.definitions.has(name.toLowerCase()) || this.savedReports.defaultReportIds.has(name.toLowerCase())) {
                    const list = this.savedReports.list(name);
                    return jsonResponse(list);
                }
            }

            return errorResponse(404, "IR-1404", "Not Found", `No endpoint matches '${pathname}' with method ${method}.`);
        } catch (err) {
            return errorResponse(
                400,
                "IR-1201",
                "Report operation failed",
                err.message || String(err),
                err.stack
            );
        }
    }
}
