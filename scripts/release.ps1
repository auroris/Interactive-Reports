# Drops a complete, versioned package set into releases\<version>\ at the repository root:
# the five .nupkg files, their .snupkg symbol packages, and a SHA256SUMS.txt manifest. The
# version comes from Directory.Build.props unless overridden. This is the manual stand-in for a
# future CI release job; nothing here pushes to a feed or touches GitHub.
#
#   pwsh scripts\release.ps1                    # full pipeline (npm ci, build, tests, pack)
#   pwsh scripts\release.ps1 -SkipTests         # pack without running the test layers
#   pwsh scripts\release.ps1 -Version 1.0.0-rc.1
#   pwsh scripts\release.ps1 -Force             # overwrite an existing releases\<version>\
#
# Push later with, for example:
#   dotnet nuget push releases\0.9.0\*.nupkg --source https://api.nuget.org/v3/index.json --api-key ...

[CmdletBinding()]
param(
    # Package version. Defaults to <Version> in Directory.Build.props; an override is passed
    # to dotnet pack as -p:Version so the props file stays the source of truth.
    [string] $Version,
    # Overwrite a releases\<version> folder that already holds packages.
    [switch] $Force,
    [switch] $SkipTests,
    [switch] $SkipInstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    if ($props -notmatch "<Version>\s*([^<\s]+)\s*</Version>") {
        throw "release.ps1: no <Version> element in Directory.Build.props; pass -Version."
    }
    $Version = $Matches[1]
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') {
    throw "release.ps1: '$Version' is not a semantic version."
}

$target = Join-Path $root "releases\$Version"
if ((Test-Path $target) -and (Get-ChildItem $target -Filter "*.nupkg" -ErrorAction SilentlyContinue)) {
    if (-not $Force) {
        throw "release.ps1: $target already contains packages. Bump the version or pass -Force to replace them."
    }
    Write-Host "==> Replacing existing packages in $target" -ForegroundColor Yellow
    Get-ChildItem $target -File | Remove-Item -Force
}
New-Item -ItemType Directory -Force $target | Out-Null

# Only an explicit override needs to reach MSBuild; otherwise the props file already applies.
$packArguments = @{ Output = "releases\$Version"; SkipTests = $SkipTests; SkipInstall = $SkipInstall }
if ($PSBoundParameters.ContainsKey("Version")) { $packArguments.Version = $Version }
& (Join-Path $PSScriptRoot "pack.ps1") @packArguments
if ($LASTEXITCODE) { throw "release.ps1: pack.ps1 failed with exit code $LASTEXITCODE." }

# Every package must carry the requested version: a stale props file or a stray
# -p:Version elsewhere would otherwise produce a mislabelled folder.
$packages = Get-ChildItem $target -Filter "*.nupkg"
$wrong = $packages | Where-Object { $_.Name -notlike "*.$Version.nupkg" }
if ($wrong) {
    throw "release.ps1: packages do not match version ${Version}: $(($wrong | ForEach-Object Name) -join ', ')"
}

# Checksums for the eventual upload step (and for anyone verifying a download).
$manifest = Join-Path $target "SHA256SUMS.txt"
Get-ChildItem $target -Filter "*.*nupkg" | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content $manifest -Encoding ascii

Write-Host "==> Release $Version ready in $target" -ForegroundColor Green
Get-Content $manifest | ForEach-Object { Write-Host "    $_" }
