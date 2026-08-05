<#
.SYNOPSIS
    Runs the live-dialect test battery against the SQL Server + Oracle test VM.
.DESCRIPTION
    Sets IR_TEST_SQLSERVER and IR_TEST_ORACLE for this process only, probes both
    ports so a down VM fails fast with one clear message, then runs the
    LiveDialectTests filter (see docs/TESTING.md).

    The embedded credential targets a throwaway VM on the VirtualBox host-only
    network (192.168.56.x is unreachable from outside this machine).
.PARAMETER Filter
    dotnet test --filter expression. Defaults to the live battery.
#>
[CmdletBinding()]
param(
    [string] $Filter = 'FullyQualifiedName~LiveDialectTests'
)

$ErrorActionPreference = 'Stop'

$vmIp = '192.168.56.101'
$env:IR_TEST_SQLSERVER = "Server=$vmIp\sqlexpress;Database=irtest;User Id=testuser;Password=win-tbps8gs7ur2;TrustServerCertificate=True"
$env:IR_TEST_ORACLE    = "User Id=testuser;Password=win-tbps8gs7ur2;Data Source=${vmIp}:1521/xepdb1"

foreach ($probe in @{ Name = 'SQL Server'; Port = 1433 }, @{ Name = 'Oracle'; Port = 1521 }) {
    $tcp = [System.Net.Sockets.TcpClient]::new()
    $reachable = $false
    try { $reachable = $tcp.ConnectAsync($vmIp, $probe.Port).Wait(5000) -and $tcp.Connected }
    catch { $reachable = $false }
    finally { $tcp.Dispose() }

    if (-not $reachable) {
        Write-Host "$($probe.Name) not reachable at ${vmIp}:$($probe.Port)" -ForegroundColor Red
        Write-Host 'Is the VirtualBox VM running? Setup notes: docs/TESTING.md' -ForegroundColor Yellow
        exit 1
    }
    Write-Host "$($probe.Name) reachable on port $($probe.Port)" -ForegroundColor Green
}

dotnet test (Join-Path $PSScriptRoot 'tests/InteractiveReport.Core.Tests') --filter $Filter -v normal
exit $LASTEXITCODE
