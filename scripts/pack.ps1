# Builds the browser bundles, runs the fast test layers, and packs the three
# distributable projects into artifacts\packages. The release workflow performs
# the same sequence; this script is the local/manual equivalent.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Step([string]$description, [scriptblock]$step) {
    Write-Host "==> $description" -ForegroundColor Cyan
    & $step
    if ($LASTEXITCODE -ne 0) {
        throw "pack.ps1: '$description' failed with exit code $LASTEXITCODE."
    }
}

Invoke-Step "npm ci" { npm ci }
Invoke-Step "Build browser bundles" { npm run build }
Invoke-Step "Client unit tests" { npm run test:unit }
Invoke-Step "dotnet test (Core)" { dotnet test tests\InteractiveReport.Core.Tests -c Release --nologo -v q }
Invoke-Step "dotnet test (AspNetCore)" { dotnet test tests\InteractiveReport.AspNetCore.Tests -c Release --nologo -v q }

$output = Join-Path $root "artifacts\packages"
foreach ($project in @(
    "src\InteractiveReport.Core",
    "src\InteractiveReport.AspNetCore",
    "src\InteractiveReport.GraphQL")) {
    Invoke-Step "dotnet pack $project" { dotnet pack $project -c Release -o $output --nologo }
}

$missing = @("InteractiveReport.Core", "InteractiveReport.AspNetCore", "InteractiveReport.GraphQL") |
    Where-Object {
        -not (Test-Path (Join-Path $output "$_.*.nupkg")) -or
        -not (Test-Path (Join-Path $output "$_.*.snupkg"))
    }
if ($missing) {
    throw "pack.ps1: expected package artifacts are missing for: $($missing -join ', ')."
}

Write-Host "==> Packages ready in $output" -ForegroundColor Green
Get-ChildItem $output -Filter "*.*nupkg" | ForEach-Object { Write-Host "    $($_.Name)" }
