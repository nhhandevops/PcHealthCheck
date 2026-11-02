<#
.SYNOPSIS
    Smoke test for PC Health Check on Windows 10.

.DESCRIPTION
    Identifies the OS first and stops if this PC is not running Windows 10 (builds 10240 to
    21999), naming the file to run instead. Then it runs the shared Windows smoke, daily-smoke.ps1:
    build, tests, kit checks, and an unelevated scan whose report must name this PC's build and
    must have taken Windows 10's branch (the "Can move to Windows 11" check, never Windows 11's
    hardware check). The script header of daily-smoke.ps1 lists every step.

    The steps live in that one file so the Windows 10 and Windows 11 smokes cannot drift apart.
    daily-smoke.ps1 is also what the scheduled task runs: it works out the OS itself, so the task
    keeps working when a laptop moves from Windows 10 to Windows 11.

    Exit code: 2 when this PC is not Windows 10, otherwise daily-smoke.ps1's (0 = nothing FAILed).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\smoke-windows10.ps1
    powershell -ExecutionPolicy Bypass -File .\smoke-windows10.ps1 -Push       # record, commit and push the row
    powershell -ExecutionPolicy Bypass -File .\smoke-windows10.ps1 -NoRecord   # try it without adding a row
#>
[CmdletBinding()]
param(
    [string[]]$BackupDir = @(),
    [switch]$NoRecord,
    [switch]$Push
)

$os = Get-CimInstance Win32_OperatingSystem -Property Caption, BuildNumber
$build = [int]$os.BuildNumber
if ($build -ge 22000) {
    Write-Host "This PC runs $($os.Caption) (build $build), not Windows 10. Run smoke-windows11.ps1 instead." -ForegroundColor Red
    exit 2
}
if ($build -lt 10240) {
    Write-Host "This PC runs $($os.Caption) (build $build), older than Windows 10. PC Health Check does not support it." -ForegroundColor Red
    exit 2
}
Write-Host "Windows 10 smoke: $($os.Caption), build $build" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'daily-smoke.ps1') -BackupDir $BackupDir -NoRecord:$NoRecord -Push:$Push
exit $LASTEXITCODE
