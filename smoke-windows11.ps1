<#
.SYNOPSIS
    Smoke test for PC Health Check on Windows 11.

.DESCRIPTION
    Identifies the OS first and stops if this PC is not running Windows 11 (build 22000 or later),
    naming the file to run instead. Then it runs the shared Windows smoke, daily-smoke.ps1:
    build, tests, kit checks, and an unelevated scan whose report must name this PC's build and
    must have taken Windows 11's branch (whether Microsoft supports Windows 11 on this hardware,
    never Windows 10's "Can move to Windows 11" check). The script header of daily-smoke.ps1 lists
    every step.

    Today the only Windows 11 machine is the dev desktop. It has DiskSpd, so the scan runs with
    --skip-benchmarks and nothing writes to its drives; the row says "no benchmarks". Run this by
    hand there, not as a second scheduled task: the T470p's task already pushes a row every
    morning, and two machines pushing the same morning can conflict.

    Exit code: 2 when this PC is not Windows 11, otherwise daily-smoke.ps1's (0 = nothing FAILed).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\smoke-windows11.ps1
    powershell -ExecutionPolicy Bypass -File .\smoke-windows11.ps1 -Push       # record, commit and push the row
    powershell -ExecutionPolicy Bypass -File .\smoke-windows11.ps1 -NoRecord   # try it without adding a row
#>
[CmdletBinding()]
param(
    [string[]]$BackupDir = @(),
    [switch]$NoRecord,
    [switch]$Push
)

$os = Get-CimInstance Win32_OperatingSystem -Property Caption, BuildNumber
$build = [int]$os.BuildNumber
if ($build -lt 22000) {
    Write-Host "This PC runs $($os.Caption) (build $build), not Windows 11. Run smoke-windows10.ps1 instead." -ForegroundColor Red
    exit 2
}
Write-Host "Windows 11 smoke: $($os.Caption), build $build" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'daily-smoke.ps1') -BackupDir $BackupDir -NoRecord:$NoRecord -Push:$Push
exit $LASTEXITCODE
