# Developing

The client-side source is in `src/client`; generated browser assets are written to
`src/InteractiveReport.Client.Json/Ui/dist` for embedding by the server project. With
Node.js 20 or later and the .NET 10 SDK installed, install the dependencies and
Playwright's Chromium browser, build the browser assets, packaged help, and .NET
solution, then start Workbench with:

```sh
npm ci
npx playwright install chromium
npm run build
npm run start
```

Workbench runs at `http://127.0.0.1:5042`; stop it with Ctrl+C.
On Linux, replace the Playwright installation command with
`npx playwright install --with-deps chromium` so Chromium's system libraries are
installed as well. Windows and Linux both support the normal build, Workbench, and
test workflows.

`npm run start:dev` (or `npm run build:watch`) rebuilds on changes. `npm test` builds
the client and runs the fast DOM unit tests, browser-server tests, and documentation
integrity tests. Browser automation is configured with Playwright; run
`npm run test:e2e` for that layer. `npm run test:all` combines those JavaScript and
browser layers. Run `dotnet test` separately for the Core, ASP.NET Core, and live-test
projects; live database cases skip unless their connection variables are configured.

The generated `src/InteractiveReport.Client.Json/Ui/dist/ir.js`, `ir-admin.js`, and
`ir-chart.js` files are embedded in the ASP.NET Core assembly and are deliberately
not committed. A source checkout must run the client build before a Release build,
a pack, or a run of the packaged UI — `dotnet pack` and `dotnet build -c Release`
fail with instructions when the bundles are missing, so a UI-less package cannot
ship silently. NuGet packaging is a Windows workflow. `scripts/pack.ps1` (also
`npm run pack:nuget`) runs the complete build, the client unit tests, the Core and
ASP.NET Core test projects, and `dotnet pack` for the five distributable projects into
`artifacts/packages`. It does not run the browser-server, Playwright, or live-database
suites. Publishing beyond that is a manual
`dotnet nuget push`.
Package consumers never need Node.js. `ir-chart.js` (the Chart.js-based chart
renderer) is fetched on demand the first time a report enters chart view; pages
that never chart never load it.

## Cutting a release locally

On Windows, `scripts/release.ps1` (also `npm run release:nuget`) runs the pack pipeline and drops a
complete, versioned package set into `releases/<version>/`: the five `.nupkg` files,
their `.snupkg` symbol packages, and a `SHA256SUMS.txt` manifest. The version comes
from `Directory.Build.props`; pass `-Version 1.0.0-rc.1` to override it for one run,
`-SkipTests` to pack without the test layers, and `-Force` to replace a folder that
already holds packages. The `releases/` folder is git-ignored. Nothing is pushed:
publishing is a manual `dotnet nuget push releases/<version>/*.nupkg`.

## Documentation assets

The [User Guide](USER-GUIDE.md) is also the in-app help: `npm run build:help`
(part of `npm run build`) renders it to `Ui/dist/help.<locale>.html` with the
screenshots inlined as lossless WebP, and the report's **?** button opens that page in
a window. Regenerate the screenshots from a temporary Workbench instance with:

```sh
npm run build:screenshots
```

The command starts Workbench, waits for it to accept requests, drives the packaged
viewer with Playwright, and stops Workbench even when capture fails. The capture widens
the window until the toolbar fits on one row, adds the numbered callouts, and writes
`docs/images/*.png`. `npm run build:help` performs the same capture before rebuilding
the packaged help pages.
