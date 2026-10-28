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

    Work folders a closed window left (Results\.work-*) are read too. A laptop checked more than
    once appears once: from its latest finished run, with what any other run found that a later
    check did not re-examine. Edit audit-rules.json and run this again to see the fleet under
    different rules; the zips are never changed.

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

# Every run on the stick, by name. A run can have a zip, a work folder, or both (KIT-007): the kit
# works in Results\.work-<name>, saves <name>.zip once the scan is done, and replaces it at the end.
# A window closed in between leaves the work folder further along than the zip, and a window closed
# during the scan leaves only the folder. Names starting with "." are never results: the kit writes
# its zips as ".partial-<name>.zip" first and renames them only once they are complete.
$zips = @(Get-ChildItem -LiteralPath $ResultsDir -Filter '*.zip' -File -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Name.StartsWith('.') })
$folders = @(Get-ChildItem -LiteralPath $ResultsDir -Directory -Force -Filter '.work-*' -ErrorAction SilentlyContinue)
$runNames = @(@($zips | ForEach-Object { $_.BaseName }) + @($folders | ForEach-Object { $_.Name.Substring(6) }) | Sort-Object -Unique)
if ($runNames.Count -eq 0) {
    Write-Host "  No result zips in $ResultsDir yet. Check a laptop first." -ForegroundColor Yellow
    exit 1
}
$rules = Read-AuditRules $RulesFile
$reportsDir = Join-Path $ResultsDir 'fleet-reports'
New-Item -ItemType Directory -Force -Path $reportsDir | Out-Null
$temp = Join-Path ([IO.Path]::GetTempPath()) ("pchealth-fleet-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$rows = New-Object System.Collections.Generic.List[object]

# Columns the summary uses to build a laptop's row but the manager does not need to see.
$script:InternalColumns = @('ReportLink', 'FromWorkFolder', 'RunName', 'StageRank', 'SerialKey', 'SerialSource', 'HardwareUuid',
    'UuidRecorded', 'Bundle', 'ScanReplace', 'ScanFix', 'DriveFix', 'DriveRead', 'RunHash', 'EarlierRuns', 'HasEarlierList', 'ClockWrong')

# One row from one run's files, whether unpacked from its zip or read from its work folder.
function Add-RunRow($Bundle, [string]$Name, [string]$ResultFile) {
    $row = Get-AuditRow $Bundle $rules
    $fromFolder = [bool](Get-JsonField $Bundle 'FromWorkFolder')
    # A work folder's kit.json names the zip it meant to write, which is missing or older: the row
    # names the folder it was read from.
    if ($fromFolder -or -not $row.ResultFile) { $row.ResultFile = $ResultFile }
    # The report link only when the report itself is there: the app writes the HTML before the JSON,
    # so a scan interrupted between the two would link a report whose findings are not in the row.
    $report = ''
    if ($Bundle.ReportHtml -and $Bundle.Report) {
        $report = "$Name.html"
        Copy-Item -LiteralPath $Bundle.ReportHtml -Destination (Join-Path $reportsDir $report) -Force
    }
    $row | Add-Member -NotePropertyName ReportLink -NotePropertyValue $report
    $row | Add-Member -NotePropertyName FromWorkFolder -NotePropertyValue $fromFolder
    $row | Add-Member -NotePropertyName RunName -NotePropertyValue $Name
    $row | Add-Member -NotePropertyName StageRank -NotePropertyValue (Get-StageRank $Bundle)
    $row | Add-Member -NotePropertyName SerialKey -NotePropertyValue (Get-SerialKey $row.Serial)
    $row | Add-Member -NotePropertyName SerialSource -NotePropertyValue ([string](Get-JsonField $Bundle.Kit 'SerialSource'))
    $row | Add-Member -NotePropertyName HardwareUuid -NotePropertyValue (Get-HardwareUuid $Bundle.Machine $Bundle.Kit)
    # Kits from KIT-007 on write the field even when the firmware gave no usable UUID.
    $recorded = ($null -ne (Get-JsonField $Bundle.Kit 'MachineUuid')) -or ($null -ne (Get-JsonField $Bundle.Machine 'MachineUuid'))
    $row | Add-Member -NotePropertyName UuidRecorded -NotePropertyValue $recorded
    $row | Add-Member -NotePropertyName Bundle -NotePropertyValue $Bundle
    $row | Add-Member -NotePropertyName RunHash -NotePropertyValue (Get-RunHash $Name)
    $row | Add-Member -NotePropertyName EarlierRuns -NotePropertyValue @(Get-JsonField $Bundle.Kit 'EarlierRuns' | Where-Object { $_ } | ForEach-Object { [string]$_ })
    # Whether its kit recorded the stick at all (KIT-007 on), even as an empty list on an empty stick.
    # The property, not its value: a function returning an empty list returns nothing at all.
    $row | Add-Member -NotePropertyName HasEarlierList -NotePropertyValue ([bool]($Bundle.Kit -and $Bundle.Kit.PSObject.Properties['EarlierRuns']))
    $row | Add-Member -NotePropertyName ClockWrong -NotePropertyValue $false
    $rows.Add($row)
}

# A run that cannot be read is a finding too: it must show as a row, not vanish, and it must say
# which laptop it is, from its name, so a later check of the same laptop replaces it.
function Add-UnreadableRow([string]$Name, [string]$ResultFile, [string]$Why) {
    $id = Get-ResultNameParts $Name ''
    $rows.Add([pscustomobject][ordered]@{
        Verdict = 'UNREADABLE'; Serial = $id.Serial; Computer = $id.Computer; Model = ''; Cpu = ''; RamGB = $null; RamSlots = ''
        SystemDisk = ''; SystemDriveFree = ''; DriveHealth = ''; Battery = ''; Windows = ''; Windows11 = ''
        OverallGrade = ''; Reasons = $Why; Notes = ''; PhysicalProblems = ''
        CheckedAt = $id.StartedAt; KitVersion = ''; AppVersion = ''; ResultFile = $ResultFile
        ScanReplace = @(); ScanFix = @(); DriveFix = @(); DriveRead = $false
        ReportLink = ''; FromWorkFolder = $false; RunName = $Name; StageRank = 0
        SerialKey = (Get-SerialKey $id.Serial); SerialSource = ''; HardwareUuid = ''; UuidRecorded = $false; Bundle = $null
        RunHash = (Get-RunHash $Name); EarlierRuns = @(); HasEarlierList = $false; ClockWrong = $false
    })
}

try {
    foreach ($name in $runNames) {
        $zipFile = $zips | Where-Object { $_.BaseName -eq $name } | Select-Object -First 1
        $folder = $folders | Where-Object { $_.Name -eq ".work-$name" } | Select-Object -First 1
        $zipBundle = $null; $zipError = ''
        if ($zipFile) {
            try {
                $dir = Join-Path $temp $name
                Expand-Archive -LiteralPath $zipFile.FullName -DestinationPath $dir -Force
                $zipBundle = Read-AuditFolder $dir
            } catch { $zipError = $_.Exception.Message }
        }
        $folderBundle = $null
        if ($folder) { $folderBundle = Read-AuditFolder $folder.FullName }

        # Whichever got further decides; on a tie, the zip. A zip that cannot be read loses to its
        # folder, so a window closed while the zip was being written still leaves the laptop's result.
        $zipRank = if ($zipBundle) { Get-StageRank $zipBundle } else { -1 }
        $folderRank = if ($folderBundle) { Get-StageRank $folderBundle } else { -1 }
        if ($folderBundle -and $folderRank -gt $zipRank) {
            $folderBundle | Add-Member -NotePropertyName FromWorkFolder -NotePropertyValue $true
            if (-not $folderBundle.Kit) {
                # A kit older than KIT-007 wrote kit.json only at its end. Its name, machine.json and
                # the report carry the rest.
                $computer = [string](Get-JsonField $folderBundle.Machine 'ComputerName')
                $id = Get-ResultNameParts $name $computer
                $folderBundle.Kit = [pscustomobject]@{
                    Serial       = $id.Serial
                    ComputerName = if ($computer) { $computer } else { $id.Computer }
                    ResultName   = $folder.Name
                    StartedAt    = $id.StartedAt
                    AppVersion   = [string](Get-JsonField $folderBundle.Report 'AppVersion')
                }
            }
            Add-RunRow $folderBundle $name $folder.Name
        } elseif ($zipBundle) {
            Add-RunRow $zipBundle $name $zipFile.Name
        } else {
            Add-UnreadableRow $name $zipFile.Name "Could not read $($zipFile.Name): $zipError"
        }
    }
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# One row per laptop. Runs are grouped into laptops by Group-AuditRuns (see there).
#
# A laptop checked more than once is shown from its latest finished check, or, if none was finished,
# its latest check, but never from that check alone (KIT-007). Whatever any check recorded stays on
# the row until a finished check known to be later re-examined it:
# - Which check is later: one that lists the other among the results already on the stick when it
#   started (EarlierRuns), whatever the clocks say. Two such checks that do not list each other were
#   made on two sticks, or a result was moved off the stick in between: their order is not known.
#   Only a check from a kit older than KIT-007 is put in order by the clocks, and not when a clock is
#   plainly wrong: dated before its kit was made, or contradicted by EarlierRuns. When the order is
#   not known, nothing is cleared.
# - Physical answers, question by question: a "problem" stays until a finished check known to be
#   later answered "ok". A skipped or unreached question changes nothing.
# - What a scan found stays until a finished check known to be later scanned again; a drive fault,
#   until such a check read the drive.
# Each carried reason names the check it came from. A stale finding kept is the safe failure: the
# row says where it came from, and a finished recheck replaces it. The summary never uses its own
# PC's clock: a row must not change with the day the summary is run.
#
# Get-RunOrder sorts by CheckedAt, which has minute precision, then by the run name, which ends in
# the start time and gains seconds when an earlier run of that minute left a zip or a work folder.
# The run name, not the file name: ".work-" would sort a later folder below an earlier zip. Compared
# ordinally, and a run with no known time sorts first: culture comparison weighs "-" and "_" oddly.
function Get-RunOrder($Row) {
    $at = if ($Row.CheckedAt) { [string]$Row.CheckedAt } else { '0000-00-00 00:00' }
    "$at|$($Row.RunName)"
}
# Notes and reasons never contain "; ": the table splits them on it.
function Add-Note($Row, [string]$Note) { $Row.Notes = if ($Row.Notes) { "$($Row.Notes); $Note" } else { $Note } }

# Is check X later than check Y? 'yes', 'no', or '' when that is not known.
function Get-Later($X, $Y) {
    if (@($X.EarlierRuns) -contains $Y.RunHash) { return 'yes' }
    if (@($Y.EarlierRuns) -contains $X.RunHash) { return 'no' }
    # Both recorded what was on the stick, and neither lists the other: they were made on two sticks,
    # or a result was moved off the stick in between. Their clocks would then decide alone, and a
    # clock set ahead would let an older check clear a later "the battery bulges".
    if ($X.HasEarlierList -and $Y.HasEarlierList) { return '' }
    $xAt = [string]$X.CheckedAt; $yAt = [string]$Y.CheckedAt
    if ($X.ClockWrong -or $Y.ClockWrong -or -not $xAt -or -not $yAt) { return '' }
    if ($xAt -eq $yAt) {
        # The same minute: the name that gained seconds came second.
        if ($X.RunName.Length -gt $Y.RunName.Length -and $X.RunName.StartsWith($Y.RunName)) { return 'yes' }
        if ($Y.RunName.Length -gt $X.RunName.Length -and $Y.RunName.StartsWith($X.RunName)) { return 'no' }
        return ''
    }
    if ([string]::CompareOrdinal($xAt, $yAt) -gt 0) { return 'yes' }
    return 'no'
}

# A clock is plainly wrong when its check is dated before the kit that made it, or when a check that
# lists another as earlier is dated before it. Which of those two clocks was wrong is not known.
function Set-ClockWrong($Runs) {
    foreach ($run in $Runs) {
        $at = [string]$run.CheckedAt
        if ($at -and [string]$run.KitVersion -match '^(\d{4}-\d{2}-\d{2})' -and [string]::CompareOrdinal($at, $Matches[1]) -lt 0) { $run.ClockWrong = $true }
    }
    foreach ($x in $Runs) {
        foreach ($y in $Runs) {
            if ([object]::ReferenceEquals($x, $y) -or -not (@($x.EarlierRuns) -contains $y.RunHash)) { continue }
            if ($x.CheckedAt -and $y.CheckedAt -and [string]::CompareOrdinal([string]$x.CheckedAt, [string]$y.CheckedAt) -lt 0) {
                $x.ClockWrong = $true; $y.ClockWrong = $true
            }
        }
    }
}

# Two reasons are one when they differ only in their numbers: a battery at 41%, 40.8% and 40.5%,
# found by three checks, is one reason, while two different drives are two.
function Get-ReasonKey([string]$Reason) { $Reason -replace '\d+([.,]\d+)?', '#' }

# The row for one laptop, from its runs, newest first by clock.
function Merge-LaptopRuns($Runs) {
    if ($Runs.Count -gt 1) { Set-ClockWrong $Runs }
    $readable = @($Runs | Where-Object { $_.Bundle })
    $finished = @($readable | Where-Object { $_.StageRank -ge 4 })
    # The latest of some checks: one that no other is known to be later than, the newest by clock
    # among several such.
    function Get-Latest($Candidates) {
        $top = @($Candidates | Where-Object {
            $c = $_
            -not @($Candidates | Where-Object { -not [object]::ReferenceEquals($_, $c) -and (Get-Later $_ $c) -eq 'yes' })
        })
        if ($top.Count -gt 0) { return $top[0] }
        return $Candidates[0]
    }
    $shown = if ($finished.Count -gt 0) { Get-Latest $finished } elseif ($readable.Count -gt 0) { Get-Latest $readable } else { $Runs[0] }
    if ($Runs.Count -eq 1 -or -not $shown.Bundle) { return $shown }
    $shownFinished = $shown.StageRank -ge 4
    $others = @($Runs | Where-Object { -not [object]::ReferenceEquals($_, $shown) })
    # A check is named by its time, and by its file too when another check of the laptop has that time.
    $times = @($Runs | ForEach-Object { [string]$_.CheckedAt })
    function Get-CheckLabel($Run) {
        $at = [string]$Run.CheckedAt
        if (@($times | Where-Object { $_ -eq $at }).Count -gt 1 -or -not $at) { "$at ($($Run.ResultFile))".Trim() } else { $at }
    }

    # Physical answers, question by question, from every real answer any run gave, newest first.
    $byQuestion = [ordered]@{}
    foreach ($run in $readable) {
        $manual = $run.Bundle.Manual
        if (-not (Get-JsonField $manual 'Asked')) { continue }
        foreach ($item in @(Get-JsonField $manual 'Items' | Where-Object { $_ })) {
            $id = [string](Get-JsonField $item 'Id')
            $answer = [string](Get-JsonField $item 'Answer')
            if (-not $id -or ($answer -ne 'ok' -and $answer -ne 'problem')) { continue }
            if (-not $byQuestion.Contains($id)) { $byQuestion[$id] = New-Object System.Collections.Generic.List[object] }
            $byQuestion[$id].Add([pscustomobject]@{ Run = $run; Item = $item; Answer = $answer })
        }
    }
    $answers = [ordered]@{}
    foreach ($id in @($byQuestion.Keys)) {
        $given = $byQuestion[$id]
        $clears = @($given | Where-Object { $_.Answer -eq 'ok' -and $_.Run.StageRank -ge 4 })
        # A "problem" that no finished check known to be later answered "ok".
        $standing = @($given | Where-Object {
            $p = $_
            $p.Answer -eq 'problem' -and -not @($clears | Where-Object { (Get-Later $_.Run $p.Run) -eq 'yes' })
        })
        $pick = if ($standing.Count -gt 0) { $standing[0] } else { @($given | Where-Object { $_.Answer -eq 'ok' })[0] }
        $answers[$id] = [pscustomobject]@{
            Id = $id; Short = [string](Get-JsonField $pick.Item 'Short'); Question = [string](Get-JsonField $pick.Item 'Question')
            Answer = $pick.Answer; Note = [string](Get-JsonField $pick.Item 'Note')
            From = if ([object]::ReferenceEquals($pick.Run, $shown)) { '' } else { Get-CheckLabel $pick.Run }
        }
    }
    # The shown run's own skips, for its count of skipped checks, where no run answered for real.
    if ((Get-JsonField $shown.Bundle.Manual 'Asked')) {
        foreach ($item in @(Get-JsonField $shown.Bundle.Manual 'Items' | Where-Object { $_ })) {
            $id = [string](Get-JsonField $item 'Id')
            if ($id -and -not $answers.Contains($id)) { $answers[$id] = $item }
        }
    }

    # What other scans found, unless a finished check known to be later re-examined it.
    $carriedReplace = New-Object System.Collections.Generic.List[string]
    $carriedFix = New-Object System.Collections.Generic.List[string]
    $carriedRecheck = New-Object System.Collections.Generic.List[string]
    $driveFrom = ''
    $seen = @{}
    foreach ($r in @($shown.ScanReplace) + @($shown.ScanFix)) { if ($r) { $seen[(Get-ReasonKey $r)] = $true } }
    $unreadReplaced = @()
    foreach ($run in $others) {
        $replacedBy = @($finished | Where-Object { -not [object]::ReferenceEquals($_, $run) -and (Get-Later $_ $run) -eq 'yes' })
        if (-not $run.Bundle) {
            if ($replacedBy.Count -gt 0) { $unreadReplaced += $run.ResultFile }
            else { $carriedRecheck.Add("A check of this laptop could not be read ($($run.ResultFile)): check this laptop again") }
            continue
        }
        $scannedAgain = @($replacedBy | Where-Object { $_.Bundle -and $_.Bundle.Report }).Count -gt 0
        $driveReadAgain = @($replacedBy | Where-Object { $_.DriveRead }).Count -gt 0
        $label = Get-CheckLabel $run
        if (-not $scannedAgain) {
            foreach ($r in @($run.ScanReplace | Where-Object { $_ })) {
                $key = Get-ReasonKey $r
                if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $carriedReplace.Add("$r (found by the $label check)") }
            }
            foreach ($r in @($run.ScanFix | Where-Object { $_ -and @($run.DriveFix) -notcontains $_ })) {
                $key = Get-ReasonKey $r
                if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $carriedFix.Add("$r (found by the $label check)") }
            }
        }
        if (-not $driveReadAgain) {
            $tag = if ($scannedAgain) { "found by the $label check, and later checks could not read drive health" } else { "found by the $label check" }
            foreach ($r in @($run.DriveFix | Where-Object { $_ })) {
                $key = Get-ReasonKey $r
                if (-not $seen.ContainsKey($key)) {
                    $seen[$key] = $true
                    $carriedFix.Add("$r ($tag)")
                    if (-not $driveFrom) { $driveFrom = $label }
                }
            }
        }
    }

    $bundle = $shown.Bundle.PSObject.Copy()
    $bundle | Add-Member -NotePropertyName MergedAnswers -NotePropertyValue @($answers.Values) -Force
    $bundle | Add-Member -NotePropertyName CarriedReplace -NotePropertyValue @($carriedReplace) -Force
    $bundle | Add-Member -NotePropertyName CarriedFix -NotePropertyValue @($carriedFix) -Force
    $bundle | Add-Member -NotePropertyName CarriedRecheck -NotePropertyValue @($carriedRecheck) -Force
    $merged = Get-AuditRow $bundle $rules
    foreach ($field in 'Verdict', 'Reasons', 'Notes', 'PhysicalProblems') { $shown.$field = $merged.$field }
    if ($driveFrom) { $shown.DriveHealth = "Fault found by the $driveFrom check" }
    if (-not $shown.Serial) {
        $named = @($Runs | Where-Object { $_.Serial } | Select-Object -First 1)
        if ($named.Count -gt 0) { $shown.Serial = $named[0].Serial }
    }

    $laterUnfinished = @($others | Where-Object { $_.Bundle -and $_.StageRank -lt 4 -and (Get-Later $_ $shown) -eq 'yes' })
    if ($shownFinished -and $laterUnfinished.Count -gt 0) {
        Add-Note $shown "A later check ($(Get-CheckLabel $laterUnfinished[0])) was not finished, and what it found is included"
    }
    $wrongClock = @($Runs | Where-Object { $_.ClockWrong })
    if ($wrongClock.Count -gt 0) {
        Add-Note $shown "The clock was wrong on the $(@($wrongClock | ForEach-Object { Get-CheckLabel $_ }) -join ', ') check, so which check came later is not known from it, and what it found is kept until a check made after it on this stick re-examines it"
    }
    $unordered = @($others | Where-Object { $_.Bundle -and -not $_.ClockWrong -and -not $shown.ClockWrong -and (Get-Later $_ $shown) -eq '' })
    if ($unordered.Count -gt 0) {
        Add-Note $shown "The $(@($unordered | ForEach-Object { Get-CheckLabel $_ }) -join ', ') check could not be put in order with this one, so what it found is kept"
    }
    if (-not $shownFinished) {
        Add-Note $shown "No check of this laptop was finished, so what each of its $($Runs.Count) checks found is included"
    }
    if ($unreadReplaced.Count -gt 0) {
        Add-Note $shown "An earlier check could not be read ($($unreadReplaced -join ', ')), and a later finished check replaces it"
    }
    return $shown
}

$latest = New-Object System.Collections.Generic.List[object]
foreach ($group in (Group-AuditRuns $rows)) {
    $runs = New-Object System.Collections.Generic.List[object]
    foreach ($run in $group) { $runs.Add($run) }
    $runs.Sort([Comparison[object]] { param($a, $b) [string]::CompareOrdinal((Get-RunOrder $b), (Get-RunOrder $a)) })
    $shown = Merge-LaptopRuns $runs
    $shown | Add-Member -NotePropertyName Runs -NotePropertyValue $runs.Count -Force
    $computers = @($runs | ForEach-Object { [string]$_.Computer } | Where-Object { $_ } | Sort-Object -Unique)
    if ($computers.Count -gt 1) { Add-Note $shown "The checks of this laptop were made under the computer names $($computers -join ', ')" }
    $latest.Add($shown)
}
# The same serial on two rows: a system board replaced (its UUID is new), or two laptops whose
# firmware reports one serial. Nothing on the stick tells which, so both rows stay, each naming the other.
foreach ($same in @($latest | Where-Object { $_.SerialKey } | Group-Object SerialKey | Where-Object { $_.Count -gt 1 })) {
    foreach ($row in $same.Group) {
        $other = @($same.Group | Where-Object { -not [object]::ReferenceEquals($_, $row) } | ForEach-Object {
            $when = switch (Get-Later $_ $row) { 'yes' { 'after this one' } 'no' { 'before this one' } default { 'in an order not known' } }
            "$($_.ResultFile), checked $when"
        })
        Add-Note $row "Another row has this serial too ($($other -join ', ')): if this laptop's system board was replaced, the row checked later shows it as it is now, and otherwise two laptops report one serial"
    }
}
$order = @{ 'REPLACE' = 0; 'FIX FIRST' = 1; 'CHECK AGAIN' = 2; 'UNREADABLE' = 3; 'READY' = 4 }
$sorted = @($latest | Sort-Object @{ Expression = { $order[$_.Verdict] } }, Model, Serial)

# --- CSV for Excel. Windows PowerShell's UTF8 adds a byte-order mark, which is what Excel needs
# to show non-English characters correctly.
$csv = Join-Path $ResultsDir 'fleet-summary.csv'
$sorted | Select-Object -Property * -ExcludeProperty $script:InternalColumns | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8

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
    $serial = "$(Html $r.Serial)<br><small>$(Html $r.Computer)$(if ($r.Runs -gt 1) { " - checked $($r.Runs) times, all included" })<br>$(Html $r.CheckedAt)</small>"
    [void]$sb.AppendLine("<tr><td class=""v $cls"">$(Html $r.Verdict)</td><td>$serial</td><td>$(Html $r.Model)</td><td>$(Html $r.Cpu)</td>" +
        "<td>$memory</td><td>$(Html $r.SystemDisk)<br><small>$(Html $r.SystemDriveFree) free</small></td><td>$(Html $r.DriveHealth)</td>" +
        "<td>$(Html $r.Battery)</td><td>$(Html $r.Windows11)</td><td>$why</td><td><small>$note</small></td><td>$link</td></tr>")
}
[void]$sb.AppendLine('</tbody></table>')
[void]$sb.AppendLine('<p><small>READY: nothing found that stops a new employee using it. It does not mean Windows 11 supports the laptop: ' +
    'the Windows 11 column and the notes say that, and it counts against a laptop only when the rules above include "able to run Windows 11". ' +
    'FIX FIRST: usable after the repair or upgrade listed. ' +
    'REPLACE: cannot meet the rules at any repair cost. CHECK AGAIN: something important was not checked, so no verdict would be honest.</small></p>')
[void]$sb.AppendLine('</body></html>')
$html = Join-Path $ResultsDir 'fleet-summary.html'
Write-Utf8NoBom $html $sb.ToString()

Write-Host ''
$sorted | Format-Table -AutoSize Verdict, Serial, Model, RamGB, Battery, DriveHealth, Windows11 | Out-String -Width 200 | Write-Host
Write-Host "  $($sorted.Count) $($noun): $($counts -join ', ')" -ForegroundColor White
Write-Host "  Table for your manager: $html" -ForegroundColor Green
Write-Host "  For Excel:              $csv" -ForegroundColor Green
