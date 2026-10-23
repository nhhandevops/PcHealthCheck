<#
.SYNOPSIS
    Runs PC Health Check on this machine and packs the results into one zip to send back.

.DESCRIPTION
    For trying the app on several machines and comparing what it reported. Put this file next to
    PcHealthCheck.exe, right-click it and choose "Run with PowerShell", or run it from a terminal.

    It runs a normal scan (no administrator rights, nothing installed, nothing changed), collects
    the reports the app produced along with enough facts about this PC to interpret them, and
    writes a single zip on your Desktop.

    WHAT ENDS UP IN THE ZIP, so nothing is a surprise when you send it:
      - the HTML and JSON reports the app writes, which include this PC's name, its Windows
        version and build, drive models and free space, the names of programs that start with
        Windows, the names of the programs using the most memory and processor time, and the
        names of your browser extensions
      - the app's console output and its exit code
      - a summary of this machine: CPU, memory, disks, display language, whether it is a laptop
      - which of the three optional tools are installed

    It does NOT include file contents, passwords, product keys, BitLocker keys, or browsing
    history. The app never reads those. Open the HTML report yourself before sending if you want
    to see exactly what is in it - that is what it is for.

.PARAMETER IncludeAdmin
    Also run the administrator checks. Raises exactly one UAC prompt. Without this, only the
    checks that need no special rights run.

.PARAMETER Label
    A short name for this machine, used in the zip filename. Defaults to the computer name.

.EXAMPLE
    .\collect-results.ps1
    .\collect-results.ps1 -IncludeAdmin -Label "laptop-cua-me"
#>
[CmdletBinding()]
param(
    [switch]$IncludeAdmin,
    [string]$Label = $env:COMPUTERNAME,
    [string]$OutputDir = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Note($text) { Write-Host "  $text" }

$exe = Join-Path $PSScriptRoot 'PcHealthCheck.exe'
if (-not (Test-Path $exe)) {
    throw "PcHealthCheck.exe was not found next to this script. Put collect-results.ps1 in the same folder as the app, then run it again."
}

# A timestamp in the filename, so running twice on one machine does not overwrite the first run.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safeLabel = ($Label -replace '[^A-Za-z0-9._-]', '-')
$work = Join-Path ([IO.Path]::GetTempPath()) "pchealth-collect-$stamp"
New-Item -ItemType Directory -Force -Path $work | Out-Null

Step 'This machine'
$os  = Get-CimInstance Win32_OperatingSystem
$cs  = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1

# PCSystemType 2 is "Mobile". Worth capturing because the battery check only runs on a laptop,
# and a desktop reporting "no battery" is a correct result rather than a missing one.
$isLaptop = $cs.PCSystemType -eq 2

$facts = [ordered]@{
    Label            = $Label
    CollectedAtLocal = (Get-Date).ToString('o')
    ComputerName     = $env:COMPUTERNAME
    Windows          = "$($os.Caption) $($os.Version) (build $($os.BuildNumber))"
    OsLanguage       = $os.OSLanguage
    MuiLanguages     = ($os.MUILanguages -join ', ')
    DisplayCulture   = (Get-Culture).Name
    DecimalSeparator = (Get-Culture).NumberFormat.NumberDecimalSeparator
    Manufacturer     = $cs.Manufacturer
    Model            = $cs.Model
    IsLaptop         = $isLaptop
    Cpu              = $cpu.Name
    CpuCores         = "$($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads"
    MemoryGB         = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
    BatteryCount     = (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Measure-Object).Count
    IsElevatedRun    = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    IncludeAdminAsked= [bool]$IncludeAdmin
    AppVersion       = (Get-Item $exe).VersionInfo.ProductVersion
}
$facts.GetEnumerator() | ForEach-Object { '  {0,-18} {1}' -f $_.Key, $_.Value }

# The decimal separator is worth its own line: a comma here is where a speed or a temperature
# silently comes out a thousand times wrong, and it is the single most useful thing to know when
# a number in the report looks absurd.
if ($facts.DecimalSeparator -ne '.') {
    Write-Host "  NOTE: this PC uses '$($facts.DecimalSeparator)' as the decimal separator - exactly the case worth testing." -ForegroundColor Yellow
}

Step 'Drives'
$drives = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType = 3' |
    Select-Object DeviceID, VolumeName, FileSystem,
        @{n = 'SizeGB'; e = { [math]::Round($_.Size / 1GB, 1) } },
        @{n = 'FreeGB'; e = { [math]::Round($_.FreeSpace / 1GB, 1) } }
$drives | Format-Table -AutoSize | Out-String | Write-Host
$disks = Get-CimInstance Win32_DiskDrive | Select-Object Index, Model, InterfaceType,
    @{n = 'SizeGB'; e = { [math]::Round($_.Size / 1GB) } }

Step 'Optional tools'
# Which of the three the app can offer are already here. A check reported as Skipped is only
# meaningful once you know whether its tool was present.
$tools = [ordered]@{
    DiskSpd = [bool]((Get-Command diskspd -ErrorAction SilentlyContinue) -or (Test-Path "$env:LOCALAPPDATA\Microsoft\WinGet\Links\diskspd.exe"))
    SevenZip = (Test-Path "$env:ProgramFiles\7-Zip\7z.exe") -or [bool](Get-Command 7z -ErrorAction SilentlyContinue)
    PawnIO  = (Test-Path "$env:ProgramFiles\PawnIO") -or (Test-Path 'HKLM:\SOFTWARE\PawnIO')
}
$tools.GetEnumerator() | ForEach-Object { '  {0,-10} {1}' -f $_.Key, $(if ($_.Value) { 'installed' } else { 'not installed' }) }

Step 'Running the scan'
$reportDir = Join-Path $work 'report'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

# Quoted: Start-Process joins arguments with spaces, so a temp path under a user name with a space
# in it ("C:\Users\Nguyen An\...") would reach the app as two arguments (KIT-001).
$scanArgs = @('--headless-scan', '--out', "`"$reportDir`"")
if ($IncludeAdmin) {
    $scanArgs += '--include-admin'
    Write-Host '  Administrator checks requested - Windows will ask for permission once.' -ForegroundColor Yellow
}
Note "PcHealthCheck.exe $($scanArgs -join ' ')"
Note 'This usually takes one to three minutes.'

$stdout = Join-Path $work 'console-output.txt'
$stderr = Join-Path $work 'console-errors.txt'
$proc = Start-Process -FilePath $exe -ArgumentList $scanArgs -Wait -NoNewWindow -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$exit = $proc.ExitCode

Step 'Result'
# The exit codes are the app's own: 0 means every selected check actually ran. Anything else is
# worth sending precisely because it is the interesting case.
switch ($exit) {
    0       { Write-Host "  Exit code 0 - the scan finished and every selected check ran." -ForegroundColor Green }
    default { Write-Host "  Exit code $exit - see console-output.txt; some checks did not finish. Send this, it is the useful case." -ForegroundColor Yellow }
}
Get-Content $stdout -Tail 12 | ForEach-Object { "  $_" }

Step 'Packing'
$facts['ScanExitCode'] = $exit
$facts | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $work 'machine.json') -Encoding UTF8
$drives | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $work 'drives.json') -Encoding UTF8
$disks  | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $work 'physical-disks.json') -Encoding UTF8
$tools  | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $work 'optional-tools.json') -Encoding UTF8

$zip = Join-Path $OutputDir "pchealth-result-$safeLabel-$stamp.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$work\*" -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $work -Recurse -Force

Step 'Done'
Write-Host "  $zip" -ForegroundColor Green
Write-Host ''
Write-Host '  Inside: the HTML and JSON reports, the console output, and a summary of this PC.'
Write-Host '  It names this computer, its drives, your startup programs and your browser extensions.'
Write-Host '  It contains no file contents, passwords, product keys or browsing history.'
Write-Host '  Open the HTML report first if you would like to see exactly what you are sending.'
