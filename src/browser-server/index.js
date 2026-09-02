// Browser-side JavaScript port of the Interactive Reports server using SQLite WASM.

export { InteractiveReportServer } from "./server.js";
export { createSqliteDb, SqliteDatabase, normalizeParam } from "./db.js";
export { installFetchInterceptor } from "./fetch-interceptor.js";
export { discoverSchema, prettify, mapSqliteType, ALL_FEATURES } from "./schema.js";
export { parseExpression, ExprError } from "./expressions/parser.js";
export { emitSqlite, escapeLikePattern } from "./expressions/emitter.js";
export { executeReport, executeLov, exportCsv } from "./executor.js";
export { renderCsvTable } from "./presentation.js";
export { EphemeralSavedReportStore } from "./saved-reports.js";
