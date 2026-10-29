<#
.SYNOPSIS
    Registers (or removes) a Task Scheduler task that runs daily-smoke.ps1 -Push every morning.

.DESCRIPTION
    Run it once, by hand, on the machine that should own the daily smoke (today the T470p: it has
    no DiskSpd, so its smoke really scans). The task runs as you, only while you are signed in,
    hidden, on battery too, and starts as soon as possible if the PC was off at the set time.
    Each run builds, tests, runs the kit checks and a real unelevated scan, then commits only
    docs/daily-smoke.md and pushes it, so other machines see the row after a pull.

    Only one machine should own the task: two machines pushing rows to the same file the same
    morning can conflict, and the losing run then leaves its row committed locally (it says so).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\register-daily-smoke.ps1              # 09:00 daily
    powershell -ExecutionPolicy Bypass -File .\register-daily-smoke.ps1 -At 13:30
    powershell -ExecutionPolicy Bypass -File .\register-daily-smoke.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$At = '09:00',
    [switch]$Remove
)

$name = 'PcHealthCheck daily smoke'
if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed '$name' (if it existed)."
    return
}

$smoke = Join-Path $PSScriptRoot 'daily-smoke.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$smoke`" -Push" `
    -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Force `
    -Description 'Runs daily-smoke.ps1 -Push: build, tests, kit checks, an unelevated scan, then commits and pushes only docs/daily-smoke.md.' | Out-Null
$info = Get-ScheduledTaskInfo -TaskName $name
Write-Host "Registered '$name': daily at $At, next run $($info.NextRunTime). Remove with -Remove."
