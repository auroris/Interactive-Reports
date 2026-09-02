# Embedding the report

The packaged `<interactive-report>` custom element is the primary way to put a report
on a page. This guide covers the element's attributes, the host JavaScript API, query
lifecycle events, client-control overrides, file downloads, theming, host stylesheets,
column renderers, and definition-owned presentation such as edit links. The
[Integration API guide](API.md) lists the same surface as reference tables.

## The custom element

The bundle contains the component's styles and renders into a shadow root, so
host styles such as Tailwind resets do not reach the widget and widget styles do
not reach the host page.

```html
<script type="module" src="/assets/ir.js"></script>
<interactive-report
  report="orders"
  saved-report="87"
  api-base="/api/reports"
  stylesheet="/assets/report-overrides.css">
</interactive-report>
```

`report` is the appsettings report configuration name. The component calls
`GET /api/reports/{name}`, selects the flagged default (or the optional numeric
`saved-report` id), and retrieves it through `GET /api/reports/{name}/{id}`. It does not
use the root configuration catalogue during ordinary bootstrap. Titles are presentation
only, and duplicate titles remain distinguishable by their Public and Private groups.
Server authorization still applies when the component requests schema, report documents,
queries, and exports.

## Host API

Once the initial report has loaded, an embedding application can retrieve an export
without presenting it as a browser download. `getExport` accepts the format token and
an optional `AbortSignal`, and resolves to `{ blob, filename, contentType, truncated }`:

```js
const report = document.querySelector("interactive-report");
const abortController = new AbortController();
const artifact = await report.getExport("csv", { signal: abortController.signal });

// The host decides what retrieval means: upload it, attach it, cache it, or save it.
await uploadReport(artifact.blob, artifact.filename, artifact.contentType);
```

The Actions-menu CSV command retrieves the same artifact and then invokes the browser's
download behavior.

The element also exposes its accepted report document as a detached JSON-compatible
object. `submitReportDocument` replaces the working document transactionally, posts it
through the ordinary query endpoint, adopts the server-enriched document, rerenders the
report, and resolves to a detached query result. A failed submission restores the last
validated document; a canceled or superseded submission resolves to `undefined`.

```js
const state = report.getReportDocument();
state.search = "urgent";
state.page.index = 1;

const result = await report.submitReportDocument(state);
console.log(result.rows, report.getReportDocument());
```

Column headers, the filter editor, and the highlight editor use a searchable list of
values from the currently viewed table. Each lookup posts the complete current report document, so unsaved filters
and computed state participate, then requests one column and optional search text. The
server applies ordinary query authorization and returns at most 50 distinct values. Host
JavaScript can use the same path. The chooser is editable: selecting a result uses it,
while Enter or **Use Typed Value** accepts the search text without another input field.
Lookup search is case-insensitive and partial by default. An accepted text value is an
exact case-insensitive filter or highlight condition unless typed text contains `*`.
For example, `Ac*Corp` is a wildcard match and `Ac\*Corp` matches a literal asterisk.
The characters `%` and `_` have no wildcard meaning in this user-facing syntax:

```js
const document = report.getReportDocument();
const values = await report.getListOfValues({
  document,
  table: document.activeTable,
  column: "STATUS",
  search: "pen"
});
```

These methods require the initial query to have completed. Their arguments and return
values never expose the widget's mutable working objects. Input must be a JSON-compatible
object; a JSON string should be parsed by the caller first.

## Query lifecycle events

Every query, including initial load, saved-report loads, ordinary UI edits, and host
submissions, dispatches a bubbling, composed `ir-before-query` event immediately before
the request. Its `detail` is `{ document, source, requestId, signal }`. The detached
`document` is mutable during synchronous event dispatch; its final value is serialized
and sent. Calling `preventDefault()` cancels that query. `source` is one of `initial`,
`user`, `saved-report`, `host`, or `refresh`.

Ordinary UI edits use a 200 ms trailing-edge debounce. Rapid chip removals, toggles,
paging commands, or other packaged-control mutations accumulate in the working document
and send only the final state. If a request is already in flight, the next edit aborts it
immediately before starting the debounce window. Initial and saved-report loads, explicit
`submitReportDocument()` calls, exports, and administration refreshes are not delayed.

After a current successful query has been adopted and rendered, `ir-query-complete`
dispatches with detached `{ document, result, submitted, source, requestId }` snapshots.
It is observational: changing its detail cannot mutate the report. Submit a changed copy
with `submitReportDocument` when a returned document needs another query.

```js
report.addEventListener("ir-before-query", event => {
  event.detail.document.search = event.detail.document.search?.trim();
});

report.addEventListener("ir-query-complete", event => {
  auditReportQuery(event.detail.source, event.detail.document);
});
```

## Client controls

The schema's `features` list is the server's initial suggestion for packaged client
controls, not an authorization boundary or a ceiling. Embedding JavaScript may force a
control on or off, restore the suggestion with `null`, update several controls together,
or clear every override:

```js
report.setControlEnabled("filter", true);
report.setControlEnabled("download", false);
report.setControlEnabled("filter", null); // inherit the server suggestion again

report.setControlOverrides({ search: true, sort: false, savedReports: true });
console.log(report.isControlEnabled("search"), report.getControlOverrides());
report.clearControlOverrides();
```

Control names are `search`, `columns`, `rename`, `columnSettings`, `filter`, `sort`,
`pagination`, `controlBreak`, `highlight`, `aggregate`, `compute`, `groupBy`, `pivot`,
`chart`, `savedReports`, and `download`. Names are matched case-insensitively and retained
in canonical spelling. Overrides stay on the element across report changes. Enabling
`savedReports` after load lazily requests the list. Client overrides affect only packaged
UI: endpoints still perform authorization, validation, and their configured download or
saved-report checks.

The standard boolean `disabled` property and attribute temporarily make the entire
package-owned surface inert without altering these overrides:

```js
report.disabled = true;
await performHostOperation();
report.disabled = false;
```

`styleSheet` reflects the `stylesheet` attribute. Assigning another URL replaces the
shadow-root link immediately; assigning `null` removes it. The stylesheet belongs to
the host element and remains in place when `report` changes.

## File downloads

File downloads are a peer client, not an engine endpoint. Register and map the optional
package independently:

```csharp
using InteractiveReport.Client.FileDownload;

builder.Services.AddInteractiveReportFileDownload();
// …
app.MapInteractiveReportFileDownload("/api/download");
```

The JavaScript client posts its current report document to
`POST /api/download/{name}/csv`. The file client detaches the document, sets paging to
all rows, applies the central `Export` authorization and `download` feature gate, then
submits the mutated document through `IInteractiveReportServer.QueryForDownload` before
rendering CSV. Set the element's `download-base` attribute when the route is mounted at
a non-default prefix.

## Behavior notes

Without a client override, the widget follows the definition's `features` suggestion
([Architecture](ARCHITECTURE.md) §4): menu entries, view buttons, the search bar, and the
saved-report select render only for listed features. Independently, the server refuses
CSV export and saved-report creation when their configured `download` / `savedReports`
policies are absent.

A definition's `editLink` pencil and `createLink` button navigate like ordinary
anchors — relative URLs resolve against the host page, and routing to the edit or
create form is entirely the host application's concern. A host that would rather open
its own editor listens for `ir-edit` / `ir-create` instead (see
[Edit links, create buttons, and column overrides](#edit-links-create-buttons-and-column-overrides)).

Page size is configured through **Actions → Pagination**. The standard choices are
10, 50, 100, 500, 1000, and All; numeric choices above a definition's `maxPageSize`
are omitted. All is stored as `page.size: 0`. A positive definition `maxRows` is a
hard response cap for All grid rows, All Group By groups, and CSV export regardless of
the client request. Set `maxRows` to `0` or a negative number for unlimited results.
CSV export reports a positive-cap truncation through `X-IR-Truncated`.

Each row in **Actions → Sort** also offers Nulls: Default, First, or Last. First
and Last are stored on that sort instruction as `nulls: "first"` or `"last"` and
produce the same placement on every supported database. Default omits the field and
preserves the database dialect's ordinary ordering behavior. Header-menu quick sorts
continue to use Default.

Control-break columns render in the break heading instead of repeating in each detail
row. A subtotal appears only with the logical end of its break group, even when that
group crosses a page boundary, and grand totals appear only on the report's final page.

## Attributes and theming

`saved-report` is optional. When present, the component loads that visible numeric
document id before the first query. Omit it to load the document named by `report`.

`api-base` may be a relative path or an absolute URL. If it is omitted, the
component infers the API prefix from the script URL. The older `base` attribute
remains available as an alias. Theme tokens such as `--ir-accent` can be set on
the custom element without exposing its internal CSS.

```css
interactive-report {
  --ir-accent: #7c3aed;
  --ir-accent-soft: #f3e8ff;
  --ir-font: Inter, system-ui, sans-serif;
  --ir-radius: 0.5rem;
}

interactive-report::part(toolbar) {
  padding: 1rem;
}
```

The supported theme properties are `--ir-accent`, `--ir-accent-soft`,
`--ir-border`, `--ir-border-light`, `--ir-bg`, `--ir-bg-soft`,
`--ir-bg-header`, `--ir-text`, `--ir-text-muted`, `--ir-danger`,
`--ir-radius`, `--ir-font`, and `--ir-font-size`, plus the chart tokens
`--ir-chart-1` … `--ir-chart-8` (categorical palette; slot 1 is also the
single-series color), `--ir-chart-grid`, and `--ir-chart-text`. The supported
structural parts are `surface`, `toolbar`, `notices`, `chips`,
`table-container`, `chart-container`, `table`, `pager`, `menu`,
and `dialog`. Editor dialogs are movable, modeless windows, so the report remains
available while they are open. Short destructive confirmations remain modal.

### Dark mode

Interactive Reports includes built-in dark mode support with an accessible, high-contrast palette aligned with the APEX Universal Theme dark style.

1. **Automatic (System Preference)**: By default, the component detects and honors `prefers-color-scheme: dark`.
2. **Framework / Document Inheritance**: The component automatically activates dark mode when nested in an ancestor element with `data-theme="dark"`, `data-bs-theme="dark"` (Bootstrap 5.3+), `theme="dark"`, or the `.dark` class (Tailwind).
3. **Explicit Control**: You can force a specific theme using the `theme` attribute or property on `<interactive-report>`:
   ```html
   <!-- Explicit dark mode -->
   <interactive-report report="orders" theme="dark"></interactive-report>

   <!-- Explicit light mode, even if OS or parent page is in dark mode -->
   <interactive-report report="orders" theme="light"></interactive-report>
   ```
   Or programmatically:
   ```js
   const report = document.querySelector("interactive-report");
   report.theme = "dark"; // "dark", "light", or null/undefined to follow system/context
   ```

All chart colors, dialog backdrops, action popups, chips, control break headers, and inputs automatically adjust to match the active theme.

## Host stylesheets and CSS classes

An application integrator can set the element's `stylesheet` attribute or reflected
`styleSheet` property to a relative or absolute stylesheet URL. The component places
one `<link>` inside the report's shadow root after the packaged styles, so its rules can
target report internals without leaking into the host page. For example:

```html
<interactive-report report="orders" stylesheet="/css/orders-report.css">
</interactive-report>
```

```css
.amount-column { font-variant-numeric: tabular-nums; }
.emphasized { font-weight: 700; }
```

Column Settings accepts space-separated class names and saves them in the column's
`formats.classes` list. Classes apply to the column header, data cells, and aggregate
cells. Tokens begin with a letter or `_`, then use letters, digits, `_`, or `-`; the
component's `ir-` prefix is reserved. Report documents can therefore select
integrator-defined rules but cannot supply CSS or choose a stylesheet URL. Report
definitions do not carry stylesheet configuration. The host's
Content Security Policy still governs stylesheet loading.

## Column renderers

Column Settings also offers `Text (Default)`, `Link`, and `Image` display modes.
A link selects a URL column and a text column; an image selects a URL column. Source
columns do not have to be visible: the server schema-checks them and includes them only
in row data required by the grid and CSV renderers. Hidden sources remain absent from
displayed and exported column metadata. CSV applies effective labels and scalar masks,
uses a link's displayed text, uses an image's URL, and uses an action's raw label. It
never embeds browser HTML, CSS, or highlight styling. Relative and HTTP(S) URLs are
accepted for link and image sources;
links additionally accept `mailto:` and `tel:`. Active or embedded-content schemes
such as `javascript:` and `data:` render as ordinary text instead.

A fourth renderer, `displayAs: "action"`, is definition-authored only (Column
Settings never offers it, but preserves it across restyles): the cell's value is a
button label — a NULL label renders no button — and clicking dispatches a composed
`ir-action` CustomEvent from the report element with `{ command, row, column }`,
where the row includes the format's schema-bound `keyColumn` value. The built-in
admin listing is its first consumer; in CSV an action cell exports its raw label.

## Edit links, create buttons, and column overrides

A definition can also declare an APEX-style edit pencil, a create-record button, and
per-column overrides — configuration, not report state:

```json
"orders": {
  "connection": "MainDb",
  "sql": "SELECT ORDER_ID, CUSTOMER, AMOUNT, NOTES FROM ORDERS",
  "editLink": {
    "urlTemplate": "/orders/{ORDER_ID}/edit",
    "label": "Edit order"
  },
  "createLink": {
    "url": "/orders/new",
    "label": "New order"
  },
  "columns": {
    "AMOUNT": { "helpText": "Order total before tax." },
    "NOTES": { "hideLabel": true, "sortable": false, "filterable": false }
  }
}
```

`editLink` renders a leading pencil column in grid view. Its `{COLUMN}` placeholders
reference definition-schema columns; the referenced values travel as hidden row data (like
renderer source columns), are URL-encoded into the template client-side, and the
result is an ordinary anchor — middle-click and open-in-new-tab work, `target:
"_blank"` adds `rel="noopener"`, and a row whose placeholder value is NULL shows no
pencil. The edit column has an empty heading (its `label` is the accessible name and
tooltip), never appears in column pickers, search, sorts, or filters, and is absent
from CSV exports and grouped/pivoted/charted views.

`createLink` renders a primary-styled button on the toolbar, after **Actions**, in every
view. Its `url` is a constant (there is no row yet, so placeholders are rejected at
load), `label` is the button text (default *Create*), and `target` behaves as for the
pencil. Unlike the toolbar's feature-driven controls it is never hidden by the
`features` policy or a client override: configuring it is what shows it.

### Observing edit and create activations

Every activation of either control dispatches a bubbling, composed, **cancelable**
CustomEvent from the report element: `ir-edit` with `{ url, row }` (the row copy
includes the hidden template columns, so the key is always there) and `ir-create`
with `{ url }`. In the default `"mode": "navigate"` the anchor then follows its URL
unless a listener calls `preventDefault()`, which lets a host intercept selectively —
route through a single-page-app router, say, and fall back to the real link for
modifier clicks.

A host that never wants navigation sets `"mode": "event"` on the link. The control is
then rendered as a `<button>` with the same icon, label, and accessible name, so
keyboard and assistive-technology activation behave like any button, and the event is
its whole behavior. In event mode `createLink.url` may be omitted (`url` arrives as
`null`); `editLink.urlTemplate` stays required because it is what declares which row
values ride along with the event. Substitution and the protocol allowlist still apply,
so a NULL key still withholds the pencil.

```js
report.addEventListener("ir-create", event => {
  event.preventDefault();          // no-op in event mode; cancels navigation otherwise
  openOrderEditor(null);
});

report.addEventListener("ir-edit", event => {
  event.preventDefault();
  openOrderEditor(event.detail.row.ORDER_ID);
});

// After the host saves, re-run the current document so the grid reflects the write
// without disturbing the user's search, filters, sort, or page.
await report.submitReportDocument(report.getReportDocument());
```

The Workbench sample's `crud.html` page and the in-browser demo both host a slide-over
editor on exactly this pattern.

`columns` overrides one column at a time: `label` replaces `columnLabels` for that
column (configuring both is rejected), `hideLabel` blanks the table heading while
menus and dialogs keep the real name, `sortable: false` / `filterable: false` remove
those controls (control breaks count as sorting; computed columns are always exempt),
and `helpText` appears as a note in the column's header menu. The server also strips
sorts, breaks, and filter rules that violate the restrictions from incoming documents
into `ignored[]`, so saved reports created before a restriction degrade gracefully
instead of erroring.
