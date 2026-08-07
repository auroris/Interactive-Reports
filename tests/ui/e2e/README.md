# Browser UI tests

The shared Playwright configuration starts the real Workbench on
`http://127.0.0.1:5042` and runs these tests in Chromium. Locators use accessible roles
and labels through the components' open shadow roots, so the scenarios exercise the UI
as a user sees it rather than calling component methods.

The test server always starts as a fresh process and redirects saved reports to
`test-results/ui/workbench-saved.db`. Browser runs therefore neither reuse an
incompatible developer server nor modify `samples/Workbench/App_Data`.

`application.spec.js` covers direct report configuration, a file-configured primary document,
query, search, paging, report-attribute changes, CSV export, saved-report persistence, administration,
and non-administrator authorization. Saved reports use random names and are removed in
`finally` blocks, allowing the suite to run against an existing developer Workbench.

```sh
npm run test:ui
```
