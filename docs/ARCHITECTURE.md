# InteractiveReport — Architecture

An Oracle APEX Interactive Reports equivalent for ASP.NET Core: the developer defines a
report as a single SELECT statement; end users get runtime filtering, sorting, control
breaks, aggregates, computed columns, highlighting, alternate views, and saved report
states — all composed server-side into one parameterized query per request.

Engine-first design: the product boundary is a JSON protocol. UI is a replaceable
consumer of that protocol.

---

## 1. Core principle: the trust boundary

**The developer owns SQL. The client owns state. State is data, never code.**

- Report definitions (base SQL, connection, limits, authorization) live server-side,
  referenced by friendly name. Bare SQL never crosses the network in either direction.
- The client sends a *report state document* (JSON): filters, sorts, breaks, aggregates,
  computed columns, highlights, paging, view mode.
- Every element of the state document is validated before composition:
  - column names → must exist in the discovered schema (or be declared computed columns);
  - operators / aggregate functions → closed enums;
  - computed-column and expression text → parsed by our constrained grammar into an AST;
    only whitelisted functions; we emit the SQL, never the client.
- Identifiers never pass through raw: the client sends names, we match them against the
  schema-discovered column set and use *our* copy, quoted by SqlKata.

This mirrors the APEX division of labor (developer writes the region SQL, users own the
Actions-menu state), relocated into ASP.NET Core configuration.

## 2. Chosen architecture: live pushdown via SqlKata

Rejected alternative — staging (extract into SQLite/DuckDB, transform locally): fewer
dialect concerns and production-DB isolation, but adds a refresh/staleness policy,
security-context cache keying, a type-fidelity shim, and snapshot lifecycle. Pushdown
keeps one code path and APEX-faithful always-live semantics.

**Revisit trigger:** if interactive exploration load on a source database becomes a real
problem, staging can return *behind the same protocol* — the state document, validation,
and renderer don't care whether the base is a live subquery or a staged snapshot.

The composition core (the APEX trick):

```csharp
var core = new Query()
    .FromRaw($"({def.Sql}) ir_base")   // alias without AS: Oracle rejects AS in table aliases
    /* + user filters and search from the validated state doc */;

var count = core.Clone().AsCount();
var page  = core.Clone().Select(cols).OrderBy(...).ForPage(index, size);
var sql   = compilerForDialect.Compile(page);   // parameterized; never string-concatenated
```

(Select/sort/paging apply only to the page clone, so the count query needs no
component-clearing and structurally cannot disagree with the page query.)

`Clone()` is load-bearing: the page query, total count, column aggregates, and
control-break totals all derive from one composed core and cannot disagree.

**Pushdown line:** only operations that change *which rows come back* push down —
filters, search, sorts, aggregates, group-by, computed columns. Presentation stays in
C#: highlight rules evaluate over the fetched page; the pivot view pivots a pushed-down
GROUP BY result in memory. This fences per-dialect work to predicates plus a function
whitelist, and eliminates the worst dialect divergence (native PIVOT syntax) entirely.

## 3. Solution layout

| Project | Responsibility |
|---|---|
| `src/InteractiveReport.Core` | State model, validation, expression parser, query composition (SqlKata), execution, schema discovery, highlight evaluation, in-memory pivot, export. No ASP.NET dependencies. |
| `src/InteractiveReport.AspNetCore` | Endpoint mapping (`MapInteractiveReports`), config-backed definition store, auth integration, JSON protocol shaping, problem+json errors. `Ui/` holds the packaged product UI (§14), embedded and served by the same mapping. |
| `samples/Workbench` | Dev harness: SQLite sample DB. `index.html`/`admin.html` host the packaged UI; `plain.html` is the deliberately plain JS page exercising every engine feature — the living spec for the protocol. |
| `tests/InteractiveReport.Core.Tests` | Composer golden tests (state doc → expected SQL, ×3 dialects), expression parser tests, SQLite end-to-end integration tests. |

Target framework: `net8.0` (Umbraco 13 LTS floor; builds under SDK 8/10).

## 4. Report definitions

Bound from `IConfiguration` (v1), behind an interface so a database-backed store can
exist later without touching the engine:

```csharp
public interface IReportDefinitionStore
{
    ValueTask<ReportDefinition?> Find(string name, CancellationToken ct);
    ValueTask<IReadOnlyList<ReportSummary>> List(CancellationToken ct); // for authz-filtered listing
}
```

```json
"InteractiveReport": {
  "Reports": {
    "open-orders": {
      "title": "Open Orders",
      "connection": "MainDb",
      "dialect": "SqlServer",            // SqlServer | Oracle | Sqlite | Postgres
      "sql": "SELECT o.ORDER_ID, o.CUSTOMER, o.AMOUNT, o.ORDER_DATE FROM ORDERS o WHERE o.SALES_REP = @currentUser",
      "contextParams": { "currentUser": { "claim": "sub" } },
      "authorization": { "policy": "SalesRead" },
      "maxRows": 100000,
      "defaultPageSize": 50,
      "defaultState": { "sorts": [ { "col": "ORDER_DATE", "dir": "desc" } ] }
    }
  }
}
```

Notes:
- `contextParams` values resolve **server-side only** (claims by default; host may register
  an `IContextParameterResolver` for anything else). Client-supplied values can never bind
  to them — they are a separate parameter class from filter values. This is the
  `:APP_USER` pattern from APEX translated to claims, and it is the row-level security story.
- Base SQL must not end with `ORDER BY` (breaks subquery wrapping on SQL Server; APEX has
  the same rule). Validated at definition load with a clear error.
- Definitions version in git and deploy with the app: schema changes and report changes travel together.

## 5. Report state document

The single artifact that is simultaneously: the request body, the saved report, and the
shareable view state. Versioned (`"v": 1`) for forward migration.

```json
{
  "v": 1,
  "search": "acme",
  "filters": [
    { "col": "STATUS", "op": "in", "value": ["SHIPPED", "PENDING"] },
    { "col": "AMOUNT", "op": "gt", "value": 1000 }
  ],
  "sorts":  [ { "col": "ORDER_DATE", "dir": "desc" } ],
  "columns": ["ORDER_ID", "CUSTOMER", "AMOUNT", "ORDER_DATE", "c1"],
  "computed": [
    { "id": "c1", "label": "Amount w/ Tax", "expr": "ROUND(AMOUNT * 1.0825, 2)" }
  ],
  "breaks": ["REGION"],
  "aggregates": [ { "col": "AMOUNT", "fn": "sum" } ],
  "highlights": [
    { "id": "h1", "scope": "row",
      "condition": { "col": "AMOUNT", "op": "gt", "value": 10000 },
      "style": { "bg": "#fff3cd" } }
  ],
  "view": { "mode": "grid" },
  "page": { "index": 1, "size": 50 }
}
```

**Filter operators (closed set):** `eq ne lt le gt ge between in nin contains ncontains
starts ends blank nblank`.
- `contains/ncontains/starts/ends` are case-insensitive by definition (`LOWER()` both sides).
- `blank/nblank`: semantics owned by the filter layer per dialect — on Oracle,
  `'' IS NULL`, so "blank" is `IS NULL`; elsewhere it is `IS NULL OR = ''` for text.
- `search` is the toolbar search: OR of `contains` across visible text columns.

**Aggregate functions (closed set):** `count sum avg min max countDistinct`.
- `sum/avg` require number columns; `min/max` allow number/date/text; `count/countDistinct`
  allow anything. `count` counts non-null values of the column (row count is `totalRows`).
- SQL Server `AVG` gets a float cast (integer AVG truncates there); other dialects native.
- Control-break columns sort first (a user sort on a break column contributes its
  direction) and are forced into the selection so renderers can group; break totals mirror
  the page's group ordering.
- (`median` deferred: no portable SQL across our three dialects; candidate for in-memory
  computation later.)

**Computed columns:** ids live in a separate namespace (`c1`, `c2`, …); may not shadow
schema column names; referenced by id in `columns`, `sorts`, `filters`, `aggregates`,
`highlights`.

**Views:** `grid` (default) · `groupBy` (`{ "mode": "groupBy", "groupBy": ["REGION"],
"values": [{"col","fn"}...] }` — pushed down, groups paginated with their own count
query; response columns are the dims + `__count` + `v0..vN`) · `pivot` (`{ "mode":
"pivot", "rows": [...], "cols": [...], "values": [...] }` — one grouped query over
rows+cols dims transformed in memory; synthetic cell columns `p{col}_{value}` with
human labels; empty `values` ⇒ implicit counts). Caps: `maxPivotColumns` per definition
(default 60) and a hard 10,000-group source ceiling — both surface as precise 400s.
Grid-only features (breaks, highlights, grid aggregates, non-dim sorts) are noted in
`ignored[]` in alternate views, never fatal.

**Resilience:** state elements referencing columns that no longer exist in the schema are
*dropped, not fatal* — the response lists what was ignored (`"ignored": [...]`) so a saved
report survives a definition change gracefully instead of 500ing.

## 6. HTTP protocol

Mounted by the host: `app.MapInteractiveReports("/api/reports").RequireAuthorization(...)`.

| Endpoint | Purpose |
|---|---|
| `GET  /api/reports` | List reports the caller is authorized to see (name, title). |
| `GET  /api/reports/{name}/schema` | Column metadata + default state + capabilities. |
| `POST /api/reports/{name}/query` | Body = state document → page of results. |
| `GET  /api/reports/whoami` | The caller's canonical identity value (only when `whoamiEnabled`). |
| `GET  /api/reports/{name}/saved` | Saved reports visible to the caller: globals + their own. |
| `POST /api/reports/{name}/saved` | Save the posted state under a title (global publish = admin). |
| `GET/PUT/DELETE /api/reports/saved/{id}` | Load / modify / delete one saved report (matrix in §13). |
| `GET  /api/reports/admin/saved` | Administrator: every saved report in the system. |
| `POST /api/reports/{name}/export` | Same state, same gate, no paging → CSV (UTF-8 BOM, label headers), capped at `maxRows` with `X-IR-Truncated` header. XLSX/HTML later. |
| `GET  /api/reports/ui/{file}` | Packaged UI assets (§14). Anonymous by design; content-hash ETags. |

POST is the primary verb deliberately: state documents outgrow querystrings, and GET puts
filter values into proxy/server logs. Shareable deep links arrive later as saved-state
ids, not state-in-URL.

**Query response shape:**

```json
{
  "columns": [ { "name": "AMOUNT", "label": "Amount", "type": "decimal", "computed": false } ],
  "rows": [ { "ORDER_ID": 1042, "CUSTOMER": "Acme", "AMOUNT": 1234.50, "ORDER_DATE": "2026-07-30" } ],
  "page": { "index": 1, "size": 50 },
  "totalRows": 1423,
  "aggregates": { "AMOUNT": { "sum": 8842210.75 } },
  "breakTotals": [ { "key": { "REGION": "WEST" }, "rows": 310, "aggregates": { "AMOUNT": { "sum": 1200000.00 } } } ],
  "highlights": [ { "row": 3, "id": "h1" } ],
  "ignored": [],
  "elapsedMs": 41
}
```

Rows as objects (not positional arrays): negligible size at page granularity, much
friendlier to consume. Aggregates/break totals are computed over the **whole filtered
set** via the cloned queries — never over the visible page.

**Errors:** RFC 7807 `application/problem+json`, sanitized. Database and compiler
exceptions are caught, logged server-side with a correlation id, and returned as a
generic problem document carrying that id. **No SQL text, no parameter values, no
provider error internals ever reach the client.** Validation failures are the exception:
they are precise and verbose (which filter, which column, why), because they reference
only what the client already sent.

## 7. Composition pipeline

```
resolve definition (store)                         404 if absent
→ authorize (endpoint gate + per-report policy)    403/401
→ resolve context params (claims/resolver)
→ discover/fetch cached schema
→ validate state doc against schema + enums        400 problem+json (precise)
→ parse computed-column & filter expressions → AST
→ build core query:
    wrap base SQL as subquery (ir_base)
    [if computed columns] second wrap layer:
        SELECT ir_base.*, <expr> AS c1 FROM (base) ir_base  → AS ir_calc
        (aliases become filterable/sortable universally — no dialect
         supports referencing a SELECT alias in WHERE reliably)
    apply filters, search, sorts (breaks force-prepended to sort list)
→ derive via Clone():
    page query   (+ ForPage)
    count query  (ClearComponent order → AsCount)
    aggregates   (ClearComponent order/limit → SELECT fn(col)…)
    break totals (… → GROUP BY break cols + aggregate fns)
→ compile (dialect compiler) → execute (Dapper, dynamic rows, CancellationToken)
→ post-process in C#: highlight evaluation, pivot transform (pivot view only)
→ shape response
```

Execution runs the derived queries concurrently on separate connections where the
provider allows; SQLite runs them sequentially on one connection.

## 8. Expression language

Small, typed, and closed — a **documented portable subset**, not "whatever the target
database accepts". Used for computed columns; filter values stay structured (the
`op`/`value` model) and do not use the expression language in v1.

**Pipeline (staged):** text → *syntax* (untyped tree with source positions; lexer +
recursive descent, binary operators via a Pratt precedence loop) → *bind* (schema +
function registry → typed AST; all typing rules live here) → *emit* (registry-driven
per-dialect SQL). The stages are `ExprSyntaxParser` → `ExprBinder` → `ExprEmitter`,
fronted by the `ExprParser.Parse` facade.

```
expr        := or
or          := and (OR and)*
and         := not (AND not)*
not         := NOT not | predicate                 (NOT binds looser than comparisons)
predicate   := additive ( cmp additive | IS [NOT] NULL )*
cmp         := '=' | '<>' | '!=' | '<' | '<=' | '>' | '>='     (!= normalizes to <>)
additive    := term (('+'|'-'|'||') term)*
term        := factor (('*'|'/') factor)*
factor      := number | 'string' | NULL | column | func '(' args ')'
             | '(' expr ')' | '-' factor | case
case        := CASE [expr] (WHEN expr THEN expr)+ [ELSE expr] END
func        := UPPER | LOWER | TRIM | LENGTH | SUBSTR | CONCAT
             | ROUND | ABS | COALESCE
             | YEAR | MONTH | DAY                  (date part extraction)
args        := expr (',' args)*
```

**Type discipline.** Values are number/text/date. Conditions (boolean) arise from
comparisons, `IS [NOT] NULL`, and `AND`/`OR`/`NOT`, and are consumed by searched-CASE
`WHEN`s and by `NOT`/`AND`/`OR` — nowhere else. A computed column's result must be a
value: SQL Server has no scalar boolean, so the portable subset doesn't either (the
error says to wrap the condition in `CASE WHEN … THEN 1 ELSE 0 END`). Comparisons
require both operands of the same kind; comparing conditions (chained comparisons) is
rejected.

Boolean-*valued* columns (a SQL Server `BIT`, say) are the one bridge: they may stand
directly in condition position (`CASE WHEN IS_PRIORITY THEN …`), and the emitter
lowers them to an explicit predicate (`([IS_PRIORITY] = 1)`) because T-SQL accepts a
bit as a value but never as a condition. Postgres is the exact inverse — its booleans
are real conditions and `= 1` would be a boolean/integer type error — so there the
column emits bare. The type checker's view (bool column ≈ condition) and each
database's view meet at emission, not in the user's face.

**NULL rules** (explicit, because SQL's are silent):
- `NULL` is a value of every type; its type comes from context — function arguments,
  concatenation, arithmetic (`AMOUNT + NULL` is a number-typed NULL), CASE branches.
  `COALESCE` and CASE branch unification skip NULLs; all-NULL means "cannot infer a
  type" — an error.
- `x = NULL` never matches in SQL; the binder rejects it and points at `IS NULL`.
- Simple `CASE x WHEN …` uses SQL equality, so `WHEN NULL` never matches — rejected,
  pointing at the searched form with `IS NULL`.
- `CASE` without `ELSE` yields NULL for unmatched rows (SQL default, allowed).

**Function registry** (`ExprFunctions`): one entry per function — arity, argument
rules, result-kind inference, per-dialect emitter. Adding a function is adding a row;
there is no enum and no switches to grow. The registry is the only dialect-specific
surface outside operators:

| AST | SqlServer | Oracle | Sqlite | Postgres |
|---|---|---|---|---|
| `SUBSTR(s,a[,n])` | `SUBSTRING(s,a,n)` (2-arg → `LEN(s)` for n) | `SUBSTR(s,a[,n])` | `SUBSTR(s,a[,n])` | `SUBSTR(s,a[,n])` |
| `a || b` / `CONCAT` | `CONCAT(a,b,…)` | `(a || b || …)` | `CONCAT(a,b,…)` (3.44+) | `CONCAT(a,b,…)` |
| `LENGTH(s)` | `LEN(s)` | `LENGTH(s)` | `LENGTH(s)` | `LENGTH(s)` |
| `ROUND(x,n)` | `ROUND(x,n)` | `ROUND(x,n)` | `ROUND(x,n)` | `ROUND(CAST(x AS NUMERIC), CAST(n AS INT))` |
| `YEAR(d)` | `YEAR(d)` | `EXTRACT(YEAR FROM d)` | `CAST(strftime('%Y',d) AS INTEGER)` | `EXTRACT(YEAR FROM d)` |
| `COALESCE` | `COALESCE` | `COALESCE` | `COALESCE` | `COALESCE` |

- The emitter produces SQL fragments **we** wrote, injected via `SelectRaw` with `?`
  bindings for every literal — client text never reaches SQL; only the AST does. The one
  keyword literal is `NULL` itself (ours, not client data). Every binary operation is
  parenthesized.
- `CASE`, comparisons, `IS NULL`, and `AND/OR/NOT` emit **identically on all three
  dialects** — they are the portable core; only functions carry dialect idioms.
- Semantics notes: concatenation treats NULL as empty everywhere (CONCAT on
  SqlServer/Sqlite/Postgres; Oracle's `||` natively); `YEAR/MONTH/DAY` accept ISO date
  *text* because SQLite date columns discover as text — emitted natively where the
  engine converts text itself (SQLite strftime, SQL Server implicit ISO conversion)
  and with explicit conversions where EXTRACT is strictly typed (Oracle
  `TO_DATE(SUBSTR(x,1,10),'YYYY-MM-DD')`, Postgres `CAST(x AS TIMESTAMP)`). Non-ISO
  text in a date-part function is a runtime error on those dialects — ISO is the
  documented contract.
- Computed columns cannot reference other computed columns (no dependency ordering in v1).
- Known limitation: no date literals yet, so date columns compare only against date
  columns — use `YEAR()/MONTH()/DAY()` for date-part conditions, or filters for date
  ranges. A `DATE '…'` literal with per-dialect emission is the designed extension
  point.

## 9. Dialect strategy

- SqlKata compilers: `SqlServerCompiler`, `OracleCompiler`, `SqliteCompiler`,
  `PostgresCompiler`. Dialect is declared per definition (not inferred) — explicit
  beats clever.
- SqlKata owns: identifier quoting, parameter naming, pagination syntax (including Oracle
  12c `OFFSET/FETCH`).
- We own (per-dialect semantic decisions, centralized in the filter/operator layer):
  - Oracle `'' IS NULL` → `blank` operator semantics (§5);
  - case-insensitive text matching policy (`LOWER` both sides; SqlKata compiles the
    same intent as native `ILIKE` on Postgres);
  - Oracle ADO specifics: `BindByName = true`, parameter prefix — isolated in the
    Oracle execution adapter;
  - boolean columns in expression condition position: `= 1` lowering everywhere
    except Postgres, whose native booleans emit bare (§8);
  - Postgres folds unquoted identifiers to lowercase: absorbed by case-insensitive
    schema matching and response dictionaries, with quoted identifiers only where we
    own both sides (the saved-report table DDL);
  - date literal handling: parameters only, never inline date strings.
- Golden tests lock the emitted SQL per dialect so drift is loud.

## 10. Schema discovery

- At definition load (or first use), run the wrapped base with a `WHERE 1 = 0` probe
  (chosen over `Limit(0)`, which SqlKata treats as "no limit"), read
  `DbDataReader.GetColumnSchema()` under `CommandBehavior.SchemaOnly`.
- Column model: `Name, ClrType, ProviderType, IsNullable, Label` (label defaults to
  prettified name; definition may override).
- Cached per definition; invalidated on configuration reload (`IOptionsMonitor`) or
  explicit admin refresh later. Discovery failures surface at startup/first-use as clear
  errors naming the report — not at user query time.
- This replaces APEX's data-dictionary knowledge: *the developer's SELECT plus the
  discovered schema is the model.* No semantic-model layer.

## 11. Execution & guardrails

- `IReportConnectionFactory` (host-registered): named connection → open `DbConnection`.
  Hosts should point report connections at a **read-only database principal** — the
  engine only ever SELECTs, but the principal should make that a guarantee, not a habit.
- `maxRows` (per definition): hard cap composed into every query (`page.size` is clamped;
  exports clamp to `maxRows`). A response flag indicates truncation.
- Command timeout per definition (default modest, e.g. 30s); `CancellationToken` flows
  from the HTTP request so abandoned browsers stop occupying the database.
- Logging: SQL text at Debug only; parameter *values* never logged above Debug;
  correlation id at Information joins HTTP log ↔ engine log ↔ problem response.

## 12. Security model

Layered, default-deny:

1. **Endpoint gate** — host chains `.RequireAuthorization(...)` on
   `MapInteractiveReports`; standard ASP.NET Core, so it composes with whatever auth the
   host already has (Umbraco members, cookies, OIDC). The engine has no auth mechanism of
   its own — deliberately.
2. **Per-report policy** — `authorization.policy` / `roles` in the definition, checked via
   `IAuthorizationService` before composition. Reports absent from `GET /api/reports` if
   the caller fails the check.
3. **Default-deny** — no authorization block ⇒ authenticated-only. Anonymous requires
   explicit `"allowAnonymous": true`. The lazy path is the safe path.
4. **Row-level security** — server-resolved `contextParams` (§4).
5. **Hygiene** — sanitized errors (§6), read-only principal (§11), no-SQL-over-the-wire
   (§1), CSRF: hosts using cookie auth should apply their antiforgery convention to the
   POST endpoints (documented host responsibility; the mapping API accepts additional
   endpoint conventions so this is one chained call).

## 13. Saved reports & administration

**Identity.** The engine reduces the authenticated user to one canonical identity value
(`ReportIdentity`): explicit `identityClaim` config if set, else NameIdentifier → `sub` →
`Identity.Name`. That value is saved-report ownership and the administrator match.
`GET /whoami` (opt-in via `whoamiEnabled`, off by default) exists so an operator can see
the exact value to put in `administrators` — which is a config list, matched
case-insensitively exact.

**Storage.** `ISavedReportStore` / `SqlSavedReportStore`: table `IR_SAVED_REPORTS`
(ID text GUID · REPORT_NAME · TITLE · OWNER · IS_GLOBAL 0/1 · STATE_JSON · MODIFIED_UTC
ISO-8601 text). Cross-dialect-uniform storage types on purpose; auto-created unless
`autoCreate` is disabled. Location via `savedReports.connection` (a named connection —
point it at the data database to co-locate saved reports with report data), defaulting
zero-config to a local SQLite file `App_Data/interactivereport.saved.db`.

**Authorization matrix** (enforced at the endpoint layer; the store is dumb):

| Actor | May |
|---|---|
| Owner (private) | read, update title/state, delete |
| Anyone with report access | read globals for that report |
| Administrator | everything: list all, publish/unpublish global, reassign owner, update/delete any |

Denials hide existence (404) except where the caller provably knows the resource — an
owner reaching for admin-only powers (publish, reassign) gets an explicit 403. Global
reports are shared infrastructure: mutating one is admin-only even for its owner.
Saved-report loads still pass the underlying report definition's authorization gate.

## 14. Packaged UI

The product UI is a consumer of the JSON protocol, nothing more — it ships *with* the
AspNetCore package but the engine never depends on it. Everything the plain Workbench
harness proves about the protocol, the packaged UI does for real users, styled after
APEX's Interactive Reports.

**Consumption** (any host page, Umbraco included):

```html
<script type="module" src="/api/reports/ui/ir.js"></script>
<interactive-report report="open-orders"></interactive-report>

<script type="module" src="/api/reports/ui/ir-admin.js"></script>
<interactive-report-admin></interactive-report-admin>
```

- Custom elements, no build step, no dependencies: plain ES modules embedded in the
  assembly and served at `{prefix}/ui/{file}` by `MapInteractiveReports`. `base`
  defaults to the prefix the script was loaded from (attribute overrides). Changing
  the `report` attribute re-initializes in place.
- Modules: `ir.js` (element, state doc plumbing, toolbar/menus), `ir-api.js`
  (fetch + problem+json), `ir-ui.js` (menu/dialog primitives), `ir-render.js`
  (chips, grid, pager), `ir-dialogs.js` (the Actions dialogs), `ir-admin.js`
  (admin element), `ir.css`.
- **Feature surface**: scoped toolbar search (all text columns or one column → filter);
  Actions menu (Columns shuttle, Filter, Sort, Control Break, Highlight, Aggregate,
  Compute with token-insert helpers, Group By, Pivot, Save/Save As/Delete/Reset,
  CSV download); column-header menus (sort/hide/break/filter); settings chips with
  APEX-style enable/disable checkboxes; break groups with per-column subtotal rows and
  grand-total rows; row/cell highlights; groupBy/pivot rendering; saved-report select
  (Primary Report + Global/Private groups); `ignored[]` and problem+json surfaced as
  notices — validation problems render *inside* the originating dialog, which stays
  open (apply is optimistic: mutate, re-query, roll back on failure).
- **Client-only state** (chip disabled flags, `_`-prefixed keys) is stripped at
  serialization: the server, saved reports, and exports only ever see the canonical
  state document.
- **Styling**: light DOM, every rule namespaced `.ir-*` under CSS custom properties
  (`--ir-accent`, …). `ir.css` auto-links once per document; a host can pre-link its
  own `<link data-ir-css>` to fully retheme. No Shadow DOM — hosts theme it; popups
  and dialogs mount on `<body>` and are styled standalone.
- **Asset endpoint is anonymous** even when the host chains `RequireAuthorization` on
  the group: the assets are public package code (no data, no secrets), and a
  session-expired page that cannot load the script cannot even say "sign in". Every
  data endpoint keeps the full gate. ETags are content hashes (SHA-256 prefix) with
  `Cache-Control: no-cache` — an assembly-version tag would 304 stale content across
  rebuilds of the same version.
- The admin element drives the §13 administrator surface: list everything,
  publish/unpublish, reassign owner, inspect the stored state document, delete. It
  simply loses its data (404) for non-administrators and says so.

## 15. Milestones

- **M1 — It's alive** ✅ *(2026-08-04)*: state doc (filters/sorts/paging/search) +
  composer + SQLite execution + schema discovery + Workbench grid over a sample DB.
  Golden tests ×3 dialects for the composer.
- **M2 — Numbers** ✅ *(2026-08-05)*: aggregates, control breaks + break totals, total
  count; schema/list endpoints; auth wiring (endpoint gate, per-report policy verified
  against a real host policy, context params).
- **M3 — Expressions** ✅ *(2026-08-05)*: computed-column grammar/AST/emitters with the
  ir_calc second wrap (computed columns filter/sort/aggregate/break uniformly);
  highlights evaluated server-side with SQL-parity NULL semantics; `ignored[]` resilience
  extended to highlight rules.
- **M4 — Views & export** ✅ *(2026-08-05)*: groupBy view (pushed down, group-count
  pagination), pivot-in-memory view (capped, implicit-count default), CSV export with
  truncation signaling — all three views export through the same pipeline.
- **M5 — Persistence & proof:** ~~saved reports (private/public, per user)~~ *(done
  early — see §13, including administration/whoami)*; SQL Server + Oracle verification
  passes; hardening (timeouts, caps, logging discipline). *Prep complete 2026-08-05:
  env-gated live-dialect battery (docs/TESTING.md), operator × dialect golden matrix,
  SQL-safety corpus, Oracle BindByName fix, parser recursion guard, Debug-only SQL
  logging. Live battery verified green ×2 dialects 2026-08-05.*
- **M6 — The real UI** ✅ *(2026-08-05)*: packaged APEX-style widget + saved-report
  administration widget (§14), embedded-asset serving, Workbench pages rebuilt around
  them (plain harness preserved as the protocol spec). Verified end-to-end in the
  browser against the SQLite sample: filters/chips/toggles, header menus, computed
  columns, highlights, breaks + subtotal/grand rows, groupBy, pivot, scoped search,
  saved reports (save-as/publish/reassign/state/delete), CSV download, validation
  problems in-dialog, `ignored[]` notices, per-report policy gate.
- **M7 — Expression core v2** ✅ *(2026-08-06)*: staged pipeline (untyped syntax →
  binder → registry-driven emitter, §8); searched and simple `CASE`, comparisons,
  `AND/OR/NOT`, `IS [NOT] NULL`, typed `NULL` with COALESCE/CASE inference; `ExprFn`
  enum and its switches replaced by the function registry. Existing emissions locked
  byte-identical by the golden suite; CASE proven end-to-end on SQLite and live
  against SQL Server + Oracle (battery green ×24, 2026-08-06).
- **M8 — PostgreSQL** ✅ *(2026-08-06)*: fourth dialect end to end — compiler,
  operator matrix (native `ILIKE` for case-insensitive matching), `EXTRACT` date
  parts, `ROUND` signature casts, native-boolean condition emission (the inverse of
  SQL Server's `= 1` lowering), quoted-identifier saved-report DDL, identifier-folding
  absorbed by case-insensitive schema matching. Live battery green 41/41 across
  SQL Server + Oracle + PostgreSQL.

## Appendix: decision log

| Decision | Alternative | Why |
|---|---|---|
| Live pushdown (SqlKata) | SQLite/DuckDB staging | One code path; no refresh/cache-keying/type-shim lifecycle; APEX-faithful liveness. Staging can return behind the same protocol if source load demands it. |
| Definitions in config by name | Client-supplied SQL | Trust boundary; SQL never crosses the wire; definitions version with the app. |
| Borrow host auth | Engine-owned API keys | Hosts (Umbraco et al.) already have real auth; a second mechanism would be weaker and clash. |
| POST-primary protocol | GET + querystring state | State size; filter values leak into logs via GET; deep links return later as saved-state ids. |
| Rows as JSON objects | Positional arrays | Page-granularity size difference is negligible; consumption ergonomics win. |
| Highlights & pivot in C# | Push down | They don't change row selection; avoids the ugliest dialect SQL (PIVOT) and keeps JS dumb. |
| `net8.0` | `net10.0` | Umbraco 13 LTS floor; SDK 8 present; bump is cheap later. |
| whoami off by default | always on | It's an information endpoint; enabling is a deliberate operator act (samples enable it). |
| Admin match case-insensitive exact | case-sensitive | Operator-friendly for emails/usernames; GUID-style values don't collide under folding. |
| Saved-report ids are text GUIDs | identity/sequence columns | One DDL shape across SQLite/SqlServer/Oracle; no sequence plumbing. |
| Timestamps as ISO text, flags as 0/1 | native per-dialect types | Uniform semantics and sorting across dialects for an engine-internal table. |
| Global mutations admin-only, even for the owner | owner-managed globals | A published report is shared infrastructure; publishing and unpublishing are curation acts. |
| Microsoft.Data.Sqlite dependency in the AspNetCore package | host-supplied providers only | The zero-config default saved-report store must work with no host setup; report-data connections remain host-supplied. |
| Decimal parameters bind as double on SQLite | decimal-as-TEXT (provider default) | The provider's TEXT binding breaks comparisons against affinity-less expressions (computed columns) via SQLite's cross-type ordering; double is SQLite's native numeric storage, so the conversion is faithful to the engine. |
| Pivot caps: 60 column groups (configurable) + hard 10k source groups | unbounded pivot | An unbounded pivot is a memory/usability grenade; the caps surface as precise 400s telling the user what to change. |
| CSV: UTF-8 BOM, label headers, X-IR-Truncated header | bare UTF-8, name headers, silent truncation | Excel needs the BOM to detect encoding; users recognize labels, not internal names; silent truncation reads as complete data. |
| Views share the export pipeline | grid-only export | Exporting "what the view shows" falls out of running the same validated state unpaged — no special cases. |
| Oracle BindByName via reflection in CommandBuilder | reference ODP.NET from Core | ODP.NET binds by position by default; context params appear first in SQL but are added last, so positional binding silently misbinds. Reflection keeps Core provider-free. |
| UI: no-build vanilla ES modules as custom elements | React/Vite bundle | No node toolchain in a .NET repo; embeddable in any host (Umbraco: one script tag + one element); the protocol keeps the JS dumb enough that a framework buys little. |
| UI assets embedded in the AspNetCore assembly, served under the mapped prefix | RCL static web assets | Zero host setup (`UseStaticFiles`/`_content` not required), one mapping call delivers API + UI, works identically in any host. |
| UI asset endpoint `AllowAnonymous` | inherit group auth | Assets are public package code (readable on any feed); an auth-gated script tag turns "session expired" into a blank region that can't even say "sign in". Data endpoints keep the full gate. |
| Asset ETags hash content | assembly version tag | Version-tagged ETags 304 stale content across rebuilds of the same version (bitten in dev; would bite ops on patch releases). |
| Light DOM + `.ir-*` prefix + CSS custom properties | Shadow DOM | Hosts want to theme the region, not fight encapsulation; prefix discipline is enough isolation and keeps the DOM inspectable. |
| Chip disable-toggles are client-only state, stripped at serialization | protocol-level `disabled` flags | The engine's state document stays canonical (validation, saved reports, exports agree); a toggle is presentation-side convenience. |
| Save persists only enabled items | persist disabled items too | Round-tripping disabled items would need protocol support; "what you see is what you saved" is predictable. |
| Expression pipeline staged: untyped syntax → bind → emit | grow the single-pass parser | NULL, CASE result inference, and overloads need types the parser can't know mid-parse; positions survive to the error message; each stage is testable alone. |
| Function registry (arity/rules/inference/emitters as data) | ExprFn enum + switches | Two switches per function was already drift-prone at 12 functions; a registry row is one place, and the registry doubles as the subset's documentation. |
| Bool is internal to expressions; computed columns must yield values | allow boolean results | SQL Server has no scalar boolean; the error teaches the portable form (CASE WHEN … THEN 1 ELSE 0 END) instead of failing per-dialect. |
| `x = NULL` and simple-CASE `WHEN NULL` are rejected | let SQL's null semantics apply | Both silently never match — silence is the one thing a validation layer must never emit; the errors point at IS NULL / searched CASE. |
| Bool-valued columns lower to `= 1` predicates in condition position | reject bare bool columns as conditions | `CASE WHEN IS_PRIORITY THEN …` is the natural spelling; T-SQL's bit-is-not-boolean rule is an emission detail, not a user error. Proven live on SQL Server. |
| NULL participates in arithmetic | number-only operands | Consistency: NULL already joined functions, concat, and CASE branches; `AMOUNT + NULL` failing while `CONCAT(NULL, …)` passed was an inconsistency, not a rule. |
| No date literals in v1 expressions | text-vs-date comparison | Implicit text→date conversion is an NLS/format trap on Oracle; a typed DATE '…' literal is the clean extension point. |
| Postgres ROUND emits signature casts | bind precision as int | `round(numeric, integer)` is the only two-arg ROUND Postgres has; casting both arguments in SQL keeps bindings uniform across dialects (goldens: 2 always binds as decimal). |
| Date parts on ISO text convert at emission (Oracle TO_DATE, Postgres CAST) | dialect-aware binding rules | The portable subset's types stay dialect-free; EXTRACT's strictness is an emission detail. Rejecting text outright would break the SQLite date-as-text story the feature exists for. |
| Guid filter values bind as Guid | string binding for Other kind | Postgres rejects `uuid = text` outright; "Other" is a family, not a type, so binding consults the discovered CLR type where it matters. Unparseable UUIDs die as precise validation errors, not provider errors. |
