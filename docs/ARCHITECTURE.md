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
  - aggregate functions → a closed enum;
  - computed-column, filter, and highlight expressions → parsed by our constrained grammar into an AST;
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

**Pushdown line:** row selection and predicate evaluation push down: filters, search,
sorts, aggregates, group-by, computed columns, and private true/false projections for
highlights. C# converts those private projections into highlight hits and pivots grouped
results in memory. This gives filters and highlights one predicate implementation while
still avoiding the worst dialect divergence (native PIVOT syntax) entirely.

## 3. Solution layout

| Project | Responsibility |
|---|---|
| `src/InteractiveReport.Core` | State model, validation, expression parser, query composition (SqlKata), execution, schema discovery, highlight evaluation, in-memory pivot, export. No ASP.NET dependencies. |
| `src/InteractiveReport.AspNetCore` | Endpoint mapping (`MapInteractiveReports`), config-backed definition store, auth integration, JSON protocol shaping, problem+json errors. `Ui/` holds the packaged product UI (§14), embedded and served by the same mapping. |
| `samples/Workbench` | Dev harness: SQLite sample DB. `index.html`/`admin.html` host the packaged UI; `plain.html` is the deliberately plain JS page exercising every engine feature — the living spec for the protocol. |
| `tests/InteractiveReport.Core.Tests` | Composer golden tests (state doc → expected SQL, ×4 dialects), expression parser tests, SQLite end-to-end integration tests. |

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
      "columnLabels": { "ORDER_ID": "Order #", "CUSTOMER": "Customer Name" },
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
- `columnLabels` maps real column names to friendly display labels for base queries
  whose column names aren't presentable. **Friendly names are client-side
  presentation**: the server never applies this map to its schema, validation, results,
  or exports — its entire role is to be handed to the client as the `labels` of the
  default report the schema endpoint sends down (§5). From there reports own their
  labels; the client resolves display names; and every column reference crossing the
  wire — filters, sorts, `labels` keys themselves — uses the real name. Blank or
  case-colliding entries are config errors; entries naming no actual column are simply
  unused display data.
- Base SQL must not end with `ORDER BY` (breaks subquery wrapping on SQL Server; APEX has
  the same rule). Validated at definition load with a clear error.
- Definitions version in git and deploy with the app: schema changes and report changes travel together.

## 5. Report state document

The single artifact that is simultaneously: the request body, the saved report, and the
shareable view state. Versioned (`"v": 2`) for forward migration. Version 2 replaces
the version 1 column/operator/value filter shape with shared boolean expressions; version
1 state must be migrated before loading.

```json
{
  "v": 2,
  "search": "acme",
  "filters": [
    { "enabled": true, "expr": "IN_LIST(STATUS, 'SHIPPED', 'PENDING')" },
    { "enabled": false, "expr": "AMOUNT > 1000" }
  ],
  "sorts":  [ { "col": "ORDER_DATE", "dir": "desc" } ],
  "columns": ["ORDER_ID", "CUSTOMER", "AMOUNT", "ORDER_DATE", "c1"],
  "labels": { "ORDER_ID": "Ticket #" },
  "computed": [
    { "id": "c1", "enabled": true, "label": "Amount w/ Tax",
      "expr": "ROUND(AMOUNT * 1.0825, 2)" }
  ],
  "breaks": ["REGION"],
  "aggregates": [ { "col": "AMOUNT", "fn": "sum" } ],
  "highlights": [
    { "id": "h1", "enabled": true, "scope": "row",
      "expr": "ROUND(AMOUNT, 2) > 10000",
      "style": { "bg": "#fff3cd" } }
  ],
  "view": { "mode": "grid" },
  "page": { "index": 1, "size": 50 }
}
```

**Expression rules:** computed columns, filters, and highlights all contain an `enabled`
flag and an `expr`. Computed columns must bind to a number, text, or date value; filters
and highlights must bind to a true/false condition. All three consume the complete
expression language in §8. A computed value defines a column, a true filter keeps the
row, and a true highlight paints its row or target cell.
Cell highlighting has priority: renderers apply matching row styles first, then cell
styles. `CONTAINS`, `STARTS_WITH`, and `ENDS_WITH` are case-insensitive; `IN_LIST`
provides typed membership. Blank behavior is written explicitly as `IS NULL`, or
`IS NULL OR col = ''` when empty text should also count.
- `search` is the toolbar search: OR of `contains` across visible text columns.
- `labels` (real column name → display label) is **pure client-side presentation** the
  server stores and round-trips but never interprets: execution, validation, result
  metadata, and CSV headers all use server-derived labels (prettified names, computed
  rule labels, `sum(Amount)`-style synthetics), so a rename is a client rendering
  concern and never a server conversation. The client resolves display names as
  `labels[name] ?? server label` — including rebuilding synthetic groupBy/chart metric
  labels from the view spec it authored. A computed column still names itself on its
  own rule. Like every state property, a present map replaces the default wholesale
  and `{}` explicitly clears it.
- A partial request resolves over `defaultState` once: a missing property inherits,
  while an explicit empty string/list clears the default. `{ "mode": "grid" }`
  explicitly overrides an alternate default view.
- `GET /{name}/schema` always returns a complete `defaultState`, and it is the one
  place friendly names leave the server: a definition's `columnLabels` become the
  default report's `labels` unless the configured state carries its own. When no
  default is configured the server synthesizes an empty state — which, by the
  null-columns rule, means every schema column in database order, flavored by the
  mapping. A client never invents its own notion of "the default report".

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
human labels; empty `values` ⇒ implicit counts) · `chart` (below). Caps:
`maxPivotColumns` per definition (default 60), a hard 10,000-group pivot source
ceiling, and `maxChartPoints` per definition (default 1,000, ceiling 10,000) — all
surface as precise 400s. Grid-only features (breaks, highlights, grid aggregates,
non-dim sorts) are noted in `ignored[]` in alternate views, never fatal.

**Chart view** (APEX-style: one chart per report, single metric):

```json
"view": {
  "mode": "chart", "type": "bar",
  "label": "STATUS", "value": "AMOUNT", "fn": "sum",
  "orientation": "vertical",
  "sort": { "by": "value", "dir": "desc" },
  "labelAxisTitle": "Status", "valueAxisTitle": "Total"
}
```

- `type`: `bar | line | area | pie`. `label`: any text/number/date/bool column
  (computed included). With `fn`, the composer groups by the label and aggregates
  `value` through the shared grouped shape; `fn: "count"` may omit `value` and
  becomes `COUNT(*)`; without `fn`, every filtered row is a point and `value` must
  itself be a number column.
- **Chart validation is stricter than grid aggregation**: the metric must come out
  numeric, so `min/max` chart only number columns (grid aggregation also allows
  date/text). The schema endpoint advertises the stricter set as
  `capabilities.chartAggregateFunctions`.
- The chart query runs over the **complete filtered rowset** (computed columns,
  filters, search — never the visible page). Sorting lives inside the spec
  (`sort.by: label|value`, value sorts tie-break on the label); grid `sorts` are
  reported in `ignored[]` while chart view is active. `orientation` and the axis
  titles are presentation carried in state; pie ignores them.
- The response keeps the generic two-column shape: the label column as itself plus
  the metric (`v0` labeled like `sum(Amount)`, `__count` for bare counts, or the raw
  value column). Exceeding `maxChartPoints` is a precise validation error — the
  server never silently truncates a chart, because a truncated pie misstates
  proportions. Export in chart view emits exactly the charted points.

**Resilience:** structural state elements referencing columns that no longer exist are
dropped into `ignored[]`. Expressions are typed programs, so an unknown referenced column
is a precise validation error. Disabled filters/highlights are not parsed or planned,
which lets an off instruction remain in saved state while its schema is being revised.

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
→ compile enabled expression rules                 typed definition/predicate/decoration plan
→ build core query:
    wrap base SQL as subquery (ir_base)
    [if computed columns] second wrap layer:
        SELECT ir_base.*, <expr> AS c1 FROM (base) ir_base  → AS ir_calc
        (aliases become filterable/sortable universally — no dialect
         supports referencing a SELECT alias in WHERE reliably)
    apply filter predicates and search
→ derive via Clone():
    page query   (+ private highlight predicate projections + ForPage)
    count query  (ClearComponent order → AsCount)
    aggregates   (ClearComponent order/limit → SELECT fn(col)…)
    break totals (… → GROUP BY break cols + aggregate fns)
→ compile (dialect compiler) → execute (provider-neutral DbCommand/DbDataReader, CancellationToken)
→ post-process in C#: projection markers → ordered highlight hits; pivot transform
→ remove private projections and shape visible rows
→ shape response
```

The execution path is split by responsibility rather than view mode:

- `ReportExecutor` is the application-service orchestrator. It selects the view path and
  coordinates validation, composition, execution, and response timing.
- `ReportConnectionManager` owns opening connections and applying trusted session policy,
  including timezone configuration.
- `ReportQueryReader` owns command compilation/execution and maps the engine's stable
  ordinal query layouts into provider-neutral rows.
- `PivotTableBuilder`, `ReportResultColumns`, and `ReportRowProjector` are database-free response shapers. Pivot
  mechanics and protocol metadata can evolve without changing connection code.
- `StateValidator` is the validation facade. Feature validators own effect metadata such
  as computed-column identity and highlight scope/style. `ExpressionRuleCompiler` is the
  single enabled → metadata → parse/bind → result-contract pipeline for computed columns,
  filters, and highlights. It produces an `ExpressionRulePlan` whose typed effects keep
  definition, row-predicate, and decoration phases explicit.
- `ExpressionRuleSqlApplicator` translates those typed effects into projection, `WHERE`,
  or private-marker SQL while `QueryComposer` remains responsible for phase ordering.
- `HighlightEvaluator` consumes database-computed markers, ordering row hits before cell
  hits. It does not reimplement expression semantics in memory.
- In the ASP.NET Core adapter, `ReportRequestAccess` owns per-definition authorization and
  server-trusted context parameters. Query and export share one state-request pipeline so
  their validation and sanitized error behavior stay aligned.

Derived queries run sequentially on one prepared connection per request. This keeps one
transaction/session context and remains SQLite-friendly; provider-specific parallelism is
a future optimization if measurements justify its extra connection pressure.

## 8. Expression language

Small, typed, and closed — a **documented portable subset**, not "whatever the target
database accepts". Used for computed columns and, in condition position, by both filters
and highlights.

**Pipeline (staged):** rule → enabled check → effect-metadata validation → *syntax*
(untyped tree with source positions; lexer + recursive descent, binary operators via a
Pratt precedence loop) → *bind* (schema + function registry → typed AST; all typing rules
live here) → result-contract check → typed effect → *emit* (registry-driven per-dialect
SQL). `ExpressionRuleCompiler` owns the common rule stages. Expression internals remain
`ExprSyntaxParser` → `ExprBinder` → `ExprEmitter`; `ExpressionRequirement.Value` and
`.Predicate` describe what the consuming effect accepts.

The compiled plan runs in dependency order: definition effects project computed columns
and extend the schema; row-predicate effects enter `WHERE`; decoration effects project
private highlight markers from the filtered rowset. The value pipeline is shared even
though each effect deliberately lands in a different query stage.

```
expr        := or
or          := and (OR and)*
and         := not (AND not)*
not         := NOT not | predicate                 (NOT binds looser than comparisons)
predicate   := additive ( cmp additive | IS [NOT] NULL
             | BETWEEN additive AND additive )*    (inclusive; bounds never reordered)
cmp         := '=' | '<>' | '!=' | '<' | '<=' | '>' | '>='     (!= normalizes to <>)
additive    := term (('+'|'-'|'||') term)*         (date ± n = whole calendar days)
term        := factor (('*'|'/') factor)*
factor      := number | 'string' | NULL | column | func '(' args ')'
             | '(' expr ')' | '-' factor | case
case        := CASE [expr] (WHEN expr THEN expr)+ [ELSE expr] END
func        := UPPER | LOWER | TRIM | LENGTH | SUBSTR | CONCAT
             | ROUND | ABS | COALESCE
             | CONTAINS | STARTS_WITH | ENDS_WITH | IN_LIST
             | YEAR | MONTH | DAY                  (date part extraction)
             | NOW | TO_DATE | TO_STRING | DATE_TRUNC
args        := expr (',' args)*
```

**Type discipline.** Values are number/text/date. Conditions (boolean) arise from
comparisons, `BETWEEN`, `IS [NOT] NULL`, and `AND`/`OR`/`NOT`, and are consumed by
searched-CASE `WHEN`s, row conditions, and by `NOT`/`AND`/`OR`. A computed column's result must be a
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

**Dates** (SQL comparison semantics — there are no `BEFORE`/`AFTER`/`BETWEEN()`
functions; the operators are the vocabulary):
- `NOW()` is the engine's current timestamp: session-local where the engine has a
  session timezone (Oracle `LOCALTIMESTAMP`, Postgres `NOW()`), the server's clock
  where there is no such concept (`GETDATE()`, SQLite `datetime('now','localtime')`).
- `TO_DATE(value)` converts canonical ISO `YYYY-MM-DD` text to a date at midnight; a
  Date input is an identity conversion, which keeps expressions portable when one
  provider discovers a column as Date and SQLite discovers it as Text. String
  literals are validated at bind time; column contents are the documented ISO
  contract — invalid rows become a provider error or NULL. Text is never implicitly
  a date. Input format masks are deferred (a future second argument would use a
  portable, validated vocabulary, not native masks).
- `DATE_TRUNC(unit, date)` returns the start of the unit; the unit is a
  case-insensitive string **literal**, initially `DAY`/`MONTH`/`YEAR` (WEEK deferred
  until week-start semantics are defined; QUARTER/HOUR/MINUTE can be added later).
- `date ± n` moves by **whole calendar days**. The binder requires integrality it
  can establish — integer literals, integer-typed columns, `YEAR/MONTH/DAY/LENGTH`,
  one-arg `ROUND`, and `+`/`-`/`*` over those — and rejects everything else
  (fractional offsets, `n + date`, `date - date`, `*`, `/`).
- Comparisons and `BETWEEN` (inclusive at both boundaries; reversed bounds are not
  reordered) use plain SQL semantics on the **complete temporal value** — equality
  means the full timestamp, and NULL comparisons are SQL-unknown. Calendar-day
  equality goes through `DATE_TRUNC('DAY', …)`; the timestamp-range idiom is
  half-open: `d >= DATE_TRUNC('DAY', NOW()) AND d < DATE_TRUNC('DAY', NOW()) + 1`.
- `TO_STRING(date [, format])` renders text (default `YYYY-MM-DD`; NULL in, NULL
  out). Formats are a closed portable vocabulary — tokens `YYYY MM DD HH24 MI SS`,
  separators space `-` `/` `:` `T` — validated at bind, translated per dialect
  (`FORMAT` with quoted separators / `TO_CHAR` with a double-quoted T / `strftime`),
  and bound as a parameter, never passed through as native format syntax. On SQL
  Server the culture is pinned (`'en-US'`) — the session language must not choose
  digits or calendar. Numeric formatting is a separate future design.
- Bare dates do not concatenate: `||` and `CONCAT` reject them with a pointer to
  `TO_STRING`. Implicit date-to-text rendering follows engine settings (session
  language, `NLS_DATE_FORMAT`, `DateStyle`) — the one place they would leak into
  report output, so the conversion stays explicit.
- **Timezone is connection configuration, not expression vocabulary.** The language
  assumes single-timezone, wall-clock data. A definition may set `TimeZone` (a region name
  or offset, bindable from appsettings): the executor pins the session when it opens
  the connection — `ALTER SESSION SET TIME_ZONE` on Oracle, `SET TIME ZONE` on
  Postgres — and `NOW()` then follows it. Unset means the server's own setting, and
  on engines with no session timezone (SQL Server, SQLite) a configured value is
  **deliberately ignored** — their clock is the server's/process's, pinned at the OS
  or service level if at all. Hosts can equally pin via their
  `IReportConnectionFactory` or connection string (e.g. Npgsql `Timezone=…`). Oracle
  connection pools keep session state, so definitions sharing a named connection
  should agree on `TimeZone`. UTC-stored columns and per-user timezones are out of
  scope — there is deliberately no timezone vocabulary in expressions.
- Date producers emit **typed NULLs** for literal NULL arguments (`CAST(NULL AS
  DATETIME2/DATE/TIMESTAMP)`; TO_STRING has the text equivalent): a bare NULL loses
  the type — Oracle makes `TO_DATE(NULL) + 1` a NUMBER, Postgres an INTERVAL, and
  Postgres cannot resolve `date_trunc` over an untyped NULL at all.
- SQLite stores dates as text, so the logical Date type rides on canonical
  `datetime()` text (`YYYY-MM-DD HH:MM:SS`): every date producer emits that form,
  and comparison sites (comparisons, BETWEEN, simple-CASE matching) normalize
  non-producer operands through `datetime()` — date-only text would otherwise sort
  before its own midnight timestamp.

**Function registry** (`ExprFunctions`): one entry per function — arity, argument
rules, result-kind inference, and an emission strategy. Adding a function is adding a
row; there is no enum and no switches to grow. `ExprFunctionEmitter` owns the dialect SQL,
while `ExprDateRules` owns the portable truncation and format vocabularies shared by bind
and emit. The emitter is the only dialect-specific function surface outside operators:

| AST | SqlServer | Oracle | Sqlite | Postgres |
|---|---|---|---|---|
| `SUBSTR(s,a[,n])` | `SUBSTRING(s,a,n)` (2-arg → `LEN(s)` for n) | `SUBSTR(s,a[,n])` | `SUBSTR(s,a[,n])` | `SUBSTR(s,a[,n])` |
| `a || b` / `CONCAT` | `CONCAT(a,b,…)` | `(a || b || …)` | `CONCAT(a,b,…)` (3.44+) | `CONCAT(a,b,…)` |
| `LENGTH(s)` | `LEN(s)` | `LENGTH(s)` | `LENGTH(s)` | `LENGTH(s)` |
| `ROUND(x,n)` | `ROUND(x,n)` | `ROUND(x,n)` | `ROUND(x,n)` | `ROUND(CAST(x AS NUMERIC), CAST(n AS INT))` |
| `YEAR(d)` | `YEAR(d)` | `EXTRACT(YEAR FROM d)` | `CAST(strftime('%Y',d) AS INTEGER)` | `EXTRACT(YEAR FROM d)` |
| `COALESCE` | `COALESCE` | `COALESCE` | `COALESCE` | `COALESCE` |
| `NOW()` | `GETDATE()` | `LOCALTIMESTAMP` | `datetime('now','localtime')` | `NOW()` |
| `TO_DATE(x)` (text) | `CAST(x AS DATETIME2)` | `TO_DATE(x,'YYYY-MM-DD')` | `datetime(x)` | `TO_DATE(x,'YYYY-MM-DD')` |
| `DATE_TRUNC('MONTH',d)` | `CAST(DATEFROMPARTS(YEAR(d),MONTH(d),1) AS DATETIME2)` | `TRUNC(d,'MM')` | `datetime(d,'start of month')` | `DATE_TRUNC('month',d)` |
| `TO_STRING(d,ƒ)` | `FORMAT(d,ƒ,'en-US')` | `TO_CHAR(d,ƒ)` | `strftime(ƒ,d)` | `TO_CHAR(d,ƒ)` |
| `d + n` / `d - n` | `DATEADD(DAY,±n,d)` | `(d ± n)` | `datetime(d,(±n)\|\|' days')` | `(d ± n*INTERVAL '1 day')` |

(ƒ is the portable mask translated into that engine's format vocabulary and bound
as a parameter.)

- The emitter produces SQL fragments **we** wrote, injected via `SelectRaw` with `?`
  bindings for every literal — client text never reaches SQL; only the AST does. The one
  keyword literal is `NULL` itself (ours, not client data). Every binary operation is
  parenthesized.
- `CASE`, comparisons, `BETWEEN`, `IS NULL`, and `AND/OR/NOT` emit **identically on
  every dialect** — the portable core; only functions, date arithmetic, and SQLite's
  date-comparand normalization (above) carry dialect idioms.
- Semantics notes: concatenation treats NULL as empty everywhere (CONCAT on
  SqlServer/Sqlite/Postgres; Oracle's `||` natively); `YEAR/MONTH/DAY` accept ISO date
  *text* because SQLite date columns discover as text — emitted natively where the
  engine converts text itself (SQLite strftime, SQL Server implicit ISO conversion)
  and with explicit conversions where EXTRACT is strictly typed (Oracle
  `TO_DATE(SUBSTR(x,1,10),'YYYY-MM-DD')`, Postgres `CAST(x AS TIMESTAMP)`). Non-ISO
  text in a date-part function is a runtime error on those dialects — ISO is the
  documented contract.
- Computed columns cannot reference other computed columns (no dependency ordering in v1).
- There is no `DATE '…'` literal: `TO_DATE('YYYY-MM-DD')` is the date literal, and its
  argument is validated at bind time. Date columns compare against `NOW()`, `TO_DATE`,
  `DATE_TRUNC`, and date arithmetic with plain SQL operators.

## 9. Dialect strategy

- SqlKata compilers: `SqlServerCompiler`, `OracleCompiler`, `SqliteCompiler`,
  `PostgresCompiler`. Dialect is declared per definition (not inferred) — explicit
  beats clever.
- SqlKata owns: identifier quoting, parameter naming, pagination syntax (including Oracle
  12c `OFFSET/FETCH`).
- We own (per-dialect semantic decisions, centralized in expression emission):
  - case-insensitive condition functions (`LOWER` both sides around `LIKE`);
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
- Column model: `Name, ClrType, ProviderType, IsNullable, Label` — the label is the
  server's neutral derivation (prettified name), nothing more. Friendly names never
  enter discovery; they are client-side presentation delivered through the default
  report (§4, §5).
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
zero-config to a local SQLite file `App_Data/interactivereport.saved.db`. Each operation
uses one validated configuration snapshot, and auto-creation is tracked per
connection/dialect/table target so live configuration changes cannot mix query and storage
targets or inherit stale initialization state.

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
<interactive-report report="open-orders" api-base="/api/reports"></interactive-report>

<script type="module" src="/api/reports/ui/ir-admin.js"></script>
<interactive-report-admin api-base="/api/reports"></interactive-report-admin>
```

- Custom elements with no runtime dependencies: bundled ES modules are embedded in
  the assembly and served at `{prefix}/ui/{file}` by `MapInteractiveReports`. `base`
  defaults to the prefix the script was loaded from. The explicit `api-base`
  attribute overrides that inference; `base` remains its compatibility alias.
  `report` is an optional preferred report name. Initialization resolves it against
  `GET {prefix}` (the caller's authorized, uniquely named report catalog); an absent
  or unavailable preference falls back to the first visible report without probing
  the unavailable name. If a catalogued preference fails to initialize, the widget
  tries the remaining visible reports in catalog order. Multiple visible reports
  produce a toolbar selector.
  Changing the `report` or API location re-initializes in place. Consumers need no
  build step.
- Each element renders into its own shadow root, including menus and dialogs. The
  stylesheet is compiled into the JavaScript bundle, so host resets and utility
  classes cannot enter the component and component styles cannot escape onto the
  host page. Hosts can theme documented `--ir-*` custom properties on the element.
- Source modules: `ir.js` (element, toolbar/menus), `ir-state.js` (pure normalization,
  serialization, and scoped-search expression construction), `ir-api.js`
  (fetch + problem+json), `ir-ui.js` (menu/dialog primitives), `ir-render.js`
  (chips, grid, pager), `ir-dialogs.js` (the Actions dialogs), `ir-admin.js`
  (admin element), `ir.css`. `npm run build` uses esbuild to compile the stylesheet
  and modules into two self-contained entry bundles in `Ui/dist`; these generated
  assets are committed so a normal .NET build does not require Node.js.
- **Feature surface**: scoped toolbar search (all text columns or one typed column → expression filter);
  Actions menu (Columns shuttle, Filter, Sort, Control Break, Highlight, Aggregate,
  Compute with token-insert helpers, Group By, Pivot, Chart, Save/Save As/Delete/Reset,
  CSV download); column-header menus (sort/rename/hide/break/filter — Rename writes a
  `labels` override for base columns and edits the rule label for computed ones; blank
  restores the schema default); settings chips with
  APEX-style enable/disable checkboxes for expression rules; break groups with per-column subtotal rows and
  grand-total rows; row/cell highlights; groupBy/pivot rendering; saved-report select
  (Primary Report + Global/Private groups); `ignored[]` and problem+json surfaced as
  notices — validation problems render *inside* the originating dialog, which stays
  open (apply is optimistic: mutate, re-query, roll back on failure).
- **Chart rendering**: `ir-chart.js` is a third self-contained bundle (Chart.js
  tree-shaken to bar/line/area/pie plus scales, tooltip, legend, filler) that the
  main bundle loads with a runtime-computed dynamic `import()` the first time chart
  view opens — grid-only pages never fetch it, and the URL resolves relative to the
  served `ir.js` so it works under any prefix. All Chart.js configuration stays in
  that module; none enters protocol or saved state. Colors, grid lines, and label
  ink come from `--ir-chart-1..8`, `--ir-chart-grid`, and `--ir-chart-text` tokens
  (slot 1 doubles as the single-series color; the categorical order is fixed and
  CVD-validated on the light surface). The canvas carries `role="img"` with a
  generated description ("Bar chart of Sum of Amount by Status, N data points"),
  and a "View chart data" disclosure renders the same label/value dataset as a
  real table — canvas pixels are invisible to assistive tech, so the table is part
  of the feature, not polish. The widget destroys the Chart.js instance on view
  switches, report changes, and disconnect; a canvas without a 2d context (headless
  hosts) degrades to the description + table rather than erroring.
- **Enabled state:** computed, filter, and highlight checkboxes write their canonical
  `enabled` property, which survives saving and export. Breaks and aggregates have no
  enabled protocol state, so their chips edit or remove them without a false toggle.
- Schema metadata advertises `stateVersion`, expression functions, and aggregate
  functions by column type. Query results include the effective base+computed schema,
  so clients do not duplicate language catalogs or guess computed types.
- **Styling**: shadow DOM isolates every rule, including the bundled styles for
  popups and dialogs. Deliberate theming crosses the boundary through the supported
  `--ir-*` custom properties and `::part()` names; ordinary host selectors cannot
  reach internal controls, and no stylesheet is added to the host document.
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
  highlights evaluated server-side with SQL-parity NULL semantics.
- **M4 — Views & export** ✅ *(2026-08-05)*: groupBy view (pushed down, group-count
  pagination), pivot-in-memory view (capped, implicit-count default), CSV export with
  truncation signaling — all three views export through the same pipeline.
- **M5 — Persistence & proof:** ~~saved reports (private/public, per user)~~ *(done
  early — see §13, including administration/whoami)*; SQL Server + Oracle verification
  passes; hardening (timeouts, caps, logging discipline). *Prep complete 2026-08-05:
  env-gated live-dialect battery (docs/TESTING.md), condition × dialect golden matrix,
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
  condition matrix, `EXTRACT` date
  parts, `ROUND` signature casts, native-boolean condition emission (the inverse of
  SQL Server's `= 1` lowering), quoted-identifier saved-report DDL, identifier-folding
  absorbed by case-insensitive schema matching. Live battery green 53/53 across
  SQL Server + Oracle + PostgreSQL after shared filter/highlight predicates and
  highlight projections were added.
- **M9 — Charts** ✅ *(2026-08-06)*: APEX-style chart view (bar/line/area/pie, one
  label + one numeric metric, optional aggregation, chart-owned sort, orientation,
  axis titles) as a fourth `ViewMode` — additive `ViewSpec` fields, no state-version
  bump. Composition reuses the shared grouped shape over the filtered core;
  `maxChartPoints` (default 1,000) rejects oversized charts precisely instead of
  truncating; chart-mode export emits the charted points. Packaged UI gains the
  Chart dialog/chip/view with a lazily loaded tree-shaken Chart.js bundle, chart
  theme tokens, and a canvas description + "View chart data" table for assistive
  tech.
- **M10 — Friendly names** ✅ *(2026-08-07)*: client-side display names. A definition's
  `columnLabels` and a state's `labels` map (additive field, no version bump) carry
  real column name → display label, and the server's only involvement is delivering
  the mapping as the default report's labels on the schema endpoint — which now always
  returns a complete `defaultState`, synthesizing an empty one when unconfigured. The
  engine, validation, result metadata, and CSV export never interpret labels; the
  packaged UI resolves display names from its state document (grid, chips, dialogs,
  synthetic groupBy/chart metric labels) and gains header-menu Rename with
  blank-restores-default semantics.

## Appendix: decision log

| Decision | Alternative | Why |
|---|---|---|
| Live pushdown (SqlKata) | SQLite/DuckDB staging | One code path; no refresh/cache-keying/type-shim lifecycle; APEX-faithful liveness. Staging can return behind the same protocol if source load demands it. |
| Definitions in config by name | Client-supplied SQL | Trust boundary; SQL never crosses the wire; definitions version with the app. |
| Borrow host auth | Engine-owned API keys | Hosts (Umbraco et al.) already have real auth; a second mechanism would be weaker and clash. |
| POST-primary protocol | GET + querystring state | State size; filter values leak into logs via GET; deep links return later as saved-state ids. |
| Rows as JSON objects | Positional arrays | Page-granularity size difference is negligible; consumption ergonomics win. |
| Highlight predicates push down as private booleans; pivot stays in C# | Interpret highlight expressions in C# or use native PIVOT | Filters and highlights share one typed predicate implementation; private markers are removed before the response. Pivot still avoids the least-portable SQL surface. |
| `net8.0` | `net10.0` | Umbraco 13 LTS floor; SDK 8 present; bump is cheap later. |
| whoami off by default | always on | It's an information endpoint; enabling is a deliberate operator act (samples enable it). |
| Admin match case-insensitive exact | case-sensitive | Operator-friendly for emails/usernames; GUID-style values don't collide under folding. |
| Saved-report ids are text GUIDs | identity/sequence columns | One DDL shape across SQLite/SqlServer/Oracle; no sequence plumbing. |
| Timestamps as ISO text, flags as 0/1 | native per-dialect types | Uniform semantics and sorting across dialects for an engine-internal table. |
| Global mutations admin-only, even for the owner | owner-managed globals | A published report is shared infrastructure; publishing and unpublishing are curation acts. |
| Microsoft.Data.Sqlite dependency in the AspNetCore package | host-supplied providers only | The zero-config default saved-report store must work with no host setup; report-data connections remain host-supplied. |
| Decimal parameters bind as double on SQLite | decimal-as-TEXT (provider default) | The provider's TEXT binding breaks comparisons against affinity-less expressions (computed columns) via SQLite's cross-type ordering; double is SQLite's native numeric storage, so the conversion is faithful to the engine. |
| Pivot caps: 60 column groups (configurable) + hard 10k source groups | unbounded pivot | An unbounded pivot is a memory/usability grenade; the caps surface as precise 400s telling the user what to change. |
| Chart overflow is a precise 400, never truncation | truncate at the point cap like grid export | A truncated bar chart is misleading; a truncated pie is a lie — its proportions claim to describe the whole. Export truncation keeps its header signal; charts get an error naming the cap. |
| One chart, one metric (APEX model) | multi-series charts | Covers the dominant reporting ask with a small state surface; multi-series, click-to-filter, legends-as-controls, image export, and "Other" folding stay open as increments that extend — not rework — this shape. |
| Chart.js in a lazily imported third bundle | Apache ECharts; server-rendered SVG | Chart.js covers exactly bar/line/area/pie, tree-shakes small, MIT. ECharts earns its size only when multi-series/zoom/dense-data arrive. The lazy chunk keeps grid-only pages at their old weight; embedding keeps the no-CDN packaging story. |
| CSV: UTF-8 BOM, label headers, X-IR-Truncated header | bare UTF-8, name headers, silent truncation | Excel needs the BOM to detect encoding; users recognize labels, not internal names; silent truncation reads as complete data. |
| Views share the export pipeline | grid-only export | Exporting "what the view shows" falls out of running the same validated state unpaged — no special cases. |
| Oracle BindByName via reflection in CommandBuilder | reference ODP.NET from Core | ODP.NET binds by position by default; context params appear first in SQL but are added last, so positional binding silently misbinds. Reflection keeps Core provider-free. |
| UI: no-build vanilla ES modules as custom elements | React/Vite bundle | No node toolchain in a .NET repo; embeddable in any host (Umbraco: one script tag + one element); the protocol keeps the JS dumb enough that a framework buys little. |
| UI assets embedded in the AspNetCore assembly, served under the mapped prefix | RCL static web assets | Zero host setup (`UseStaticFiles`/`_content` not required), one mapping call delivers API + UI, works identically in any host. |
| UI asset endpoint `AllowAnonymous` | inherit group auth | Assets are public package code (readable on any feed); an auth-gated script tag turns "session expired" into a blank region that can't even say "sign in". Data endpoints keep the full gate. |
| Asset ETags hash content | assembly version tag | Version-tagged ETags 304 stale content across rebuilds of the same version (bitten in dev; would bite ops on patch releases). |
| Light DOM + `.ir-*` prefix + CSS custom properties | Shadow DOM | Hosts want to theme the region, not fight encapsulation; prefix discipline is enough isolation and keeps the DOM inspectable. |
| Expression-rule `enabled` is canonical protocol state | strip disabled instructions | A saved computed column, filter, or highlight is either on or off; disabling does not delete the author's expression, label, or color choice. |
| Compiled rule + typed effect plan | separate computed/filter/highlight expression pipelines | Parsing, binding, result contracts, and enabled behavior are one pipeline; definition, row-inclusion, and decoration effects still make query placement explicit. |
| Cell styles apply after row styles | depend on rule order | Cell highlighting has explicit priority over the background/foreground inherited from a row highlight. |
| Expression pipeline staged: untyped syntax → bind → emit | grow the single-pass parser | NULL, CASE result inference, and overloads need types the parser can't know mid-parse; positions survive to the error message; each stage is testable alone. |
| Function registry (arity/rules/inference/emitters as data) | ExprFn enum + switches | Two switches per function was already drift-prone at 12 functions; a registry row is one place, and the registry doubles as the subset's documentation. |
| Bool is internal to expressions; computed columns must yield values | allow boolean results | SQL Server has no scalar boolean; the error teaches the portable form (CASE WHEN … THEN 1 ELSE 0 END) instead of failing per-dialect. |
| `x = NULL` and simple-CASE `WHEN NULL` are rejected | let SQL's null semantics apply | Both silently never match — silence is the one thing a validation layer must never emit; the errors point at IS NULL / searched CASE. |
| Bool-valued columns lower to `= 1` predicates in condition position | reject bare bool columns as conditions | `CASE WHEN IS_PRIORITY THEN …` is the natural spelling; T-SQL's bit-is-not-boolean rule is an emission detail, not a user error. Proven live on SQL Server. |
| NULL participates in arithmetic | number-only operands | Consistency: NULL already joined functions, concat, and CASE branches; `AMOUNT + NULL` failing while `CONCAT(NULL, …)` passed was an inconsistency, not a rule. |
| No date literals in v1 expressions | text-vs-date comparison | Implicit text→date conversion is an NLS/format trap on Oracle; a typed DATE '…' literal is the clean extension point. |
| Postgres ROUND emits signature casts | bind precision as int | `round(numeric, integer)` is the only two-arg ROUND Postgres has; casting both arguments in SQL keeps bindings uniform across dialects (goldens: 2 always binds as decimal). |
| Date parts on ISO text convert at emission (Oracle TO_DATE, Postgres CAST) | dialect-aware binding rules | The portable subset's types stay dialect-free; EXTRACT's strictness is an emission detail. Rejecting text outright would break the SQLite date-as-text story the feature exists for. |
| Dates use SQL comparison operators and BETWEEN | `BEFORE()`/`AFTER()`/`BETWEEN()` functions | No new vocabulary to learn and nothing to mistranslate: `<`/`>=`/BETWEEN already mean the right thing in SQL, and the binder's same-kind rule keeps them typed. `TO_DATE('…')` doubles as the date literal, superseding the planned `DATE '…'`. |
| Date arithmetic is whole calendar days, integrality established at bind | intervals or fractional days | Whole days cover the reporting cases; "provably integral or rejected" beats each dialect truncating fractions differently and silently. |
| NOW() is session-local (LOCALTIMESTAMP on Oracle) | UTC everywhere; Oracle SYSDATE | Stored DATE columns are wall time, so UTC would sit an offset away. SYSDATE looks idiomatic but follows the DB host's clock, not the session; LOCALTIMESTAMP honors the session-timezone contract. SQL Server and SQLite have no session timezone — there the server's clock is the only clock. |
| SQLite logical Date = canonical datetime() text, normalized at comparison sites | native-typed dates; compare raw text | SQLite has no date type — producers emit one canonical text form and comparisons wrap stray operands in datetime(), because date-only text sorts before its own midnight timestamp. Physical storage stays text; the type system stays honest. |
| TO_STRING formats are a closed token set, translated and bound per dialect | pass native masks through | Native masks don't port (strftime ≠ TO_CHAR ≠ .NET) and raw pass-through would hand client text to the SQL layer. A validated vocabulary translates exactly and the mask still rides as a binding. |
| Timezone pins at the connection (definition `TimeZone`), not in expressions | AT_TZ()/NOW('UTC') vocabulary; UTC everywhere | A report's clock is a property of the data source, not of each expression: the definition's `TimeZone` pins the session at open, and unset means the server's setting. Engines without a session timezone (SQL Server, SQLite) silently ignore the setting rather than erroring — it requests session behavior that simply doesn't exist there, and their clock follows the host either way. Expression-level vocabulary couldn't fix them portably and would hand a timezone decision to every report author. |
| Bare dates rejected in concatenation | implicit rendering; auto-wrap in TO_STRING | Implicit date-to-text is the one place engine settings (session language, NLS, DateStyle) would leak into output, and it differs per engine. Rejection matches the language's explicit-conversion rule (TO_DATE inbound, TO_STRING outbound); auto-wrapping would pick a format silently. |
| Friendly names are client-side only; the server delivers `columnLabels` as the default report's `labels` and never applies them | apply labels server-side at discovery/validation so results and exports carry them | Display naming is presentation, and presentation belongs to the client: the engine's schema, validation, results, and exports speak real names and neutral labels, so no execution surface depends on captions. The cost is accepted openly — server-rendered artifacts (CSV headers, synthetic `sum(…)` labels in metadata) use server labels, with the client rebuilding grid/chart synthetics from the view spec it authored. |
| Report `labels` maps are opaque state, not validated | ignored[]/errors for unknown or blank entries | The server validates what it executes; a map it never reads has nothing to validate. Unknown keys are unused display data, exactly as resilient as a saved report is entitled to be. (Config-side `columnLabels` still fail fast on blank/case-colliding entries — config mistakes, not state.) |
| Schema endpoint synthesizes an empty `defaultState` | nullable `defaultState` | Every client would otherwise invent its own "no default configured" behavior. An empty state already *means* the right thing — all columns, database order — and it is also the delivery vehicle: the definition's mapping rides down as its `labels`. |
