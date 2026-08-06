# InteractiveReports

## Client build

The client-side source is in `src/InteractiveReport.AspNetCore/Ui`. Install the
toolchain and create the browser bundles with:

```sh
npm ci
npm run build
```

`npm run dev` rebuilds on changes. `npm test` runs the UI unit tests, and
`npm run verify` runs both the tests and a production build.

The generated `Ui/dist/ir.js` and `Ui/dist/ir-admin.js` files are embedded in
the ASP.NET Core assembly. They are checked in so consuming and building the
.NET projects does not require Node.js.

## Embedding the report

The bundle contains the component's styles and renders into a shadow root, so
host styles such as Tailwind resets do not reach the widget and widget styles do
not reach the host page.

```html
<script type="module" src="/assets/ir.js"></script>
<interactive-report
  report="open-orders"
  api-base="/api/reports">
</interactive-report>
```

`report` is a preferred initial report, not an authorization bypass. The component
first reads the caller's authorized report list. It loads the named report only when
present; otherwise it falls back to the first visible report without requesting the
unavailable name. If a visible preferred report fails during schema or initial-query
loading, initialization continues through the remaining visible reports. When several
reports are visible, the toolbar exposes a report selector. The attribute may be
omitted to select the first visible report directly.

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
`--ir-radius`, `--ir-font`, and `--ir-font-size`. The supported structural parts
are `surface`, `toolbar`, `report-select`, `notices`, `chips`, `table-container`,
`table`, `pager`, `menu`, `dialog-overlay`, and `dialog`.
