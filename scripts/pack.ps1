# Local packaging pipeline: installs and builds the browser client, runs the fast test suites,
# and packs all five distributable projects into an output folder (artifacts\packages by
# default). It mirrors the release workflow closely enough to validate package contents before
# publishing. scripts\release.ps1 wraps it to drop a versioned set into releases\.
#
#   pwsh scripts\pack.ps1                       # full pipeline into artifacts\packages
#   pwsh scripts\pack.ps1 -SkipTests            # pack only (bundles are still rebuilt)
#   pwsh scripts\pack.ps1 -Output some\folder

[CmdletBinding()]
param(
    # Folder that receives the .nupkg and .snupkg files. Relative paths resolve against the
    # repository root.
    [string] $Output = "artifacts\packages",
    # Skips the client unit tests and the two fast .NET test projects. The browser bundles and
    # help page are always rebuilt so a pack can never ship stale UI.
    [switch] $SkipTests,
    # Skips `npm ci`; use when node_modules is already current.
    [switch] $SkipInstall,
    # Overrides <Version> from Directory.Build.props for this pack only.
    [string] $Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Runs one named pipeline stage and turns a nonzero native-process exit code into a terminating
# error. `description` is printed for progress; `step` is invoked synchronously and returns no value.
function Invoke-Step([string]$description, [scriptblock]$step) {
    Write-Host "==> $description" -ForegroundColor Cyan
    & $step
    if ($LASTEXITCODE -ne 0) {
        throw "pack.ps1: '$description' failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipInstall) { Invoke-Step "npm ci" { npm ci } }
Invoke-Step "Build browser bundles and help page" { npm run build }
if (-not $SkipTests) {
    Invoke-Step "Client unit tests" { npm run test:unit:run }
    Invoke-Step "dotnet test (Core)" { dotnet test tests\InteractiveReport.Core.Tests -c Release --nologo -v q }
    Invoke-Step "dotnet test (AspNetCore)" { dotnet test tests\InteractiveReport.AspNetCore.Tests -c Release --nologo -v q }
}

$output = [System.IO.Path]::GetFullPath((Join-Path $root $Output))
New-Item -ItemType Directory -Force $output | Out-Null
if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    $Version = $props.Project.PropertyGroup.Version
}
[string[]] $versionArguments = @("-p:Version=$Version")
$projects = [ordered]@{
    "src\InteractiveReport.Core" = "AurorisNet.InteractiveReports.Core"
    "src\InteractiveReport.AspNetCore" = "AurorisNet.InteractiveReports.AspNetCore"
    "src\InteractiveReport.Client.Json" = "AurorisNet.InteractiveReports"
    "src\InteractiveReport.Client.GraphQL" = "AurorisNet.InteractiveReports.Client.GraphQL"
    "src\InteractiveReport.Client.FileDownload" = "AurorisNet.InteractiveReports.Client.FileDownload"
}
foreach ($project in $projects.Keys) {
    Invoke-Step "dotnet pack $project" { dotnet pack $project -c Release -o $output --nologo @versionArguments }
}

$missing = $projects.Values |
    Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $output "$_.${Version}.nupkg")) -or
        -not (Test-Path -LiteralPath (Join-Path $output "$_.${Version}.snupkg"))
    }
if ($missing) {
    throw "pack.ps1: expected package artifacts are missing for: $($missing -join ', ')."
}

Write-Host "==> Packages ready in $output" -ForegroundColor Green
Get-ChildItem $output -Filter "*.*nupkg" | ForEach-Object { Write-Host "    $($_.Name)" }
