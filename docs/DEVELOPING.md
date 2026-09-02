# Developing

The client-side source is in `src/client`; generated browser assets are written to
`src/InteractiveReport.Client.Json/Ui/dist` for embedding by the server project. Install
the toolchain and create the browser bundles with:

```sh
npm ci
npm run build
```

`npm run dev` rebuilds on changes. `npm test` builds the client and runs the fast
DOM unit tests. Browser automation is configured with Playwright; install Chromium
once with `npx playwright install chromium`, then run `npm run test:ui`.
`npm run verify` runs both test layers.

The generated `src/InteractiveReport.Client.Json/Ui/dist/ir.js`, `ir-admin.js`, and
`ir-chart.js` files are embedded in the ASP.NET Core assembly and are deliberately
not committed. A source checkout must run the client build before a Release build,
a pack, or a run of the packaged UI — `dotnet pack` and `dotnet build -c Release`
fail with instructions when the bundles are missing, so a UI-less package cannot
ship silently. `scripts/pack.ps1` (also `npm run pack`) chains the client build, the
fast test layers, and `dotnet pack` for the five distributable projects into
`artifacts/packages`; publishing beyond that is currently a manual
`dotnet nuget push` (release automation is deliberately still open).
Package consumers never need Node.js. `ir-chart.js` (the Chart.js-based chart
renderer) is fetched on demand the first time a report enters chart view; pages
that never chart never load it.

## Cutting a release locally

`scripts/release.ps1` (also `npm run release`) runs the pack pipeline and drops a
complete, versioned package set into `releases/<version>/`: the five `.nupkg` files,
their `.snupkg` symbol packages, and a `SHA256SUMS.txt` manifest. The version comes
from `Directory.Build.props`; pass `-Version 1.0.0-rc.1` to override it for one run,
`-SkipTests` to pack without the test layers, and `-Force` to replace a folder that
already holds packages. The `releases/` folder is git-ignored. Nothing is pushed:
publishing is still a manual `dotnet nuget push releases/<version>/*.nupkg`, and a
GitHub release job is deliberately still open.

## Documentation assets

The [User Guide](USER-GUIDE.md) is also the in-app help: `npm run build:help`
(part of `npm run build`) renders it to `Ui/dist/help.<locale>.html` with the
screenshots inlined as lossless WebP, and the report's **?** button opens that page in
a window. Regenerate the screenshots from the live Workbench with:

```sh
dotnet run --project samples/Workbench --urls http://127.0.0.1:5042
npm run docs:screenshots
```

The script drives the packaged viewer with Playwright, widens the window until the
toolbar fits on one row, adds the numbered callouts, and writes `docs/images/*.png`.
Run `npm run build:help` afterwards so the packaged help page picks up the new images.
