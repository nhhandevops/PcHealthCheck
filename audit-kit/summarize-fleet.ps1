<#
.SYNOPSIS
    Turns every laptop's result zip into one table: one row and one verdict per laptop.

.DESCRIPTION
    Started by "2 - Summarize all laptops.cmd". Reads Results\*.zip, applies the pass rules in
    audit-rules.json, and writes into the Results folder:
      fleet-summary.html  - the table for your manager, worst laptops first, each row linked to
                            that laptop's full report
      fleet-summary.csv   - the same table for Excel
      fleet-reports\      - each laptop's full HTML report, named like its zip

    A laptop checked more than once appears once, from its latest run. Edit audit-rules.json and
    run this again to see the fleet under different rules; the zips are never changed.

.PARAMETER ResultsDir
    The folder holding the result zips. Default: the Results folder next to this script.

.PARAMETER RulesFile
    The pass rules. Default: audit-rules.json next to this script.
#>
[CmdletBinding()]
param(
    [string]$ResultsDir = '',
    [string]$RulesFile = ''
)

$ErrorActionPreference = 'Stop'
$kitDir = $PSScriptRoot
. (Join-Path $kitDir 'audit-common.ps1')
if (-not $ResultsDir) { $ResultsDir = Join-Path $kitDir 'Results' }
if (-not $RulesFile) { $RulesFile = Join-Path $kitDir 'audit-rules.json' }

$zips = @(Get-ChildItem -LiteralPath $ResultsDir -Filter '*.zip' -File -ErrorAction SilentlyContinue | Sort-Object Name)
if ($zips.Count -eq 0) {
    Write-Host "  No result zips in $ResultsDir yet. Check a laptop first." -ForegroundColor Yellow
    exit 1
}
$rules = Read-AuditRules $RulesFile
$reportsDir = Join-Path $ResultsDir 'fleet-reports'
New-Item -ItemType Directory -Force -Path $reportsDir | Out-Null
$temp = Join-Path ([IO.Path]::GetTempPath()) ("pchealth-fleet-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$rows = New-Object System.Collections.Generic.List[object]
try {
    foreach ($zip in $zips) {
        $dir = Join-Path $temp $zip.BaseName
        try {
            Expand-Archive -LiteralPath $zip.FullName -DestinationPath $dir -Force
            $bundle = Read-AuditFolder $dir
            $row = Get-AuditRow $bundle $rules
            if (-not $row.ResultFile) { $row.ResultFile = $zip.Name }
            $report = ''
            if ($bundle.ReportHtml) {
                $report = "$($zip.BaseName).html"
                Copy-Item -LiteralPath $bundle.ReportHtml -Destination (Join-Path $reportsDir $report) -Force
            }
            $row | Add-Member -NotePropertyName ReportLink -NotePropertyValue $report
            $rows.Add($row)
        } catch {
            # A zip that cannot be read is a finding too: it must show as a row, not vanish.
            $rows.Add([pscustomobject][ordered]@{
                Verdict = 'UNREADABLE'; Serial = ''; Computer = ''; Model = ''; Cpu = ''; RamGB = $null; RamSlots = ''
                SystemDisk = ''; SystemDriveFree = ''; DriveHealth = ''; Battery = ''; Windows = ''; Windows11 = ''
                OverallGrade = ''; Reasons = "Could not read this zip: $($_.Exception.Message)"; Notes = ''; PhysicalProblems = ''
                CheckedAt = ''; KitVersion = ''; AppVersion = ''; ResultFile = $zip.Name; ReportLink = ''
            })
        }
    }
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# One row per laptop: the latest run of each serial. Laptops without a serial are all kept.
$latest = New-Object System.Collections.Generic.List[object]
foreach ($group in ($rows | Group-Object { if ($_.Serial) { $_.Serial } else { [guid]::NewGuid().ToString() } })) {
    $newest = $group.Group | Sort-Object CheckedAt -Descending | Select-Object -First 1
    $newest | Add-Member -NotePropertyName Runs -NotePropertyValue $group.Count -Force
    $latest.Add($newest)
}
$order = @{ 'REPLACE' = 0; 'FIX FIRST' = 1; 'CHECK AGAIN' = 2; 'UNREADABLE' = 3; 'READY' = 4 }
$sorted = @($latest | Sort-Object @{ Expression = { $order[$_.Verdict] } }, Model, Serial)

# --- CSV for Excel. Windows PowerShell's UTF8 adds a byte-order mark, which is what Excel needs
# to show non-English characters correctly.
$csv = Join-Path $ResultsDir 'fleet-summary.csv'
$sorted | Select-Object -Property * -ExcludeProperty ReportLink | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8

# --- HTML for people. Everything inline: the page works offline and from a USB stick.
function Html([string]$Text) { return [System.Security.SecurityElement]::Escape($Text) }
$counts = $sorted | Group-Object Verdict | ForEach-Object { "$($_.Count) $($_.Name)" }
$rulesText = "at least $($rules.minRamGb) GB memory; " +
    $(if ($rules.requireSsd) { 'an SSD; ' } else { '' }) +
    "battery at least $($rules.minBatteryPct)% of design; " +
    "at least $($rules.minSystemDriveFreePct)% free on the system drive; " +
    $(if ($rules.requireWindows11Ready) { 'able to run Windows 11; ' } else { '' }) +
    $(if ($rules.driveWarningsNeedFix) { 'no drive warnings at all' } else { 'no drive failures (early warnings are noted, not counted)' })

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Laptop fleet summary</title>')
[void]$sb.AppendLine('<meta name="viewport" content="width=device-width, initial-scale=1">')
[void]$sb.AppendLine('<style>body{font:14px/1.45 Segoe UI,Arial,sans-serif;margin:24px;color:#1b1f24;background:#fff}h1{font-size:22px;margin:0 0 4px}' +
    'p{margin:4px 0}table{border-collapse:collapse;width:100%;margin-top:16px}th,td{border:1px solid #d0d7de;padding:6px 8px;vertical-align:top;text-align:left}' +
    'th{background:#f3f4f6;position:sticky;top:0}td.v{font-weight:600;white-space:nowrap}.READY{background:#dcfce7}.FIX{background:#fef3c7}' +
    '.REPLACE{background:#fee2e2}.CHECK,.UNREADABLE{background:#e0e7ff}small{color:#57606a}</style></head><body>')
$noun = if ($sorted.Count -eq 1) { 'laptop' } else { 'laptops' }
[void]$sb.AppendLine("<h1>Laptop fleet summary</h1><p>$(Html ("$($sorted.Count) $($noun): " + ($counts -join ', ')))</p>")
[void]$sb.AppendLine("<p><small>Rules used: $(Html $rulesText). Edit audit-rules.json and run the summary again to change them. Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm').</small></p>")
[void]$sb.AppendLine('<table><thead><tr><th>Verdict</th><th>Serial</th><th>Model</th><th>Processor</th><th>Memory</th><th>System disk</th>' +
    '<th>Drive health</th><th>Battery</th><th>Windows 11</th><th>Why</th><th>Notes</th><th>Report</th></tr></thead><tbody>')
foreach ($r in $sorted) {
    $cls = ($r.Verdict -split ' ')[0]
    $memory = if ($null -ne $r.RamGB) { "$($r.RamGB) GB" } else { '' }
    if ($r.RamSlots) { $memory += "<br><small>$(Html $r.RamSlots)</small>" }
    $why = (($r.Reasons -split '; ' | Where-Object { $_ } | ForEach-Object { Html $_ }) -join '<br>')
    $note = (($r.Notes -split '; ' | Where-Object { $_ } | ForEach-Object { Html $_ }) -join '<br>')
    $link = if ($r.ReportLink) { "<a href=""fleet-reports/$(Html $r.ReportLink)"">open</a>" } else { '' }
    $serial = "$(Html $r.Serial)<br><small>$(Html $r.Computer)$(if ($r.Runs -gt 1) { " - checked $($r.Runs) times, latest shown" })<br>$(Html $r.CheckedAt)</small>"
    [void]$sb.AppendLine("<tr><td class=""v $cls"">$(Html $r.Verdict)</td><td>$serial</td><td>$(Html $r.Model)</td><td>$(Html $r.Cpu)</td>" +
        "<td>$memory</td><td>$(Html $r.SystemDisk)<br><small>$(Html $r.SystemDriveFree) free</small></td><td>$(Html $r.DriveHealth)</td>" +
        "<td>$(Html $r.Battery)</td><td>$(Html $r.Windows11)</td><td>$why</td><td><small>$note</small></td><td>$link</td></tr>")
}
[void]$sb.AppendLine('</tbody></table>')
[void]$sb.AppendLine('<p><small>READY: nothing found that stops a new employee using it. FIX FIRST: usable after the repair or upgrade listed. ' +
    'REPLACE: cannot meet the rules at any repair cost. CHECK AGAIN: something important was not checked, so no verdict would be honest.</small></p>')
[void]$sb.AppendLine('</body></html>')
$html = Join-Path $ResultsDir 'fleet-summary.html'
Write-Utf8NoBom $html $sb.ToString()

Write-Host ''
$sorted | Format-Table -AutoSize Verdict, Serial, Model, RamGB, Battery, DriveHealth, Windows11 | Out-String -Width 200 | Write-Host
Write-Host "  $($sorted.Count) $($noun): $($counts -join ', ')" -ForegroundColor White
Write-Host "  Table for your manager: $html" -ForegroundColor Green
Write-Host "  For Excel:              $csv" -ForegroundColor Green
