# InteractiveReports

## Client build

The client-side source is in `src/client`; generated browser assets are written to
`src/InteractiveReport.AspNetCore/Ui/dist` for embedding by the server project. Install
the toolchain and create the browser bundles with:

```sh
npm ci
npm run build
```

`npm run dev` rebuilds on changes. `npm test` builds the client and runs the fast
DOM unit tests. Browser automation is configured with Playwright; install Chromium
once with `npx playwright install chromium`, then run `npm run test:ui`.
`npm run verify` runs both test layers.

The generated `src/InteractiveReport.AspNetCore/Ui/dist/ir.js`, `ir-admin.js`, and
`ir-chart.js` files are embedded in the ASP.NET Core assembly. Generated bundles are
deliberately not committed: the release pipeline builds them before packing the .NET
projects. Package consumers therefore do not require Node.js, while source checkouts
must run the client build before packing or running the packaged UI. `ir-chart.js` (the
Chart.js-based chart renderer) is fetched on demand the first time a report enters
chart view; pages that never chart never load it.

## Configured report documents

A report definition can reference source-controlled documents. Paths are relative to
the host's content root unless absolute:

```json
"orders": {
  "connection": "MainDb",
  "dialect": "SqlServer",
  "sql": "SELECT ORDER_ID, CUSTOMER, CUSTOMER_URL, THUMBNAIL_URL, AMOUNT FROM ORDERS",
  "styleSheet": "/css/orders-report.css",
  "documentFiles": [
    "ReportDocuments/orders.primary.json",
    "ReportDocuments/orders.finance.json"
  ]
}
```

Each file contains its selector title, whether it supplies Primary Report, and the
normal versioned state document:

```json
{
  "title": "Primary Report",
  "primary": true,
  "state": {
    "v": 2,
    "columns": [ "ORDER_ID", "CUSTOMER", "THUMBNAIL_URL", "AMOUNT" ],
    "sorts": [ { "col": "AMOUNT", "dir": "desc" } ],
    "formats": {
      "CUSTOMER": {
        "displayAs": "link",
        "urlColumn": "CUSTOMER_URL",
        "textColumn": "CUSTOMER"
      },
      "THUMBNAIL_URL": {
        "displayAs": "image",
        "urlColumn": "THUMBNAIL_URL"
      },
      "AMOUNT": { "mask": "currency:CAD", "classes": [ "amount-column", "emphasized" ] }
    }
  }
}
```

At most one file may be primary. It overrides the inline `defaultState` and synthetic
default; other files appear as global saved reports. Configured documents are always
read-only, including for report administrators, although Save As can create an editable
database copy under another title. A configured title takes precedence over an existing
database report with the same title, and new title collisions are rejected. Ensure the
host project copies these files to its build and publish output; the Workbench project
shows one way to do that.

The packaged administration panel can download any listed report as this canonical
`{ title, primary, state }` JSON envelope. This makes a database-backed saved report
ready to add to `documentFiles` without manually reconstructing the file. **Upload
JSON…** takes a configured report name and one of these envelopes, validates its state
against that report's current schema through the same ingestion pipeline as query and
export, and imports it as the administrator's private saved report for live testing.
The envelope's `primary` flag remains publication metadata and does not make an
uploaded database copy primary.

To log the exact SQL command text submitted to the configured database, enable Debug
for the report executor category. Parameter values are never logged:

```json
"Logging": {
  "LogLevel": {
    "InteractiveReport.Core.Execution.ReportExecutor": "Debug"
  }
}
```

`styleSheet` is an application-controlled relative or HTTP(S) URL. The component
places its `<link>` inside the report's shadow root after the packaged styles, so its
rules can target report internals without leaking into the host page. For example:

```css
.amount-column { font-variant-numeric: tabular-nums; }
.emphasized { font-weight: 700; }
```

Column Settings accepts space-separated class names and saves them in the column's
`formats.classes` list. Classes apply to the column header, data cells, and aggregate
cells. Tokens begin with a letter or `_`, then use letters, digits, `_`, or `-`; the
component's `ir-` prefix is reserved. Report documents can therefore select
developer-defined rules but cannot supply CSS or choose a stylesheet URL. The host's
Content Security Policy still governs stylesheet loading.

Column Settings also offers `Text (Default)`, `Link`, and `Image` display modes.
A link selects a URL column and a text column; an image selects a URL column. Source
columns do not have to be visible: the server schema-checks them and includes them only
in row data required by the grid and CSV renderers. Hidden sources remain absent from
displayed and exported column metadata. In CSV, a Display As cell contains the encoded
HTML fragment shown in the browser: `<a class="ir-cell-link">` or
`<img class="ir-cell-image">`; ordinary cells remain raw values. Relative and HTTP(S)
URLs are accepted for both renderers;
links additionally accept `mailto:` and `tel:`. Active or embedded-content schemes
such as `javascript:` and `data:` render as ordinary text instead.

Number masks cover grouped integers and one through four fixed decimal places,
invariant two-place decimals, CAD/USD/EUR/GBP/JPY currency, and zero through two-place
percentages. Date masks cover ISO dates and date-times, localized medium/long dates,
localized times, and localized medium/long date-times. The same scalar formatter is
used by text cells, link text, aggregates, group and pivot values, and the chart's
accessible data table. A synthetic group/pivot/chart metric inherits the format of its
source column; count-only metrics have no source format.

Query JSON carries CLR `Int64`, `UInt64`, and `Decimal` values as invariant strings.
The column metadata still identifies them as numbers, and the client parses, rounds,
groups, and masks every number-like column value through bundled `big.js` arbitrary-
precision arithmetic. Values such as
`9007199254740993` therefore do not lose a digit in JavaScript. Chart pixel coordinates
are the sole exception: Chart.js requires a JavaScript number, while its accompanying
data table retains the exact formatted value.

Numeric aggregate pickers include median alongside sum, average, minimum, maximum,
count, and distinct count. Median uses the same grouped query path for report totals,
control-break totals, Group By, pivot, and chart metrics on every supported database.

Highlights have a report-facing name and a positive sequence. Matching rules are
applied from lower to higher sequence, so the highest sequence wins when rules set the
same style; cell highlights are applied after row highlights. Legacy documents without
these fields use the rule id as the name and list position in increments of ten as the
sequence.

## Embedding the report

The bundle contains the component's styles and renders into a shadow root, so
host styles such as Tailwind resets do not reach the widget and widget styles do
not reach the host page.

```html
<script type="module" src="/assets/ir.js"></script>
<interactive-report
  report="open-orders"
  saved-report="My Open Orders"
  api-base="/api/reports">
</interactive-report>
```

`report` is required and is the only report-definition name the component requests.
There is no configured-report catalog or report selector. Server authorization still
applies when the component requests that report's schema, saved reports, queries, and
exports.

The widget adapts to the definition's `features` whitelist (docs/ARCHITECTURE.md §4):
menu entries, view buttons, the search bar, and the saved-report select render only
for whitelisted features, and the server additionally refuses CSV export and
saved-report creation for reports that do not whitelist `download` / `savedReports`.

Page size is configured through **Actions → Pagination**. The standard choices are
10, 50, 100, 500, 1000, and All; numeric choices above a definition's `maxPageSize`
are omitted. All is stored as `page.size: 0` and deliberately returns every matching
grid row or Group By group without applying `maxRows`. CSV export ignores pagination
and continues to use its independent `maxRows` cap and truncation header.

Each row in **Actions → Sort** also offers Nulls: Default, First, or Last. First
and Last are stored on that sort instruction as `nulls: "first"` or `"last"` and
produce the same placement on every supported database. Default omits the field and
preserves the database dialect's ordinary ordering behavior. Header-menu quick sorts
continue to use Default.

Control-break columns render in the break heading instead of repeating in each detail
row. A subtotal appears only with the logical end of its break group, even when that
group crosses a page boundary, and grand totals appear only on the report's final page.

`saved-report` is optional. When present, the component finds a visible saved report by
its title (case-insensitive) and loads it before the first query. The title must identify
exactly one visible saved report. A missing or ambiguous title loads Primary Report and
shows a warning. Omit the attribute to start from Primary Report.

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
`dialog-overlay`, and `dialog`.
