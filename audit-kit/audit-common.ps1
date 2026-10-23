# Shared by audit-laptop.ps1 and summarize-fleet.ps1, so the verdict shown on a laptop's screen
# and the one in the fleet table are computed by the same code and cannot disagree.
# Windows PowerShell 5.1 compatible and ASCII only: 5.1 reads a script without a byte-order mark
# in the ANSI code page, so any non-ASCII character here would be garbled on some laptops.

$script:AuditKitVersion = '2026-10-23.1'

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false))
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
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
    $ownVerdicts = @($verdicts | Where-Object { $script:DriveHealthIds -contains $_.Metric.Id -and $_.Metric.Id -ne 'events.disk_errors_90d' })
    $eventVerdicts = @($verdicts | Where-Object { $_.Metric.Id -eq 'events.disk_errors_90d' })
    $driveRead = @($verdicts | Where-Object { $_.Metric.Id -like 'smart.*' -or $_.Metric.Id -eq 'storage.wear_pct' }).Count -gt 0
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
    $batteryText = 'No battery'
    if ($battery.Count -gt 0) {
        $lowest = ($battery | ForEach-Object { [double]$_.Metric.Value.Number } | Measure-Object -Minimum).Minimum
        $batteryText = "$lowest%"
        if ($lowest -lt [double]$Rules.minBatteryPct) { $fix.Add("Replace the battery: $lowest% of its design capacity left") }
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
        CheckedAt          = if ($kit) { [string]$kit.StartedAt } else { '' }
        KitVersion         = if ($kit) { [string]$kit.KitVersion } else { '' }
        AppVersion         = if ($kit) { [string]$kit.AppVersion } elseif ($report) { [string]$report.AppVersion } else { '' }
        ResultFile         = if ($kit) { [string]$kit.ResultName } else { '' }
    }
}
