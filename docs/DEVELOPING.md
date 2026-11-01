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

Workbench has no login: every request is authenticated as `workbench-dev`, which
`appsettings.json` lists as an administrator, so the packaged pages and the
policy-protected sample report open without credentials. A request that carries an
`X-Workbench-User` header runs as that identity instead; the Playwright suite uses it to
exercise denial, ownership, and non-administrator paths, and it is handy for trying them
by hand.

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
suites. Use the manual GitHub Actions workflow below to publish a release, or
`dotnet nuget push` to publish locally built packages yourself.
Package consumers never need Node.js. `ir-chart.js` (the Chart.js-based chart
renderer) is fetched on demand the first time a report enters chart view; pages
that never chart never load it.

## Publishing a release from GitHub

The [release workflow](../.github/workflows/release.yml) is **manual only**: pushes,
tags, and pull requests do not trigger it. Once the workflow is on the repository's
default branch, open **Actions → Release NuGet packages → Run workflow**:

1. Select the branch or tag to release.
2. Enter a semantic version such as `1.0.3` or `1.0.3-rc.1`, or leave it blank to use
   `Version` from `Directory.Build.props` (currently `1.0.2`). An override applies
   to every package and its internal dependencies without editing the repository.
3. Leave **Also publish the packages to nuget.org** unchecked to create just the
   GitHub Release, or check it after configuring Trusted Publishing below.
4. Run the workflow. It installs .NET 10, Node.js 24, and Chromium on Windows, then
   runs `scripts/release.ps1` with the build and test stages enabled, plus the
   documentation checks.

Every successful build uploads an Actions artifact containing the five `.nupkg`
files, five `.snupkg` symbol packages, and `SHA256SUMS.txt`. The next job verifies
those checksums and publishes a GitHub Release tagged `v<version>` at the exact
commit that was built, with generated release notes and all eleven files attached.
Versions containing a prerelease suffix produce GitHub prereleases. An existing tag
must point to the same commit; an existing release is never overwritten. Use a new
version for a new release.

The optional NuGet job runs after the GitHub Release and publishes the same packages
and their symbols. If NuGet publishing fails, the GitHub Release and Actions artifact
remain available; correct the setup and use **Re-run failed jobs**. Already published
package versions are skipped. The workflow does not change the version in Git or
create a version-bump commit.

### One-time nuget.org setup

This workflow uses [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
so no long-lived NuGet API key is stored in GitHub:

1. In nuget.org, sign in as the account that will publish the packages and create a
   Trusted Publishing policy with repository owner `auroris`, repository
   `Interactive-Reports`, and workflow file **`release.yml`** (just the filename).
   Leave the environment field empty; this workflow does not use an environment.
2. Scope the policy to `AurorisNet.InteractiveReports*` and allow both publishing new
   packages and new versions. The first publication must be permitted to create all
   five package IDs listed in [Architecture](ARCHITECTURE.md#package-and-dependency-structure).
   Package IDs must be available or owned by your account; the workflow does not
   reserve the name in advance.
3. In the GitHub repository, open **Settings → Secrets and variables → Actions →
   Variables** and create **`NUGET_USER`** with that nuget.org account's username.

GitHub Releases use the workflow's built-in `GITHUB_TOKEN` with `contents: write`;
no personal access token is required. The NuGet job alone requests `id-token: write`
to exchange its GitHub identity for a short-lived publishing key.

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
the packaged help pages, and so does `npm run build`, which `pack.ps1` and
`release.ps1` run: stop a Workbench you started with `npm run start` first, because the
capture needs port 5042, and expect the screenshot files to change on a machine with
different fonts or display scaling.
