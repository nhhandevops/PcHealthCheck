# Shared by audit-laptop.ps1 and summarize-fleet.ps1, so the verdict shown on a laptop's screen
# and the one in the fleet table are computed by the same code and cannot disagree.
# Windows PowerShell 5.1 compatible and ASCII only: 5.1 reads a script without a byte-order mark
# in the ANSI code page, so any non-ASCII character here would be garbled on some laptops.

$script:AuditKitVersion = '2026-10-25.1'

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false))
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

# One field of a parsed JSON file, or $null when that file was written before the field existed.
# Plain $Object.Name would throw under Set-StrictMode, which the kit's tests run with, and every
# zip already collected is missing the fields added after it was made.
function Get-JsonField($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if (-not $prop) { return $null }
    return $prop.Value
}

# --- The clock.
# A laptop that has been switched off for months comes up with a stale clock, and Windows steps it
# to the right time the moment it finds a network - which can land in the middle of an audit. The
# kit therefore measures every elapsed time with a Stopwatch and records the step itself, in
# seconds, as ClockJumpSeconds in kit.json. Negative means the clock was pushed back (KIT-004).
#
# 120 seconds, not a handful: a Get-Date and a Stopwatch are not read at the same instant, Windows
# slews the clock by small amounts on its own, and a laptop that sleeps while the questions are
# being answered makes the wall clock run ahead of the Stopwatch. None of those is worth a line in
# the manager's table, and a note that shows up on healthy laptops is a note nobody reads.
$script:ClockJumpNoticeSeconds = 120

# How far the wall clock moved by itself: the wall-clock span of a run, less the span its Stopwatch
# measured. 0 on a laptop whose clock was already right.
function Measure-ClockJump([datetime]$Started, [datetime]$Finished, [double]$ElapsedSeconds) {
    return [int][math]::Round((($Finished - $Started).TotalSeconds - $ElapsedSeconds))
}

# The one sentence both the operator and the manager are shown, or '' when the clock behaved.
# No semicolon in it on purpose: Notes are split on '; ' for display.
function Get-ClockJumpText([int]$JumpSeconds) {
    if ([math]::Abs($JumpSeconds) -lt $script:ClockJumpNoticeSeconds) { return '' }
    $minutes = [int][math]::Round([math]::Abs($JumpSeconds) / 60.0)
    $direction = if ($JumpSeconds -lt 0) { 'back' } else { 'forward' }
    return "This laptop's clock moved $direction about $minutes minutes during the check, so the times recorded for it, its file name included, are approximate. The health readings are not affected"
}

# What this run recorded, or $null for a zip collected before the kit measured it.
function Get-ClockJumpSeconds($Kit) {
    $raw = Get-JsonField $Kit 'ClockJumpSeconds'
    if ($null -eq $raw) { return $null }
    $value = 0
    if (-not [int]::TryParse([string]$raw, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { return $null }
    return $value
}

function Get-ClockJumpNote($Kit) {
    $jump = Get-ClockJumpSeconds $Kit
    if ($null -eq $jump) { return '' }
    return Get-ClockJumpText $jump
}

# Which recorded time dates this run. Normally the start, which is also what its file name carries.
# When the clock was stepped mid-run the start was read before the step and the finish after it, so
# the finish is the one that lines up with the rest of the fleet - and this value is what decides
# which run of a serial the summary shows as the latest.
function Get-AuditCheckedAt($Kit) {
    $jump = Get-ClockJumpSeconds $Kit
    if ($null -ne $jump -and [math]::Abs($jump) -ge $script:ClockJumpNoticeSeconds) {
        $finished = [string](Get-JsonField $Kit 'FinishedAt')
        if ($finished) { return $finished }
    }
    return [string](Get-JsonField $Kit 'StartedAt')
}

# The pass rules, from audit-rules.json, with the defaults filled in for anything missing.
function Read-AuditRules([string]$Path) {
    $rules = [ordered]@{
        minRamGb              = 8
        requireSsd            = $true
        minBatteryPct         = 60
        minSystemDriveFreePct = 15
        requireWindows11Ready = $true
        driveWarningsNeedFix  = $false
    }
    $file = Read-JsonFile $Path
    if ($file) {
        foreach ($key in @($rules.Keys)) {
            if ($null -ne $file.$key) { $rules[$key] = $file.$key }
        }
    }
    return [pscustomobject]$rules
}

# Everything one laptop's run produced, from its unpacked folder.
function Read-AuditFolder([string]$Dir) {
    $reportJson = Get-ChildItem -LiteralPath (Join-Path $Dir 'report') -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    $reportHtml = Get-ChildItem -LiteralPath (Join-Path $Dir 'report') -Filter '*.html' -ErrorAction SilentlyContinue | Select-Object -First 1
    [pscustomobject]@{
        Dir        = $Dir
        Kit        = Read-JsonFile (Join-Path $Dir 'kit.json')
        Machine    = Read-JsonFile (Join-Path $Dir 'machine.json')
        Manual     = Read-JsonFile (Join-Path $Dir 'manual-checks.json')
        Disks      = Read-JsonFile (Join-Path $Dir 'disks.json')
        Report     = if ($reportJson) { Read-JsonFile $reportJson.FullName } else { $null }
        ReportHtml = if ($reportHtml) { $reportHtml.FullName } else { $null }
    }
}

$script:GradeRank = @{ 'Good' = 1; 'Normal' = 2; 'Bad' = 3 }

function Get-WorstGrade($Verdicts) {
    $worst = $null
    foreach ($v in @($Verdicts)) {
        $rank = $script:GradeRank[[string]$v.Grade]
        if ($rank -and (-not $worst -or $rank -gt $script:GradeRank[$worst])) { $worst = [string]$v.Grade }
    }
    return $worst
}

function Format-Subject($Verdict) {
    if ($Verdict.Metric.Subject) { return "$($Verdict.Metric.Label) ($($Verdict.Metric.Subject))" }
    return [string]$Verdict.Metric.Label
}

# The readings that say whether a drive is failing. Temperatures and power-on hours are left out:
# they describe use, not damage.
$script:DriveHealthIds = @(
    'smart.predict_failure', 'smart.reallocated', 'smart.pending_sectors', 'smart.offline_uncorrectable',
    'smart.reported_uncorrectable', 'smart.command_timeout', 'smart.crc_errors', 'smart.ssd_percent_consumed',
    'smart.attributes_at_threshold', 'storage.wear_pct', 'storage.read_errors_uncorrected',
    'storage.write_errors_uncorrected', 'events.disk_errors_90d', 'storage.volume.healthy'
)

# Drives that are not inside the laptop. The audit always runs from a USB drive, and that one
# cannot be unplugged, so AUDIT-GUIDE's "unplug every other USB drive" can never cover it: the
# filter has to be here. A drive whose bus we cannot positively identify as external stays in,
# because missing a failing internal drive sends a bad laptop to an employee, which is the
# costlier mistake of the two.
$script:ExternalBusTypes = @('USB', '1394', 'SD', 'MMC')

# The highest battery reading still treated as a measurement, in percent of design.
$script:BatteryPlausibleMaxPct = 105

# The app names a drive "<friendly name> <em dash> <size> GB". Written by code point because
# this file must stay ASCII (see the header).
$script:SubjectSeparator = ' ' + [char]0x2014

function Get-ExternalDriveNames($Disks) {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($disk in @($Disks)) {
        if (-not $disk) { continue }
        if ($script:ExternalBusTypes -notcontains [string]$disk.BusType) { continue }
        $name = ([string]$disk.FriendlyName).Trim()
        if ($name) { $names.Add($name) }
    }
    return $names
}

# Matching on the name plus the separator, never the name alone: two drives called
# "Samsung SSD" and "Samsung SSD Pro" must not be mistaken for each other.
function Test-ExternalDrive([string]$Subject, $ExternalNames) {
    $subject = ([string]$Subject).Trim()
    if (-not $subject) { return $false }
    foreach ($name in @($ExternalNames)) {
        if ($subject -eq $name) { return $true }
        if ($subject.StartsWith($name + $script:SubjectSeparator)) { return $true }
    }
    return $false
}

# The checks whose failure makes a verdict unsafe. Any other check that did not finish is a note.
$script:CriticalModules = @('sys.overview', 'volumes', 'battery', 'disk.smart', 'disk.reliability')

# One row of the fleet table: the facts, and the verdict with every reason for it.
# READY       - nothing found that stops a new employee using it.
# FIX FIRST   - usable after a repair or upgrade the reasons name (battery, drive, memory...).
# REPLACE     - cannot meet the rules at any repair cost (today: a processor Windows 11 refuses).
# CHECK AGAIN - something important was not checked, so no verdict would be honest.
function Get-AuditRow($Bundle, $Rules) {
    $kit = $Bundle.Kit; $m = $Bundle.Machine; $report = $Bundle.Report; $manual = $Bundle.Manual
    $replace = New-Object System.Collections.Generic.List[string]
    $fix     = New-Object System.Collections.Generic.List[string]
    $recheck = New-Object System.Collections.Generic.List[string]
    $notes   = New-Object System.Collections.Generic.List[string]
    $verdicts = @()
    if ($report) { $verdicts = @($report.Verdicts) }

    # --- Physical checks first: a swollen battery outranks everything else on the list.
    $manualProblems = @()
    if ($manual -and $manual.Asked) {
        foreach ($item in @($manual.Items)) {
            if ($item.Answer -eq 'problem') {
                $text = if ($item.Note) { "$($item.Short): $($item.Note)" } else { [string]$item.Short }
                $manualProblems += $text
                if ($item.Id -eq 'battery_swelling') { $fix.Insert(0, 'SWOLLEN BATTERY: do not charge or use it; replace the battery first') }
                else { $fix.Add("Physical check failed - $text") }
            }
        }
        $skipped = @(@($manual.Items) | Where-Object { $_.Answer -eq 'not checked' }).Count
        if ($skipped -gt 0) { $notes.Add("$skipped physical check(s) skipped") }
        if ($manual.ExtraNote) { $notes.Add("Remark: $($manual.ExtraNote)") }
    } else {
        $notes.Add('Physical checks not done')
    }

    # --- The clock. A step does not make any reading wrong, only the times recorded beside them,
    # so it is a note and never a recheck: a laptop is not re-audited because Windows fixed its
    # clock, and a zip from before the kit measured this says nothing at all.
    $clockNote = Get-ClockJumpNote $kit
    if ($clockNote) { $notes.Add($clockNote) }

    # --- Did the scan itself run, and with administrator rights?
    if (-not $report) {
        $exitText = if ($kit) { $kit.ScanExitCode } else { 'unknown' }
        $recheck.Add("The scan produced no report (exit code $exitText)")
    } else {
        if (-not $report.RanElevated) {
            if ($kit -and $kit.AdminRequested) { $recheck.Add('The administrator prompt was declined, so drive health was NOT checked') }
            else { $recheck.Add('Run without administrator rights, so drive health was NOT checked') }
        }
        foreach ($module in @($report.Modules)) {
            if ($module.Status -eq 'PartiallyCompleted' -or $module.Status -eq 'Failed') {
                if ($script:CriticalModules -contains $module.ModuleId) { $recheck.Add("Did not finish: $($module.ModuleName)") }
                else { $notes.Add("Did not finish: $($module.ModuleName)") }
            }
        }
    }

    # --- Drive health. The drive's own readings (SMART, wear, error counters) decide it. Disk errors
    # in the event log are reported separately: they can come from a USB drive, and without the
    # drive's own readings an event-log grade must not stand in for drive health.
    $externalNames = Get-ExternalDriveNames $Bundle.Disks
    $driveVerdicts = @($verdicts | Where-Object { $script:DriveHealthIds -contains $_.Metric.Id -and $_.Metric.Id -ne 'events.disk_errors_90d' })
    $externalVerdicts = @($driveVerdicts | Where-Object { Test-ExternalDrive ([string]$_.Metric.Subject) $externalNames })
    $ownVerdicts = @($driveVerdicts | Where-Object { -not (Test-ExternalDrive ([string]$_.Metric.Subject) $externalNames) })
    $eventVerdicts = @($verdicts | Where-Object { $_.Metric.Id -eq 'events.disk_errors_90d' })
    # Only the laptop's own drives count as having been read. A scan that read nothing but the
    # audit's USB drive has not read this laptop's drive health, and must not look as if it had.
    $driveRead = @($ownVerdicts | Where-Object { $_.Metric.Id -like 'smart.*' -or $_.Metric.Id -eq 'storage.wear_pct' }).Count -gt 0
    # Excluded, never silently dropped: a real fault on the auditor's own drive is still worth knowing.
    $externalIssues = @($externalVerdicts | Where-Object { $_.Grade -eq 'Bad' -or $_.Grade -eq 'Normal' } | ForEach-Object { Format-Subject $_ })
    if ($externalIssues.Count -gt 0) {
        $notes.Add("Not counted against this laptop, these drives are plugged in over USB: $($externalIssues -join ', ')")
    }
    if (-not $driveRead -and $report -and $report.RanElevated) {
        $recheck.Add('No drive inside this laptop reported its own health, so drive health was NOT checked')
    }
    $driveGrade = Get-WorstGrade $ownVerdicts
    $driveIssues = @($ownVerdicts | Where-Object { $_.Grade -eq 'Bad' -or $_.Grade -eq 'Normal' } | ForEach-Object { Format-Subject $_ })
    $driveText = if (-not $driveRead) { 'Not read' } elseif ($driveGrade) { $driveGrade } else { 'Read, not graded' }
    if ($driveGrade -eq 'Bad') { $fix.Add("Replace the drive: $($driveIssues -join ', ')") }
    elseif ($driveGrade -eq 'Normal') {
        if ($Rules.driveWarningsNeedFix) { $fix.Add("Drive warnings: $($driveIssues -join ', ')") }
        else { $notes.Add("Drive shows early warnings, worth watching: $($driveIssues -join ', ')") }
    }
    $eventGrade = Get-WorstGrade $eventVerdicts
    $eventIssues = @($eventVerdicts | Where-Object { $_.Grade -eq 'Bad' -or $_.Grade -eq 'Normal' } | ForEach-Object { Format-Subject $_ })
    if ($eventGrade -eq 'Bad') { $fix.Add("Disk errors in the event log, check that drive: $($eventIssues -join ', ')") }
    elseif ($eventGrade -eq 'Normal') { $notes.Add("A few disk errors in the event log: $($eventIssues -join ', ')") }

    # --- Drive type: Windows 11 on a hard disk is slow enough to cost an employee real time.
    $diskType = if ($m) { [string]$m.SystemDiskMediaType } else { '' }
    $diskText = if ($m -and $m.SystemDiskModel) { "$diskType $($m.SystemDiskSizeGB) GB ($($m.SystemDiskModel))" } else { $diskType }
    if ($Rules.requireSsd -and $diskType -eq 'HDD') { $fix.Add('Replace the hard disk with an SSD') }
    elseif ($diskType -ne 'SSD' -and $diskType -ne 'HDD') { $notes.Add('The system drive did not say whether it is an SSD or a hard disk') }

    # --- Memory
    $ram = $null
    if ($m -and $m.InstalledRamGB) { $ram = [double]$m.InstalledRamGB }
    else {
        $total = @($verdicts | Where-Object { $_.Metric.Id -eq 'ram.total_gb' }) | Select-Object -First 1
        if ($total) { $ram = [math]::Round([double]$total.Metric.Value.Number) }
    }
    $slots = if ($m -and $m.RamSlotsTotal) { "$($m.RamSlotsUsed) of $($m.RamSlotsTotal) slots used" } else { '' }
    if ($null -ne $ram -and $ram -lt [double]$Rules.minRamGb) {
        $fix.Add("Add memory: $ram GB installed, the rule asks for $($Rules.minRamGb) GB" + $(if ($slots) { " ($slots)" } else { '' }))
    }

    # --- Battery
    $battery = @($verdicts | Where-Object { $_.Metric.Id -eq 'battery.capacity_pct' })
    # Only a believable, graded reading is a measurement. Newer app builds leave this one
    # ungraded when the gauge reports more than the pack was built to hold; the figure is
    # re-checked here as well, so a zip collected by an older build - which graded 109.7% as
    # Good - is read correctly too, instead of telling the manager that a battery nobody could
    # measure is in better shape than one that was measured.
    # 105, not 100: a fresh pack routinely measures a shade over design. Kept in step with
    # BatteryCapacity.PlausibleMaxPct in PcHealth.Core.
    $batteryGraded = @($battery | Where-Object {
        $script:GradeRank[[string]$_.Grade] -and
        [double]$_.Metric.Value.Number -gt 0 -and
        [double]$_.Metric.Value.Number -le $script:BatteryPlausibleMaxPct
    })
    $batteryText = 'No battery'
    if ($batteryGraded.Count -gt 0) {
        $lowest = ($batteryGraded | ForEach-Object { [double]$_.Metric.Value.Number } | Measure-Object -Minimum).Minimum
        $batteryText = "$lowest%"
        if ($lowest -lt [double]$Rules.minBatteryPct) { $fix.Add("Replace the battery: $lowest% of its design capacity left") }
    } elseif ($battery.Count -gt 0) {
        $batteryText = 'Not reliable'
        $recheck.Add('The battery gauge reported a figure it cannot hold, so battery wear was NOT established')
        $notes.Add("Battery gauge reported $(($battery | ForEach-Object { [double]$_.Metric.Value.Number } | Measure-Object -Maximum).Maximum)% of design; recalibrate (full discharge, then a full uninterrupted charge) and check again")
    } elseif ($m -and $m.IsLaptop) {
        $batteryText = 'Not read'
        $recheck.Add('Laptop, but battery wear was NOT read')
    }

    # --- Free space on the system drive
    $systemDrive = if ($m -and $m.SystemDrive) { [string]$m.SystemDrive } else { 'C:' }
    $free = @($verdicts | Where-Object { $_.Metric.Id -eq 'storage.volume.free_pct' -and ([string]$_.Metric.Subject).StartsWith($systemDrive) }) | Select-Object -First 1
    $freeText = ''
    if ($free) {
        $freeText = "$($free.Metric.Value.Number)%"
        if ([double]$free.Metric.Value.Number -lt [double]$Rules.minSystemDriveFreePct) {
            $fix.Add("Free up space on $($systemDrive): $($free.Metric.Value.Number)% free")
        }
    }

    # --- Windows 11
    $build = 0
    if ($m -and $m.WindowsBuild) { $build = [int]$m.WindowsBuild }
    $ready = @($verdicts | Where-Object { $_.Metric.Id -eq 'os.win11_ready' }) | Select-Object -First 1
    $codesMetric = @($verdicts | Where-Object { $_.Metric.Id -eq 'os.win11_blockers' }) | Select-Object -First 1
    $codes = if ($codesMetric) { [string]$codesMetric.Metric.Value.Text } else { '' }
    if ($build -ge 22000) { $win11 = 'Already Windows 11' }
    elseif ($ready -and $ready.Metric.Value.Flag -eq $true) { $win11 = 'Yes' }
    elseif ($ready) {
        $processor = $codes -match '(^|\s)(Cpu\w*|PopCnt|Sse\w*)'
        $win11 = "No ($codes)"
        if ($Rules.requireWindows11Ready) {
            if ($processor) { $replace.Add("Windows 11 will not run on this processor ($codes)") }
            else { $fix.Add("Windows 11 blocked by $($codes): turn on TPM / Secure Boot in the firmware settings") }
        }
    } else {
        $win11 = 'Not assessed'
        if ($Rules.requireWindows11Ready) { $recheck.Add('Windows has not assessed this laptop for Windows 11') }
    }

    # --- Heat and hardware errors
    foreach ($id in @('cpu.temperature_c', 'cpu.zone_temperature_c')) {
        foreach ($v in @($verdicts | Where-Object { $_.Metric.Id -eq $id -and $_.Grade -eq 'Bad' })) {
            $fix.Add("Runs very hot ($($v.Metric.Value.Number) C): clean the fan and heatsink, renew the thermal paste")
        }
    }
    foreach ($v in @($verdicts | Where-Object { $_.Metric.Id -eq 'fans.airflow_ok' -and $_.Grade -eq 'Bad' })) { $fix.Add("Fan or airflow problem: $(Format-Subject $v)") }
    foreach ($v in @($verdicts | Where-Object { $_.Metric.Id -eq 'events.whea_uncorrectable_90d' -and $_.Grade -eq 'Bad' })) {
        $fix.Add('Hardware errors logged (WHEA): test the memory (Windows Memory Diagnostic) before handing it over')
    }
    foreach ($v in @($verdicts | Where-Object { $_.Metric.Id -eq 'events.bugcheck_90d' -and $_.Grade -ne 'Good' -and $_.Grade -ne 'Unknown' })) {
        $notes.Add("Blue screens in the last 90 days: $($v.Metric.Value.Number)")
    }

    $verdict = if ($replace.Count) { 'REPLACE' } elseif ($fix.Count) { 'FIX FIRST' } elseif ($recheck.Count) { 'CHECK AGAIN' } else { 'READY' }
    $model = if ($m) { [string]$m.FriendlyModel } else { '' }
    [pscustomobject][ordered]@{
        Verdict            = $verdict
        Serial             = if ($kit) { [string]$kit.Serial } else { '' }
        Computer           = if ($kit) { [string]$kit.ComputerName } elseif ($report) { [string]$report.MachineName } else { '' }
        Model              = $model
        Cpu                = if ($m) { [string]$m.Cpu } else { '' }
        RamGB              = $ram
        RamSlots           = $slots
        SystemDisk         = $diskText
        SystemDriveFree    = $freeText
        DriveHealth        = $driveText
        Battery            = $batteryText
        Windows            = if ($m) { [string]$m.Windows } else { '' }
        Windows11          = $win11
        OverallGrade       = if ($report) { [string]$report.OverallGrade } else { '' }
        Reasons            = (@($replace) + @($fix) + @($recheck)) -join '; '
        Notes              = @($notes) -join '; '
        PhysicalProblems   = $manualProblems -join '; '
        CheckedAt          = Get-AuditCheckedAt $kit
        KitVersion         = if ($kit) { [string]$kit.KitVersion } else { '' }
        AppVersion         = if ($kit) { [string]$kit.AppVersion } elseif ($report) { [string]$report.AppVersion } else { '' }
        ResultFile         = if ($kit) { [string]$kit.ResultName } else { '' }
    }
}
