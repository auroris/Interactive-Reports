# Logical report plan implementation

## Objective

Convert the existing `from` plus `composables` report document into a canonical,
schema-bound logical plan and lower that plan to SQLKata. The report document remains
domain data. It does not declare compiler phases, propagation scopes, SQL clauses, or
physical aliases.

The target compiler pipeline is:

```text
report document DAG
  -> typed composable syntax
  -> canonical table specification
  -> schema-bound immutable relation plan
  -> SQLKata relation
  -> terminal query bundle
  -> dialect SQL and bindings
```

## Semantic model

Every named table compiles to two products:

```text
BoundTable
  Export
    completed relation
    public schema
    inheritable structural metadata
  LocalResult
    visible projection
    ordering
    highlights
    footer aggregates
    control-break totals
    renderer/view information

  RequestOverlay
    search
    paging and delivery limits
```

A child table consumes only its parent's `Export`. Its `LocalResult` begins empty.
This is the boundary that prevents a parent's selection, ordering, paging, highlights,
breaks, totals, or renderer choice from becoming part of a derived relation.

## Natural ordering

The `composables` array is serialization order, not execution order. Each composable's
handler contributes semantic facts and dependencies. The planner normalizes those facts
into this natural order for each table:

```text
1. imported relation (`definition` or a completed parent export)
2. optional shape transformation (Group, Pivot, or Chart data)
3. computed columns, topologically ordered by column dependencies
4. exported filters
5. structural metadata over the completed public schema
6. table-local result specification
7. request-local Search over the completed active relation
8. delivery operations such as paging or export limits
```

The ordering is a partial order. Independent operations may be normalized or fused;
dependent operations retain their required ordering. Examples:

- Visible-column selection and filtering commute because selection is terminal and does
  not project the exported relation.
- A computed column must precede a filter or computed column that references it.
- A Pivot owned by a table precedes that table's computed columns and filters.
- A filter that must run before a Pivot belongs to the Pivot table's parent.
- A table may own at most one Group, Pivot, or Chart data transformation. Further shape
  composition uses another named table and a `from` edge.

Document order must never resolve ambiguous declarations. Conflicting shapes, duplicate
output identities, incompatible selections, or conflicting metadata assignments produce
validation errors. Operations with genuine combination rules, such as filters, footer
metrics, and highlights with explicit precedence, are combined by those rules.

## Composable effects

Composable kinds are not forced into one exclusive bucket. A handler may contribute an
export transformation, exported metadata, a local-result instruction, or more than one.

| Kind | Exported relation/contract | Owner-local result |
|---|---|---|
| `compute` | Adds derived public columns | None |
| `filter` | Restricts exported rows | None |
| `group` | Replaces schema with keys, row count, and metrics | Group renderer/default ordering hints |
| `pivot` | Produces a dynamic wide relation | Matrix renderer, totals request, default ordering |
| `chart` | Produces category/value data | Chart renderer, axes, orientation, point ordering |
| `labels` | Structural/default display metadata where applicable | Table-specific display overrides |
| `formats` | Safe scalar-format lineage where applicable | Styles, link/image/action renderers and dependencies |
| `select` | None | Visible response projection |
| `sort` | None | Main-row ordering |
| `highlight` | None | Page annotation predicates and styles |
| `break` | None | Ordering prefix, lookahead, and break-total query |
| `aggregate` | None | Whole-relation footer query |

`search` and `page` remain request overlays. Search is normalized into a transient filter
at its defined table scope. Page is a delivery option for the active table only.

## Column identities

Three namespaces remain separate:

1. Stable public logical IDs used by document expressions and table references.
2. Private physical SQL aliases allocated during lowering.
3. Display labels used by clients and rendered exports.

Authored synthetic columns use document-wide stable persisted IDs such as `ir1`, `ir2`, and so on.
They are assigned when authored, never from compiler traversal order. Computed and metric
outputs share the same collision-checked logical namespace.

Dynamic Pivot cells require stable identity derived from or registered against:

```text
(stable owning table id, metric id, canonical typed pivot key) -> public column id
```

Physical aliases never enter report JSON, expressions, schema caches, or response row
keys.

## Internal types

The planning layer should expose immutable values resembling:

```text
ReportPlan
  ActiveTableId
  Tables
  RequestOverlay

CanonicalTableSpec
  Shape
  Computed
  Filters
  Metadata
  LocalResult

BoundTable
  ExportedRelation
  BoundLocalResult

ExportedRelation
  RelationNode
  OrderedColumnContract

ColumnContract
  LogicalId
  Type and nullability
  Lineage
  Default label
  Format source
```

Every normalized rule or operation that can produce a field-level diagnostic retains
its original document path. Collection-only leaves such as dimension names inherit
their owning operation's path. Bound expressions contain the existing expression AST,
not source strings.

## SQLKata lowering

SQLKata is a backend for the bound relation plan, not the logical model. Lowering returns:

```text
LoweredRelation
  Query
  public-to-physical column map
  output contract
  relation-stage count
```

Initial boundary rules favor clarity:

- Wrap the trusted configured SQL once as the opaque source.
- Make every schema-changing operation addressable as a derived relation.
- Make computed outputs addressable before a dependent operation references them.
- Emit filters against the relation produced by Shape and Compute planning.
- Clone or wrap shared parent relations so lowering one child cannot mutate a sibling.
- Add safe fusion only after semantic equivalence tests exist.

Pivot remains a two-stage plan: bounded key discovery followed by a wide conditional
aggregation. Its completed public relation is addressable by later computed columns,
filters, or child tables.

Pivot therefore forms an explicit data-dependent continuation boundary rather than one
fully knowable pre-execution tree:

```text
bind/lower bounded Pivot discovery
  -> execute discovery
  -> register the dynamic output contract
  -> resume same-table Compute and Filter binding
  -> expose the completed Export to descendants
```

Because a table may own only one Shape, dynamic cell identity uses the stable owning
table identity, metric identity, and canonical typed Pivot key. It must never use the
composable array path. The generated schema contract owns the public-ID registry and its
collision policy; composables remain free of compiler bookkeeping.

### Temporary Pivot totals limitation

The current Pivot totals query branches from the Pivot input relation. It does not yet
consume the completed post-Pivot relation. Until totals are lowered from that completed
relation, `totals: true` is rejected when the same table also declares computed columns
or filters, or when request search targets that table. These combinations would
otherwise display totals for a different row/value set than the main result.

Filters declared on the Pivot table's parent remain supported because they are already
part of the Pivot input and therefore affect both the wide relation and totals branch.
The validation error is attached to the Pivot composable's `.totals` property.

## Terminal query bundle

The active table's bound local result produces independent queries over the same completed
relation:

```text
ExecutionBundle
  MainRows
  Count
  FooterAggregates?
  BreakTotals?
  PivotTotals?
  Export
```

Pivot discovery is not a terminal statement. It executes at the data-dependent
continuation boundary before the wide relation and its local result can be bound. The
terminal bundle therefore contains only statements that remain executable after that
contract exists.

Selection, ordering, highlights, and paging affect `MainRows`; selection and ordering
also shape Export. Count excludes all terminal behavior. Footer and break aggregates run
over the complete filtered relation. Private renderer dependencies and highlight markers
never enter the public schema.

## Implementation sequence

### 1. Canonical planning foundation

- Add immutable canonical specification and operation-effect types.
- Add an exhaustive semantics registry for every composable kind.
- Normalize composables without consulting array position for phase ordering.
- Reject ambiguous shapes and conflicting singleton declarations.
- Infer computed-column dependencies and topologically order them.
- Retain source paths for every normalized item.

### 2. Schema binding

- Resolve the table dependency DAG parent first and memoize completed exports.
- Bind the optional Shape against the imported schema.
- Bind computed columns in dependency order.
- Bind filters against the completed computed schema.
- Derive structural metadata and then bind local-result instructions.
- Detect cycles, missing columns, duplicate identities, and complexity violations.

### 3. Relational lowering

- Replace direct `TableComposable` interpretation with bound relation-node visitors.
- Lower source, Shape, Compute, Filter, and metadata operations to SQLKata.
- Keep logical IDs and physical aliases separate at every boundary.
- Implement Pivot discovery and stable dynamic output contracts.

### 4. Terminal planning and execution

- Build the complete execution bundle from one lowered relation.
- Move select, sort, highlight, break, aggregate, paging, export, and renderer dependency
  behavior behind the bound local-result plan.
- Make query, export, schema refresh, REST, and GraphQL use the same plan.

### 5. Cleanup

- Remove the existing mixed validate-and-lower compiler paths after their responsibilities
  move to the new planner.
- Keep the report-document contract as `from` plus `composables`.
- Update architecture documentation and plan-debug rendering.

## Required verification

- Exhaustive semantics test: an unclassified composable kind fails.
- Natural-order tests: shuffled documents produce equivalent canonical plans and results.
- Dependency tests: computed chains order correctly and cycles fail precisely.
- Shape tests: Shape runs before same-table compute/filter; parent filters run before child Shape.
- Inheritance tests: children receive exports but no parent-local result behavior.
- Conflict tests: multiple shapes and incompatible singleton declarations fail independently
  of array order.
- Query-bundle tests: each composable affects exactly the intended SQL statements.
- Four-dialect SQL lowering tests.
- Dynamic Pivot identity and descendant-binding tests.
- Pivot continuation tests across discovery cache hits and changing discovered keys.
- Golden bound-plan/debug snapshots and SQLKata visitor parity tests covering SQL,
  bindings, output contracts, and metadata lineage.
- Immutability tests for documents, parent plans, and sibling lowering.
- End-to-end query, export, totals, breaks, highlights, chart, Pivot, search, and paging tests.

## Current implementation status

The planned refactor is implemented:

- A closed composable-semantics catalog normalizes each table into an immutable
  `CanonicalTableSpec`. Shape, dependency-ordered Compute, Filter, Metadata, and
  LocalResult phases are inferred from meaning and do not depend on array position.
  Conflicts and dependency cycles retain their original document paths.
- Schema binding produces immutable opaque-source, Export-reference, Group, Chart,
  resolved-Pivot, Compute, Filter, Metadata, and request-local Search relation nodes.
  Every node owns one ordered output contract containing logical identity, type,
  nullability, labels, format source, child-visible mask, and structural lineage.
- Each named table exports a `BoundTableExport`; a child's root can refer only to that
  value. The parent's local selection, ordering, highlights, breaks, aggregates,
  renderer dependencies, search, and delivery settings are structurally unreachable.
- Pivot is a real continuation boundary. The compiler lowers and executes bounded key
  discovery, converts provider values to canonical typed keys, registers the completed
  dynamic contract, then resumes ordinary same-table Compute and Filter binding. Parent
  and discovery results are memoized for descendants without trusting advisory schema
  caches.
- Authored computed columns and metrics use document-wide persisted `irN` ids. Dynamic
  Pivot cells receive opaque `irN` ids derived from `(stable table id, metric id,
  canonical typed key)`. Existing cells keep their ids when keys are added or discovery
  order changes; the registry remains compiler state and never enters report JSON.
- `SqlKataRelationLowerer` is the dedicated recursive backend for bound relation nodes.
  It returns the query, logical-to-physical map, output contract, and stage count, and
  independently re-lowers Export references so siblings cannot mutate one another. The
  existing private physical-alias allocator is intentionally unchanged.
- `CanonicalLocalResultBinder` creates one immutable owner-local result. The active
  target also owns an immutable request overlay, and `TerminalExecutionBundleBuilder`
  derives main rows, count, footer aggregates, break totals, Pivot totals, and export
  from one completed relation after any Pivot continuation has resolved.
- Query, export, schema refresh, definition-root requests, REST, and GraphQL now use the
  same recursive compiler. The DTO-facing no-table branch and materialized Pivot/table
  processors have been removed; report documents remain only `from` plus composables
  and their domain values.
- Deterministic plan rendering, output-contract, visitor parity, four-dialect lowering,
  natural-order, inheritance, conflict, immutability, query-bundle, dynamic Pivot,
  continuation, and end-to-end tests cover the new boundaries.

Two deliberate boundaries remain. The trusted configured SQL is still opaque, and the
current physical-alias scheme is retained; both were explicitly excluded from this
refactor. Pivot totals also retain the temporary compatibility restriction documented
above until their branch lowers from the completed post-Pivot relation.
