# Interactive Reports architecture

Interactive Reports is a server-side relational query engine with optional HTTP and
browser adapters. An application supplies a trusted report definition. A user supplies
a report document that describes how to transform and present that report. The engine
validates the document, compiles it into parameterized SQL for the configured database,
executes a bounded set of queries, and returns data with the effective document and its
presentation metadata.

This document describes the production architecture and the responsibilities of its
major components. It intentionally does not serve as a configuration reference, route
catalog, user manual, or development plan. Those subjects live in:

- [Getting started](GETTING-STARTED.md) for configuration and report documents.
- [Integration API](API.md) for registration, extension points, REST routes, and public
  .NET and browser APIs.
- [Authorization](AUTHORIZATION.md) for the complete action and resource model.
- [Embedding the report](EMBEDDING.md) for the custom element and its host contract.
- [GraphQL adapter](GRAPHQL.md) for the optional GraphQL transport.
- [User Guide](USER-GUIDE.md) for report operations and expression syntax.
- [Developing](DEVELOPING.md) and [Testing](TESTING.md) for repository workflows.

## System context

The normal deployment consists of an ASP.NET Core host, one or more report databases,
and, when saved reports are enabled, a relational persistence store. The persistence
store contains report documents and authorization metadata. It does not contain cached
report rows.

```text
 Browser or API client
          |
          v
  +----------------------------- ASP.NET Core host -----------------------------+
  |                                                                           |
  |  JSON adapter       GraphQL adapter       File-download adapter            |
  |       \                  |                       /                         |
  |        +---------- IInteractiveReportServer -----------+                  |
  |                            |                                               |
  |             definitions, authorization, persistence                       |
  |                            |                                               |
  |                       ReportExecutor                                       |
  |                            |                                               |
  |        validation -> bound relation plan -> SQL -> result shaping          |
  +----------------------------|-----------------------------------------------+
                               |
                    ADO.NET connections
                      /                \
             report database     saved-report store
```

The adapters translate transport concerns into the same typed server operations. They
do not implement separate query or authorization rules. A host can also call
`IInteractiveReportServer` directly and omit HTTP entirely.

## Architectural invariants

The implementation is organized around several invariants.

### The server owns executable input

The report definition contains the base `SELECT`, connection, trusted parameters,
limits, and authorization policy. It is loaded on the server by name. SQL text and
connection information are never accepted from a report document or returned to a
client.

The engine treats the configured `SELECT` as an opaque source relation. It may wrap the
statement, but it does not parse or rewrite the statement itself. The definition is
therefore trusted application code.

### Report documents are data

A report document names columns and declares operations such as filters, computed
columns, grouping, sorting, and formatting. It cannot contain SQL fragments. The engine
validates names against a discovered or derived schema, parses expressions with its own
closed grammar, binds them to typed columns, and emits the SQL itself.

User values become command parameters. Column references are resolved to canonical
schema entries before the SQL backend quotes them. No client-supplied identifier or
literal is copied directly into executable SQL.

### Every transport crosses one application boundary

`IInteractiveReportServer` is the transport-neutral application boundary. It owns
configuration discovery, document loading, queries, exports, saved-report operations,
and authorization administration. JSON, GraphQL, file download, and in-process callers
all receive the same visibility, ownership, validation, and failure semantics.

### Relational work stays in the database

Filters, searches, computed columns, groups, pivots, sorts, aggregates, and highlight
predicates are pushed into the report database. The server shapes returned rows and
metadata, but does not reproduce the relational engine in memory.

This makes report results live views of the source database. Interactive Reports does
not extract report data into a local analytical database or maintain a row cache.

### All branches derive from one completed relation

The page, count, footer aggregates, control-break totals, pivot totals, and highlight
markers are built from the same validated relation plan. Their SQL may be issued as
separate statements, but their predicates and computed schema cannot drift through
independent composition paths.

## Package and dependency structure

The distributable projects target .NET 8. Dependencies point from optional adapters
toward the server and core; the core has no ASP.NET Core dependency.

| Project or directory | Architectural responsibility |
|---|---|
| `InteractiveReport.Core` | Report and state models, structural validation, canonical planning, expression binding, SQL lowering, schema discovery, execution, and persistence contracts and implementations. |
| `InteractiveReport.AspNetCore` | Dependency injection, configuration-backed definitions, connection resolution, startup validation, trusted request context, authorization, configured-document synchronization, and `IInteractiveReportServer`. |
| `InteractiveReport.Client.Json` | REST and administration endpoints, coded HTTP errors, viewer pages, and embedded browser assets. |
| `InteractiveReport.Client.GraphQL` | Optional query-only GraphQL.NET adapter over saved documents and the central server boundary. |
| `InteractiveReport.Client.FileDownload` | Optional authorized CSV endpoint and CSV presentation. |
| `src/client` | Source for the report and administration custom elements and chart integration. Its bundles are embedded in `InteractiveReport.Client.Json`. |
| `src/browser-server` | A SQLite WASM implementation used by the standalone browser demo. It is not part of the production .NET request path or the authority for server semantics. |
| `samples/Workbench` | Development host that composes all production packages against a seeded SQLite database. |
| `tests` | Core, ASP.NET Core, live-dialect, browser-unit, browser-server, and Playwright coverage. |

The dependency direction is:

```text
InteractiveReport.Core
        ^
        |
InteractiveReport.AspNetCore
        ^
        |
        +-- InteractiveReport.Client.Json
        +-- InteractiveReport.Client.GraphQL
        +-- InteractiveReport.Client.FileDownload
```

The three client packages are siblings. For example, the file-download package does
not call a JSON endpoint, and the GraphQL package does not reimplement the REST layer.
Each calls the server boundary directly.

## Runtime domain model

Three inputs define a request:

1. A `ReportDefinition` identifies the trusted SQL source and server policy.
2. A `ReportState` describes the user's relational and presentation choices.
3. An `InteractiveReportRequestContext` carries the principal, scoped services, and
   trace identity supplied by the host or transport.

The result contains the active table's rows and metadata plus a detached, effective
report document. The browser adopts this returned document, including refreshed schema
metadata, instead of assuming that its submitted copy was authoritative.

### Report definitions

`IReportDefinitionStore` resolves a definition by its stable application name. The
default `ConfigurationReportDefinitionStore` reads monitored ASP.NET Core options and
returns a detached, validated snapshot for each lookup. It also resolves the configured
connection and derives the SQL dialect from that connection before execution.

Definitions can contain default report state. Request fields overlay that default at
the top level, after which the engine works only on a deep copy. A request cannot mutate
the configured default or observe an options object while it is being reloaded.

### Report documents and named relations

A report document contains a case-insensitive map of named tables and selects one as
`activeTable`. Each table reads either from the reserved `definition` source or from
another named table:

```text
definition -> orders -> regional-summary -> chart
                \
                 +-> customer-pivot
```

This is a directed relation graph, constrained to parent chains. Recursive compilation
rejects missing parents, cycles, excessive depth, and documents over the configured
structural ceilings.

Each table has two distinct outputs:

- Its **export** is the completed relation, public schema, and inheritable metadata that
  a child table may consume.
- Its **local result** contains selection, ordering, breaks, footer aggregates,
  highlights, and other instructions used only when that table is the request target.

This distinction prevents a parent's page size, visible columns, sort order, or visual
decoration from silently changing a descendant's data semantics. A child begins from
the parent's exported relation, not from the rows the parent happened to render.

### Canonical operation order

The order of entries in a table's `composables` array is serialization, not executable
semantics. `CanonicalTableNormalizer` converts the mutable document into a deterministic
specification. The effective phase order is:

```text
parent export
    -> optional shape (Group, Pivot, or Chart)
    -> computed columns in dependency order
    -> filters
    -> labels and formats
    -> active-request search
    -> target-local selection, ordering, breaks, aggregates, and highlights
    -> paging and result materialization
```

Only one relational shape is owned by a table. Additional views are represented as
additional named tables over a shared parent. Computed columns can depend on other
computed columns in the same table; the normalizer topologically orders them and
rejects dependency cycles.

Per-table `schema` values in a report document are response caches for editors and
renderers. They are never trusted for binding, authorization, or SQL generation. The
server derives live schemas while compiling and refreshes the caches in the returned
document.

## Server request pipeline

A query passes through the following stages.

```text
adapter
  -> IInteractiveReportServer
  -> definition authorization envelope
  -> executable definition snapshot
  -> action, resource, ownership, and feature authorization
  -> trusted context parameter resolution
  -> ReportExecutor
       -> structural validation and default resolution
       -> base-schema discovery or cache lookup
       -> recursive table compilation
       -> expression parsing and typed binding
       -> provider-neutral bound relation plan
       -> SqlKata lowering and terminal query construction
       -> database read scope and sequential command execution
       -> public row and metadata shaping
  -> transport-specific success or coded failure
```

### Definition and authorization resolution

Where the definition store supports `IReportDefinitionAuthorizationStore`, the server
first loads a lightweight envelope containing only the canonical report name and its
authorization settings. This allows a denied request to fail before connection strings
or executable report details are hydrated.

After definition authorization, the server checks the requested action and resource.
Saved-report ownership, publication rules, administrator requirements, feature gates,
and host-provided authorizers are applied at this layer. Only then does it resolve
trusted context parameters from claims or a host replacement.

See [Authorization](AUTHORIZATION.md) for the complete gate ordering and failure rules.

### Structural validation and resolution

The engine validates the entire submitted document for null members, duplicate or
case-colliding identities, unknown operation kinds, graph bounds, and collection limits.
It then overlays the request on the definition's default state and creates a detached
effective document.

Structural validation is deliberately earlier than schema-dependent binding. This
keeps malformed object graphs from reaching recursive compilation and allows errors to
identify exact document paths.

### Schema discovery

The base schema is discovered by wrapping the trusted definition SQL and opening a
schema-only reader over a relation constrained to return no rows. Discovered column
names and provider types become the authoritative logical schema for the request.

`SchemaCache` shares one discovery task among concurrent requests for the same report
name, connection, dialect, SQL, and context-parameter signature. Failed discoveries are
evicted so a later request can retry. Configuration reload clears the cache.

Schemas created by computed columns and static shapes are derived from the bound plan.
Pivot output is data-dependent, so pivot compilation performs a bounded distinct-key
query before it can finish the output contract.

### Canonical planning and binding

`ComposableTableCompiler` recursively compiles the active table and any table whose
document schema cache needs refresh. Shared ancestors are memoized within the request.
The compiler keeps immutable exported plans separate from the target-specific request
overlay.

Binding converts names and expression syntax into typed, provider-neutral relation
nodes. The main node categories are:

- the opaque configured SQL source;
- references to completed parent exports;
- shape, compute, filter, and metadata relations;
- the active request's search relation.

Every node carries an ordered output contract with logical column identities, types,
lineage, labels, formats, and diagnostic source paths. Later stages operate on this
contract rather than returning to the untrusted document.

The expression subsystem uses a lexer, parser, typed binder, and dialect-aware emitter.
Its function and operator set is closed. Computed expressions must produce values;
filter and highlight expressions must produce predicates. Time-sensitive expressions
use one UTC instant captured for the request so every derived statement agrees.

### SQL lowering and terminal queries

`SqlKataRelationLowerer` is the only backend for bound relation nodes. It turns the
immutable relation tree into SqlKata queries while maintaining a private mapping from
logical column identity to physical aliases. SqlKata then supplies provider-specific
identifier quoting, parameters, and paging syntax.

`CanonicalLocalResultBinder` binds instructions owned by the active table.
`TerminalExecutionBundleBuilder` derives the required datasets from the same lowered
relation. Depending on the requested view, the bundle can include:

- page or export rows;
- total row count;
- footer aggregates;
- control-break totals;
- pivot totals;
- private highlight marker projections.

The engine projects highlight predicates as private database columns. Result shaping
turns those markers into row and cell metadata and removes them from public rows.

### Execution and result shaping

`ReportConnectionManager` opens one connection for the engine portion of a request,
applies trusted session settings, and creates the configured read-consistency scope.
`ReportQueryReader` executes the derived statements sequentially on that connection and
maps their stable ordinal layouts into provider-neutral values.

Sequential execution limits connection pressure and works consistently with SQLite.
When a definition requests snapshot consistency, the manager maps that requirement to
the provider's appropriate transaction behavior. When it requests no cross-statement
consistency, each statement observes the database according to its normal isolation
rules.

The executor finally removes private columns, applies public column identities and
metadata, assembles totals and decorations, and attaches the effective document. Row,
page, pivot, chart, and structural limits are enforced before or during these stages so
user state cannot create unbounded work.

## Database architecture

### Live pushdown

The engine composes over the configured query in the source database:

```sql
SELECT ...
FROM (
    /* trusted report definition */
) ir_base
WHERE /* engine-emitted predicates with bound values */
ORDER BY /* schema-bound identifiers */
```

Nested derived tables establish semantic boundaries between phases, such as making a
computed column available to a later filter. Count and aggregate queries clone or
re-lower the same completed relation before adding their terminal projection.

This design preserves current source data and the database's native type, collation,
index, and optimizer behavior. It also means interactive workloads run against the
configured database. Production deployments should prefer a reporting database or
read replica with a least-privileged, read-only principal, especially when the host is
internet-facing.

### Dialect boundary

The supported dialect families are SQL Server, PostgreSQL, Oracle, and SQLite. Oracle
11g compatibility is selected after server-version detection when required.

The SQL dialect is a property of the resolved connection. It is detected from a
code-registered connection type or from the configured provider token; wrapper
connections can declare it explicitly. This prevents a report definition from claiming
a dialect inconsistent with the connection it will execute against.

SqlKata owns general SQL rendering. Interactive Reports owns semantic differences that
cannot be delegated safely, including expression functions, date operations, boolean
predicates, null ordering, median support, snapshot setup, and older Oracle paging.
These differences are isolated behind the lowering, expression-emission, connection,
and persistence components rather than spread through transports or UI code.

SQLite is a direct package dependency of the ASP.NET Core integration. Other ADO.NET
providers are loaded from the host application's dependency graph, keeping optional
database drivers out of applications that do not use them.

## Authorization architecture

Interactive Reports consumes the `ClaimsPrincipal` established by the host. It does not
authenticate users or issue credentials.

`IReportAuthorizationService` evaluates server policy without depending on `HttpContext`
or an HTTP result type. Its inputs are the principal, an `InteractiveReportAction`, and
an `InteractiveReportAuthorizationResource`. The checks compose:

```text
host endpoint policy
    + report-definition policy
    + saved-report ownership and publication rules
    + configured application authorizers
    + trusted context parameters in the base SQL
```

The host endpoint policy is enforced by ASP.NET Core around mapped routes. The remaining
checks occur inside the central server boundary, so they also protect GraphQL and
in-process calls. A hidden browser control is never considered an authorization
mechanism.

Context parameters are resolved on the server, normally from claims. They bind values
referenced by the trusted base query and provide the row-level restriction mechanism.
Client state cannot set or replace them.

Internal exceptions are logged with a trace identifier and converted to sanitized,
coded failures. Adapters map those failures into their own wire format without exposing
connection strings, configured SQL, or provider diagnostics.

## Saved reports and configured documents

A saved report persists a report document, its report family, visibility, ownership,
revision metadata, and origin. It persists a query description, not query results.

`ISavedReportStore` defines the persistence boundary. `SqlSavedReportStore` implements
it over the configured relational store and handles the supported database dialects.
The report data source and saved-report store can be the same database, but they have
separate responsibilities and can use different connections.

Configured report-document files are source-controlled document bodies. The
`ConfiguredReportDocumentSynchronizer` reconciles their identities and metadata with
the saved-report store so configured and user-created documents can share listing and
selection behavior. Configured documents remain read-only through application APIs.

Authorization metadata uses a separate store abstraction and participates in the same
connection and dialect infrastructure. Administration pages call server operations;
they do not access either persistence table directly.

## Transport and browser architecture

### JSON adapter

`InteractiveReport.Client.Json` maps the primary REST surface, saved-report and
authorization administration operations, schema and list-of-values requests, viewer
pages, and embedded static assets. Endpoints are thin: they deserialize input, construct
an `InteractiveReportRequestContext`, call `IInteractiveReportServer`, and map its
result to HTTP.

The package returns stable error codes with sanitized descriptions and trace identifiers.
Exact route and payload contracts are documented in [Integration API](API.md).

### GraphQL adapter

`InteractiveReport.Client.GraphQL` provides query-only discovery and execution of saved
documents. Paging, search, and sorting overrides are applied to a detached document,
which is then submitted through `IInteractiveReportServer`. The adapter has no mutation
model and no independent report catalogue or authorization path.

### File-download adapter

`InteractiveReport.Client.FileDownload` authorizes an export, asks the server for the
unpaged active result, and renders CSV from the returned public data and presentation
metadata. It does not bypass row limits; a response can explicitly report truncation.

### Browser custom elements

The browser bundle is a presentation client over the JSON protocol. The
`<interactive-report>` element owns working report state, dialogs, rendering, chart
integration, localization, and browser events. It renders inside a shadow root and does
not need a host-side front-end framework.

The element maintains at most one active query per widget and coalesces edits made while
that query is running. On success it adopts the server-returned effective document. On
failure it retains the last accepted state. This makes server validation the commit
boundary for user edits while keeping UI interaction responsive.

The `<interactive-report-admin>` element uses the same transport module and server
operations for persistence and authorization administration. Neither element is a
security boundary.

## Lifecycle, concurrency, and caching

Most registered engine and store components are stateless singletons or own
thread-safe caches. Request-specific mutable state stays in detached definition and
document copies, request contexts, and request-scoped compilers.

The base-schema cache is the only report-data-related process cache. It stores metadata,
not rows. Its key contains the schema-affecting definition fields, and configuration
reload invalidates it. Request-local compilation memoizes named-table ancestors only for
the life of that request.

The startup hosted service validates report definitions, resolves their connections and
dialects, verifies configured provider activation without opening a database connection,
checks document-file declarations, and checks persistence table-name invariants before
the host begins serving normal work. Saved-report storage itself remains optional and is
resolved when a persistence or administration operation needs it. Later configuration
reloads are validated when their snapshots are resolved, and failures are reported
through the standard sanitized error path.

Cancellation tokens flow from adapters through authorization, discovery, compilation,
database commands, and result reading. A canceled request does not convert into an
internal server failure. Command timeouts and document limits provide separate bounds
when a client remains connected.

## Extension points

The primary host extension points align with architectural boundaries rather than SQL
composition internals:

| Extension point | Purpose |
|---|---|
| `IReportDefinitionStore` | Supply report definitions from configuration, a database, or another trusted source. |
| `IReportDefinitionAuthorizationStore` | Resolve a lightweight authorization envelope before hydrating executable definitions. |
| `IReportConnectionFactory` / `AddConnection` | Create unopened ADO.NET connections controlled by the host. |
| `IContextParameterResolver` | Resolve trusted values used by configured SQL. |
| `IInteractiveReportAuthorizer` | Add application-specific operation authorization. |
| `ISavedReportStore` | Replace report-document persistence. |
| `IInteractiveReportUserProvider` | Supply account choices to administration UI without granting authority. |
| `IInteractiveReportServer` | Invoke the application boundary directly or build another transport adapter. |

The bound-plan and SQL-lowering layers are internal. Custom callers extend the product
at the definition, connection, context, authorization, persistence, or transport
boundaries instead of injecting SQL into the composition pipeline.

## Key architectural decisions

| Decision | Consequence |
|---|---|
| Keep configured SQL server-side and represent user changes as data. | The server can validate a closed input language and retain control of executable SQL. |
| Push relational work into the source database. | Results stay live and use native database capabilities; deployments must manage interactive load on that database. |
| Use a typed bound relation plan between state and SQL. | Validation, operation order, lineage, and dialect lowering remain separate concerns. |
| Give named tables exported and local-result boundaries. | Relations remain composable without leaking a parent's presentation or paging state. |
| Treat document schema as an advisory cache. | Clients can edit dynamic tables without stale metadata becoming an execution authority. |
| Derive all terminal datasets from one completed relation. | Page data, counts, totals, and decorations share the same semantics. |
| Put authorization and report operations behind `IInteractiveReportServer`. | Optional transports cannot drift in visibility, ownership, or execution policy. |
| Package transports separately. | Hosts pay only for the dependencies and public surfaces they use. |
| Cache schema but not report rows. | Repeated requests avoid discovery overhead while preserving live-data semantics and simpler invalidation. |
