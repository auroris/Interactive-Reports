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
| `src/InteractiveReport.AspNetCore` | Endpoint mapping (`MapInteractiveReports`), config-backed definition store, auth integration, JSON protocol shaping, problem+json errors. `Ui/dist` holds the generated client assets (§14), embedded and served by the same mapping. |
| `src/InteractiveReport.GraphQL` | Optional GraphQL.NET transport over saved reports. Looks up every origin through `ISavedReportStore` and reuses ASP.NET authorization, context resolution, validation, and execution. |
| `src/client` | Product UI source modules and the three browser-bundle entry points. |
| `samples/Workbench` | Dev harness: SQLite sample DB. `index.html` and `admin.html` host the packaged report and administration elements. |
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
        "urlTemplate": "/orders/{ORDER_ID}/edit",  // {COLUMN} = base schema columns
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
      "styleSheet": "/css/open-orders.css",
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
    "v": 2,
    "columns": [ "ORDER_ID", "CUSTOMER", "AMOUNT", "ORDER_DATE" ],
    "sorts": [ { "col": "ORDER_DATE", "dir": "desc", "nulls": "last" } ]
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
  standard sanitized problem document.
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
- `features` is a whitelist of end-user features (APEX's per-action Actions-menu
  configuration collapsed to a flat token list). Absent means everything;
  present means exactly what is listed. Known tokens (`ReportFeatures`): `search`,
  `columns`, `rename`, `columnSettings`, `filter`, `sort`, `pagination`, `controlBreak`, `highlight`,
  `aggregate`, `compute`, `groupBy`, `pivot`, `chart`, `savedReports`, `download`.
  (`columnSettings` gates the per-column settings dialog; its visibility checkbox
  additionally needs `columns`, whose visible-list it writes.) Unknown, blank,
  or duplicate entries fail fast at definition load. The schema endpoint always sends
  the resolved effective list; the packaged UI removes the chrome for everything else
  (menu entries, view buttons, search bar, saved-report select — state a default or
  saved report already carries still displays, as locked chips). Two tokens are also
  server-enforced with 403s because they persist or egress data: `download` at the
  export endpoint and `savedReports` at saved-report creation (existing saved reports
  stay governed by the §13 matrix so a config change never strands rows). The rest is
  deliberately presentation-level: the query endpoint accepts any valid state
  document, because hiding a dialog is not a data boundary — context params (§12) are.
  Note the JSON config binder reads `[]` as absent; to lock a report down, list the
  one or two features it should keep.
- Base SQL must not end with `ORDER BY` (breaks subquery wrapping on SQL Server; APEX has
  the same rule). Validated at definition load with a clear error by a comment-, string-,
  and quoted-identifier-aware scan for the clause at parenthesis depth 0 — `'order by'`
  as data or documentation never trips it, and comments cannot hide a real clause.
- `documentFiles` paths are relative to the host content root unless absolute. Each
  file is a `{ title, primary, state }` envelope around the ordinary versioned state
  document. Every file joins the saved-report selector as a global read-only document.
  `primary` seeds the stored flag on first synchronization; administrators may later
  flag or unflag the row without modifying the file. Configured titles shadow database
  reports by case-insensitive title; the administrator list retains shadowed rows so
  they can be renamed or removed. PUT may change only the primary flag; other updates
  and DELETE return 403, while Save As remains available. Hosts must include the
  referenced files in their build/publish output.
- `styleSheet` is a relative or HTTP(S) URL chosen by the report developer. The schema
  delivers it to the component, which links it inside the shadow root after the
  packaged styles. Relative URLs resolve against the host page; CSP `style-src` still
  applies. The URL never enters report state, so saved/global reports cannot redirect
  CSS loading.
- `editLink` renders APEX's edit pencil: a leading synthetic grid column whose anchor
  navigates to `urlTemplate` with `{COLUMN}` placeholders substituted from the row
  (values URL-encoded; the result still passes the renderer protocol allowlist).
  Definition chrome like `styleSheet` — not a feature token, never in report state,
  and independent of the user's column selection: template columns are schema-bound
  and projected as hidden row data exactly like renderer source columns, so hiding a
  referenced column never breaks the pencil. The header is visually empty with the
  label as its accessible name; a row whose placeholder value is NULL renders no
  pencil (`CASE`-null a key in the base SQL to withhold rows, as the actions
  convention does); the column never appears in pickers, search, sorts, filters, or
  exports, and only grid mode shows it — grouped and pivoted rows have no single
  source row to edit. Template syntax fails fast at load (unmatched/empty/nested
  braces, zero placeholders, non-http(s) absolute URLs); a placeholder naming no
  live schema column disables the pencil for that schema (omitted from the payload,
  logged, and surfaced as an `ignored[]` notice on queries) rather than erroring.
  Config keys cannot express column names containing `:` (a configuration-binder
  limitation shared with `columnLabels`).
- `columns` is the per-column override map (keyed by base column name,
  case-insensitive; unknown names tolerated like `columnLabels`): `label` supersedes
  `columnLabels` — configuring the same column's label in both maps fails fast, and
  `columnLabels` remains supported as the label-only shorthand; `hideLabel` blanks
  the table header cell while menus, dialogs, pickers, break headings, and the
  accessible name keep the real label (labels can never be blank — this flag is the
  APEX empty-heading pattern without unnameable columns); `sortable: false` removes
  the column's sort controls and control breaks (breaks force `ORDER BY`);
  `filterable: false` removes its filter controls and expression tokens;
  `helpText` renders as a note at the bottom of the column's header menu (a report
  whose whitelist removes every header-menu feature has no menu to carry it).
  Computed columns are always exempt — restrictions bind to base columns, so
  `c1 = AMOUNT * 2` stays sortable while `AMOUNT` is not, with no transitive
  analysis to reason about. Enforcement follows the whitelist philosophy: the
  client hides the controls, and the server strips violating sorts, breaks, and
  filter rules from incoming documents into `ignored[]` — stale saved reports
  degrade visibly instead of erroring — while a `defaultState` that contradicts
  its own definition fails fast at load. This is presentation-level courtesy, not
  a data boundary (context params, §12, remain the security story).
- Definitions version in git and deploy with the app: schema changes and report changes travel together.

## 5. Report state document

The single artifact that is simultaneously: the request body, the saved report, and the
shareable view state. Versioned (`"v": 3`). Version 3 restructures the document as a
literal **pipeline of table stages**; versions 1 and 2 are rejected with a precise error
(no migrator — nothing external consumes old documents, by owner decision).

**The model: a report is a pipeline of tables.** Stage 1 (`source`) is the developer's
SELECT; each later stage reshapes its input table (`group`, `spread`) or renders it
(`chart`). Every *table* stage carries a **layer** of the same per-table instructions —
column selection, labels, formats, computed columns, sorts, highlights — each bound to
**that stage's own output schema**. "Computing" therefore exists at every stage:
source-layer computed columns are filterable and groupable (they extend the schema
before any reshaping), while a group-layer computed column derives from dims and
metrics (ratio of sums, share of count). Filtering and search live in the source layer
only at T0 — they select which rows exist before any table is built — but every layer
reserves the slot (a group-stage filter is HAVING semantics, a planned fast follow).

```json
{
  "v": 3,
  "search": "acme",
  "page": { "index": 1, "size": 50 },
  "pipeline": [
    {
      "shape": { "kind": "source" },
      "layer": {
        "columns": ["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "ORDER_DATE", "c1"],
        "labels": { "ORDER_ID": "Ticket #" },
        "formats": {
          "CUSTOMER": { "displayAs": "link", "urlColumn": "CUSTOMER_URL" },
          "AMOUNT": { "mask": "currency:USD", "align": "right" }
        },
        "computed": [
          { "id": "c1", "enabled": true, "label": "Amount w/ Tax",
            "expr": "ROUND(AMOUNT * 1.0825, 2)" }
        ],
        "filters": [
          { "enabled": true, "expr": "IN_LIST(STATUS, 'SHIPPED', 'PENDING')" },
          { "enabled": false, "expr": "AMOUNT > 1000" }
        ],
        "sorts": [ { "col": "ORDER_DATE", "dir": "desc", "nulls": "last" } ],
        "highlights": [
          { "id": "h1", "name": "Large order", "sequence": 10,
            "enabled": true, "scope": "row",
            "expr": "c1 > 10000", "style": { "bg": "#fff3cd" } }
        ],
        "breaks": [],
        "aggregates": [ { "col": "AMOUNT", "fn": "sum" } ]
      }
    },
    {
      "shape": {
        "kind": "group",
        "by": ["CUSTOMER", "STATUS"],
        "values": [ { "id": "m1", "col": "AMOUNT", "fn": "sum" },
                    { "id": "m2", "col": "c1", "fn": "avg" } ]
      },
      "layer": {
        "computed": [
          { "id": "c2", "enabled": true, "label": "Avg per Order",
            "expr": "ROUND(m1 / __count, 2)" }
        ],
        "sorts": [ { "col": "m1", "dir": "desc" } ],
        "formats": { "m2": { "mask": "decimal2" } },
        "labels": { "m1": "Total" }
      }
    },
    {
      "shape": { "kind": "spread", "cols": ["STATUS"], "totals": true },
      "layer": {
        "labels": { "m1@[\"SHIPPED\"]": "Shipped Total" },
        "formats": {}
      }
    }
  ],
  "shelf": {
    "chart": [ { "shape": { "kind": "chart", "type": "pie",
                            "label": "CUSTOMER", "value": "AMOUNT", "fn": "sum" },
                 "layer": {} } ]
  }
}
```

**Pipeline shapes.** The first stage is always `source`. T0 accepts exactly four
pipelines — `[source]` (grid), `[source, group]` (group by), `[source, group, spread]`
(pivot), `[source, chart]` (chart) — anything else is a precise validation error.
The client's view mode is *derived from the tail*, not stored: there is no `view`
field. `page` applies to the terminal table (grid pages rows, group by pages groups;
spread and chart run unpaged under their existing caps).

**Shelf.** `shelf` parks configured alternate tails (stage arrays after `source`),
keyed by their derived mode name. The toolbar swaps tails between `pipeline` and
`shelf`; the server never validates shelf entries — they are inert retained
configuration, exactly as the old `views` registry was.

**Schema snapshot (retired 2026-08-28).** Documents no longer carry a
client-checked schema snapshot. Server-delivered documents are authoritative, and
saved reports are accepted liberally: the client adopts a document as-is and the
server judges it on query — hard problems return a validation response (the client
rolls the failed operation back to its last validated state), soft drift lands in
`ignored[]`. The client stamps nothing on save and strips a legacy document's
`schema` key on adoption; the server-side machinery (the state model's snapshot
member, resolver copy, and default-state stamping) was removed with it — a legacy
row's `schema` JSON property now falls through as an unknown member on hydration.

**Stable synthetic names.** Group metrics are named by their spec-assigned ids
(`m1`, `m2`, … — a namespace like computed columns' `c1`, unique within the stage,
never shadowing schema columns); the implicit row count is always `__count`. Spread
cell columns are `{metricId}@{JSON array of column-dimension value strings}` — e.g.
`m1@["SHIPPED"]`, `__count@["SHIPPED","2026"]` — value-derived and therefore stable
under data drift and spec reordering (the old positional `v0`/`p{k}_{v}` names
silently changed meaning when the values list was reordered or the data shifted).
Null dimension values serialize as JSON `null`. Labels carry the human form
("sum(Amount) · SHIPPED"); the name is identity, not presentation.

**Expression rules:** computed columns, filters, and highlights all contain an `enabled`
flag and an `expr`. Computed columns must bind to a number, text, or date value; filters
and highlights must bind to a true/false condition. All three consume the complete
expression language in §8. A computed value defines a column, a true filter keeps the
row, and a true highlight paints its row or target cell.
Each highlight also has a display `name` and positive `sequence`. Within row or cell
scope, matching rules apply from lower to higher sequence, so the highest sequence wins
when rules set the same style. Cell highlighting has priority over row highlighting.
Legacy documents derive the name from `id` and the sequence from list position in
increments of ten. `CONTAINS`, `STARTS_WITH`, and `ENDS_WITH` are case-insensitive; `IN_LIST`
provides typed membership. Blank behavior is written explicitly as `IS NULL`, or
`IS NULL OR col = ''` when empty text should also count.
- `search` is the toolbar search: OR of `contains` across the source layer's visible
  text columns. It is a source-layer concern (stage 1 selects which rows exist) and
  applies identically under every tail.
- Each layer's `labels` (column name → display label, keyed by that stage's output
  names) is presentation, never a program: it does not gate execution or validation
  (unknown keys are unused display data), and query responses keep server-derived
  labels — the client resolves display names as `stage labels[name] ?? source
  labels[name (pass-through dims)] ?? synthetic/server label`. The document is the
  single source of truth for what the user sees, so the one server consumer is
  **export**: rendering a file is rendering the user's screen, and the posted
  document's labels apply to its headers and synthetic labels (the active document
  may never have been saved, so nothing can be looked up server-side). Ingestion
  resolves source labels once for every path — request `??` effective Default state
  `??` the definition's `columnLabels` — mirroring the default report the schema
  endpoint delivers. A computed column still names itself on its own rule. Like
  every state property, a present map replaces the default wholesale and `{}`
  explicitly clears it.
- `formats` (real column name → `{ mask, align, bold, italic, fg, bg, classes[],
  displayAs, urlColumn, textColumn }`) is the
  second presentation map, written by the Column Settings dialog and following every
  labels inheritance rule: wholesale-replace with `{}`
  as the explicit clear, resolvable from the effective Default state so definitions
  can ship default formatting. Masks are a closed client-side token vocabulary per
  column type (`integer`, `decimal1`…`decimal4`, `plain`, `currency:CAD|USD|EUR|GBP|JPY`,
  and `percent0`…`percent2` for numbers; `date`, `datetime`, `datetimeSeconds`, `time`,
  `timeSeconds`, `dateMedium`, `dateLong`, `dateTimeMedium`, and `dateTimeLong` for
  dates); unknown tokens and indigestible values fall
  through to default rendering — a mask is a lens, never a gate. Inline styling is the
  same constrained property set highlights use. `classes` selects rules from the
  definition's trusted shadow-root `styleSheet`; the client accepts conservative CSS
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
  dependencies to the grid row projection without adding them to displayed column
  metadata. Unknown dependencies become `ignored[]`. The client constructs DOM nodes
  directly and permits relative/HTTP(S) URLs, plus `mailto:`/`tel:` for links;
  active-content and embedded-content schemes fall back to text. Grid CSV exports
  serialize Display As cells to the same encoded `<a class="ir-cell-link">` /
  `<img class="ir-cell-image">` fragments the browser constructs; action cells export
  their raw label text (a command button has no CSV shape); ordinary cells stay raw
  apart from the CSV formula guard (§6), and hidden renderer sources never become
  exported columns. Highlight styles win over column styles where
  both apply. Text is the base renderer and owns masks; link text composes the base
  renderer, including the selected text source column's own mask. `displayAs` and its
  renderer source columns are a **source-layer** affordance only — an aggregate is
  never a link or image — so group/spread-layer formats carry masks, alignment, and
  styling. Metric and cell metadata carry `formatSource`, and the client resolves a
  column's effective format as `terminal-stage formats[name] ?? source-layer formats
  [formatSource ?? pass-through name] ?? default` — a metric inherits its source
  column's currency mask until the view overrides it.
- A partial request resolves over the effective Default state once: a stored primary
  report titled `Default`, then inline `defaultState`, then the synthetic empty state. `search`
  and `page` resolve property-wise (missing inherits, explicit empty clears);
  `pipeline`, `shelf`, and `schema` replace the default **wholesale** when present —
  stage arrays do not merge, matching the existing list semantics.
- A persisted pipeline is also the view a saved report opens with: its tail is the
  active mode. Only the pipeline is validated and executed; shelf entries and layer
  settings a given tail does not consume (e.g. source breaks under a group tail)
  remain inert until their stage is terminal again.
- `GET /{name}/schema` always returns a complete `defaultState`, and it is the one
  place friendly names leave the server: a definition's `columnLabels` become the
  default report's source-layer `labels` unless the effective Default state carries
  its own. When no default is configured the server synthesizes an empty pipeline
  (`[source]`, empty layer) — which, by the null-columns rule, means every schema
  column in database order, flavored by the mapping. A client never invents its own
  notion of "the default report".

**Aggregate functions (closed set):** `count sum avg median min max countDistinct`.
- `sum/avg/median` require number columns; `min/max` allow number/date/text; `count/countDistinct`
  allow anything. `count` counts non-null values of the column (row count is `totalRows`).
- SQL Server `AVG` gets a float cast (integer AVG truncates there); other dialects native.
- Median uses a portable ranked derived query: `ROW_NUMBER` orders each dimension's
  non-null values, `COUNT(value)` locates the middle position(s), and an outer `AVG`
  produces the continuous median. The shared shape covers report aggregates, break
  totals, Group By, pivot, and chart metrics without optional database extensions.
- Control-break columns sort first (a user sort on a break column contributes its
  direction) and are forced into the selection so renderers can group. The renderer
  moves them into the break heading rather than repeating them in detail rows. A paged
  break query reads one private lookahead row: `breakContinues` tells the client to defer
  a subtotal when the final visible group crosses the page boundary. Grand totals render
  only at the logical end of the report.

**Computed columns:** ids live in separate namespaces per rule kind (`c1`, `c2`, … for
computed; `m1`, `m2`, … for group metrics); may not shadow the owning stage's schema
names; referenced by id anywhere that stage's columns are legal. A source computed
column is a first-class input to later stages (a group dim, a metric source, a chart
label); a group-layer computed column derives from that stage's dims, `__count`, and
metrics, and — when a spread follows — spreads into cells like any other metric.
No layer's computed columns may reference each other (no dependency ordering, same
rule at every stage).

**Stages.**
- `source` — the developer's SELECT wrapped as `ir_base` (+ `ir_calc` when the layer
  has computed columns), with the layer's filters and the toolbar search applied.
  Terminal rendering (grid) consumes the full layer: visible columns, sorts, breaks,
  aggregates, highlights, formats. Breaks, aggregates, and `displayAs` renderers are
  source-only at T0; filters are source-only at T0 (later stages reserve the slot).
- `group` — `{ "by": [dims...], "values": [{id, col, fn}...] }`, pushed down as GROUP
  BY over the source core. Output schema (static, derivable without running a query):
  dims + `__count` + metrics by id + the layer's computed columns. Empty `values` ⇒
  `__count` alone. The layer's computed/sorts/highlights bind against that schema —
  sort by metric, highlight on a ratio — and are pushed down through one more wrap
  (`ir_stage`). Groups paginate with their own count query when terminal.
- `spread` — `{ "cols": [subset of group.by], "totals": true? }`: the preceding group
  stage's output pivots in memory; `cols` values become cell columns named
  `{metricId}@{values…}`, rows keyed by the remaining dims. Group-layer computed
  metrics spread like declared metrics (pre-spread compute — ratio-of-sums per cell).
  The spread layer carries `labels`/`formats` only at T0 (never-validated presentation,
  dormant when a data value vanishes, revived when it returns); cross-cell computed and
  highlights are deferred — cell columns are data-dependent and cannot be
  schema-validated before execution. Group-layer sorts under a spread are honored for
  row dims and ignored (with a notice) for metrics. Optional bottom totals come from a
  second source query grouped by `cols` alone, wrapped the same way so computed metrics
  total correctly.
- `chart` — a renderer, not a table: the existing chart spec fields ride in the shape,
  its layer stays empty, and chart-owned sorting/validation are unchanged.

Caps: `maxPivotColumns` per definition (default 60), a hard 10,000-group spread source
ceiling, and `maxChartPoints` per definition (default 1,000, ceiling 10,000) — all
surface as precise 400s.

**Chart stage** (APEX-style: one chart per report, single metric):

```json
{ "shape": {
    "kind": "chart", "type": "bar",
    "label": "STATUS", "value": "AMOUNT", "fn": "sum",
    "orientation": "vertical",
    "sort": { "by": "value", "dir": "desc" },
    "labelAxisTitle": "Status", "valueAxisTitle": "Total" },
  "layer": {} }
```

- `type`: `bar | line | area | pie`. `label`: any text/number/date/bool column
  (computed included). With `fn`, the composer groups by the label and aggregates
  `value` through the shared grouped shape; `fn: "count"` may omit `value` and
  becomes `COUNT(*)`; without `fn`, every filtered row is a point and `value` must
  itself be a number column.
- **Chart validation is stricter than grid aggregation**: the metric must come out
  numeric, so `min/max` chart only number columns (grid aggregation also allows
  date/text), and pie metrics must be non-negative. The schema endpoint advertises
  the stricter function set as `capabilities.chartAggregateFunctions`; negative pie
  data is rejected after query execution with a precise validation error.
- The chart query runs over the **complete filtered rowset** (computed columns,
  filters, search — never the visible page). Sorting lives inside the spec
  (`sort.by: label|value`, value sorts tie-break on the label); source-layer sorts
  remain configured but inactive while the chart tail is active. `orientation` and
  the axis titles are presentation carried in state; pie ignores them.
- The response keeps the generic two-column shape: the label column as itself plus
  the metric (`v0` labeled like `sum(Amount)`, `__count` for bare counts, or the raw
  value column). When those names would collide with the label, the metric gains a
  `_metric` suffix; chart points are read by ordinal so a legitimate `v0` or
  `__count` label can never be overwritten. Exceeding `maxChartPoints` is a precise validation error — the
  server never silently truncates a chart, because a truncated pie misstates
  proportions. Export in chart view emits exactly the charted points.

**Resilience:** structural state elements referencing columns that no longer exist are
dropped into `ignored[]`. Expressions are typed programs, so an unknown referenced column
is a precise validation error. Disabled filters/highlights are not parsed or planned,
which lets an off instruction remain in saved state while its schema is being revised.
Layered over that, a user edit that breaks a downstream dependency (deleting a
computed column a group stage consumes) deletes the dependent stages and shelf
entries outright rather than limping (T0 coarse invalidation). Presentation maps are
exempt — unknown `labels`/`formats` keys stay dormant by design.

## 6. HTTP protocol

Mounted by the host: `app.MapInteractiveReports("/api/reports").RequireAuthorization(...)`.

| Endpoint | Purpose |
|---|---|
| `GET  /api/reports/{name}/schema` | Column metadata + default state + capabilities + resolved feature whitelist (§4). |
| `POST /api/reports/{name}/query` | Body = state document → page of results. |
| `GET  /api/reports/whoami` | The caller's canonical identity value (only when `whoamiEnabled`). |
| `GET  /api/reports/{name}/saved` | Visible reports: primary and global reports + configured read-only alternatives + the caller's own. Configured titles win. |
| `POST /api/reports/{name}/saved` | Save the posted state under a title (global/primary publication = admin). 403 when `savedReports` is not whitelisted (§4). |
| `GET/PUT/DELETE /api/reports/saved/{id}` | Load / modify / delete one report document (matrix in §13; configured documents reject mutation). |
| `GET/POST /api/reports/__saved-reports/{schema,query}` | Administrator listing through the ordinary report pipeline; action cells carry saved-report ids. |
| `GET  /api/reports/admin/saved/{id}/document` | Administrator: download a canonical `{ title, primary, state }` source-file envelope. |
| `POST /api/reports/admin/{name}/documents` | Administrator: validate a source-file envelope against the named report and import a private saved copy for testing. |
| `POST /api/reports/{name}/export` | Same state, same gate, no paging → CSV (UTF-8 BOM; headers are the posted document's display labels, §5), capped at `maxRows` with `X-IR-Truncated` header. Text-sourced cells (labels included) that would trigger spreadsheet formula evaluation — leading `=` `+` `-` `@` tab CR — get the OWASP apostrophe guard by default, since RFC 4180 quoting does not stop Excel from evaluating them; non-text values (negative numbers, dates) are never altered, and `CsvWriter`'s `CsvCellPolicy.Verbatim` opts hosts with non-spreadsheet consumers out. 403 when `download` is not whitelisted (§4). XLSX/HTML later. |
| `GET  /api/reports/ui/{file}` | Packaged UI assets (§14). Anonymous by design; content-hash ETags. |

POST is the primary verb deliberately: state documents outgrow querystrings, and GET puts
filter values into proxy/server logs. Shareable deep links arrive later as saved-state
ids, not state-in-URL.

**Query response shape:**

```json
{
  "columns": [ { "name": "AMOUNT", "label": "Amount", "type": "number", "computed": false } ],
  "rows": [ { "ORDER_ID": "9007199254740993", "CUSTOMER": "Acme", "AMOUNT": "1234.50", "ORDER_DATE": "2026-07-30" } ],
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

Rows as objects (not positional arrays): negligible size at page granularity, much
friendlier to consume. Aggregates/break totals are computed over the **whole filtered
set** via the cloned queries — never over the visible page. `breakContinues` describes
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

**Errors:** RFC 7807 `application/problem+json`, sanitized. Database and compiler
exceptions are caught, logged server-side with a correlation id, and returned as a
generic problem document carrying that id. **No SQL text, no parameter values, no
provider error internals ever reach the client.** Validation failures are the exception:
they are precise and verbose (which filter, which column, why), because they reference
only what the client already sent. Two strictness rules keep foreign input from becoming
500s or silent reinterpretation: structural nulls (a null pipeline stage, list element, or
required identifier/expression property — shapes the protocol serializer never writes) are
rejected by a pre-validation pass with per-path 400s, and numeric enum tokens
(`"dir": 99`) are malformed JSON outright, because the serializer only ever emits
camelCase strings and an integer would deserialize into an undefined member that
downstream code silently reinterprets.

## 7. Composition pipeline

```
resolve definition (store)                         404 if absent
→ authorize (endpoint gate + per-report policy)    403/401
→ resolve context params (claims/resolver)
→ ingest document (one pipeline, query + export):
    discover/fetch cached schema
    resolve doc over effective Default state (source labels: … ?? columnLabels)
    walk the stage pipeline:                       400 problem+json (precise)
        source: validate layer against base ∪ source computed (effective schema)
        group:  derive static output schema (dims + __count + metrics by id
                + layer computed) and validate its layer against it
        spread: cols ⊆ group.by; layer restricted to labels/formats (never validated)
        chart:  existing chart validation over the effective schema
    compile enabled expression rules per stage     typed definition/predicate/decoration plans
→ [export only] apply document display labels      metadata surfaces; names/SQL untouched
→ build source core:
    wrap base SQL as subquery (ir_base)
    [if source computed] second wrap layer:
        SELECT ir_base.*, <expr> AS c1 FROM (base) ir_base  → AS ir_calc
        (aliases become filterable/sortable universally — no dialect
         supports referencing a SELECT alias in WHERE reliably)
    apply filter predicates and search
→ [group stage] wrap core as the grouped table:
    SELECT dims, COUNT(*) AS __rows, fn(col) AS m1 … GROUP BY dims
    [if group layer computes/decorates/sorts by metric] one more wrap:
        SELECT ir_stage.*, <expr> AS c2, <markers> FROM (grouped) ir_stage
    order by group-layer sorts (dims, __count, metrics, computed)
→ [spread stage] run the grouped table unpaged (capped) for the in-memory pivot;
    totals (optional) re-run it grouped by cols alone through the same wrap
→ derive via Clone() (terminal grid) / group-count query (terminal group):
    page query   (+ private highlight predicate projections + ForPage)
    count query  (ClearComponent order → AsCount)
    aggregates   (ClearComponent order/limit → SELECT fn(col) AS a0…)
    break totals (… → GROUP BY break cols; COUNT(*) AS __count + a0…)
→ compile (dialect compiler) → execute (provider-neutral DbCommand/DbDataReader, CancellationToken)
→ post-process in C#: projection markers → ordered highlight hits; spread transform
    (cell columns {metricId}@{values…})
→ remove private projections and shape visible rows
→ shape response
```

The execution path is split by responsibility rather than view mode:

- `ReportExecutor` is the application-service orchestrator. Its `IngestDocument` is the
  unified entry for every request carrying a state document — query and export, saved
  server-side or never saved — pairing cached schema discovery with `StateValidator`;
  the export path then applies the ingested document's display labels
  (`ValidatedState.WithDisplayLabels`) before selecting the view path and coordinating
  composition, execution, and response timing.
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
- `HighlightEvaluator` consumes database-computed markers. It orders row hits before
  cell hits and, within each scope, applies lower sequences before higher sequences.
  It does not reimplement expression semantics in memory.
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
  read consistency without additional data locks. Grid and Group By datasets return as
  ordered `SYS_REFCURSOR` outputs from one anonymous PL/SQL block and are consumed via
  `DbDataReader.NextResult`; no permanent package, temporary object, or elevated DDL
  permission is required. The scope ends with `ROLLBACK`. Single-statement paths
  (chart and ordinary export) do not open a consistency scope.
- Positive `page.size` values are clamped to `maxPageSize` (default 1000). The
  allow-listed value `0` means **All** and deliberately composes no page limit or
  offset for grid and Group By queries. CSV export ignores pagination altogether and
  retains its independent `maxRows` cap and truncation signal.
- `maxRows` (per definition): hard cap for exports. It does not cap the explicit
  **All** pagination choice.
- Command timeout per definition (default modest, e.g. 30s); `CancellationToken` flows
  from the HTTP request so abandoned browsers stop occupying the database.
- Logging: the final `DbCommand.CommandText` for report queries, schema probes, and
  session setup is logged at Debug only; parameter *values* are never logged.
  Correlation id at Information joins HTTP log ↔ engine log ↔ problem response.

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
the exact value to put in `administrators` — which is a config list, matched
case-insensitively exact. It also reports whether that list and application operation
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
configured rows whose file left the configuration, and never stamps timestamps (rows
carry the file's mtime). The store is therefore the single listing surface — the
end-user list, the admin list, and the built-in `__saved-reports` report all read
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
visibility filters in memory with the same `OrdinalIgnoreCase` the authorization
matrix uses, so database collation never decides who sees a row.

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

The resource carries the report name and immutable current saved-report metadata.
Create, update, and document-upload operations also carry the mutable typed definition
that will be validated and persisted. Authorization evaluates the base mutation first,
then derives publication and owner actions from the effective definition. A callback
can therefore narrow a public proposal to a private save, while any privilege added by
a later handler still emits its required administrator action. The action vocabulary is
ViewReport, Query, Export, List/Read/Create/Update/DeleteSavedReport,
PublishGlobalReport, PublishPrimaryReport, ChangeSavedReportOwner,
ListAllSavedReports, ManageAuthorization, DownloadReportDocument, and
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
<interactive-report report="open-orders" api-base="/api/reports"></interactive-report>

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
  `saved-report`, or the API location re-initializes in place. Consumers need no build
  step.
- Each element renders into its own shadow root, including menus and dialog windows. Editor
  windows use manual popovers to enter the browser top layer without making the report
  inert; short destructive confirmations use native modal dialogs. The
  packaged stylesheet is compiled into the JavaScript bundle, so host resets and
  utility classes cannot enter the component and component styles cannot escape onto
  the host page. A definition's optional `styleSheet` link is inserted inside that
  root after the packaged styles. Hosts can also theme documented `--ir-*` custom
  properties on the element.
- Source modules live in `src/client`, organized by concern. The root holds only the
  three bundle entries (`ir.js`, `ir-admin.js`, `ir-chart.js` — thin registration/
  re-export files whose basenames fix the `Ui/dist` output names) and the shared
  `ir.css`. `core/` is widget-agnostic plumbing: `api.js` (fetch + problem+json +
  served-prefix inference + canonical error text), `identity.js` (the shared
  optional-whoami policy and concurrent-request coalescing), `dom.js` (element
  builder, icons, banners, form helpers), `menu.js` (popup menus), `dialog.js`
  (modal dialogs), `widget.js` (shadow-root mount/teardown, shared notices,
  compiles in `ir.css`). `report/` is the report
  widget: `element.js` (the custom element — state-document lifecycle: build,
  POST, route the response; exposes `doc`/`els`/`apply`/`runQuery`/notices as the
  surface its feature modules call), `state.js` (pure normalization,
  serialization, scoped-search expression construction), `schema.js` (column
  metadata + label resolution), `skeleton.js` (toolbar/frame), `search.js`,
  `menus.js` (Actions + header menus), `saved.js` (saved-report management),
  `export.js`, plus `render/` (`format.js`, `chips.js`, `grid.js`,
  `chart-view.js`, `pager.js`) and `dialogs/` (`parts.js` shared building blocks,
  `columns.js`, `rules.js` — the expression-rule dialogs mirroring the server's
  unified rule pipeline, `grid.js`, `view.js`, `save.js`). `admin/element.js` is
  the admin widget; `chart/` (`theme.js`, `render.js`) backs the chart bundle.
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
  resolves with no `api-base` attribute. Served anonymously for the same reason the
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
  `labels` override for base columns and edits the rule label for computed ones; blank
  restores the schema default). The Column Settings dialog edits one column at a time
  (edits stage per column, so several columns can be configured in one visit): a
  Visible checkbox that writes the same `doc.columns` list the shuttle owns
  (re-shown columns append to the end — no second source of truth), alignment,
  a display-mode selector (text/link/image) with explicit renderer source columns,
  a per-type format-mask select, bold/italic, text/background colors, validated custom
  CSS classes, and a live
  preview fed by the column's own data; everything but visibility lands in the
  doc's `formats` map (§5), applied by the grid renderer to header alignment,
  cells, and aggregate-row alignment; settings chips with
  APEX-style enable/disable checkboxes for expression rules; removing a computed-column chip also removes
  its references from columns, rules, formats, and renderer sources — and deletes dependent
  group/spread/chart stages and shelf entries outright (T0 coarse invalidation, §5);
  break groups with per-column subtotal rows and grand-total rows; row/cell highlights;
  group/spread rendering; saved-report select
  (Default + Primary/Global/Private groups); `ignored[]` and problem+json surfaced as
  notices — validation problems render *inside* the originating dialog, which stays
  open (apply is transactional: mutate a clone, install, re-query, and on failure
  restore the last server-validated state — so a throwing mutator, an overlapping
  edit, or a failed saved-report load can never strand a half-mutated or
  never-validated document). Menus are
  **stage-aware**: Columns, Column Settings, Compute, Highlight, and Sort operate on
  the *current* terminal table — the source layer in grid, the group stage's derived
  schema (dims + `__count` + metrics + computed) under a group tail, the spread
  layer's presentation maps under a spread tail — while Filter always edits the
  source layer. On load the client adopts the delivered document as-is — server
  documents are authoritative, saved reports are accepted liberally — and the
  server judges it on query: a validation failure rolls the load back to the last
  validated state, soft drift arrives as `ignored[]` (§5). The whole
  surface is gated by the definition's feature whitelist (§4), which arrives resolved
  on the schema payload: menu entries vanish with their headings and separators, the
  search bar / view buttons / Actions button / saved-report select hide when their
  features (or every entry under them) are gone, header cells stop being clickable
  when no header-menu entry survives, and chips owned by an absent feature render
  locked — visible state, no toggle/edit/remove (except leaving a locked view for the
  grid, which stays possible). Reset remains as long as any doc-mutating feature
  exists; a missing `features` field (older server) means everything is on.
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
- **M9 — Charts** ✅ *(2026-08-06)*: APEX-style chart view (bar/line/area/pie, one
  label + one numeric metric, optional aggregation, chart-owned sort, orientation,
  axis titles) as a fourth `ViewMode` — additive `ViewSpec` fields, no state-version
  bump. Composition reuses the shared grouped shape over the filtered core;
  `maxChartPoints` (default 1,000) rejects oversized charts precisely instead of
  truncating; chart-mode export emits the charted points. Packaged UI gains the
  Chart dialog/chip/view with a lazily loaded tree-shaken Chart.js bundle, chart
  theme tokens, and a canvas description + "View chart data" table for assistive
  tech.
- **M10 — Friendly names** ✅ *(2026-08-07)*: client-side display names over a unified
  document flow. A definition's `columnLabels` and a state's `labels` map (additive
  field, no version bump) carry real column name → display label; the schema endpoint
  always returns a complete effective `defaultState` (stored primary `Default`, inline state,
  or synthetic fallback) whose
  labels deliver the mapping to the client. Document ingestion is one pipeline
  (`ReportExecutor.IngestDocument` → `StateValidator`) that resolves labels for every
  path; query surfaces stay on neutral server labels — the packaged UI resolves
  display names from its state document (grid, chips, dialogs, synthetic
  groupBy/chart metric labels) and gains header-menu Rename — while export, the
  server rendering the user's screen, applies the posted document's labels to headers
  and synthetics via `ValidatedState.WithDisplayLabels`.
- **M11 — Feature whitelist** ✅ *(2026-08-07)*: per-report `features` whitelist (§4)
  — sixteen canonical tokens covering the Actions menu, search, views, saved
  reports, and download; validated fail-fast at definition load, resolved onto the
  schema payload, and applied by the packaged UI (chrome removal + locked chips).
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
- **M13 — Column settings** ✅ *(2026-08-07)*: per-column presentation via the state
  document's second map, `formats` (§5) — closed-vocabulary format masks, alignment,
  bold/italic, text/background colors — plus a Column Settings dialog (feature token
  `columnSettings`) whose Visible checkbox writes the `doc.columns` list itself.
  Masks and styling remain client-only; definitions ship default formatting through the effective Default state,
  with no new config surface. Remaining per-column configuration candidates: LOVs,
  help text, per-column sort/filter permissions.
- **M14 — Trusted custom CSS** ✅ *(2026-08-07)*: a definition-owned `styleSheet`
  URL is delivered through schema and linked inside the report's shadow root. Column
  Settings writes validated class tokens to `formats.classes`; the grid applies them
  to headers, cells, and aggregates while filtering malformed and reserved `ir-*`
  state. Reports select application-authored rules but cannot inject CSS or URLs.
- **M15 — Column renderers** ✅ *(2026-08-07)*: the grid owns a per-column renderer
  seam with text, link, and image implementations. Column Settings selects the display
  mode and its URL/text source columns. Hidden dependencies are schema-bound server-side
  and projected only as renderer inputs. Grid CSV exports use the same encoded HTML
  fragments as browser cells while leaving ordinary values raw. DOM construction,
  HTML encoding, and a URL protocol allowlist keep report data out of active-content surfaces.
- **M16 — Actions pagination** ✅ *(2026-08-07)*: Actions → Pagination owns the
  report document's page limit with APEX choices 10, 50, 100, 500, 1000, and All.
  Numeric values respect `maxPageSize`; All is the explicit `page.size: 0` protocol
  value and runs grid/Group By without a limit. The footer is navigation-only, and
  export remains unpaged under its separate `maxRows` contract.
- **M17 — Explicit null sorting** ✅ *(2026-08-07)*: every grid/Group By sort rule
  may carry additive `nulls: first|last`; absence retains the dialect default. The
  Sort dialog exposes Default/First/Last, while header quick-sorts remain Default.
  One schema-bound composer path orders grid pages, break totals, grouped views, and
  grid exports consistently, using native syntax except for SQL Server's null-rank key.
- **M18 — Aggregate and break boundaries** ✅ *(2026-08-07)*: numeric median uses a
  portable ranked aggregate shape shared by totals and aggregate views. Highlights gain
  names and explicit, validated sequence precedence. Control-break dimensions move into
  headings; a one-row page lookahead defers subtotals for continuing groups, and grand
  totals render only at the report's logical end.
- **M19 — Pipeline v3** ✅ *(2026-08-10)*: the state document becomes a literal
  pipeline of table stages, each with its own layer (§5) — group by and pivot become
  first-class tables with per-stage columns/labels/formats/computed/sorts/highlights;
  pivot decomposes into `group` + `spread` with pre-spread computed metrics; stable
  metric/cell identity (`m1`, `m1@["…"]`) replaces positional `v0`/`p{k}_{v}`; the
  schema snapshot rides in the document with client-side match-or-reset (T0 coarse
  invalidation). Versions 1–2 rejected; no external consumers, no migrator.
  Verified: composer goldens ×4 dialects including the `ir_stage`/`ir_stage_calc`
  wraps and metric ordering; SQLite end-to-end for group-layer compute/sort/highlight
  and spread cells/totals (incl. totals-unsafe computed exclusion); packaged-UI unit
  suite; Playwright e2e against the live Workbench (saved pipeline+shelf+snapshot
  round-trip, coarse computed deletion, v3 admin document upload). Live-dialect
  battery updated but not yet run. Remaining post-T0 candidates: group-stage filters
  (HAVING), aggregates/breaks on the group stage, cross-cell pivot expressions,
  dependency-aware snapshot pruning.
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
  breaks count as sorting), and `helpText` (header-menu note). No state-model or
  `stateVersion` change; query payloads untouched. Verified: template parse/binding
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
  wrapped in problem-document shaping for post-reload breakage. Packaged
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
| Highlight predicates push down as private booleans; pivot stays in C# | Interpret highlight expressions in C# or use native PIVOT | Filters and highlights share one typed predicate implementation; private markers are removed before the response. Pivot still avoids the least-portable SQL surface. |
| `net8.0` | `net10.0` | Umbraco 13 LTS floor; SDK 8 present; bump is cheap later. |
| whoami off by default | always on | It's an information endpoint; enabling is a deliberate operator act (samples enable it). |
| Admin match case-insensitive exact | case-sensitive | Operator-friendly for emails/usernames; GUID-style values don't collide under folding. |
| Saved-report ids are text GUIDs | identity/sequence columns | One DDL shape across SQLite/SqlServer/Oracle; no sequence plumbing. |
| Timestamps as ISO text, flags as 0/1 | native per-dialect types | Uniform semantics and sorting across dialects for an engine-internal table. |
| Global/primary flags admin-only; content remains owner-managed | all published mutations admin-only | Publication is a curation act, while title/state and deletion remain owner actions. |
| Primary is a separate admin-controlled flag; primary `Default` overrides generated Default | configured-file primary is the default | Several curated reports may be primary and public, while one stable title controls default-state replacement and unflagging restores the generated fallback. |
| Configured report files use the saved-report protocol with `isReadOnly` | separate configured-report API or expose file origin | One selector and load path keeps the document model coherent. Generic mutability is what clients need; the storage source remains a server concern. |
| Microsoft.Data.Sqlite dependency in the AspNetCore package | host-supplied providers only | SQLite remains a supported out-of-box `dataSource`; persistence still requires an explicit target and never creates a database merely because the package was installed. |
| Decimal parameters bind as double on SQLite | decimal-as-TEXT (provider default) | The provider's TEXT binding breaks comparisons against affinity-less expressions (computed columns) via SQLite's cross-type ordering; double is SQLite's native numeric storage, so the conversion is faithful to the engine. |
| Pivot caps: 60 column groups (configurable) + hard 10k source groups | unbounded pivot | An unbounded pivot is a memory/usability grenade; the caps surface as precise 400s telling the user what to change. |
| Chart overflow is a precise 400, never truncation | truncate at the point cap like grid export | A truncated bar chart is misleading; a truncated pie is a lie — its proportions claim to describe the whole. Export truncation keeps its header signal; charts get an error naming the cap. |
| One chart, one metric (APEX model) | multi-series charts | Covers the dominant reporting ask with a small state surface; multi-series, click-to-filter, legends-as-controls, image export, and "Other" folding stay open as increments that extend — not rework — this shape. |
| Chart.js in a lazily imported third bundle | Apache ECharts; server-rendered SVG | Chart.js covers exactly bar/line/area/pie, tree-shakes small, MIT. ECharts earns its size only when multi-series/zoom/dense-data arrive. The lazy chunk keeps grid-only pages at their old weight; embedding keeps the no-CDN packaging story. |
| CSV: UTF-8 BOM, label headers, X-IR-Truncated header | bare UTF-8, name headers, silent truncation | Excel needs the BOM to detect encoding; users recognize labels, not internal names; silent truncation reads as complete data. |
| Views share the export pipeline | grid-only export | Exporting "what the view shows" falls out of running the same validated state unpaged — no special cases. |
| Oracle BindByName via reflection in CommandBuilder | reference ODP.NET from Core | ODP.NET binds by position by default; context params appear first in SQL but are added last, so positional binding silently misbinds. Reflection keeps Core provider-free. |
| UI: packaged vanilla ES modules as custom elements | React/Vite application | Package consumers need no frontend toolchain; hosts embed one script tag + one element. The repository uses esbuild only to produce the release bundles, and the protocol keeps the JS small enough that a framework buys little. |
| UI assets embedded in the AspNetCore assembly, served under the mapped prefix | RCL static web assets | Zero host setup (`UseStaticFiles`/`_content` not required), one mapping call delivers API + UI, works identically in any host. |
| UI asset endpoint `AllowAnonymous` | inherit group auth | Assets are public package code (readable on any feed); an auth-gated script tag turns "session expired" into a blank region that can't even say "sign in". Data endpoints keep the full gate. |
| Asset ETags hash content | assembly version tag | Version-tagged ETags 304 stale content across rebuilds of the same version (bitten in dev; would bite ops on patch releases). |
| Shadow DOM + theme properties + configured inner stylesheet | Light DOM + `.ir-*` prefix | Isolation keeps host resets out and report rules in. Theme tokens cover broad branding; a developer-owned stylesheet inside the root supports deliberate internal and per-column styling. |
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
| Friendly names live in the document; the server delivers `columnLabels` as the default report's `labels` and keeps query surfaces neutral | apply labels at discovery/validation so all results carry them | Display naming is presentation: the engine's schema, validation, and query metadata speak real names and neutral labels, so no execution surface depends on captions, and the client renders its own document's names (rebuilding grid/chart synthetics from the view spec it authored). |
| Report `labels` maps are never validated | ignored[]/errors for unknown or blank entries | The server validates what gates execution; labels gate nothing. Unknown keys are unused display data, exactly as resilient as a saved report is entitled to be. (Config-side `columnLabels` still fail fast on blank/case-colliding entries — config mistakes, not state.) |
| Export applies the posted document's labels (ingest → `WithDisplayLabels`) | neutral export headers; look up the user's saved report server-side | An export is the server rendering the user's screen, and the active document may never have been saved — the posted document is the only source of truth. One ingestion pipeline resolves labels (request ?? defaultState ?? columnLabels, matching the delivered default report); the export path relabels metadata surfaces only, so composition, row keys, and golden SQL are untouched. |
| Schema endpoint synthesizes an empty `defaultState` | nullable `defaultState` | Every client would otherwise invent its own "no default configured" behavior. An empty state already *means* the right thing — all columns, database order — and it is also the delivery vehicle: the definition's mapping rides down as its `labels`. |
| Feature control is a flat whitelist on the definition | APEX-style per-action objects; per-column attribute model | One `features` array covers the lockdown need with one concept; absent = everything keeps existing configs working. The richer per-column attribute model (alignment, masks, LOVs, per-column permissions) layers on later without reshaping this. |
| Whitelist is presentation-level except `download` and `savedReports` creation | validate posted state docs against the whitelist | Hiding a dialog is not a data boundary — the query endpoint already accepts any valid document, and context params (§12) are the security story. The two enforced tokens are the ones that egress (unpaged export) or persist (saved-report rows); enforcing at creation only keeps existing saved reports manageable after a config change. |
| Locked chips: state from an absent feature displays read-only | hide the chips; let them stay editable | The chip strip is the doc made visible — hiding active filters would misrepresent the data shown, and editing them would reopen the very dialogs the whitelist removed. Leaving a locked view for the grid stays possible: it abandons the feature rather than using it. |
| Column formatting is a second document map (`formats`); only renderer source names reach server projection | apply every mask/style server-side | Masks and styles remain client presentation, while link/image dependencies must be schema-bound before entering SQL. One base text renderer owns scalar masks; link/image compose it, and synthetic metric metadata retains its format source. Grid CSV exports intentionally serialize Display As modes as browser-like encoded HTML; hidden inputs never become columns. |
| Masks are closed per-type tokens (Intl-backed) | freeform mask strings (APEX FML/999G999D99) | Same rule as TO_STRING: a portable vocabulary has stable meaning while `Intl` supplies the user's separators, symbols, and ordering; it cannot smuggle anything. Unknown tokens fall through to default rendering instead of erroring, because a display mask must never break a report. |
| Int64/UInt64/Decimal travel as invariant JSON strings; all numeric columns use bundled `big.js` | send every numeric database value as a JSON number; maintain separate integer/decimal formatters | JavaScript parses JSON numbers as IEEE-754 doubles. Typed metadata retains numeric behavior, while one arbitrary-precision path handles parsing, comparison, scaling, and rounding without silent digit loss. |
| Column classes select a definition-owned shadow-root stylesheet | freeform style/CSS in report state; page-level classes | The URL and CSS stay application-controlled; saved reports carry only conservative class tokens and cannot select reserved `ir-*` behavior. Page CSS cannot cross the shadow boundary, while freeform report CSS would be an injection surface. |
| The dialog's Visible checkbox writes `doc.columns` | a per-column `visible` flag in formats | One source of truth: the shuttle, the header Hide, and the checkbox all edit the same list, so they can never disagree; re-shown columns append to the end, matching how a user thinks about "bring it back". |
| Link/image renderers use explicit source columns | arbitrary HTML or URL/text templates | Column names can be schema-bound and safely projected. Direct DOM construction preserves escaping, and a protocol allowlist blocks active-content URLs without inventing a template language. |
| v3 document is a literal stage pipeline + shelf | nest per-view layers inside a views registry; keep grid at the doc root | Every table stage carries the same layer bound to its own schema — no grid-vs-view special case, and pivot honestly decomposes into group (SQL) + spread (memory) with the compute slot between them. The shelf keeps the fork (configured alternate tails) without pretending the document is linear. |
| Mode is derived from the pipeline tail | a stored `view` field | A stored mode can disagree with the stages; the tail cannot. Toolbar switching is tail-swapping against the shelf. |
| Metrics carry explicit ids (`m1`); spread cells are `{metricId}@{JSON values}` | positional `v0..vN` / `p{k}_{v}` names | Positional names silently change meaning when the values list reorders or data shifts — the failure class this engine exists to prevent. Value-derived cell names make never-validated presentation maps naturally dormant/revivable under data drift. |
| Schema snapshot lives in the state document; the client checks and resets | definition-side snapshot; server-side enforcement | The document is the thing that was authored against a schema, so it records which one; both comparison inputs already reach the client. T0 mismatch = don't run, explain, reset the working copy (stored rows untouched); the same diff powers post-T0 dependency-aware pruning with no protocol change. |
| Edit link is a constrained URL template in the definition | explicit source columns only (the M15 renderer rule) | A scoped reversal, not a repeal: M15's rejection targeted templates in *report state* — untrusted documents. The definition author already writes the raw SQL, so the trust boundary is unchanged; the template is URL-only (no HTML), placeholders are schema-bound and URL-encoded at substitution, the result still passes the protocol allowlist, and the template never enters report state — the M15 rule keeps holding for documents. Computing URLs in SQL instead would pollute the discovered schema with a link column every picker shows. |
| Per-column overrides are a definition map delivered beside the schema (`columnOverrides`) | extend `ColumnInfo`; put flags in the state document | The per-column attribute model M11 anticipated. `ColumnInfo` is shared with query responses (and `availableColumns` overlays schema columns client-side, which would erase flags after the first query), so a parallel map keeps query payloads byte-identical. Labels ride the existing `columnLabels`/default-report channel so precedence has one implementation; sort/filter restrictions follow the whitelist philosophy — client hides controls, server strips violations into `ignored[]` so stale saved reports degrade instead of erroring — and computed columns are exempt to keep the rule predictable without transitive analysis. |
| Dialect is a property of the connection, derived from the driver | per-report `dialect` as source of truth; derived-with-cross-check | A report's dialect can never legitimately differ from its connection's — the old per-report field only ever produced silently wrong SQL, and an omitted value silently bound as enum 0 (SqlServer). Provider tokens fix it statically; code factories are sniffed from one unopened connection (zero I/O); wrappers declare it where the wrapper is created (`AddConnection` overload). Leftover config keys are superseded, not rejected: the derived value is correct by construction, and removing vestigial config surface beats building rejection machinery for it. |
| Per-report `dataSource` with `=`-discrimination | a named Connections config section; nested report groups | Matches the owner's model — a report is a name, a connection string, and a SELECT — with one property and zero indirection. A bare name references the standard `ConnectionStrings` section (never silently a literal), so Umbraco's `umbracoDbDSN` + `_ProviderName` convention works untouched; resolved sources become content-addressed internal connections, so a config edit rolls the schema-cache identity for free. Code-registered `connection` names remain the programmatic path. |
| Providers load reflectively from the host's dependency graph | hard provider references; per-dialect adapter packages | The engine needs only name → unopened `DbConnection` (the Oracle BindByName reflection set the precedent). Tokens map to assembly-qualified types; a missing driver fails at startup naming the exact package. No dependency bloat for hosts that never touch a given engine, no extra packages to version, and Umbraco hosts already ship SqlClient + Sqlite. |
| Packaged pages serve an anonymous shell for any name | 404 unknown names; gate the page like the data | The shell is public package markup with zero data — rendering identically for every name is what makes it disclose nothing, and the element's schema call is the real gate (an auth-gated page could not even tell a signed-out user to sign in). Same rationale as the AllowAnonymous assets. |
| Source maps stay embedded in the package | strip maps on pack | One hermetic build shape (content-hash ETags stay honest across dev and package), and readable stack traces from the minified bundles during production support are worth ~2 MB in a server-side package. |
| MIT + full nuget.org metadata at 0.9.0, shared via `Directory.Build.props` | per-project metadata; private-feed-first | One file owns identity/license/Source Link/symbols for all three packages; 0.9.0 signals pre-1.0 while the package soaks in a real host. Dependencies pin 8.0.x so the packages do not drag 10.x `Microsoft.Extensions.*` into Umbraco 13's 8.x graph. |
| Group-stage filters reserved but not populated at T0 | ship HAVING filters now; forbid forever | Filtering means "which rows exist" (stage 1) in the user's model today; the layer slot makes group filters a dialog-UX task later, not a pipeline change. |
| v1/v2 documents rejected outright | migrate v2 into v3 | Owner-confirmed: nothing external consumes documents or the wire protocol; a migrator would be dead code the day it lands. |
| Server documents are authoritative; saved reports adopt liberally (snapshot gate retired) | keep the client-side schema-snapshot match-or-reset (the snapshot row above) | A full reversal of the snapshot row (owner, 2026-08-28): the server already judges every document on query — hard problems return a validation response the client now rolls back transactionally, soft drift degrades through `ignored[]` — so the client-side predictive gate second-guessed the authoritative judge and refused documents over recorded diffs the document might never reference. Saves stamp nothing; adoption strips the legacy `schema` key; the server-side machinery (state-model member, resolver copy, default-state stamping) was removed with it, legacy rows hydrating past the unknown member. |
| Explicit `none` / `snapshot` consistency policy; provider owns the mechanism | automatic best-effort transactions; one portable mega-statement | `none` is a valid no-side-effect choice. `snapshot` is either honored or rejected, never silently degraded. Oracle uses `SET TRANSACTION READ ONLY` plus an anonymous PL/SQL multi-`REF CURSOR` batch, Postgres repeatable read, SQLite a read transaction, and SQL Server SNAPSHOT only when the administrator enables it. This keeps one application contract without pretending database mechanisms or operational requirements are identical. |
| CSV text cells get the apostrophe formula guard by default | rely on RFC 4180 quoting; sanitize only on request | Quoting does not stop Excel from evaluating `=`/`+`/`-`/`@`-leading cells, exported database text can be attacker-authored, and the writer explicitly targets Excel (BOM). Only text-sourced cells are touched — numbers and dates keep full fidelity — and `CsvCellPolicy.Verbatim` remains for non-spreadsheet consumers. |
| Title uniqueness backed by a user-rows-only unique index | trust the endpoint pre-check; span both origins | The check-then-insert race made the documented guarantee advisory. A code-computed `TITLE_KEY` (trim + invariant casefold) compares identically on every collation; the index covers user rows only because configured documents deliberately shadow user titles and sync must never fail on that. The store translates violations into the pre-check's own 409, and `Put` now treats only provider-classified unique violations as a lost insert race — anything else propagates rather than being reported as applied. |
| Route-literal report names (`ui`, `saved`, `whoami`, `admin`) fail fast | document the shadowing; namespace system endpoints | Literal-first routing makes such reports silently unreachable or — worse — partially reachable (`saved` answers queries but not schema). Failing configuration names the conflict; moving system endpoints would break every existing consumer for four names nobody should use. |
| Foreign-input strictness: numeric enums and structural nulls are 400s | accept and reinterpret; let nulls surface as 500s | The protocol serializer only ever writes camelCase enum strings and never writes nulls, so both shapes are provably foreign — rejecting them cannot break a legitimately saved document (liberal acceptance intact). `dir: 99` previously validated as an undefined member and executed as *something*; a null pipeline stage crashed the resolver into a sanitized 500. |
| GraphQL rows adopt the REST exact-number contract | exact-number scalars; leave GraphQL.NET's serializer alone | The same report must not have two wire semantics: dynamic row values (`ComplexScalar`) silently rounded Int64/Decimal through doubles in JS clients. Normalizing rows to invariant strings mirrors `IrJson`; `totalRows`/`elapsedMs` stay `Long` scalars because their schema type declares number semantics and their magnitudes fit a double exactly. |
| Saved rows record the configured definition key as `REPORT_NAME` | persist the route token as requested | Case-insensitive lookup with route-cased persistence scattered one report's rows across spellings on case-sensitive databases (`/orders` vs `/Orders`). The configured key is the identity; alternate casing stays accepted at the boundary and dies there. |
