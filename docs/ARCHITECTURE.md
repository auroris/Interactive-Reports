# InteractiveReport — Architecture

An Oracle APEX Interactive Reports equivalent for ASP.NET Core: the developer defines a
report as a single SELECT statement; end users get runtime filtering, sorting, control
breaks, aggregates, computed columns, highlighting, alternate views, and saved report
states — all composed server-side into one recursive relational plan. A request executes
the bounded statements that plan needs for discovery, count, totals, and page data.

Engine-first design: the product boundary is a JSON protocol. UI is a replaceable
consumer of that protocol.

---

## 1. Core principle: the trust boundary

**The developer owns SQL. The client owns state. State is data, never code.**

- Report definitions (configured SQL, connection, limits, authorization) live server-side,
  referenced by friendly name. Bare SQL never crosses the network in either direction.
- The client sends a *report state document* (JSON): an unordered map of named table
  compositions, their ordered operations, paging, and an active table id.
- The complete document is structurally bounded up front. Every operation that enters
  compilation is then validated at its exact position in the recursive relation:
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
sorts, aggregates, group-by, computed columns, Pivot's portable grouped/conditional
relation, and private true/false projections for highlights. C# converts private
projections into response metadata, but every child-visible table remains SQL that can
be wrapped by another named table. Native provider-specific PIVOT syntax stays outside
the portability surface.

## 3. Solution layout

| Project | Responsibility |
|---|---|
| `src/InteractiveReport.Core` | State model, canonical planning, expression parser, SQLKata lowering, execution, schema discovery, highlight evaluation, SQL-backed named-table Pivot, export. No ASP.NET dependencies. |
| `src/InteractiveReport.AspNetCore` | Endpoint mapping (`MapInteractiveReports`), standard OpenAPI metadata and public wire contracts, config-backed definition store, auth integration, JSON protocol shaping, coded errors. `Ui/dist` holds the generated client assets (§14), embedded and served by the same mapping. |
| `src/InteractiveReport.GraphQL` | Optional GraphQL.NET transport over saved reports. Looks up every origin through `ISavedReportStore` and reuses ASP.NET authorization, context resolution, validation, and execution. |
| `src/client` | Product UI source modules and the three browser-bundle entry points. |
| `samples/Workbench` | Dev harness: SQLite sample DB. `index.html`, `fr.html`, and `admin.html` host the packaged report in English and Canadian French plus the administration element; Swagger UI and GraphiQL expose the REST and GraphQL developer surfaces. |
| `tests/InteractiveReport.Core.Tests` | Composer golden tests (state doc → expected SQL, ×4 dialects), expression parser tests, SQLite end-to-end integration tests. |
| `tests/InteractiveReport.AspNetCore.Tests` | HTTP, saved-report, authorization, configured-document, and GraphQL transport integration tests. |

Target framework: `net8.0` (Umbraco 13 LTS floor; builds under SDK 8/10). Package
dependencies pin 8.0.x for the same reason. Shared NuGet metadata (MIT, repo URL,
Source Link, snupkg symbols, version) lives in the root `Directory.Build.props`;
`scripts/pack.ps1` (`npm run pack`) builds the client bundles, runs the fast test
layers, and packs the three `src/` projects — an MSBuild guard fails Release builds
and packs when `Ui/dist` is empty, so a UI-less package cannot ship. Publish
automation is deliberately undecided (owner discussion pending); packing is the
scripted boundary today.

## 4. Report definitions

Bound from `IConfiguration` (v1), behind an interface so a database-backed store can
exist later without touching the engine:

```csharp
public interface IReportDefinitionStore
{
    ValueTask<ReportDefinition?> Find(string name, CancellationToken ct);
}
```

The optional `IReportDefinitionAuthorizationStore` resolves only canonical name and
authorization settings. The HTTP and GraphQL adapters call the centralized
authorization module with that envelope before the configuration store validates
connections or hydrates the executable definition. Stores that do not implement the
optional interface retain the original full-definition lookup path.

```json
"InteractiveReport": {
  "Reports": {
    "open-orders": {
      "title": "Open Orders",
      "dataSource": "MainDb",            // ConnectionStrings name (no '='), or a literal connection string
      "sql": "SELECT o.ORDER_ID, o.CUSTOMER, o.AMOUNT, o.ORDER_DATE FROM ORDERS o WHERE o.SALES_REP = @currentUser",
      "columnLabels": { "ORDER_ID": "Order #", "CUSTOMER": "Customer Name" },
      "editLink": {
        "urlTemplate": "/orders/{ORDER_ID}/edit",  // {COLUMN} = definition-schema columns
        "label": "Edit order",                     // pencil aria-label/tooltip; default "Edit"
        "target": "_self"                          // or "_blank" (rendered with rel="noopener")
      },
      "columns": {
        "AMOUNT": { "helpText": "Order total before tax." },
        "NOTES": { "hideLabel": true, "sortable": false, "filterable": false }
      },
      "contextParams": { "currentUser": { "claim": "sub" } },
      "authorization": { "policy": "SalesRead" },
      "features": [ "search", "filter", "sort", "pagination", "savedReports", "download" ],
      "maxRows": 100000,
      "defaultPageSize": 50,
      "maxPageSize": 1000,
      "documentFiles": [
        "ReportDocuments/open-orders.primary.json",
        "ReportDocuments/open-orders.finance.json"
      ]
    }
  }
}
```

```json
// ReportDocuments/open-orders.primary.json
{
  "title": "Default",
  "primary": true,
  "state": {
    "activeTable": "open-orders",
    "tables": {
      "open-orders": {
        "from": "definition",
        "schema": null,
        "composables": [
          { "kind": "select", "columns": [ "ORDER_ID", "CUSTOMER", "AMOUNT", "ORDER_DATE" ] },
          { "kind": "sort", "sorts": [ { "col": "ORDER_DATE", "dir": "desc", "nulls": "last" } ] }
        ]
      }
    }
  }
}
```

Notes:
- `dataSource` is the report's database, one property with two forms discriminated
  by `=` (every valid ADO.NET connection string is key=value pairs): a bare value
  names an entry under the standard `ConnectionStrings` section — a missing name is
  a fail-fast error, never silently a literal — and a value containing `=` is the
  literal string. The ADO.NET provider resolves from an explicit one-word
  `provider` (`sqlite` | `sqlServer` | `postgres` | `oracle`), else from the
  `ConnectionStrings:{name}_ProviderName` companion entry (the Umbraco/classic
  convention), else a fail-fast error listing the tokens. Only SQLite is a hard
  package dependency; the other drivers load reflectively from the host's own
  dependency graph, and a missing one fails at startup naming the exact package.
  The alternative is a code-registered named `connection`
  (`AddConnection(name, factory)`); a definition sets exactly one of the two, and
  connection names beginning `__ir:` are reserved.
- **Dialect is derived, never configured.** It is a property of the connection: a
  `dataSource`'s provider token fixes it, and a code-registered factory's dialect is
  detected from the connection type it creates (one unopened instance, zero I/O,
  cached; wrapper/profiler types the detector cannot recognize declare it at
  registration — `AddConnection(name, factory, ReportDialect.X)`). The definition
  store stamps the resolved dialect onto every snapshot before execution; a leftover
  `dialect` key in old configuration binds but is superseded. Configuration is
  validated at startup by a hosted service (host fails to start naming the fix);
  mistakes introduced later by a live config reload surface per request as the
  standard sanitized coded-error document.
- `contextParams` values resolve **server-side only** (claims by default; host may register
  an `IContextParameterResolver` for anything else). Client-supplied values can never bind
  to them — they are a separate parameter class from filter values. This is the
  `:APP_USER` pattern from APEX translated to claims, and it is the row-level security story.
- `columnLabels` maps real column names to friendly display labels for base queries
  whose column names aren't presentable. **Friendly names are client-side
  presentation**: the server never applies this map to discovery, validation, query
  metadata, or schema caches. It is handed to the client as the `labels` of the default
  report (§5), and the export path applies the submitted document's effective labels
  because a file is server-rendered presentation. Every executable column reference
  crossing the wire — filters, sorts, and `labels` keys themselves — still uses the
  real name. Blank or case-colliding entries are config errors; entries naming no
  actual column are simply unused display data.
- `features` is the server's packaged-control suggestion (APEX's per-action Actions-menu
  configuration collapsed to a flat token list). Absent suggests everything;
  present suggests exactly what is listed. Known tokens (`ReportFeatures`): `search`,
  `columns`, `rename`, `columnSettings`, `filter`, `sort`, `pagination`, `controlBreak`, `highlight`,
  `aggregate`, `compute`, `groupBy`, `pivot`, `chart`, `savedReports`, `download`.
  (`columnSettings` suggests the per-column settings dialog; its visibility checkbox
  additionally needs `columns`, whose visible-list it writes.) Unknown, blank,
  or duplicate entries fail fast at definition load. The schema endpoint always sends
  the resolved suggestion; without a client override the packaged UI removes the chrome for everything else
  (menu entries, view buttons, search bar, saved-report select — state a default or
  saved report already carries still displays, as locked chips). Embedding JavaScript may
  force any packaged control on or off; this changes only client presentation. Two tokens are also
  server-enforced with 403s because they persist or egress data: `download` at the
  export endpoint and `savedReports` at saved-report creation (existing saved reports
  stay governed by the §13 matrix so a config change never strands rows). The rest is
  deliberately presentation-level: the query endpoint accepts any valid state
  document, because hiding a dialog is not a data boundary — context params (§12) are.
  Note the JSON config binder reads `[]` as absent; to lock a report down, list the
  one or two features it should keep.
- Configured report SQL must not end with `ORDER BY` (breaks subquery wrapping on SQL Server; APEX has
  the same rule). Validated at definition load with a clear error by a comment-, string-,
  and quoted-identifier-aware scan for the clause at parenthesis depth 0 — `'order by'`
  as data or documentation never trips it, and comments cannot hide a real clause.
- `documentFiles` paths are relative to the host content root unless absolute. Each
  file is a `{ title, primary, state }` envelope around the ordinary state
  document. Every file joins the saved-report selector as a global read-only document.
  `primary` seeds the stored flag on first synchronization; administrators may later
  flag or unflag the row without modifying the file. Configured titles shadow database
  reports by case-insensitive title; the administrator list retains shadowed rows so
  they can be renamed or removed. PUT may change only the primary flag; other updates
  and DELETE return 403, while Save As remains available. Hosts must include the
  referenced files in their build/publish output.
- `editLink` renders APEX's edit pencil: a leading synthetic grid column whose anchor
  navigates to `urlTemplate` with `{COLUMN}` placeholders substituted from the row
  (values URL-encoded; the result still passes the renderer protocol allowlist).
  It is definition chrome, not a feature token, never in report state, and independent
  of the user's column selection: template columns are schema-bound
  and projected as hidden row data exactly like renderer source columns, so hiding a
  referenced column never breaks the pencil. The header is visually empty with the
  label as its accessible name; a row whose placeholder value is NULL renders no
  pencil (`CASE`-null a key in the configured SQL to withhold rows, as the actions
  convention does); the column never appears in pickers, search, sorts, filters, or
  exports, and only grid mode shows it — grouped and pivoted rows have no single
  source row to edit. Template syntax fails fast at load (unmatched/empty/nested
  braces, zero placeholders, non-http(s) absolute URLs); a placeholder naming no
  live schema column disables the pencil for that schema (omitted from the payload,
  logged, and surfaced as an `ignored[]` notice on queries) rather than erroring.
  Config keys cannot express column names containing `:` (a configuration-binder
  limitation shared with `columnLabels`).
- `columns` is the per-column override map (keyed by definition column name,
  case-insensitive; unknown names tolerated like `columnLabels`): `label` supersedes
  `columnLabels` — configuring the same column's label in both maps fails fast, and
  `columnLabels` remains supported as the label-only shorthand; `hideLabel` blanks
  the table header cell while menus, dialogs, pickers, break headings, and the
  accessible name keep the real label (labels can never be blank — this flag is the
  APEX empty-heading pattern without unnameable columns); `sortable: false` removes
  the column's sort controls and control breaks (breaks force `ORDER BY`);
  `filterable: false` removes its filter controls and expression tokens;
  `helpText` renders as a note at the bottom of the column's header menu (a report
  whose effective client control policy removes every header-menu feature has no menu to carry it).
  Computed columns are always exempt — restrictions bind to definition columns, so
  `ir1 = AMOUNT * 2` stays sortable while `AMOUNT` is not, with no transitive
  analysis to reason about. Enforcement follows the whitelist philosophy: the
  client hides the controls, and the server strips violating sorts, breaks, and
  filter rules from incoming documents into `ignored[]` — stale saved reports
  degrade visibly instead of erroring — while a `defaultState` that contradicts
  its own definition fails fast at load. This is presentation-level courtesy, not
  a data boundary (context params, §12, remain the security story).
- Definitions version in git and deploy with the app: schema changes and report changes travel together.

## 5. Report state document

The single artifact that is simultaneously the request body, the saved report, and the
shareable view state. It is self-describing and carries no document version number.

**The model: named, recursively composed relations.** The developer's configured SQL
is the reserved input `definition`. A document contains an unordered `tables` map.
Every table says `from: "definition"` or names another table. When it names a table,
the server first completes that parent's exported relation, wraps the exported SQL as
the child's source relation, and carries only the parent's public schema and explicitly
inheritable metadata forward. The parent's local result instructions never cross the
edge. The server then applies the child's own `composables`. This repeats at every `from` edge;
`activeTable` selects the completed relation to execute. Map order has no meaning, and
identifiers such as `base`, `pivot`, or `summary` have no engine meaning.
`definition` is the sole reserved input sentinel and cannot itself be a key in
`tables`; every other nonblank, case-unique table id is opaque to the engine.

Composable kinds are data, not subclasses. The `composables` array is serialization
storage, not execution order. The canonical planner classifies each declaration and
normalizes a table into its natural partial order: one optional `group`, `pivot`, or
`chart` shape; dependency-ordered `compute` columns; `filter` predicates; structural
metadata; and owner-local result instructions. Independent declarations commute, while
genuine dependencies are explicit. Conflicting shapes, output identities, singleton
instructions, or metadata assignments fail instead of being resolved by array position.
Mutable document DTOs stop at this normalization boundary. The named-table compiler
binds typed canonical nodes directly against the schema produced by the preceding node;
each node owns its original document path, so validation does not reconstruct document
objects or translate diagnostics from a lowered array position.

`select`, `sort`, `break`, `aggregate`, and `highlight` are local result instructions:
they apply when their owning table is active, but are not encoded as projection order,
footer rows, or decoration inside a child derived table. Full renderer and style choices
are local as well. A child consumes only the parent's completed export and starts with an
empty local result.

`search` is a transient request overlay rather than an inherited table composable. It
filters the completed active relation, after that table's Shape, computed columns, and
filters. It never enters the memoized export, changes a schema cache, or affects a child.
`page` likewise applies only to the active table's local response.

```json
{
  "search": "acme",
  "page": { "index": 1, "size": 50 },
  "activeTable": "pivot",
  "tables": {
    "base": {
      "from": "definition",
      "schema": null,
      "composables": [
        { "kind": "compute", "computed": [
          { "id": "ir1", "enabled": true, "label": "Amount w/ Tax",
            "expr": "ROUND(AMOUNT * 1.0825, 2)" }
        ] },
        { "kind": "filter", "filters": [
          { "enabled": true, "expr": "IN_LIST(STATUS, 'SHIPPED', 'PENDING')" }
        ] },
        { "kind": "select",
          "columns": ["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "ORDER_DATE", "ir1"] },
        { "kind": "labels", "labels": { "ORDER_ID": "Ticket #" } },
        { "kind": "formats", "formats": {
          "CUSTOMER": { "displayAs": "link", "urlColumn": "CUSTOMER_URL" },
          "AMOUNT": { "mask": "currency:USD", "align": "right" }
        } }
      ]
    },
    "pivot": {
      "from": "base",
      "schema": null,
      "composables": [
        { "kind": "pivot",
          "rows": ["CUSTOMER"],
          "cols": ["STATUS"],
          "values": [
            { "id": "ir2", "col": "AMOUNT", "fn": "sum" },
            { "id": "ir3", "col": "ir1", "fn": "avg" }
          ] },
        { "kind": "compute", "computed": [
          { "id": "ir4", "enabled": true, "label": "Shipped Less Pending",
            "expr": "COALESCE(ir1203710688847562946, 0) - COALESCE(ir4118732696648452850, 0)" }
        ] },
        { "kind": "filter", "filters": [ { "enabled": true, "expr": "ir4 >= 1000" } ] },
        { "kind": "sort", "sorts": [ { "col": "ir4", "dir": "desc" } ] },
        { "kind": "labels", "labels": { "ir1203710688847562946": "Shipped Total" } },
        { "kind": "formats",
          "formats": { "ir8813168485634683321": { "mask": "decimal2" } } }
      ]
    },
    "groupBy": {
      "from": "base",
      "schema": null,
      "composables": [
        { "kind": "group",
          "by": ["CUSTOMER"],
          "values": [ { "id": "ir5", "col": "AMOUNT", "fn": "sum" } ] },
        { "kind": "filter", "filters": [ { "enabled": true, "expr": "ir5 > 1000" } ] },
        { "kind": "sort", "sorts": [ { "col": "ir5", "dir": "desc" } ] },
        { "kind": "break", "breaks": ["CUSTOMER"] },
        { "kind": "aggregate", "aggregates": [ { "col": "ir5", "fn": "sum" } ] }
      ]
    },
    "chart": {
      "from": "base",
      "schema": null,
      "composables": [
        { "kind": "chart", "type": "pie",
          "label": "CUSTOMER", "value": "AMOUNT", "fn": "sum" }
      ]
    },
    "group-chart": {
      "from": "groupBy",
      "schema": null,
      "composables": [
        { "kind": "chart", "type": "bar",
          "label": "CUSTOMER", "value": "ir5" }
      ]
    }
  }
}
```

`base`, `groupBy`, `pivot`, and `chart` illustrate the simple sibling layout authored
by the packaged UI: the latter three name `base` in `from`. Their names are still
opaque. `group-chart` illustrates a valid foreign composition one level deeper. Its
Chart consumes the completed Group SQL and schema; neither the server nor the document
format needs a special Group-to-Chart branch.

**Ancestry and relation-changing composables.** Each selected ancestry must terminate
at `definition`, must be acyclic, and may contain at most 64 named tables. Every child
wraps its completed parent Export once before applying its own canonical specification.
Group, Pivot, and Chart may therefore recur across several `from` edges, but a single
table owns at most one of them. Each Shape binds to its imported schema; for example,
Group can consume a parent Pivot's generated columns and Chart can consume a parent
Group metric. Invalid references
are precise validation errors, not reasons to classify the chain as an unsupported
table type. A composable can live on any table, including a table named `base` whose
`from` is `definition`; the name does not make it a special species. A terminal Chart
response runs unpaged under its point cap when both required Chart output columns remain
visible. Hiding either through terminal `select` intentionally yields an ordinary paged
table over the same completed relation. The completed parent is the semantic input; the
SQL planner may elide a wrapper that cannot affect the resulting relation.

**Structural and rule ceilings.** A submitted document may contain at most 64 tables,
a maximum `from` depth of 64, and 512 composables in total. These are server resource
bounds, not packaged-UI conventions, and include inactive alternatives because one
submission may ask the server to refresh all of their null schema caches. A selected
composition may declare at most 20 computed-column rules and 50 filter rules across
the relation-changing composables that build it; an active terminal response may
declare at most 50 highlight rules. Pivot groups/columns, Chart points, rows, paging,
expression depth, and command time carry their own caps. A shape accepts at most 256
metrics, a completed relation exposes at most 900 columns, and each Pivot may generate
at most 1,800 bound cell predicates. A selected chain may contain at most 256
relation stages, reduced to 22 on SQL Server to reserve room under its nested-query
limit for terminal wrappers. The cumulative guard is per compiled command: on any
supported dialect it may contain at most 2,000 bound parameters including server
context values.
Disabled rules still occupy their declared slot. Exceeding a ceiling is a precise
validation error at the document or composable path.

**UI classification and foreign authors.** The packaged UI normally authors four
simple sibling expressions: a definition-backed base plus Group By, Pivot, and Chart
tables whose `from` names that base. It switches by changing `activeTable`; alternate
configurations remain ordinary entries in `tables`. It recognizes those built-in views
by composable predicates, never by identifier or map position. If an external document
contains deeper or multi-compositor composition that does not map uniquely to one toolbar
mode, the UI keeps and submits it without choosing an arbitrary “first” table or
rewriting its composables. Participating relational and metadata composables from
ancestors remain visible as read-only chips; editors mutate only direct composables
owned by the selected table. The server remains the authority on whether the selected
expression is executable.

The client treats a document returned by the server as authoritative. Its readers are
liberal about harmless casing and outer whitespace and preserve unfamiliar content;
they do not impose a second semantic validator. New or edited UI-owned nodes use the
canonical protocol spelling, and the server canonicalizes the table references and
composable discriminators it compiles before returning or persisting the detached copy.

**Per-table schema cache.** Each table may carry `schema`, the complete public schema
most recently produced by its completed relation. `select` controls terminal display
visibility and therefore does not remove columns from this cache; a future relational
projection would be a distinct composable. The cache is advisory client data, not an
assertion about the configured SQL. The server never uses it to bind expressions,
authorize a request, or choose a query plan.

The client sets `schema: null` on a table whose input or exported relation composables
changed and on every transitive descendant that names it in `from`. Metadata and
owner-local edits are interpreted live without invalidating schemas, and search does not
invalidate schemas. Whenever a document is submitted,
the server recursively compiles every table whose cache is null from its completed
parent, writes the fresh schema into a detached copy, and uses live schemas for
validation throughout.
Query responses return that enriched copy as `document` alongside the requested data;
the client adopts it as its current document. Save and update validation perform the
same refresh before persistence. This makes omitted and explicitly null caches work for
new documents while letting edits refresh only the affected subgraph. The server also
replaces every cache it necessarily compiled while reaching those targets or the active
table, even when the submitted cache was non-null. Dormant alternatives that were not
compiled retain advisory snapshots; those snapshots never affect execution, binding,
or access control and are replaced when the alternative is selected or invalidated.

**Stable synthetic names.** Authored computed columns and Group/Pivot metrics share one
document-wide namespace (`ir1`, `ir2`, …), allocated when the user creates them rather
than from compiler traversal or array order. The implicit row count is normally `__count`. If that
name is already a dimension or metric in a later Group, leading underscores are added
until the count name is unique.

Pivot cell columns also use opaque `irN` logical ids. The server derives each id from
the stable owning-table id, metric id, and a length-framed, type-tagged Pivot key, then
registers it in the completed output contract. For example, the JSON sample above's
`pivot`/`ir2`/text `SHIPPED` identity produces `ir1203710688847562946`. Adding another
key or changing discovery order cannot rename an existing cell, and numeric `1`, text
`"1"`, null, dates, and binary values remain distinct. A deterministic salt avoids an
authored-id collision; the negligible case of two dynamic identities sharing a digest
is rejected instead of being resolved by discovery order. The report document stores no
registry or key-derived spelling. Expressions and metadata refer to ids returned in the
live schema. Labels carry the human form ("sum(Amount) · SHIPPED"); the id is identity,
not presentation.

**Expression rules:** computed columns, filters, and highlights all contain an `enabled`
flag and an `expr`. Computed columns must bind to a number, text, or date value; filters
and highlights must bind to a true/false condition. All three consume the complete
expression language in §8. A computed value defines a column, a true filter keeps the
row, and a true highlight paints its row or target cell.
Each highlight also has a display `name` and positive `sequence`. Within row or cell
scope, matching rules apply from lower to higher sequence, so the highest sequence wins
when rules set the same style. Cell highlighting has priority over row highlighting.
When sequence is omitted, canonical planning assigns unused ten-step values in stable
highlight-id order; array position is never precedence. `CONTAINS`, `STARTS_WITH`, and
`ENDS_WITH` are case-insensitive; `IN_LIST`
provides typed membership. Blank behavior is written explicitly as `IS NULL`, or
`IS NULL OR col = ''` when empty text should also count.
- `search` is the toolbar search: OR of `contains` across eligible text columns in the
  completed active relation. It runs after the active table's Shape, computed columns,
  and filters. This request overlay is neither stored nor inherited like a table
  composable and does not participate in schema discovery or cache invalidation.
- A `labels` composable maps current column names to display labels. It is
  presentation, never a program: unknown keys are unused display data, and query
  responses keep server-derived labels. The compiler carries completed column metadata
  across each `from` edge. Same-table declarations merge case-insensitively; conflicting
  assignments are ambiguous and fail, while any explicit `{}` clears inherited labels
  once before the table's overlays, independent of array position. Shape composables
  preserve metadata on pass-through columns and
  give synthetic columns explicit source provenance when a label can be inherited.
  The document is the single source of truth for what the user sees, so the one server
  consumer is **export**: the shared terminal renderer applies the active relation's
  completed labels to table and Chart headers. Query results and every schema cache
  remain structural. On a relation sourced directly from `definition`, same-key
  assignments override `columnLabels` defaults and an explicit `{}` clears all inherited
  defaults; a computed column still supplies its own structural label on its compute rule.
- A `formats` composable maps current column names to `{ mask, align, bold, italic,
  fg, bg, classes[], displayAs, urlColumn, textColumn, command, keyColumn }`. It is written by the Column
  Settings dialog. The owning table composes the complete format for its local result;
  disjoint assignments merge, conflicting assignments fail, and `{}` clears its
  accumulated view independent of document position. Across a `from`
  edge, only the safe scalar `mask` lineage is exported. Alignment, emphasis, colors,
  classes, renderer modes, commands, and renderer dependency columns remain local to
  their owner. Generated columns may inherit an exported mask through explicit
  provenance. The effective Default state can carry formats so definitions can ship
  default formatting. Masks are a closed client-side token vocabulary per
  column type (`integer`, `decimal1`…`decimal4`, `plain`, `currency:CAD|USD|EUR|GBP|JPY`,
  and `percent0`…`percent2` for numbers; `date`, `datetime`, `datetimeSeconds`, `time`,
  `timeSeconds`, `dateMedium`, `dateLong`, `dateTimeMedium`, and `dateTimeLong` for
  dates); unknown tokens and indigestible values fall
  through to default rendering — a mask is a lens, never a gate. Inline styling is the
  same constrained property set highlights use. `classes` selects rules from the
  application integrator's shadow-root stylesheet; the client accepts conservative CSS
  identifier tokens, drops malformed/reserved state, and refuses the component's
  `ir-` namespace in the dialog. A report document can select classes but cannot carry
  CSS or a URL. `displayAs` chooses ordinary text, link, image, or action rendering.
  Links identify URL and text source columns; images identify a URL source; actions
  carry `{ command, keyColumn }` — the cell's own value is the button label (a
  NULL/blank label renders no button, which is how a definition withholds an action
  from individual rows), and clicking dispatches a composed `ir-action` CustomEvent
  from the host element with `{ command, row, column }`, the row copy including the
  schema-bound `keyColumn` value. Actions are definition-authored: Column Settings
  never offers them, but preserves them across unrelated restyles. The server
  consumes only renderer source names: it schema-binds them and adds valid
  dependencies to the terminal row projection without adding them to displayed column
  metadata. Unknown dependencies become `ignored[]`. The client constructs DOM nodes
  directly and permits relative/HTTP(S) URLs, plus `mailto:`/`tel:` for links;
  active-content and embedded-content schemes fall back to text. Every terminal-table
  CSV export
  serialize Display As cells to the same encoded `<a class="ir-cell-link">` /
  `<img class="ir-cell-image">` fragments the browser constructs; action cells export
  their raw label text (a command button has no CSV shape); ordinary cells stay raw
  apart from the CSV formula guard (§6), and hidden renderer sources never become
  exported columns. Highlight styles win over column styles where
  both apply. Text is the base renderer and owns masks; link text composes the base
  renderer, including the selected text source column's own mask. A format declaration
  binds against its table's completed public schema; its array position is not a schema
  boundary and it is not dispatched by a grid/group/pivot table type. Metric and cell metadata carry
  `formatSource`, allowing a synthetic output column to inherit its input column's
  currency mask until a later formats composable overrides it.
- A partial request resolves over the effective Default state once: a stored primary
  report titled `Default`, then inline `defaultState`, then the synthetic empty state. `search`
  and `page` resolve property-wise (missing inherits, explicit empty clears);
  `activeTable` and `tables` replace their defaults when present. Table maps and
  composable lists do not merge element by element.
- A persisted document opens with its named `activeTable`. Other map entries remain
  available compositions; dictionary position does not choose a default or a mode.
- `GET /{name}/schema` always returns a complete `defaultState`, and it is the one
  place friendly names leave the server: a definition's `columnLabels` become a
  `labels` composable on the default report's definition-input table unless the
  effective Default state carries its own. When no default is configured the server
  synthesizes a table whose `from` is `definition`; its empty composition means every
  schema column in database order, flavored by the mapping. A client never invents
  its own notion of "the default report".

**Aggregate functions (closed set):** `count sum avg median min max countDistinct`.
- `sum/avg/median` require number columns; `min/max` allow number/date/text; `count/countDistinct`
  allow anything. `count` counts non-null values of the column (row count is `totalRows`).
- SQL Server `AVG` gets a float cast (integer AVG truncates there); other dialects native.
- Median uses a portable ranked derived query: `ROW_NUMBER` orders each dimension's
  non-null values, `COUNT(value)` locates the middle position(s), and an outer `AVG`
  produces the continuous median. The shared grouped relation covers report aggregates, break
  totals, Group By, pivot, and chart metrics without optional database extensions.
- Control-break columns sort first (a user sort on a break column contributes its
  direction) and are forced into the selection so renderers can group. The renderer
  moves them into the break heading rather than repeating them in detail rows. A paged
  break query reads one private lookahead row: `breakContinues` tells the client to defer
  a subtotal when the final visible group crosses the page boundary. Grand totals render
  only at the logical end of the report. When these terminal instructions belong to a
  table containing `group`, they consume that table's completed grouped relation after
  its metrics, computed columns, and filters.
  `BreakTotal.rows` therefore counts grouped rows, and aggregates over `__count` can
  recover the corresponding input-row count when needed.

**Computed columns:** authored computed and metric ids share one document-wide `irN`
namespace and may not shadow an imported or shaped schema column. Within a table, the
planner infers references between computed expressions and topologically orders them,
regardless of which `compute` containers or array positions held the rules. A cycle is
an error. Because Shape is naturally first, a pre-Group/Pivot/Chart value belongs in the
parent table; post-Shape computed columns can use that Shape's complete output schema.

**Shape composables.** Group, Pivot, and Chart consume the imported relation and emit
another relation with a new public schema. They do not change the class of the owning
table. A table owns at most one Shape; shape composition is expressed by another named
table and a `from` edge. Computed columns, filters, metadata, and local instructions bind
to the Shape's completed schema regardless of their document-array positions.

- `group` — `{ "kind": "group", "by": [dims...], "values": [{id, col, fn}...] }`
  groups the imported relation. Its output is dimensions + a row-count column
  (normally `__count`) + metrics by id; an empty `values` list produces only the
  dimensions and row count. Later compute/filter/sort/select/
  highlight/break/aggregate/labels/formats composables bind against that output. A
  later filter is therefore post-aggregate, and later footer or break aggregates
  consume the completed post-filter grouped table.
- `pivot` — `{ "kind": "pivot", "rows": [...], "cols": [...], "values":
  [{id, col, fn}...], "totals": true? }` groups the relation produced so far by
  `rows + cols`, discovers a bounded set of column keys, and emits a portable wide SQL
  relation. Distinct typed `cols` keys become opaque `irN` cell ids derived from the
  owning table, metric id, and canonical key. The returned live schema is the authority
  for those ids; values and captions never become protocol identifiers. Once the
  data-dependent contract exists, every later composable, including a child table,
  binds to that schema and wraps that SQL normally. Optional bottom totals are terminal
  response data re-aggregated from the same Pivot input by `cols` alone; they are not
  rows inherited by a child. Until totals are lowered over the completed post-Pivot
  relation, `totals: true` is rejected with same-table compute/filter or active request
  search so totals cannot silently describe a different row set. It is also rejected
  when a footer aggregate would claim the same generated-cell/function response key;
  those two values come from different relations and need distinct semantics.
- `chart` — `{ "kind": "chart", ... }` produces the chart's ordinary two-column
  result table. Later ordinary composables can filter, sort, select, label, format,
  highlight, break, or aggregate those columns under the same rules. Rendering that
  result as a chart is a UI choice derived from the presence of this composable.

Caps: `maxPivotColumns` per definition (default 60), a hard 10,000-group Pivot source
ceiling, at most 256 metrics and 900 generated columns per shape, at most 1,800 bound
cell predicates per Pivot, and `maxChartPoints` per definition (default 1,000, ceiling
10,000) — all surface as precise 400s.

**Chart composable** (APEX-style, one metric per composable):

```json
{ "kind": "chart", "type": "bar",
  "label": "STATUS", "value": "AMOUNT", "fn": "sum",
  "orientation": "vertical",
  "sort": { "by": "value", "dir": "desc" },
  "labelAxisTitle": "Status", "valueAxisTitle": "Total" }
```

- `type`: `bar | line | area | pie`. `label`: any text/number/date/bool column
  (computed included). With `fn`, the composer groups by the label and aggregates
  `value` through the shared grouped relation; `fn: "count"` may omit `value` and
  becomes `COUNT(*)`; without `fn`, every filtered row is a point and `value` must
  itself be a number column.
- **Chart validation is stricter than grid aggregation**: the metric must come out
  numeric, so `min/max` chart only number columns (grid aggregation also allows
  date/text), and pie metrics must be non-negative. The schema endpoint advertises
  the stricter function set as `capabilities.chartAggregateFunctions`; negative pie
  data is rejected after query execution with a precise validation error.
- The chart query runs over the **complete input rowset** produced by earlier compute,
  filter, and search operations, never a visible page. Chart sorting lives inside the
  spec (`sort.by: label|value`, value sorts tie-break on the label); an ordinary sort
  after the chart composes over its output. `orientation` and the axis titles are
  presentation carried in state; pie ignores them.
- The response keeps the generic two-column shape: the label column as itself plus
  the metric (`v0` labeled like `sum(Amount)`, `__count` for bare counts, or the raw
  value column). When those names would collide with the label, the metric gains a
  `_metric` suffix; chart points are read by ordinal so a legitimate `v0` or
  `__count` label can never be overwritten. Exceeding `maxChartPoints` is a precise validation error — the
  server never silently truncates a chart, because a truncated pie misstates
  proportions. Export in chart view emits exactly the charted points. If terminal
  `select` hides either required column, the completed Chart relation remains valid but
  the client renders and exports the selected columns as a generic table; Chart-only
  point and pie checks no longer apply.

**Resilience:** structural state elements referencing columns that no longer exist are
dropped into `ignored[]`. Expressions are typed programs, so an unknown referenced column
is a precise validation error. Disabled filters/highlights are not parsed or planned,
which lets an off instruction remain in saved state while its schema is being revised.
An edit invalidates the changed table's schema cache and the caches of its descendants;
it does not delete or reclassify those table expressions. If the resulting composition
is invalid, the server returns the precise path and the packaged client rolls the edit
back transactionally. Presentation maps are exempt from column drift errors: unknown
`labels`/`formats` keys stay dormant by design.

## 6. HTTP protocol

Mounted by the host: `app.MapInteractiveReports("/api/reports").RequireAuthorization(...)`.

| Endpoint | Purpose |
|---|---|
| `GET  /api/reports/{name}/schema` | Column metadata + default state + capabilities + resolved packaged-control suggestion (§4). |
| `POST /api/reports/{name}/query` | Body = state document → enriched document (null table caches filled) + page of results. |
| `GET  /api/reports/whoami` | Bootstrap diagnostic for the caller's canonical identity value (only when `whoamiEnabled`); grants no authority. |
| `GET  /api/reports/{name}/saved` | Visible reports: primary and global reports + configured read-only alternatives + the caller's own. Configured titles win. |
| `POST /api/reports/{name}/saved` | Refresh null table caches, then save the posted state under a title (global/primary publication = admin). 403 when the configured `savedReports` policy is absent (§4), independently of client controls. |
| `GET/PUT/DELETE /api/reports/saved/{id}` | Load / modify / delete one report document (matrix in §13; configured documents reject mutation). |
| `GET/POST /api/reports/__saved-reports/{schema,query}` | Administrator listing through the ordinary report pipeline; action cells carry saved-report ids. |
| `GET  /api/reports/admin/saved/{id}/document` | Administrator: download a canonical `{ title, primary, state }` source-file envelope. |
| `POST /api/reports/admin/{name}/documents` | Administrator: validate a source-file envelope against the named report and import a private saved copy for testing. |
| `GET  /api/reports/admin/users` | Administrator: invoke the optional host user-directory provider after endpoint authorization. |
| `/api/reports/admin/authorization/**` | Administrator: list or change database administrator and report-user grants through the centralized endpoint boundary. |
| `POST /api/reports/{name}/export` | Same state, same gate, no paging → CSV (UTF-8 BOM; headers are the posted document's display labels, §5), capped when `maxRows` is positive with `X-IR-Truncated` header. Text-sourced cells (labels included) that would trigger spreadsheet formula evaluation — leading `=` `+` `-` `@` tab CR — get the OWASP apostrophe guard by default, since RFC 4180 quoting does not stop Excel from evaluating them; non-text values (negative numbers, dates) are never altered, and `CsvWriter`'s `CsvCellPolicy.Verbatim` opts hosts with non-spreadsheet consumers out. 403 when the configured `download` policy is absent (§4), independently of client controls. XLSX/HTML later. |
| `GET  /api/reports/ui/{file}` | Packaged UI assets (§14). Anonymous by design; content-hash ETags. |

POST is the primary verb deliberately: state documents outgrow querystrings, and GET puts
filter values into proxy/server logs. Shareable deep links arrive later as saved-state
ids, not state-in-URL.

**Query response shape:**

```json
{
  "document": {
    "activeTable": "orders",
    "tables": {
      "orders": {
        "from": "definition",
        "schema": [
          { "name": "AMOUNT", "label": "Amount", "type": "number", "computed": false }
        ],
        "composables": []
      }
    }
  },
  "availableColumns": [
    { "name": "AMOUNT", "label": "Amount", "type": "number", "computed": false }
  ],
  "columns": [ { "name": "AMOUNT", "label": "Amount", "type": "number", "computed": false } ],
  "rows": [ { "AMOUNT": "1234.50" } ],
  "page": { "index": 1, "size": 50 },
  "totalRows": "1423",
  "aggregates": { "AMOUNT": { "sum": "8842210.75" } },
  "breakTotals": [ { "key": { "REGION": "WEST" }, "rows": "310", "aggregates": { "AMOUNT": { "sum": "1200000.00" } } } ],
  "breakContinues": false,
  "highlights": [ { "row": 3, "id": "h1" } ],
  "ignored": [],
  "elapsedMs": "41"
}
```

`document` is the submitted state with every null per-table schema cache filled by the
server. `availableColumns` is the completed active relation's public schema before the
terminal `select` hides columns; `columns` is the visible response projection. Rows are
objects (not positional arrays): negligible size at page granularity, much friendlier
to consume. Aggregates/break totals are computed over the **whole completed active
relation** via cloned queries, never over the visible page. `breakContinues` describes
only the last visible break group and is false for unpaged results. It lets the renderer
withhold an otherwise premature subtotal; the grand total is likewise displayed only
when the current page reaches `totalRows`. The adapter serializes CLR
`Int64`, `UInt64`, and `Decimal` values as invariant JSON strings, including boxed row
and aggregate values. JavaScript's JSON parser therefore never rounds them through an
IEEE-754 double. The `type: "number"` metadata remains authoritative, and the client
feeds both exact strings and legacy JSON numbers into the bundled `big.js` path for
comparison, scaling, and rounding. Ordinary 32-bit integers and floating-point values
remain JSON numbers on the wire. `BigInt` is used only to hand an already-rounded exact
integer to `Intl.NumberFormat` for locale grouping. Chart.js coordinates are converted to
`Number` only at the chart pixel boundary; the accessible chart-data table stays exact.

**Errors:** every JSON API failure is `application/json` containing the single public
`InteractiveReportError` wire class:

```json
{
  "code": "IR-1202",
  "description": "An unexpected error occurred while processing the report.",
  "title": "Report execution failed",
  "traceId": "0HN..."
}
```

`code` and `description` are required. The `IR-nnnn` catalog behaves like a
product-specific ORA series: one stable code identifies one core message. The English
`description` and optional `title` are fallback presentation text that a localized client
may replace. Optional `details` carries contextual text such as a state path, rejected
value, or JSON parser diagnostic; it is deliberately not part of the translated core
message. `traceId` appears only when a server log entry holds additional diagnostic
detail. Authentication challenges and hidden 404 denials use the same shape, so the
client no longer needs status-specific bodiless fallbacks. Static viewer-page and asset
misses remain ordinary bodyless 404s because those routes are not JSON APIs.

The packaged client's `locales/en.js` and `locales/fr-CA.js` catalogs own the complete
English and Canadian French presentation layer, including the coded-error catalog.
`core/localization.js` resolves the nearest `lang` attribute across shadow boundaries,
then the page language and browser preferences, with English as the final fallback.
All French variants currently select `fr-CA`. Stable semantic keys are formatted by the
bundled `intl-messageformat` runtime using ICU messages, so interpolation and plural
rules remain translator-owned instead of being assembled in component code.

Static report and administration chrome, validation, notices, accessible labels, and
client-side number/date presentation use that locale. Server-owned report titles,
column labels, data, and error `details` are deliberately not translated. A known error
code replaces the server title and description; `details` is appended unchanged. An
unknown code shows the server fallback so separately deployed client and server packages
remain operable while their catalogs are briefly out of step. Catalogs are compiled into
the self-contained bundles and add no client-side request.

The current HTTP code catalog is:

| Code | Core message identity |
|---|---|
| `IR-1000` | Authentication is required. |
| `IR-1001` | The report is absent or access is deliberately hidden. |
| `IR-1002` | The saved report is absent or access is deliberately hidden. |
| `IR-1003` | An optional JSON endpoint is unavailable. |
| `IR-1004` | The authenticated caller is denied. |
| `IR-1005` | Authorization infrastructure failed unexpectedly. |
| `IR-1100` | A report feature is disabled. |
| `IR-1101` | The export format is unsupported. |
| `IR-1200` | The report-state body is not valid JSON. |
| `IR-1201` | The report state failed semantic validation. |
| `IR-1202` | Report execution, storage, provider work, or live definition resolution failed unexpectedly. |
| `IR-1300` | The saved-report create body is not valid JSON. |
| `IR-1301` | A saved-report title is invalid. |
| `IR-1302` | A saved-report create body has no state. |
| `IR-1303` | The saved-report update body is not valid JSON. |
| `IR-1304` | A saved-report owner is invalid. |
| `IR-1305` | A report-document upload is not valid JSON. |
| `IR-1306` | A report-document title is invalid. |
| `IR-1307` | A report document has no state. |
| `IR-1308` | A hydrated report definition unexpectedly has no state. |
| `IR-1309` | A user-authored saved-report title conflicts. |
| `IR-1310` | A title conflicts with a configured read-only report. |
| `IR-1311` | A configured report cannot be mutated. |
| `IR-1400` | An authorization-administration body is not valid JSON. |
| `IR-1401` | The restriction value is missing. |
| `IR-1402` | The authorization identity is invalid. |
| `IR-1403` | A user restriction conflicts with an anonymous or administrators-only definition. |
| `IR-1404` | A user grant conflicts with an anonymous or administrators-only definition. |
| `IR-1500` | The GraphQL HTTP transport is unsupported. |

Database and compiler exceptions are caught and logged server-side. **No SQL text, no
parameter values, no provider error internals ever reach the client.** Validation
failures are the exception: their `details` field contains newline-separated `path: message`
entries because they reference only what the client already sent. Two
strictness rules keep foreign input from becoming 500s or silent reinterpretation:
structural nulls (a null composable, list element, or required
identifier/expression property, shapes the protocol serializer never writes) are
rejected by a pre-validation pass with per-path 400s, and numeric enum tokens
(`"dir": 99`) are malformed JSON outright, because the serializer only ever emits
camelCase strings and an integer would deserialize into an undefined member that
downstream code silently reinterprets.

## 7. Composition evaluation

```
resolve definition (store)                         404 if absent
→ authorize (endpoint gate + per-report policy)    403/401
→ resolve context params (claims/resolver)
→ resolve document over effective Default state
→ structural validation                             400 coded error (precise paths)
→ refresh every table whose schema cache is null:
    recursively compile its parent to definition
    wrap the parent's Export SQL as this table's source
    canonicalize this table by composable semantics, not array position
    apply Shape, dependency-ordered Compute, Filter, and exported metadata
    execute bounded data-dependent discovery where required (notably Pivot)
    write the complete output schema into a detached document copy
    never consult an existing cache for binding or authorization
→ compile activeTable by the same recursive rule, to a maximum depth of 64:
    group/pivot/chart
                optionally shape the imported relation and public schema
    compute     add columns in inferred dependency order
    filter      restrict the completed computed relation
    labels/formats
                resolve metadata over the completed public schema
    select/sort/break/aggregate/highlight
                record local-result work for the owning table only
→ compile enabled expressions                       typed definition/predicate/decoration plans
→ build the relation as nested subqueries where ordering requires it:
    SELECT previous.*, <expr> AS ir1 FROM (previous) previous
    filter the completed Shape + Compute relation
→ apply the request search overlay to the completed active relation
→ derive the active table's page/count/aggregate/break/highlight datasets from its
  completed relation; ancestor terminal response instructions do not enter child SQL
→ compile (dialect compiler) → execute (provider-neutral DbCommand/DbDataReader, CancellationToken)
→ remove private projections and shape visible rows
→ return the enriched document beside data and metadata
```

The execution path is split by responsibility rather than view mode:

- `ReportExecutor` is the application-service orchestrator. It resolves and validates
  submitted documents against live schemas, refreshes null per-table caches, and
  coordinates recursive composition, execution, response timing, and the enriched
  query document. Query and export consume the same completed relation and column
  metadata; export adds the active table's terminal rendering.
- `ReportConnectionManager` owns opening connections and applying trusted session policy,
  including timezone configuration.
- `ReportQueryReader` owns command compilation/execution and maps the engine's stable
  ordinal query layouts into provider-neutral rows.
- `ComposableTableCompiler` resolves named parents and memoizes only immutable
  `BoundTableExport` values. Each child begins at an explicit Export-reference node;
  owner-local selection, ordering, decoration, totals, renderer choices, search, and
  paging are structurally absent from that edge.
- Canonical binding produces explicit opaque-source, Export-reference, Shape, Compute,
  Filter, Metadata, and request-local Search relation nodes. Every node owns an ordered
  output contract containing logical identities, labels, format lineage, and column
  lineage, plus the original document path needed for diagnostics. Pivot discovery is
  an explicit data-dependent continuation: discover bounded typed keys, register the
  wide contract, then resume same-table Compute and Filter binding.
- `SqlKataRelationLowerer` is the sole relation-node backend. It recursively visits the
  immutable tree, independently re-lowers shared exports for sibling isolation, and
  returns SQLKata plus the public-to-physical map and output contract. Physical aliases
  remain a private backend concern.
- `CanonicalLocalResultBinder` binds the active owner's selection, ordering,
  highlights, breaks, and aggregates. `TerminalExecutionBundleBuilder` then creates
  main rows, count, footer, break, Pivot totals, and export statements from one
  completed relation. Pivot discovery has already executed at the continuation
  boundary. Query, export, schema refresh, REST, and GraphQL all enter
  through this compiler path.
- `ExpressionRuleCompiler` is the
  single enabled → metadata → parse/bind → result-contract pipeline for computed columns,
  filters, and highlights. It produces an `ExpressionRulePlan` whose typed effects keep
  definition, row-predicate, and decoration phases explicit.
- `ExpressionRuleSqlApplicator` translates those typed effects into projection, `WHERE`,
  or private-marker SQL after canonical planning has fixed semantic phase order.
- Highlight processing consumes database-computed private markers from the active
  completed relation. It orders row hits before cell hits and, within each scope,
  applies lower sequences before higher sequences.
- In the ASP.NET Core adapter, `IReportAccessService` owns definition-aware and
  definition-free endpoint authorization plus server-trusted context parameters. Query
  and export share one state-request pipeline so their validation and sanitized error
  behavior stay aligned.

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

Numeric `/` always means fractional numeric division. The SQL emitter promotes its
numerator with `1.0` before dividing, so providers cannot silently choose integer
division for integer operands anywhere in a recursively composed relation.

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
- `NOW()` is one UTC `DateTime` captured for the validated request. The emitter binds
  that value into every SQL statement, including dynamic relation discovery. It is not
  a database clock function and does not vary
  between query branches in one execution.
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
- **Timezone is connection configuration for developer SQL and database-native
  conversions, not for portable-expression `NOW()`.** A definition may set `TimeZone`
  (a region name or offset, bindable from appsettings): the executor pins the session
  when it opens the connection — `ALTER SESSION SET TIME_ZONE` on Oracle and `SET TIME
  ZONE` on Postgres. Unset leaves the server setting alone. SQL Server and SQLite have
  no equivalent session timezone, so they deliberately ignore the option. This policy
  still governs configured SQL, implicit conversions, and native functions issued by
  that SQL; expression `NOW()` remains the request's bound UTC instant on every
  provider. Hosts may also pin sessions through `IReportConnectionFactory` or a
  connection string. Oracle pools retain session state, so definitions sharing a
  named connection should agree on `TimeZone`. Per-user timezone vocabulary remains
  out of scope; local-wall-clock columns need an explicit conversion in developer SQL
  before comparison with UTC expression values.
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
| `NOW()` | `?` (bound UTC `DateTime`) | `?` (bound UTC `DateTime`) | `?` (bound UTC `DateTime`) | `?` (bound UTC `DateTime`) |
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
- Text comparisons and sorts inherit the database column/expression collation, because
  the four providers do not share a portable collation name or syntax. Deployments that
  require ordinal ordering must configure a binary/ordinal report collation explicitly;
  the composer does not inject a provider-specific rewrite.
- Semantics notes: concatenation treats NULL as empty everywhere (CONCAT on
  SqlServer/Sqlite/Postgres; Oracle's `||` natively); `YEAR/MONTH/DAY` accept ISO date
  *text* because SQLite date columns discover as text — emitted natively where the
  engine converts text itself (SQLite strftime, SQL Server implicit ISO conversion)
  and with explicit conversions where EXTRACT is strictly typed (Oracle
  `TO_DATE(SUBSTR(x,1,10),'YYYY-MM-DD')`, Postgres `CAST(x AS TIMESTAMP)`). Non-ISO
  text in a date-part function is a runtime error on those dialects — ISO is the
  documented contract.
- Computed columns may reference other computed outputs in the same table. The
  canonical planner derives those dependencies from the untyped syntax tree, rejects
  cycles, and binds each expression only after its prerequisites extend the schema.
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

- **Deployment posture:** the HTTP surface is engineered so an application can expose
  it to the internet, but that is a survivable boundary rather than the recommended
  topology. Prefer a trusted network. When public exposure is unavoidable, operators
  should isolate report workloads on a dedicated reporting database or read replica,
  with its own resource limits and a least-privileged read-only principal. The primary
  production database is not an appropriate interactive-report target.
- `IReportConnectionFactory` (host-registered): named connection → open `DbConnection`.
  Hosts should point report connections at a **read-only database principal** — the
  engine only reads report data, but the principal should make that a guarantee, not a
  habit.
- Multi-query consistency is an explicit per-definition policy. `none` is the default:
  count, aggregate, break-total, and page statements remain independent and no
  transaction is opened. `snapshot` requests one versioned view and is exact rather
  than best-effort: Postgres uses `REPEATABLE READ`, SQLite a read transaction, and SQL
  Server `SNAPSHOT` (which fails with guidance if `ALLOW_SNAPSHOT_ISOLATION` is off).
  The engine never substitutes locking serializable behavior or silently downgrades.
  The setting belongs only to server configuration; report state and every HTTP or
  GraphQL response omit it. SQLite's read transaction can delay writer commits in
  rollback-journal mode; operators selecting `snapshot` for a concurrently written
  SQLite database should use WAL mode.
- Oracle `snapshot` starts `SET TRANSACTION READ ONLY`, which provides transaction-level
  read consistency without additional data locks. Terminal count, aggregate, break, and
  page datasets return as ordered `SYS_REFCURSOR` outputs from one anonymous PL/SQL
  block and are consumed via `DbDataReader.NextResult`; no permanent package, temporary
  object, or elevated DDL permission is required. The scope ends with `ROLLBACK`.
  Recursive compilation, dynamic Pivot discovery, and all terminal datasets for one
  submitted request share this same read scope.
- Positive `page.size` values are clamped to `maxPageSize` (default 1000). The
  allow-listed value `0` means **All** and composes no page offset. A positive
  `maxRows` still limits the resulting active table rows except a terminal Chart, which
  uses `maxChartPoints` and rejects overflow instead of truncating.
- `maxRows` (per definition): positive values are a hard response cap for **All**
  non-Chart table queries and exports, regardless of the client request. Zero and negative
  values mean unlimited. Export truncation under a positive cap remains explicit.
- Command timeout per definition (default modest, e.g. 30s); `CancellationToken` flows
  from the HTTP request so abandoned browsers stop occupying the database.
- Logging: the final `DbCommand.CommandText` for report queries, schema probes, and
  session setup is logged at Debug only; parameter *values* are never logged.
  Correlation id at Information joins HTTP log ↔ engine log ↔ coded error response.

## 12. Security model

Layered, default-deny:

1. **Endpoint gate** — host chains `.RequireAuthorization(...)` on
   `MapInteractiveReports`; standard ASP.NET Core, so it composes with whatever auth the
   host already has (Umbraco members, cookies, OIDC). The engine has no auth mechanism of
   its own — deliberately.
2. **Per-report policy** — `authorization.policy` in the definition, checked via
   `IAuthorizationService` before composition. A failed authenticated request is returned
   as 404 so it does not disclose whether the named report exists.
3. **Application operation authorization** — optional callbacks and/or native ASP.NET
   resource handlers receive an `InteractiveReportAction`, the `ClaimsPrincipal`, and
   current resource metadata. Saved-report mutations also carry a mutable, typed
   `InteractiveReportDefinition` containing effective metadata and any client-authored
   `ReportState`. Every registered authorizer must grant every applicable action. This
   is an additional restriction over the built-in rules and the fallback administrator
   authority when the legacy list is empty. See [Authorization](AUTHORIZATION.md).
4. **Default-deny** — no authorization block ⇒ authenticated-only. Anonymous requires
   explicit `"allowAnonymous": true`. The lazy path is the safe path.
5. **Row-level security** — server-resolved `contextParams` (§4).
6. **Hygiene** — sanitized errors (§6), read-only principal (§11), no-SQL-over-the-wire
   (§1), CSRF: hosts using cookie auth should apply their antiforgery convention to the
   POST endpoints (documented host responsibility; the mapping API accepts additional
   endpoint conventions so this is one chained call).

## 13. Saved reports & administration

**Identity.** The engine reduces the authenticated user to one canonical identity value
(`ReportIdentity`): explicit `identityClaim` config if set, else NameIdentifier → `sub` →
`Identity.Name`. That value is saved-report ownership and the administrator match.
`GET /whoami` (opt-in via `whoamiEnabled`, off by default) exists so an operator can see
the exact value to put in `administrators` — which is a config list, matched with
ordinal case-sensitive equality. It also reports whether that list and application operation
authorization are configured; this is a UI hint, never an authorization decision.

**Storage.** `ISavedReportStore` / `SqlSavedReportStore`: table `IR_SAVED_REPORTS`
(ID text GUID · REPORT_NAME · TITLE · OWNER · IS_GLOBAL 0/1 · IS_PRIMARY 0/1 · STATE_JSON · MODIFIED_UTC
ISO-8601 text). Cross-dialect-uniform storage types on purpose; auto-created unless
`autoCreate` is disabled. Location via `savedReports.connection` (a named connection —
point it at the data database to co-locate saved reports with report data) or
`savedReports.dataSource` (a ConnectionStrings name or literal connection string).
There is no implicit target: a report-only host performs no persistence I/O, and
persistence/administration operations fail until one is configured. An optional
`savedReports.tablePrefix` is prepended to both store table names. Each operation uses
one validated configuration snapshot, and auto-creation is tracked per
connection/dialect/table target so live configuration changes cannot mix query and storage
targets or inherit stale initialization state.
`OWNER` remains row metadata; it is not embedded in `STATE_JSON` or the canonical
`ReportDocumentFile` envelope. Ordinary list/load/save responses return `mine` instead
of the owner identity. The administrator listing and authorization resource retain the
owner because reassignment and ownership policy require it.
Database-backed titles are case-insensitively unique within a report at the endpoint
boundary. Save As detects a visible editable title, confirms replacement, and issues a
PUT to its stable id; the server rejects duplicate creates and colliding renames with
409 even when a different client bypasses that UI.

**Configured documents.** A report definition's `documentFiles` are loaded from the
host content root, assigned stable opaque `cfg_…` endpoint ids, and **synced into the
saved-report store** as rows with
`ORIGIN = 'configured'` whenever a file signature (length + last-write) changes.
The files remain the source of truth: sync upserts under the stable id, removes
configured rows whose file left the configuration. New rows begin with the file's mtime;
each later content replacement advances that value as an optimistic-concurrency revision,
even when a deployment preserves the mtime. The store is therefore the single listing
surface — the end-user list, the admin list, and the built-in `__saved-reports` report all read
rows, and provenance is a column: summaries derive `isReadOnly` from origin. The
endpoints allow an administrator to change only `isPrimary`; content mutations and
deletion return 403 because the next sync would resurrect them. A file's primary bit
seeds a new row, while subsequent syncs preserve the administrator's stored override.
Canonical envelope downloads reflect the stored flag. A configured title shadows a
database title in the end-user list and blocks creation/rename to that title; the admin
list shows both.

**Store integrity.** `REPORT_NAME` always records the configured definition key —
lookups accept any casing at the boundary, but one spelling reaches persistence, so
case-sensitive databases filter exactly. Title uniqueness is enforced twice: the
endpoints' advisory pre-check produces the friendly 409 wording, and a unique index
over **user-origin rows only** (`REPORT_NAME`, `TITLE_KEY` — a key computed in code
as trim + invariant casefold, so every collation compares identically) closes the
concurrent-save race, with the store translating its violation into the same 409.
Configured rows live outside the index deliberately: a checked-in document may shadow
a user title (above) and synchronization must never fail on that collision.
Auto-created tables upgrade in place — add the column, backfill keys in code, create
the index (partial on SQLite/SQL Server/Postgres; a CASE function-based index on
Oracle, which has no partial indexes) — and pre-existing duplicate user titles fail
the upgrade with instructions instead of silently keeping the race open. Owner
visibility filters in memory with the same ordinal equality the authorization
matrix uses, so database collation never decides who sees a row.

`ISavedReportStore.Get` is also a security boundary for custom stores: it returns one
detached, coherent metadata-and-state version. Authorized mutations carry that expected
snapshot back to the store. The SQL implementation compares it ordinally in managed code,
then updates or deletes only the matching `MODIFIED_UTC` revision; every replacement,
including configured-document `Put`, advances that revision. This avoids collation drift
and Oracle CLOB equality while preventing an authorization decision from reaching a newer row.

**Primary and Default.** `IS_PRIMARY` is independent of global/private scope and is
administrator-controlled. It makes a saved report visible to every caller authorized
for the underlying definition. The schema resolver looks for a primary row titled
`Default` (case-insensitive) and uses its state as `defaultState`; without one, the
definition's configured/generated Default remains in force. Unflagging or deleting
that row therefore restores the generated Default. Other primary rows are public
selectable alternatives and do not affect default-state resolution.

**Authorization matrix** (enforced at the endpoint layer; the store is dumb):

| Actor | May |
|---|---|
| Owner | read, update title/state, delete, regardless of publication status |
| Anyone with report access | read primary and global reports for that definition |
| Administrator | everything: list all, flag/unflag primary, publish/unpublish global, reassign owner, update/delete any database row |
| Anyone (configured file) | read and Save As; never update/delete the configured source |
| Administrator (configured file) | additionally flag/unflag primary; never edit/delete file-backed content |

Denials hide existence (404) except where the caller provably knows the resource — an
owner reaching for admin-only powers (primary, publish, reassign) gets an explicit 403.
Primary/global publication and ownership changes are administrator-only. Publication
does not remove the owner's ability to update the report's title/state or delete it.
Saved-report loads still pass the underlying report definition's authorization gate.

**Application authorization.** `InteractiveReportBuilder.UseAuthorization` registers a
direct `ValueTask<bool>` callback. `UseAspNetCoreAuthorization` adapts the same request
to `IAuthorizationService` as an `InteractiveReportAuthorizationRequirement` plus an
`InteractiveReportAuthorizationResource`; applications implement the normal typed
`AuthorizationHandler`. A callback can also resolve `IAuthorizationService` through
`request.RequestServices` and delegate to a named policy. These are adapters over one
pipeline, not separate security models. Multiple adapters and multiple actions compose
with AND semantics.

`IReportAccessService` is the single endpoint-facing boundary. Report endpoints use
its definition-aware path; authorization administration and user-directory endpoints
use its definition-free path before touching their stores or providers. Application
code supplies decisions through either adapter above, and host-owned endpoints can
resolve the service when they need the same contract. The opt-in `whoami` bootstrap
diagnostic remains outside application-operation authorization because it exists to
discover the exact identity needed to configure that authorization and grants nothing.

The resource carries the report name and immutable current saved-report metadata.
Create, update, and document-upload operations also carry the mutable typed definition
that will be validated and persisted. Authorization evaluates the base mutation first,
then derives publication and owner actions from the effective definition. A callback
can therefore narrow a public proposal to a private save, while any privilege added by
a later handler still emits its required administrator action. The action vocabulary is
ViewReport, Query, Export, List/Read/Create/Update/DeleteSavedReport,
PublishGlobalReport, PublishPrimaryReport, ChangeSavedReportOwner,
ListAllSavedReports, ListAuthorizationUsers, ManageAuthorization, DownloadReportDocument, and
UploadReportDocument. `false` and the
dedicated denial exception are ordinary denials. Unexpected exceptions are logged and
sanitized as 500; request cancellation propagates.

Every client-authored state accepted by create, update, or upload is hydrated into the
existing `ReportState` class graph, passed to authorization, and its executable view is
semantically validated against the current report schema after authorization mutation.
Persistence serializes that typed graph rather than copying arbitrary client JSON. An
update that did not submit state leaves `STATE_JSON` untouched. Ordinary loads retain
the opposite rule:
stored state is framed as JSON and returned without `ReportState` rehydration, so a read
does not become an implicit migration or validation event.

For operations requiring administrator authority, the union of configured and
database administrators is authoritative when nonempty. A listed identity must still
pass any configured operation authorizer; an unlisted identity cannot be promoted by
it. If both stores are empty, at least one operation authorizer must be registered and
every one must grant the concrete action. No administrator plus no authorizer is a
denial, never a fail-open default. Ordinary owner and dataset operations retain their
existing behavior when no application authorizer is registered.

The complete integrator guide, including direct callbacks, native resource handlers,
policy delegation, action/resource contracts, composition, and HTTP results, is
[Authorization](AUTHORIZATION.md).

A definition may also declare `authorization.administratorsOnly`: with a nonempty
effective administrator list, the report-level gate requires membership (401
unauthenticated, 404 non-admin). With both stores empty, the concrete application
operation must be affirmatively authorized, also using non-disclosure. A definition
can instead set `authorization.restricted` and source-controlled `users`; a database
restriction marker and database report-user grants compose additively. An optional
policy stacks with named-user access. `administratorsOnly`, `allowAnonymous`, and
named-user restriction are mutually exclusive. The built-in
`__saved-reports` listing uses it; hosts get admin-only reports for free. Names
beginning with `__` are reserved for built-in reports, and `ui`, `saved`, `whoami`,
and `admin` are reserved because literal route segments shadow `{name}` routes — a
report with one of those names would be unreachable, or worse, partially reachable:
configuration declaring any of them fails fast, and the built-in listing itself is
synthesized in the definition store
from the live SavedReports options — a plain per-dialect SELECT over the store table
whose SCOPE and action-label columns are CASE expressions over ORIGIN, resolved only
after the configured-document sync has run (which also guarantees the lazily created
table exists before schema discovery).

The authorization store is a second portable table on the resolved saved-report
connection. `IR_REPORT_AUTHORIZATION` contains deterministic rows for administrator
grants, report restriction markers, and report-user grants. It follows the saved
store's dialect and AutoCreate setting, while
`InteractiveReport:Authorization:TableName` can change its identifier. Configuration
grants are never copied into this table; the authorization center presents them as a
read-only layer over database-authored additions.

## 14. Packaged UI

The product UI is a consumer of the JSON protocol, nothing more. It ships *with* the
AspNetCore package, but the engine never depends on it. The Workbench hosts the same
packaged elements used by real applications, styled after APEX's Interactive Reports.

**Consumption** (any host page, Umbraco included):

```html
<script type="module" src="/api/reports/ui/ir.js"></script>
<interactive-report report="open-orders" api-base="/api/reports"
                    stylesheet="/css/report-overrides.css"></interactive-report>

<script type="module" src="/api/reports/ui/ir-admin.js"></script>
<interactive-report-admin api-base="/api/reports"></interactive-report-admin>
```

- Custom elements with no runtime dependencies: bundled ES modules are embedded in
  the assembly and served at `{prefix}/ui/{file}` by `MapInteractiveReports`. `base`
  defaults to the prefix the script was loaded from. The explicit `api-base`
  attribute overrides that inference; `base` remains its compatibility alias.
  `report` is required and is requested directly; no report catalog or report selector
  is exposed. The optional `saved-report` attribute selects an initially loaded saved
  report by case-insensitive title. Exactly one visible saved report must match;
  otherwise the widget loads Default and displays a warning. Changing `report`,
  `saved-report`, the API location, or `lang` re-initializes in place. `lang` supports
  English and Canadian French and may instead be inherited from an ancestor. Consumers
  need no build step.
- Each element renders into its own shadow root, including menus and dialog windows. Editor
  windows use manual popovers to enter the browser top layer without making the report
  inert; short destructive confirmations use native modal dialogs. The
  packaged stylesheet is compiled into the JavaScript bundle, so host resets and
  utility classes cannot enter the component and component styles cannot escape onto
  the host page. The report host's optional `stylesheet` attribute inserts one link
  inside that root after the packaged styles; its reflected `styleSheet` property can
  replace or remove the link at runtime. Report definitions and documents cannot choose
  a CSS source. Hosts can also theme documented `--ir-*` custom properties on the element.
- Source modules live in `src/client`, organized by concern. The root holds only the
  three bundle entries (`ir.js`, `ir-admin.js`, `ir-chart.js` — thin registration/
  re-export files whose basenames fix the `Ui/dist` output names) and the shared
  `ir.css`. `core/` is widget-agnostic plumbing: `api.js` (fetch + coded errors +
  served-prefix inference + canonical error text), `localization.js` (locale selection,
  ICU formatting, and locale-aware scalar helpers), `identity.js` (the shared
  optional-whoami policy and concurrent-request coalescing), `dom.js` (element
  builder, icons, banners, form helpers), `menu.js` (popup menus), `dialog.js`
  (modal dialogs), `widget.js` (shadow-root mount/teardown, shared notices,
  compiles in `ir.css`). `report/` is the report
  widget: `element.js` (a small public custom-element facade over a closure-owned
  controller; the controller builds the state document, POSTs it, routes responses,
  and supplies `doc`/`els`/`apply`/`runQuery`/notices only to feature modules),
  `state.js` (pure normalization,
  serialization, scoped-search expression construction), `schema.js` (column
  metadata + label resolution), `skeleton.js` (toolbar/frame), `search.js`,
  `menus.js` (Actions + header menus), `saved.js` (saved-report management),
  `export.js`, plus `render/` (`format.js`, `chips.js`, `grid.js`,
  `chart-view.js`, `pager.js`) and `dialogs/` (`parts.js` shared building blocks,
  `columns.js`, `rules.js` — the expression-rule dialogs mirroring the server's
  unified rule pipeline, `grid.js`, `view.js`, `save.js`). `admin/element.js` is
  the admin widget; `chart/` (`theme.js`, `render.js`) backs the chart bundle; and
  `locales/` contains the key-aligned English and Canadian French UI and error catalogs.
  Feature modules are free functions over the widget instance `w` — nothing
  imports the element class except its entry, so the graph stays acyclic.
  `npm run build` uses esbuild to compile the stylesheet and modules into three
  self-contained entry bundles in `Ui/dist`. The generated assets are ignored;
  `scripts/pack.ps1` builds them before packing, and an MSBuild guard fails
  Release builds and packs when they are missing — package consumers do not
  require Node.js, and a UI-less package cannot ship silently.
- **Packaged pages**: `GET {prefix}/{name}/view` hosts `<interactive-report>` and
  `GET {prefix}/admin` hosts `<interactive-report-admin>` — minimal shells emitting
  an absolute-prefix script URL so the client's script-relative api-base inference
  resolves with no `api-base` attribute. Their document language follows ASP.NET Core
  Request Localization when present, then `Accept-Language`, restricted to the two
  packaged locales. Served anonymously for the same reason the
  assets are: a shell is public package markup with zero data, and it renders
  identically for any name, so it discloses nothing — the element's schema request
  is the actual gate. Injected values (report name, `?saved-report=`) are
  HTML-encoded; `Cache-Control: no-store`; disabled via
  `InteractiveReport:ViewerPagesEnabled`. Literal-first routing means the existing
  `ui`/`saved` segments shadow reports with those names, as the data routes always
  have; `admin` and `whoami` join that set, and definition validation rejects all
  four names outright (§13). Embedding in host
  pages remains the primary consumption path.
- **Feature surface**: scoped toolbar search (all text columns or one typed column → expression filter);
  Actions menu (Columns shuttle, Column Settings, Filter, Sort, Control Break, Highlight, Aggregate,
  Compute with token-insert helpers, Group By, Pivot, Chart, Save/Save As/Delete/Reset,
  CSV download); column-header menus (sort/rename/column settings/hide/break/filter — Rename writes a
  `labels` override for ordinary columns and edits the rule label for computed ones; blank
  restores the schema default). The Column Settings dialog edits one column at a time
  (edits are staged per column, so several columns can be configured in one visit): a
  Visible checkbox that writes the same `select` composable the shuttle owns
  (re-shown columns append to the end — no second source of truth), alignment,
  a display-mode selector (text/link/image) with explicit renderer source columns,
  a per-type format-mask select, bold/italic, text/background colors, validated custom
  CSS classes, and a live
  preview fed by the column's own data; everything but visibility lands in the
  doc's `formats` map (§5), applied by the grid renderer to header alignment,
  cells, and aggregate-row alignment; settings chips with
  APEX-style enable/disable checkboxes for expression rules; removing a computed-column chip also removes
  its references from selection, rules, formats, and renderer sources; changed tables
  and their descendants receive `schema: null` so the server refreshes their caches;
  break groups with per-column subtotal rows and grand-total rows; row/cell highlights;
  Group By/Pivot rendering; saved-report select
  (Default + Primary/Global/Private groups); `ignored[]` and coded errors surfaced as
  notices — validation errors render *inside* the originating dialog, which stays
  open (apply is transactional: mutate a clone, install, re-query, and on failure
  restore the last server-validated state — so a throwing mutator, an overlapping
  edit, or a failed saved-report load can never strand a half-mutated or
  never-validated document). Menus are
  **composition-aware**: Columns, Column Settings, Compute, Filter, Highlight, and Sort
  operate on the active table's cached completed schema regardless of which relational
  composables produced it. The client authors a constrained set of compositions for its
  own toolbar but preserves valid externally authored tables and composables rather than
  inferring semantics from names or map order. On query it adopts the returned enriched
  `document`; a validation failure rolls the edit/load back to the last validated state,
  while soft drift arrives as `ignored[]` (§5). The whole
  surface follows the definition's control suggestion (§4), which arrives resolved
  on the schema payload unless client JavaScript overrides it: menu entries vanish with their headings and separators, the
  search bar / view buttons / Actions button / saved-report select hide when their
  features (or every entry under them) are gone, header cells stop being clickable
  when no header-menu entry survives, and chips owned by an absent feature render
  locked — visible state, no toggle/edit/remove (except leaving a locked view for the
  grid, which stays possible). Reset remains as long as any doc-mutating feature
  exists; a missing `features` field (older server) means everything is on.
- **Host document and control API**: after the initial query,
  `getReportDocument()` returns a detached canonical document and
  `submitReportDocument(document)` atomically replaces, queries, adopts, and renders a
  caller-supplied JSON-compatible document through the same last-validated rollback and
  request-supersession machinery as built-in editors. All query sources dispatch the
  synchronous, cancelable `ir-before-query` transform event; accepted queries dispatch
  an observational `ir-query-complete` event with detached submitted/document/result
  snapshots. Client control overrides are tri-state per canonical feature (`true`,
  `false`, or inherit): an explicit override wins over the server suggestion and is
  retained across report changes. Runtime `savedReports` enablement lazily loads the
  selector data. Packaged `user` mutations pass through a 200 ms trailing-edge debounce:
  rapid chip removals and other UI edits accumulate in the working document and only its
  final state is posted. A new edit aborts an in-flight query immediately; initial,
  saved-report, host-document, export, and administration operations remain immediate.
  The standard `disabled` property makes the complete shadow surface
  inert and closes transient UI without changing overrides. `styleSheet` reflects the
  `stylesheet` attribute and is the sole foreign-stylesheet entry point; it belongs to
  the host across report changes. Mutable documents, schema data, request state, rollback
  state, and renderer operations live on a closure-owned controller rather than the DOM
  element. None of these client choices
  bypass endpoint authorization, context resolution, document validation, or the
  server-enforced download and saved-report policies.
- **Host export API**: after the initial query, `interactive-report.getExport(format,
  { signal })` posts the current canonical state to the ordinary export endpoint and
  resolves to `{ blob, filename, contentType, truncated }` without causing browser
  navigation or download UI. The CSV menu command is a thin retrieval-plus-`saveBlob`
  wrapper over that method. Format is an open string carried to the endpoint, so future
  exporters do not change the element API. Server-side integrators resolve the
  transport-neutral `IReportFileExporter` and receive `ReportExportFile` bytes and
  metadata directly. That in-process boundary does not authorize, apply the endpoint
  feature gate, or infer a user; the host supplies explicit context parameters. The HTTP
  endpoint retains its authorization and `download` feature gates, resolves request
  context, then delegates its already-resolved definition and state to the same exporter.
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
- Schema metadata advertises expression functions and aggregate
  functions by column type. Query results include the active table's effective output schema,
  so clients do not duplicate language catalogs or guess computed types.
- **Styling**: shadow DOM isolates every rule, including the bundled styles for
  popups and dialog windows. Modeless editor windows remain component-owned while the
  Popover API lets them paint in the browser top layer; destructive confirmations use
  the native dialog top layer. Deliberate theming crosses the boundary through the supported
  `--ir-*` custom properties and `::part()` names; ordinary host selectors cannot
  reach internal controls, and no stylesheet is added to the host document.
- **Asset endpoint is anonymous** even when the host chains `RequireAuthorization` on
  the group: the assets are public package code (no data, no secrets), and a
  session-expired page that cannot load the script cannot even say "sign in". Every
  data endpoint keeps the full gate. ETags are content hashes (SHA-256 prefix) with
  `Cache-Control: no-cache` — an assembly-version tag would 304 stale content across
  rebuilds of the same version.
- Every report, saved-report, identity, administration, and GraphQL response starts
  with `Cache-Control: no-store`; only the packaged asset handler deliberately replaces
  it with the ETag-based policy above.
- The admin element drives the §13 administrator surface: list everything,
  publish/unpublish, reassign owner, inspect the stored state document, download a
  canonical file-backed envelope, upload and validate an envelope as a private saved
  copy, and delete. It simply loses its data (404) for non-administrators and says so.

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
- **M4 — Views & export** ✅ *(2026-08-05; revised 2026-08-29)*: Group composable
  (pushed down, group-count pagination), capped Pivot relation with implicit-count
  default, and CSV export through the same completed-table response path.
- **M5 — Persistence & proof:** ~~saved reports (private/public, per user)~~ *(done
  early — see §13, including administration/whoami)*; SQL Server + Oracle verification
  passes; hardening (timeouts, caps, logging discipline). *Prep complete 2026-08-05:
  env-gated live-dialect battery (docs/TESTING.md), condition × dialect golden matrix,
  SQL-safety corpus, Oracle BindByName fix, parser recursion guard, Debug-only SQL
  logging. Live battery verified green ×2 dialects 2026-08-05.*
- **M6 — The real UI** ✅ *(2026-08-05)*: packaged APEX-style widget + saved-report
  administration widget (§14), embedded-asset serving, Workbench pages rebuilt around
  them. The original plain protocol harness was retired after the packaged UI replaced
  it. Verified end-to-end in the
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
- **M9 — Charts** ✅ *(2026-08-06; revised 2026-08-29)*: APEX-style Chart composable
  (bar/line/area/pie, one label + one numeric metric, optional aggregation, chart-owned
  sort, orientation, axis titles). It emits a two-column relation and a renderer hint,
  so another composable or named child can consume its SQL like any other table;
  `maxChartPoints` (default 1,000) rejects oversized charts precisely instead of
  truncating; chart-mode export emits the charted points. Packaged UI gains the
  Chart dialog/chip/view with a lazily loaded tree-shaken Chart.js bundle, chart
  theme tokens, and a canvas description + "View chart data" table for assistive
  tech.
- **M10 — Friendly names** ✅ *(2026-08-07)*: client-side display names over a unified
  document flow. A definition's `columnLabels` and a state's `labels` map (additive
  state) carry real column name → display label; the schema endpoint
  always returns a complete effective `defaultState` (stored primary `Default`, inline state,
  or synthetic fallback) whose
  labels deliver the mapping to the client. Document ingestion is one pipeline
  (`ReportExecutor.IngestDocument` → canonical table compilation) that resolves labels for every
  path; query surfaces stay on neutral server labels — the packaged UI resolves
  display names from its state document (grid, chips, dialogs, synthetic
  groupBy/chart metric labels) and gains header-menu Rename — while export, the
  server rendering the user's screen, applies the posted document's labels to headers
  and synthetics from the completed bound output contract.
- **M11 — Feature suggestions** ✅ *(2026-08-07; client overrides added 2026-08-31)*: per-report `features` list (§4)
  — sixteen canonical tokens covering the Actions menu, search, views, saved
  reports, and download; validated fail-fast at definition load, resolved onto the
  schema payload, and applied by default by the packaged UI (chrome removal + locked chips),
  with embedding JavaScript able to override the presentation.
  `download` and `savedReports` creation are server-enforced 403s; everything else
  stays presentation-level by design. Per-column attributes (alignment, format
  masks, LOVs, per-column sort/filter permissions…) remain the next configuration
  increment.
- **M12 — Source-controlled report documents** ✅ *(2026-08-07)*: per-definition
  `documentFiles` load `{ title, primary, state }` envelopes from the host content
  root. All documents share the saved-report store with stable opaque ids and
  `isReadOnly` summaries; the file primary bit seeds administrator-controlled stored
  metadata. File titles precede database titles, content mutation is server-refused,
  Save As remains available, and the packaged report/admin clients suppress invalid controls.
- **M13 — Column settings** ✅ *(2026-08-07; revised 2026-08-29)*: per-column presentation
  via a `formats` composable (§5) — closed-vocabulary format masks, alignment,
  bold/italic, text/background colors — plus a Column Settings dialog (feature token
  `columnSettings`) whose Visible checkbox writes the active table's `select` composable.
  Masks and styling remain client-only; definitions ship default formatting through the effective Default state,
  with no new config surface. Remaining per-column configuration candidates: LOVs,
  help text, per-column sort/filter permissions.
- **M14 — Host-owned custom CSS** ✅ *(2026-08-07; revised 2026-08-31)*: the
  application integrator's `stylesheet` attribute / `styleSheet` property links one URL
  inside the report's shadow root; report definitions and documents have no CSS source.
  Column Settings writes validated class tokens to `formats.classes`; the grid applies them
  to headers, cells, and aggregates while filtering malformed and reserved `ir-*`
  state. Documents select application-authored rules but cannot inject CSS or URLs.
- **M15 — Column renderers** ✅ *(2026-08-07)*: the grid owns a per-column renderer
  seam with text, link, and image implementations. Column Settings selects the display
  mode and its URL/text source columns. Hidden dependencies are schema-bound server-side
  and projected only as renderer inputs. Grid CSV exports use the same encoded HTML
  fragments as browser cells while leaving ordinary values raw. DOM construction,
  HTML encoding, and a URL protocol allowlist keep report data out of active-content surfaces.
- **M16 — Actions pagination** ✅ *(2026-08-07)*: Actions → Pagination owns the
  report document's page limit with APEX choices 10, 50, 100, 500, 1000, and All.
  Numeric values respect `maxPageSize`; All is the explicit `page.size: 0` protocol
  value. A positive `maxRows` caps All non-Chart completed-table results, while zero or
  a negative value leaves them unlimited. The footer is navigation-only, and export remains
  unpaged under the same positive-cap/unlimited contract.
- **M17 — Explicit null sorting** ✅ *(2026-08-07; revised 2026-08-29)*: every terminal
  table sort rule
  may carry additive `nulls: first|last`; absence retains the dialect default. The
  Sort dialog exposes Default/First/Last, while header quick-sorts remain Default.
  One schema-bound composer path orders table pages, break totals, and exports
  consistently, using native syntax except for SQL Server's null-rank key.
- **M18 — Aggregate and break boundaries** ✅ *(2026-08-07)*: numeric median uses a
  portable ranked aggregate relation shared by totals and aggregate views. Highlights gain
  names and explicit, validated sequence precedence. Control-break dimensions move into
  headings; a one-row page lookahead defers subtotals for continuing groups, and grand
  totals render only at the report's logical end.
- **M19 — Composable tables** ✅ *(2026-08-10; revised 2026-08-29)*: the state document is
  an unordered map of opaque table ids. Each table names `definition` or another table
  in `from`; a child consumes the completed parent's Export and begins a fresh local
  result (§5). Group, Pivot, and Chart are Shape composables, not table subclasses, and
  may recur once per table within the bounded 64-table ancestry. Selection, sorting,
  highlighting, breaks, footer aggregates, and renderer/style choices are explicit
  owner-local work; only safe metadata lineage crosses `from`. Stable authored identity
  (`ir1`, `ir2`, …) is document-wide, while Pivot cells derive from metric and key. Each table may
  carry a nullable, non-authoritative output-schema cache; the client invalidates
  affected descendants, and the server refreshes null caches and returns the enriched
  document with query data.
- **M20 — Definition edit link + per-column overrides** ✅ *(2026-08-27)*: APEX's
  edit pencil as definition config — `editLink.urlTemplate` with schema-bound
  `{COLUMN}` placeholders, delivered canonical-cased on the schema payload, rendered
  client-side as a leading icon-only anchor (URL-encoded substitution, protocol
  allowlist, NULL value ⇒ no pencil, grid only, never in metadata/pickers/exports);
  template columns ride the existing hidden-projection channel. Plus the anticipated
  per-column attribute map (`columns`): `label` (supersedes `columnLabels`),
  `hideLabel` (blank header, accessible name kept), `sortable`/`filterable` (controls
  hidden client-side across header menus, dialogs, and pickers; server strips
  violating sorts/breaks/filter rules into `ignored[]`; computed columns exempt;
  breaks count as sorting), and `helpText` (header-menu note). No document-shape
  change; query payloads untouched. Verified: template parse/binding
  units, golden projection SQL, config fail-fast matrix + snapshot round-trip, HTTP
  schema/query/enforcement suite, packaged-UI unit suites (direct-import renderer +
  built-bundle mount), Playwright e2e against the live Workbench.
- **M21 — Redistributable package** ✅ *(2026-08-28)*: the NuGet story. Per-report
  `dataSource` (ConnectionStrings name or literal string, `=`-discriminated) with
  reflective provider tokens and startup fail-fast naming the missing driver
  package; **dialect derived from the connection** (provider token, or sniffing a
  code-registered factory's unopened connection type; wrapper escape hatch
  `AddConnection(name, factory, dialect)`) and gone from every config surface —
  `ReportDefinition.Dialect` is nullable and stamped by the store, superseding
  leftovers; `SavedReportsOptions.Dialect` removed; `SavedReports.DataSource`
  added. Startup validator (config mistakes fail boot); definition resolution
  wrapped in coded-error shaping for post-reload breakage. Packaged
  anonymous viewer/admin pages (`/{name}/view`, `/admin`) with an admin
  whoami-off guidance banner (which also surfaced and fixed the admin element's
  `remove()` method shadowing `Element.remove()`). MIT + full nuget.org metadata
  at 0.9.0 via `Directory.Build.props`, 8.0.x dependency pins, `Ui/dist` pack
  guard, `scripts/pack.ps1` (publish automation deliberately deferred to an owner
  discussion), README rewritten
  package-first with an Umbraco 13 quickstart. Verified: 354 Core + 166
  AspNetCore + 2 offline live-project tests, 62 packaged-UI unit tests, 22
  Playwright e2e (three new packaged-page scenarios; Workbench reports run
  dialect-less, one via a `_ProviderName` dataSource), pack smoke with nuspec
  inspection and a negative guard check.

## Appendix: decision log

| Decision | Alternative | Why |
|---|---|---|
| Live pushdown (SqlKata) | SQLite/DuckDB staging | One code path; no refresh/cache-keying/type-shim lifecycle; APEX-faithful liveness. Staging can return behind the same protocol if source load demands it. |
| Definitions in config by name | Client-supplied SQL | Trust boundary; SQL never crosses the wire; definitions version with the app. |
| Borrow host auth | Engine-owned API keys | Hosts (Umbraco et al.) already have real auth; a second mechanism would be weaker and clash. |
| POST-primary protocol | GET + querystring state | State size; filter values leak into logs via GET; deep links return later as saved-state ids. |
| Rows as JSON objects | Positional arrays | Page-granularity size difference is negligible; consumption ergonomics win. |
| Highlight predicates push down as private booleans; Pivot emits portable grouped/conditional SQL | Interpret expressions in C# or use native PIVOT | Filters and highlights share one typed predicate implementation; private markers are removed before the response. A Pivot remains a child-wrappable relation without admitting four incompatible native PIVOT dialects. |
| `net8.0` | `net10.0` | Umbraco 13 LTS floor; SDK 8 present; bump is cheap later. |
| whoami off by default | always on | It's an information endpoint; enabling is a deliberate operator act (samples enable it). |
| Identity and owner matching is ordinal and case-sensitive | case-fold identity values | Identity-provider subjects are opaque; folding can merge distinct principals. |
| Saved-report ids are text GUIDs | identity/sequence columns | One DDL shape across SQLite/SqlServer/Oracle; no sequence plumbing. |
| Timestamps as ISO text, flags as 0/1 | native per-dialect types | Uniform semantics and sorting across dialects for an engine-internal table. |
| Global/primary flags admin-only; content remains owner-managed | all published mutations admin-only | Publication is a curation act, while title/state and deletion remain owner actions. |
| Primary is a separate admin-controlled flag; primary `Default` overrides generated Default | configured-file primary is the default | Several curated reports may be primary and public, while one stable title controls default-state replacement and unflagging restores the generated fallback. |
| Configured report files use the saved-report protocol with `isReadOnly` | separate configured-report API or expose file origin | One selector and load path keeps the document model coherent. Generic mutability is what clients need; the storage source remains a server concern. |
| Microsoft.Data.Sqlite dependency in the AspNetCore package | host-supplied providers only | SQLite remains a supported out-of-box `dataSource`; persistence still requires an explicit target and never creates a database merely because the package was installed. |
| Decimal parameters bind as double on SQLite | decimal-as-TEXT (provider default) | The provider's TEXT binding breaks comparisons against affinity-less expressions (computed columns) via SQLite's cross-type ordering; double is SQLite's native numeric storage, so the conversion is faithful to the engine. |
| Pivot caps: 60 column groups (configurable) + hard 10k source groups | unbounded pivot | An unbounded generated schema and SQL statement is a resource/usability grenade; the caps surface as precise 400s telling the user what to change. |
| Chart overflow is a precise 400, never truncation | truncate at the point cap like grid export | A truncated bar chart is misleading; a truncated pie is a lie — its proportions claim to describe the whole. Export truncation keeps its header signal; charts get an error naming the cap. |
| One metric per Chart composable (APEX model) | multi-series charts | Covers the dominant reporting ask with a small state surface; another relation or Chart can still follow it. Multi-series, click-to-filter, legends-as-controls, image export, and "Other" folding stay open as explicit increments. |
| Chart.js in a lazily imported third bundle | Apache ECharts; server-rendered SVG | Chart.js covers exactly bar/line/area/pie, tree-shakes small, MIT. ECharts earns its size only when multi-series/zoom/dense-data arrive. The lazy chunk keeps grid-only pages at their old weight; embedding keeps the no-CDN packaging story. |
| CSV: UTF-8 BOM, label headers, X-IR-Truncated header | bare UTF-8, name headers, silent truncation | Excel needs the BOM to detect encoding; users recognize labels, not internal names; silent truncation reads as complete data. |
| Views share the export pipeline | grid-only export | Exporting "what the view shows" falls out of running the same validated state unpaged — no special cases. |
| Oracle BindByName via reflection in CommandBuilder | reference ODP.NET from Core | ODP.NET binds by position by default; context params appear first in SQL but are added last, so positional binding silently misbinds. Reflection keeps Core provider-free. |
| UI: packaged vanilla ES modules as custom elements | React/Vite application | Package consumers need no frontend toolchain; hosts embed one script tag + one element. The repository uses esbuild only to produce the release bundles, and the protocol keeps the JS small enough that a framework buys little. |
| UI assets embedded in the AspNetCore assembly, served under the mapped prefix | RCL static web assets | Zero host setup (`UseStaticFiles`/`_content` not required), one mapping call delivers API + UI, works identically in any host. |
| UI asset endpoint `AllowAnonymous` | inherit group auth | Assets are public package code (readable on any feed); an auth-gated script tag turns "session expired" into a blank region that can't even say "sign in". Data endpoints keep the full gate. |
| Asset ETags hash content | assembly version tag | Version-tagged ETags 304 stale content across rebuilds of the same version (bitten in dev; would bite ops on patch releases). |
| Shadow DOM + theme properties + host-owned inner stylesheet | Light DOM + `.ir-*` prefix | Isolation keeps host resets out and report rules in. Theme tokens cover broad branding; an integrator-owned stylesheet inside the root supports deliberate internal and per-column styling without adding CSS configuration to report definitions. |
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
| `NOW()` is one request-scoped UTC binding shared by every SQL branch and discovery query | emit each provider's clock function | One captured instant keeps count/page/aggregate branches and recursively composed Pivot/Chart relations consistent; provider clocks differ in timezone and can advance between statements. |
| SQLite logical Date = canonical datetime() text, normalized at comparison sites | native-typed dates; compare raw text | SQLite has no date type — producers emit one canonical text form and comparisons wrap stray operands in datetime(), because date-only text sorts before its own midnight timestamp. Physical storage stays text; the type system stays honest. |
| TO_STRING formats are a closed token set, translated and bound per dialect | pass native masks through | Native masks don't port (strftime ≠ TO_CHAR ≠ .NET) and raw pass-through would hand client text to the SQL layer. A validated vocabulary translates exactly and the mask still rides as a binding. |
| Definition `TimeZone` pins the database session for developer SQL, not expression `NOW()` | make it alter the portable expression clock | Session timezone still matters to configured SQL, native functions, and database conversions. Portable expressions use the separately captured UTC instant on every provider, so their meaning does not depend on connection state. |
| Numeric division promotes the SQL numerator with `1.0` | accept provider integer-division rules | Promotion prevents the same recursively composed expression from truncating on providers that use integer division for integer operands. |
| SQL text ordering follows provider collation | inject a universal collation clause | No collation syntax or collation name spans SQL Server, Oracle, SQLite, and PostgreSQL. Deployments needing exact order choose a binary/ordinal database collation explicitly. |
| Bare dates rejected in concatenation | implicit rendering; auto-wrap in TO_STRING | Implicit date-to-text is the one place engine settings (session language, NLS, DateStyle) would leak into output, and it differs per engine. Rejection matches the language's explicit-conversion rule (TO_DATE inbound, TO_STRING outbound); auto-wrapping would pick a format silently. |
| Friendly names live in the document; the server delivers `columnLabels` as the default report's `labels` and keeps query/cache surfaces neutral | apply labels at discovery/validation so all results carry them | Display naming is metadata: live schemas, per-table caches, validation, and query metadata retain structural labels, while each child receives the completed parent metadata and may override it directly. No executable surface depends on a caption. |
| Report `labels` keys are not schema-validated; canonical planning rejects only conflicting declarations | ignored[]/errors for unknown or blank entries | Unknown keys are unused display data, exactly as resilient as a saved report is entitled to be. Two different labels for the same case-insensitive key have no order-independent meaning and therefore fail. (Config-side `columnLabels` still fail fast on blank/case-colliding entries — config mistakes, not state.) |
| Export consumes the posted document's completed active relation, inheritable metadata, and owner-local terminal response | neutral export headers; look up the user's saved report server-side | An export is the server rendering the user's screen, and the active document may never have been saved. A child receives only its parent's Export contract; the shared renderer applies the active table's own visibility and cell presentation without leaking those local choices across `from`. |
| Schema endpoint synthesizes an empty `defaultState` | nullable `defaultState` | Every client would otherwise invent its own "no default configured" behavior. An empty state already *means* the right thing — all columns, database order — and it is also the delivery vehicle: the definition's mapping rides down as its `labels`. |
| Feature control is a flat server suggestion plus client-authoritative overrides | APEX-style per-action objects; treat the server list as a client ceiling | One `features` array supplies useful default chrome while an embedding application can force packaged controls on or off without rewriting schema payloads. Absent = everything keeps existing configs working. The richer per-column attribute model (alignment, masks, LOVs, per-column permissions) layers on later without reshaping this. |
| The control suggestion is presentation-level except server policies for `download` and `savedReports` creation | validate all posted state docs against the suggested controls | Hiding or force-showing a dialog is not a data boundary: the query endpoint accepts any valid document, and context params (§12) are the security story. The two independently enforced policies are the operations that egress (unpaged export) or persist (saved-report rows); enforcing at creation only keeps existing saved reports manageable after a config change. |
| Locked chips: state from a client-disabled or unsuggested feature displays read-only | hide the chips; let them stay editable | The chip strip is the doc made visible. Hiding active filters would misrepresent the data shown, and editing them would reopen controls the effective client policy removed. Leaving a locked view for the grid stays possible: it abandons the feature rather than using it. |
| Column formatting is a `formats` composable; only scalar mask lineage crosses `from` | inherit every style and renderer; apply every mask/style server-side | The active owner keeps its full presentation contract, while a child inherits only safe masks. Link/image/action dependencies are schema-bound owner-local inputs. One text renderer owns scalar masks; link/image compose it, and synthetic metric metadata retains its format source. Every terminal-table CSV export intentionally serializes Display As modes as browser-like encoded HTML; hidden inputs never become columns. |
| Masks are closed per-type tokens (Intl-backed) | freeform mask strings (APEX FML/999G999D99) | Same rule as TO_STRING: a portable vocabulary has stable meaning while `Intl` supplies the user's separators, symbols, and ordering; it cannot smuggle anything. Unknown tokens fall through to default rendering instead of erroring, because a display mask must never break a report. |
| Int64/UInt64/Decimal travel as invariant JSON strings; all numeric columns use bundled `big.js` | send every numeric database value as a JSON number; maintain separate integer/decimal formatters | JavaScript parses JSON numbers as IEEE-754 doubles. Typed metadata retains numeric behavior, while one arbitrary-precision path handles parsing, comparison, scaling, and rounding without silent digit loss. |
| Column classes select a host-owned shadow-root stylesheet | freeform style/CSS in report state; definition-owned or page-level CSS | The integrator owns the URL and CSS; saved reports carry only conservative class tokens and cannot select reserved `ir-*` behavior. Report definitions cannot choose a CSS source, page CSS cannot cross the shadow boundary, and freeform report CSS would be an injection surface. |
| The dialog's Visible checkbox writes the active table's terminal `select` composable | a relational projection; a per-column `visible` flag in formats | One source of truth: the shuttle, the header Hide, and the checkbox all edit the same list, so they can never disagree. Visibility does not remove columns from the relation inherited by a child; a future relational projection would be a separate operation. |
| Link/image renderers use explicit source columns | arbitrary HTML or URL/text templates | Column names can be schema-bound and safely projected. Direct DOM construction preserves escaping, and a protocol allowlist blocks active-content URLs without inventing a template language. |
| Document is an unordered map of named completed relations | mode-specific branch registry | `from` makes dependency explicit: each child wraps its parent's Export SQL, map order carries no meaning, and canonical composable semantics make behavior belong to operations rather than table subclasses. Inactive alternatives remain ordinary map entries. |
| UI mode is derived from composable predicates | a stored `view` field; table-name or map-position conventions | A stored mode can disagree with the composition. The packaged UI recognizes the base/Group/Pivot/Chart sibling subset it authors, while preserving deeper foreign compositions it cannot classify uniquely. |
| Authored computed and metric outputs share document-wide ids (`ir1`, `ir2`, …); dynamic Pivot cells receive opaque `irN` ids derived from `(table id, metric id, typed key)` | separate `cN`/`mN` generators; positional or display-derived cell names | One authored allocator prevents cross-kind collisions. Type-tagged, hash-derived Pivot identities are stable across discovery order and data-set additions without exposing values as protocol identifiers; the live schema supplies those ids to later composables and the client. |
| Documents carry no protocol version and each table has a nullable advisory schema cache | a root snapshot gate; no client schema knowledge | The structure is self-describing. Null gives the server an exact refresh signal, transitive descendant invalidation keeps edits cheap, and query returns the enriched document. The cache describes the completed public relation before terminal visibility; live recursive compilation remains authoritative for binding and security. |
| Edit link is a constrained URL template in the definition | explicit source columns only (the M15 renderer rule) | A scoped reversal, not a repeal: M15's rejection targeted templates in *report state* — untrusted documents. The definition author already writes the raw SQL, so the trust boundary is unchanged; the template is URL-only (no HTML), placeholders are schema-bound and URL-encoded at substitution, the result still passes the protocol allowlist, and the template never enters report state — the M15 rule keeps holding for documents. Computing URLs in SQL instead would pollute the discovered schema with a link column every picker shows. |
| Per-column overrides are a definition map delivered beside the schema (`columnOverrides`) | extend `ColumnInfo`; put flags in the state document | The per-column attribute model M11 anticipated. `ColumnInfo` is shared with query responses (and `availableColumns` overlays schema columns client-side, which would erase flags after the first query), so a parallel map keeps query payloads byte-identical. Labels ride the existing `columnLabels`/default-report channel so precedence has one implementation; sort/filter restrictions follow the whitelist philosophy — client hides controls, server strips violations into `ignored[]` so stale saved reports degrade instead of erroring — and computed columns are exempt to keep the rule predictable without transitive analysis. |
| Dialect is a property of the connection, derived from the driver | per-report `dialect` as source of truth; derived-with-cross-check | A report's dialect can never legitimately differ from its connection's — the old per-report field only ever produced silently wrong SQL, and an omitted value silently bound as enum 0 (SqlServer). Provider tokens fix it statically; code factories are sniffed from one unopened connection (zero I/O); wrappers declare it where the wrapper is created (`AddConnection` overload). Leftover config keys are superseded, not rejected: the derived value is correct by construction, and removing vestigial config surface beats building rejection machinery for it. |
| Per-report `dataSource` with `=`-discrimination | a named Connections config section; nested report groups | Matches the owner's model — a report is a name, a connection string, and a SELECT — with one property and zero indirection. A bare name references the standard `ConnectionStrings` section (never silently a literal), so Umbraco's `umbracoDbDSN` + `_ProviderName` convention works untouched; resolved sources become content-addressed internal connections, so a config edit rolls the schema-cache identity for free. Code-registered `connection` names remain the programmatic path. |
| Providers load reflectively from the host's dependency graph | hard provider references; per-dialect adapter packages | The engine needs only name → unopened `DbConnection` (the Oracle BindByName reflection set the precedent). Tokens map to assembly-qualified types; a missing driver fails at startup naming the exact package. No dependency bloat for hosts that never touch a given engine, no extra packages to version, and Umbraco hosts already ship SqlClient + Sqlite. |
| Packaged pages serve an anonymous shell for any name | 404 unknown names; gate the page like the data | The shell is public package markup with zero data — rendering identically for every name is what makes it disclose nothing, and the element's schema call is the real gate (an auth-gated page could not even tell a signed-out user to sign in). Same rationale as the AllowAnonymous assets. |
| Source maps stay embedded in the package | strip maps on pack | One hermetic build shape (content-hash ETags stay honest across dev and package), and readable stack traces from the minified bundles during production support are worth ~2 MB in a server-side package. |
| MIT + full nuget.org metadata at 0.9.0, shared via `Directory.Build.props` | per-project metadata; private-feed-first | One file owns identity/license/Source Link/symbols for all three packages; 0.9.0 signals pre-1.0 while the package soaks in a real host. Dependencies pin 8.0.x so the packages do not drag 10.x `Microsoft.Extensions.*` into Umbraco 13's 8.x graph. |
| Every child imports only its parent's completed `Export`; canonical composable phases transform relation, metadata, or the owner-local result | selectively flatten parent declarations; execute the composable array in storage order | Shape, dependency-ordered Compute, Filter, Metadata, then LocalResult is inferred from meaning, not array position. A hide-column fix belongs to the one local `select` path and therefore fixes base, grouped, pivoted, and charted responses together, while arbitrary relation-changing depth remains valid. |
| Breaks and footer aggregates consume the active table's completed relation | inherit ancestor footer datasets; re-run them against the definition SQL | Canonical relational composition stays literal: Shape, dependency-ordered Compute, and Filter determine which rows and values the active response aggregates. Ancestor local datasets are not SQL inputs. After Group, `BreakTotal.rows` counts grouped rows and summing `__count` recovers the corresponding input-row count. |
| Server-enriched documents are authoritative; per-table caches remain advisory | root schema-snapshot match-or-reset; no schema cache | The server judges every composition against live schemas. Null targets and every table compiled on the way to the active relation return live caches, even when the submitted value was non-null; dormant snapshots never participate in binding or security. Hard problems roll the client back transactionally; soft drift degrades through `ignored[]`. |
| Explicit `none` / `snapshot` consistency policy; provider owns the mechanism | automatic best-effort transactions; one portable mega-statement | `none` is a valid no-side-effect choice. `snapshot` is either honored or rejected, never silently degraded. Oracle uses `SET TRANSACTION READ ONLY` plus an anonymous PL/SQL multi-`REF CURSOR` batch, Postgres repeatable read, SQLite a read transaction, and SQL Server SNAPSHOT only when the administrator enables it. This keeps one application contract without pretending database mechanisms or operational requirements are identical. |
| CSV text cells get the apostrophe formula guard by default | rely on RFC 4180 quoting; sanitize only on request | Quoting does not stop Excel from evaluating `=`/`+`/`-`/`@`-leading cells, exported database text can be attacker-authored, and the writer explicitly targets Excel (BOM). Only text-sourced cells are touched — numbers and dates keep full fidelity — and `CsvCellPolicy.Verbatim` remains for non-spreadsheet consumers. |
| Title uniqueness backed by a user-rows-only unique index | trust the endpoint pre-check; span both origins | The check-then-insert race made the documented guarantee advisory. A code-computed `TITLE_KEY` (trim + invariant casefold) compares identically on every collation; the index covers user rows only because configured documents deliberately shadow user titles and sync must never fail on that. The store translates violations into the pre-check's own 409, and `Put` now treats only provider-classified unique violations as a lost insert race — anything else propagates rather than being reported as applied. |
| Route-literal report names (`ui`, `saved`, `whoami`, `admin`) fail fast | document the shadowing; namespace system endpoints | Literal-first routing makes such reports silently unreachable or — worse — partially reachable (`saved` answers queries but not schema). Failing configuration names the conflict; moving system endpoints would break every existing consumer for four names nobody should use. |
| Foreign-input strictness: numeric enums and structural nulls are 400s | accept and reinterpret; let nulls surface as 500s | The protocol serializer only ever writes camelCase enum strings and never writes null composables, so both shapes are provably foreign — rejecting them cannot break a legitimately saved document (liberal acceptance intact). `dir: 99` previously validated as an undefined member and executed as *something*; a null composable crashed the resolver into a sanitized 500. |
| GraphQL rows adopt the REST exact-number contract | exact-number scalars; leave GraphQL.NET's serializer alone | The same report must not have two wire semantics: dynamic row values (`ComplexScalar`) silently rounded Int64/Decimal through doubles in JS clients. Normalizing rows to invariant strings mirrors `IrJson`; `totalRows`/`elapsedMs` stay `Long` scalars because their schema type declares number semantics and their magnitudes fit a double exactly. |
| Saved rows record the configured definition key as `REPORT_NAME` | persist the route token as requested | Case-insensitive lookup with route-cased persistence scattered one report's rows across spellings on case-sensitive databases (`/orders` vs `/Orders`). The configured key is the identity; alternate casing stays accepted at the boundary and dies there. |
