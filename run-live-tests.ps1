<#
.SYNOPSIS
    Executes the live provider suite against the SQL Server, Oracle, and PostgreSQL test VM.
.DESCRIPTION
    Defines all three IR_TEST_* variables for this process, verifies that each
    database port is reachable, and then starts the dedicated live-test project
    described in docs/TESTING.md. A stopped VM therefore produces one direct error.

    The embedded credentials belong to a disposable VM on VirtualBox's host-only
    network; addresses in 192.168.56.x are not reachable outside this machine.
.PARAMETER Filter
    Accepts an optional dotnet test --filter expression. An empty value runs every
    live test, including server-level persistence and the saved-report corpus for
    each dialect.
#>
[CmdletBinding()]
param(
    [string] $Filter = ''
)

$ErrorActionPreference = 'Stop'

$vmIp = '192.168.56.101'
$env:IR_TEST_SQLSERVER = "Server=$vmIp\sqlexpress;Database=irtest;User Id=testuser;Password=win-tbps8gs7ur2;TrustServerCertificate=True"
$env:IR_TEST_ORACLE    = "User Id=testuser;Password=win-tbps8gs7ur2;Data Source=${vmIp}:1521/xepdb1"
$env:IR_TEST_POSTGRES  = "Host=$vmIp;Port=5432;Database=irtest;Username=testuser;Password=win-tbps8gs7ur2"

foreach ($probe in @{ Name = 'SQL Server'; Port = 1433 }, @{ Name = 'Oracle'; Port = 1521 }, @{ Name = 'PostgreSQL'; Port = 5432 }) {
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

$testProject = Join-Path $PSScriptRoot 'tests/InteractiveReport.Live.Tests'
$testArguments = @('test', $testProject, '-v', 'normal')
if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testArguments += @('--filter', $Filter)
}
dotnet @testArguments
exit $LASTEXITCODE
