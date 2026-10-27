# Tests for the audit kit's verdict logic (audit-kit/audit-common.ps1).
#
# The kit had no tests until the first fleet audit, where it told the manager to replace the
# drives in two laptops because of read errors on the USB drive the audit itself was running
# from. That is the defect these lock: a reading from a drive that is not in the laptop must
# never decide the laptop's verdict, and a reading nobody could trust must never read as a pass.
#
# Plain PowerShell on purpose: no Pester, because the kit must run on whatever Windows PowerShell
# 5.1 a borrowed laptop happens to have, and its tests should need no more than the kit does.
#
# Run: powershell -ExecutionPolicy Bypass -File tests\audit-kit\kit-tests.ps1

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\..\audit-kit\audit-common.ps1')

$script:Failures = 0
$script:Ran = 0

function Check([string]$What, [scriptblock]$Condition) {
    $script:Ran++
    $ok = $false
    try { $ok = [bool](& $Condition) } catch { $ok = $false; $What = "$What (threw: $($_.Exception.Message))" }
    if ($ok) {
        Write-Host "  PASS  $What"
    } else {
        Write-Host "  FAIL  $What" -ForegroundColor Red
        $script:Failures++
    }
}

# --- Fixtures ---------------------------------------------------------------

function New-Verdict([string]$Id, [string]$Grade, $Number = $null, [string]$Subject = '', [string]$Label = '') {
    if (-not $Label) { $Label = $Id }
    [pscustomobject]@{
        Grade  = $Grade
        Metric = [pscustomobject]@{
            Id      = $Id
            Label   = $Label
            Subject = $Subject
            Value   = [pscustomobject]@{ Number = $Number; Text = ''; Flag = $null }
        }
    }
}

function New-Disk([string]$Name, [string]$Bus) {
    [pscustomobject]@{ DeviceId = '0'; FriendlyName = $Name; MediaType = 'SSD'; BusType = $Bus; Size = 256060514304; HealthStatus = 'Healthy' }
}

# The app names a drive "<friendly name> <em dash> <size> GB".
function Subject-For([string]$Name, [int]$SizeGb) { "$Name $([char]0x2014) $SizeGb GB" }

# Every field the real files carry: strict mode turns a missing one into a failure, which is
# what catches a fixture that has quietly drifted away from what the kit actually writes.
function New-Bundle($Verdicts, $Disks, [bool]$Elevated = $true, [int]$ClockJumpSeconds = 0) {
    [pscustomobject]@{
        Dir     = 'fixture'
        Disks   = $Disks
        Kit     = [pscustomobject]@{
            KitVersion = 'test'; AppVersion = 'test'; Serial = 'TESTSER'; SerialSource = 'firmware'
            ComputerName = 'TEST-PC'; ResultName = 'test.zip'; StartedAt = '2026-10-25 20:00'
            FinishedAt = '2026-10-25 20:03'; AdminRequested = $true; ScanExitCode = 0
            ScanSeconds = 180; RunSeconds = 240; ClockJumpSeconds = $ClockJumpSeconds
            LeftOnLaptop = 'nothing'; ScriptErrors = @()
        }
        Machine = [pscustomobject]@{
            ComputerName = 'TEST-PC'; Manufacturer = 'LENOVO'; Model = '20J7S08T03'; SystemFamily = 'ThinkPad'
            FriendlyModel = 'LENOVO ThinkPad T470p (20J7S08T03)'; IsLaptop = $true
            BiosVersion = '1.00'; BiosDate = '2020-01-01'; Windows = 'Windows 10 Pro'
            WindowsBuild = 19045; WindowsInstalled = '2020-01-01'; Cpu = 'Intel Core i7-7820HQ'
            CpuCores = 4; InstalledRamGB = 16; RamModules = '2 x 8 GB'; RamSlotsTotal = 2; RamSlotsUsed = 2
            SystemDrive = 'C:'; SystemDiskMediaType = 'SSD'; SystemDiskBusType = 'NVMe'
            SystemDiskModel = 'Internal'; SystemDiskSizeGB = 256; BatteryCount = 1
            Culture = 'en-US'; DecimalSeparator = '.'
        }
        Manual  = [pscustomobject]@{ Asked = $true; Items = @(); ExtraNote = '' }
        Report  = [pscustomobject]@{
            SchemaVersion = 1; GeneratedAt = '2026-10-25'; MachineName = 'TEST-PC'
            OsDescription = 'Windows 10 Pro'; AppVersion = 'test'; RanElevated = $Elevated
            Modules = @(); Verdicts = @($Verdicts); Receipt = @(); Trends = @(); History = $null
            CategoryGrades = [pscustomobject]@{}; OverallGrade = 'Normal'
        }
    }
}

$rules = Read-AuditRules (Join-Path $PSScriptRoot '..\..\audit-kit\audit-rules.json')

# --- The defect from the first fleet audit ----------------------------------

Write-Host 'Drive health ignores drives that are not in the laptop'

$usbName = 'PHD 3.0 Silicon-Power'
$internalName = 'SAMSUNG MZVLB256HBHQ-000L7'

$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'storage.read_errors_uncorrected' 'Bad' 2 (Subject-For $usbName 1000) 'Uncorrected read errors'),
    (New-Verdict 'smart.predict_failure' 'Good' 0 (Subject-For $internalName 256) 'Drive predicts failure')
) @((New-Disk $usbName 'USB'), (New-Disk $internalName 'NVMe'))) $rules

Check 'the laptop is not condemned for the audit drive' { $row.DriveHealth -eq 'Good' }
Check 'no "Replace the drive" for a drive that is not in it' { $row.Reasons -notmatch 'Replace the drive' }
Check 'but the USB drive is still reported, not silently dropped' { $row.Notes -match [regex]::Escape($usbName) }
Check 'and the note says why it was not counted' { $row.Notes -match 'plugged in over USB' }

# --- The other direction: a real internal fault must still land --------------

Write-Host 'A fault on the laptop''s own drive still condemns it'

$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'smart.reallocated' 'Bad' 5 (Subject-For 'Newsmy SSD 240GB' 240) 'Reallocated sectors')
) @((New-Disk 'Newsmy SSD 240GB' 'SATA'))) $rules

Check 'an internal SATA drive is graded' { $row.DriveHealth -eq 'Bad' }
Check 'and the fix names it' { $row.Reasons -match 'Replace the drive' }

# --- An unreadable bus must not switch the check off ------------------------

Write-Host 'A drive whose bus is unknown is still checked'

foreach ($bus in @('', 'RAID', 'SAS', 'Unknown')) {
    $row = Get-AuditRow (New-Bundle @(
        (New-Verdict 'smart.reallocated' 'Bad' 9 (Subject-For 'Mystery Drive' 500) 'Reallocated sectors')
    ) @((New-Disk 'Mystery Drive' $bus))) $rules
    Check "bus '$bus' is treated as internal, not skipped" { $row.DriveHealth -eq 'Bad' }
}

# --- Similar names must not be confused for one another ---------------------

Write-Host 'A USB drive does not mask a similarly named internal one'

$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'smart.reallocated' 'Bad' 4 (Subject-For 'Samsung SSD Pro' 500) 'Reallocated sectors')
) @((New-Disk 'Samsung SSD' 'USB'), (New-Disk 'Samsung SSD Pro' 'SATA'))) $rules

Check '"Samsung SSD" over USB does not excuse "Samsung SSD Pro" inside' { $row.DriveHealth -eq 'Bad' }

# --- Nothing but the audit drive is NOT a clean bill of health --------------

Write-Host 'Reading only the audit drive means drive health was not read'

$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'smart.predict_failure' 'Good' 0 (Subject-For $usbName 1000) 'Drive predicts failure')
) @((New-Disk $usbName 'USB'))) $rules

Check 'drive health reads "Not read", never "Good"' { $row.DriveHealth -eq 'Not read' }
Check 'and the row says so out loud' { $row.Reasons -match 'NOT checked' }
Check 'so the verdict is CHECK AGAIN' { $row.Verdict -eq 'CHECK AGAIN' }

# --- A battery reading nobody can trust -------------------------------------

Write-Host 'A battery gauge that reports more than the pack can hold'

# The app leaves it ungraded (Unknown); the kit must not print it as a measurement.
$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'battery.capacity_pct' 'Unknown' 109.7 'Battery' 'Battery capacity vs design')
) @((New-Disk 'Internal' 'NVMe'))) $rules

Check 'the manager is not shown 109.7% as if it were measured' { $row.Battery -ne '109.7%' }
Check 'it reads "Not reliable"' { $row.Battery -eq 'Not reliable' }
Check 'and the row asks for another look' { $row.Reasons -match 'NOT established' }

Write-Host 'An older build that graded the impossible figure Good is still not believed'

# The zips already collected were produced before the app learned this. The kit has to read them
# correctly too, or the first fleet audit's results stay wrong for as long as they are kept.
$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'battery.capacity_pct' 'Good' 109.7 'Battery' 'Battery capacity vs design')
) @((New-Disk 'Internal' 'NVMe'))) $rules

Check 'a Good grade on an impossible figure is overruled' { $row.Battery -eq 'Not reliable' }
Check 'and the note says how to get a real reading' { $row.Notes -match 'recalibrate' }

Write-Host 'The margin leaves a fresh pack alone'

$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'battery.capacity_pct' 'Good' 102.5 'Battery' 'Battery capacity vs design')
) @((New-Disk 'Internal' 'NVMe'))) $rules

Check 'a new pack reading 102.5% is still a measurement' { $row.Battery -eq '102.5%' }

Write-Host 'An ordinary graded battery is still reported as a number'

$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'battery.capacity_pct' 'Normal' 77.5 'Battery' 'Battery capacity vs design')
) @((New-Disk 'Internal' 'NVMe'))) $rules

Check 'a graded reading still shows its figure' { $row.Battery -eq '77.5%' }
Check 'and does not ask for a recheck' { $row.Reasons -notmatch 'NOT established' }

Write-Host 'A dead battery is still called out'

$row = Get-AuditRow (New-Bundle @(
    (New-Verdict 'battery.capacity_pct' 'Bad' 4.8 'Battery' 'Battery capacity vs design')
) @((New-Disk 'Internal' 'NVMe'))) $rules

Check 'a 4.8% pack still asks to be replaced' { $row.Reasons -match 'Replace the battery' }

# --- The clock that moved during the check ----------------------------------

Write-Host 'A clock stepped during the check is reported, and changes no verdict'

$internal = @((New-Disk 'Internal' 'NVMe'))
$clean = @((New-Verdict 'smart.predict_failure' 'Good' 0 (Subject-For 'Internal' 256) 'Drive predicts failure'),
           (New-Verdict 'battery.capacity_pct' 'Good' 88 'Battery' 'Battery capacity vs design'))

$calmRow = Get-AuditRow (New-Bundle $clean $internal $true 0) $rules
$row = Get-AuditRow (New-Bundle $clean $internal $true -480) $rules
Check 'the note says the clock moved back' { $row.Notes -match 'clock moved back about 8 minutes' }
Check 'it does not become a reason' { $row.Reasons -notmatch 'clock' }
Check 'the verdict is exactly what it was without the step' { $row.Verdict -eq $calmRow.Verdict }
Check 'and not one reason was added' { $row.Reasons -eq $calmRow.Reasons }
Check 'the run is dated by its finish, which was read after the step' { $row.CheckedAt -eq '2026-10-25 20:03' }

$row = Get-AuditRow (New-Bundle $clean $internal $true 3600) $rules
Check 'a clock pushed forward is reported too' { $row.Notes -match 'clock moved forward about 60 minutes' }

Write-Host 'A clock that behaved says nothing at all'

Check 'no note on a laptop whose clock was right' { $calmRow.Notes -notmatch 'clock moved' }
Check 'and it is dated by its start, as before' { $calmRow.CheckedAt -eq '2026-10-25 20:00' }

$row = Get-AuditRow (New-Bundle $clean $internal $true 119) $rules
Check 'a 119 second wobble is below the notice threshold' { $row.Notes -notmatch 'clock moved' }
$row = Get-AuditRow (New-Bundle $clean $internal $true 120) $rules
Check 'and 120 seconds is the first that is reported' { $row.Notes -match 'clock moved forward about 2 minutes' }

Write-Host 'A zip from before the kit measured the clock still reads'

# A zip from the first fleet audit, field for field: no ClockJumpSeconds at all, and the -316
# it recorded. Serial and computer name are placeholders - this repository is public.
$old = New-Bundle $clean $internal
$old.Kit = [pscustomobject]@{
    KitVersion = '2026-10-23.1'; AppVersion = 'test'; Serial = 'TESTSER'; SerialSource = 'firmware'
    ComputerName = 'TEST-PC'; ResultName = 'test.zip'; StartedAt = '2026-10-23 16:51'
    FinishedAt = '2026-10-23 16:50'; AdminRequested = $true; ScanExitCode = 2
    ScanSeconds = -316; LeftOnLaptop = 'nothing'; ScriptErrors = @()
}
$row = Get-AuditRow $old $rules
Check 'no ClockJumpSeconds field does not throw under strict mode' { $row.Verdict -eq $calmRow.Verdict }
Check 'and it claims nothing about a clock it never measured' { $row.Notes -notmatch 'clock moved' }
Check 'it is still dated by its start' { $row.CheckedAt -eq '2026-10-23 16:51' }

Write-Host 'The jump is measured against a Stopwatch, not a second clock reading'

Check 'the real T470p run reads as the clock going back, not a negative scan' {
    (Measure-ClockJump ([datetime]::new(2026,10,23,16,51,0)) ([datetime]::new(2026,10,23,16,50,0)) 450) -eq -510
}
Check 'a healthy three minute run measures no jump' {
    (Measure-ClockJump ([datetime]::new(2026,10,25,20,0,0)) ([datetime]::new(2026,10,25,20,3,0)) 180.4) -eq 0
}
Check 'no jump produces no sentence' { (Get-ClockJumpText 0) -eq '' }

# audit-laptop.ps1 is a script, not a library: dot-sourcing it would run an audit. Reading it as
# text is the only way a test can stop the Stopwatch quietly turning back into two Get-Date calls.
$laptop = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\audit-kit\audit-laptop.ps1') -Raw
$summary = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\audit-kit\summarize-fleet.ps1') -Raw
Check 'audit-laptop.ps1 times the scan with a Stopwatch' { $laptop -match '\$scanWatch\s*=\s*\[Diagnostics\.Stopwatch\]::StartNew\(\)' }
Check 'audit-laptop.ps1 times the whole run with a Stopwatch' { $laptop -match '\$runWatch\s*=\s*\[Diagnostics\.Stopwatch\]::StartNew\(\)' }
Check 'no elapsed time is two Get-Date readings subtracted' { $laptop -notmatch '\(Get-Date\)\s*-\s*\$scan' }
Check 'and the step itself is written into kit.json' { $laptop -match 'ClockJumpSeconds\s*=' }

# --- A value that is not a number must not upset the summariser -------------

Write-Host 'A metric whose value is text, not a number, is read without error'

# malware.quick_scan_age_days used to serialize Double.MaxValue for "never scanned" and now
# serializes as text. The real JSON carries only the one property that is set, so this fixture
# deliberately has no Number member at all: under Set-StrictMode that is what would throw if the
# kit ever started reaching for .Number on a value that has none.
$textVerdict = [pscustomobject]@{
    Grade  = 'Bad'
    Metric = [pscustomobject]@{
        Id      = 'malware.quick_scan_age_days'
        Label   = 'Days since last quick scan'
        Subject = ''
        Value   = [pscustomobject]@{ Text = 'never run' }
    }
}

$row = Get-AuditRow (New-Bundle @(
    $textVerdict,
    (New-Verdict 'battery.capacity_pct' 'Good' 95 'Battery' 'Battery capacity vs design')
) @((New-Disk 'Internal' 'NVMe'))) $rules

Check 'a report carrying a text value still produces a row' { $row.Battery -eq '95%' }
Check 'and no sentinel number reaches the manager table' { ($row | Format-List | Out-String) -notmatch '17976931348623' }

# --- Disk errors in the event log from drives that are not in the laptop (KIT-005) --------------
# The real case: a T470p sent for a drive check over 47 event-log errors, every one of them on the
# two USB drives plugged in during the audit, while the disk Windows runs from had logged none.

function New-DiskAt([string]$Number, [string]$Name, [string]$Bus) {
    [pscustomobject]@{ DeviceId = $Number; FriendlyName = $Name; MediaType = 'SSD'; BusType = $Bus; Size = 256060514304; HealthStatus = 'Healthy' }
}

# The metric as the app writes it: the split sits in Detail, in DiskEventAttribution's wording.
function New-DiskErrors([int]$Count, [string]$Grade, [string]$ByDrive) {
    $detail = 'Bad blocks, retried I/O, controller resets, or NTFS corruption events. Any growth here deserves attention ' +
        [char]0x2014 + ' and a backup.'
    if ($ByDrive) { $detail += ' ' + $ByDrive }
    [pscustomobject]@{
        Grade  = $Grade
        Metric = [pscustomobject]@{
            Id = 'events.disk_errors_90d'; Label = 'Disk I/O errors (90 days)'; Subject = ''; Detail = $detail
            Value = [pscustomobject]@{ Number = $Count; Text = ''; Flag = $null }
        }
    }
}

$renumbered = ' USB drives take the next free number when plugged in, so these numbers may name drives no longer attached.'
$internalSmart = New-Verdict 'smart.reallocated' 'Good' 0 (Subject-For 'Internal SATA SSD' 240) 'Reallocated sectors'
$t470pDisks = @((New-DiskAt '0' 'Internal SATA SSD' 'SATA'), (New-DiskAt '1' 'Laptop drive in a USB caddy' 'USB'), (New-DiskAt '2' 'Audit drive' 'USB'))

$row = Get-AuditRow (New-Bundle @((New-DiskErrors 47 'Bad' ('By drive: on the disk Windows runs from (disk 0): 0; on other drives: disk 1: 34, disk 2: 13.' + $renumbered)), $internalSmart) $t470pDisks) $rules
Check 'no drive check for errors that were all on USB drives' { $row.Reasons -notmatch 'Disk errors' }
Check 'the USB errors are still reported, with their counts' { $row.Notes -match 'on USB drives \(disk 1: 34, disk 2: 13\)' }
Check 'and the note says they were not counted' { $row.Notes -match 'Not counted against this laptop, disk errors' }

$row = Get-AuditRow (New-Bundle @((New-DiskErrors 25 'Bad' ('By drive: on the disk Windows runs from (disk 0): 12; on other drives: disk 2: 13.' + $renumbered)), $internalSmart) $t470pDisks) $rules
Check 'errors on the disk Windows runs from still send it for a check' { $row.Reasons -match 'own drive: 12 in 90 days' }
Check 'and the USB drive''s share is not added to them' { $row.Reasons -notmatch 'own drive: 25' }

$row = Get-AuditRow (New-Bundle @((New-DiskErrors 3 'Normal' 'By drive: on the disk Windows runs from (disk 0): 3.'), $internalSmart) $t470pDisks) $rules
Check 'a few errors on its own drive are a note, not a reason' { $row.Reasons -notmatch 'Disk errors' }
Check 'and the note gives the count' { $row.Notes -match 'own drive: 3 in 90 days' }

$row = Get-AuditRow (New-Bundle @((New-DiskErrors 9 'Bad' 'By drive: on the disk Windows runs from (disk 0): 0; from controllers or the file system, no disk named: 9.'), $internalSmart) $t470pDisks) $rules
Check 'errors tied to no drive count against the laptop' { $row.Reasons -match 'own drive: 9 in 90 days' }
Check 'and say they are tied to no drive' { $row.Reasons -match '9 not tied to any drive' }

$desktopDisks = @((New-DiskAt '0' 'Internal NVMe' 'NVMe'), (New-DiskAt '1' 'Internal hard disk' 'SATA'))
$row = Get-AuditRow (New-Bundle @((New-DiskErrors 20 'Bad' ('By drive: on the disk Windows runs from (disk 0): 0; on other drives: disk 1: 20.' + $renumbered)), $internalSmart) $desktopDisks) $rules
Check 'a second internal drive''s errors count' { $row.Reasons -match 'own drive: 20 in 90 days' }

$row = Get-AuditRow (New-Bundle @((New-DiskErrors 8 'Bad' ('By drive: on the disk Windows runs from (disk 0): 0; on other drives: disk 3: 8.' + $renumbered)), $internalSmart) $t470pDisks) $rules
Check 'errors on a drive no longer attached are not counted' { $row.Reasons -notmatch 'Disk errors' }
Check 'but are reported as such' { $row.Notes -match 'not attached during the audit \(disk 3: 8\)' }

# The other direction: whenever the split cannot be trusted, every error still counts.
$row = Get-AuditRow (New-Bundle @((New-DiskErrors 47 'Bad' 'By drive: on the disk Windows runs from (disk 0): 0; on other drives: disk 1: 34.'), $internalSmart) $t470pDisks) $rules
Check 'a split that does not add up to the total falls back to the total' { $row.Reasons -match 'Disk errors in the event log, check that drive' }

$row = Get-AuditRow (New-Bundle @((New-DiskErrors 47 'Bad' ''), $internalSmart) $t470pDisks) $rules
Check 'no split at all falls back to the total' { $row.Reasons -match 'Disk errors in the event log, check that drive' }

$noDetail = [pscustomobject]@{
    Grade  = 'Bad'
    Metric = [pscustomobject]@{ Id = 'events.disk_errors_90d'; Label = 'Disk I/O errors (90 days)'; Subject = ''; Value = [pscustomobject]@{ Number = 47 } }
}
$row = Get-AuditRow (New-Bundle @($noDetail, $internalSmart) $t470pDisks) $rules
Check 'a metric with no Detail field (the JSON omits empty ones) falls back, without throwing' { $row.Reasons -match 'Disk errors in the event log, check that drive' }

$row = Get-AuditRow (New-Bundle @((New-DiskErrors 47 'Bad' ('By drive: on the disk Windows runs from (disk 0): 0; on other drives: disk 1: 34, disk 2: 13.' + $renumbered)), $internalSmart) @()) $rules
Check 'with no drive list, nothing is excluded on no evidence' { $row.Reasons -match 'Disk errors in the event log, check that drive' }

$storageRules = Get-Content (Join-Path $PSScriptRoot '../../src/PcHealth.Core/Rules/storage.json') -Raw | ConvertFrom-Json
$normalBand = @(@($storageRules.rules | Where-Object { $_.id -eq 'rule.storage.disk-events' })[0].bands | Where-Object { $_.grade -eq 'Normal' })[0]
Check 'the kit''s disk-error threshold matches the app''s rule' { [int]$normalBand.lte -eq $script:DiskErrorsNormalMax }

# --- The app's console output is UTF-8, and everything that shows it decodes UTF-8 (KIT-006) ----
# On the laptops every progress line read "System overview u-grave running": the app wrote its em
# dash as byte 0x97 (ANSI code page) and the kit decoded it with the console's. Writer and readers
# live in different files, so both halves are held here; the daily smoke checks the real bytes.
Check 'audit-laptop.ps1 decodes the app''s output as UTF-8' { $laptop -match 'StreamReader\(\$stream, \(New-Object Text\.UTF8Encoding \$false\)\)' }
Check 'and never with the console''s code page' { $laptop -notmatch 'StreamReader\(\$stream, \[Console\]::OutputEncoding\)' }
$collect = Get-Content (Join-Path $PSScriptRoot '../../collect-results.ps1') -Raw
Check 'collect-results.ps1 reads the console output as UTF-8' { $collect -match 'Get-Content \$stdout -Tail 12 -Encoding UTF8' }
$headless = Get-Content (Join-Path $PSScriptRoot '../../src/PcHealth.App/HeadlessScan.cs') -Raw
Check 'the app writes its console output as UTF-8' { $headless -match 'Console\.SetOut\(new StreamWriter\(Console\.OpenStandardOutput\(\), new UTF8Encoding\(false\)\)' }
Check 'and its error output too, where a crash report goes' { $headless -match 'Console\.SetError\(new StreamWriter\(Console\.OpenStandardError\(\), new UTF8Encoding\(false\)\)' }
Check 'a crash is written by the app through that writer, not left to the runtime' { $headless -match 'catch \(Exception e\)\s*\{\s*Console\.Error\.WriteLine\(\$"Unhandled exception\. \{e\}"\);\s*return ExitCrashed;' }

# --- A kit window closed before the last step must neither lose the laptop nor pass it (KIT-007)
# On 2026-10-26 the owner closed the window at the first question, and a complete elevated scan of
# the T470p was lost: the zip was written only in the last step. The kit now writes kit.json at
# every stage (started, scanned, answering, complete), saves each answer as it is given (a "no"
# before its note), saves the zip before the questions and swaps a zip in only once it is complete.
# The summary uses whichever of a run's zip and work folder got further, groups runs by serial or
# hardware UUID, and shows each laptop's latest finished check. Every case below was produced by a
# review agent that killed real kit runs or built the state on disk, in two rounds.

$win11Ok = [pscustomobject]@{
    Grade  = 'Good'
    Metric = [pscustomobject]@{ Id = 'os.win11_ready'; Label = 'Can move to Windows 11'; Subject = ''; Value = [pscustomobject]@{ Number = $null; Text = ''; Flag = $true } }
}
function New-ReadyBundle { New-Bundle @((New-Verdict 'battery.capacity_pct' 'Good' 95 'Battery' 'Battery capacity vs design'), $internalSmart, $win11Ok) $t470pDisks }
function Set-Stage($Bundle, [string]$Stage) { $Bundle.Kit | Add-Member -NotePropertyName Stage -NotePropertyValue $Stage -Force; return $Bundle }
function Remove-Report($Bundle) { $Bundle.Report = $null; return $Bundle }
function Set-FromFolder($Bundle) { $Bundle | Add-Member -NotePropertyName FromWorkFolder -NotePropertyValue $true -Force; return $Bundle }
function Set-Answers($Bundle, $Items) { $Bundle.Manual.Items = @($Items); return $Bundle }
$swollen = [pscustomobject]@{ Id = 'battery_swelling'; Short = 'Battery swelling'; Question = 'Is the case flat?'; Answer = 'problem'; Note = 'case bulges' }
$flat = [pscustomobject]@{ Id = 'battery_swelling'; Short = 'Battery swelling'; Question = 'Is the case flat?'; Answer = 'ok'; Note = '' }
$batteryRecheck = 'battery swelling question was not answered'

# -- Get-AuditRow on one run --------------------------------------------------

$baseRow = Get-AuditRow (Set-Answers (New-ReadyBundle) @($flat)) $rules
Check 'the fixture these checks start from is READY, so a change of verdict means something' { $baseRow.Verdict -eq 'READY' }
Check 'a zip from before KIT-007, with no Stage, reads as finished' { "$($baseRow.Notes) $($baseRow.Reasons)" -notmatch 'closed|zip was not written' }

$row = Get-AuditRow (Set-Answers (Set-Stage (New-ReadyBundle) 'complete') @($flat)) $rules
Check 'a run that reached its last step says nothing about closing' { "$($row.Notes) $($row.Reasons)" -notmatch 'closed|zip was not written' }

$row = Get-AuditRow (Set-Stage (New-ReadyBundle) 'scanned') $rules
Check 'closed after the scan, before any answer: said in a note' { $row.Notes -match 'before any question was answered' }
Check 'and the unanswered battery question asks for a look, so not READY' { $row.Verdict -eq 'CHECK AGAIN' -and $row.Reasons -match $batteryRecheck }

$row = Get-AuditRow (Set-Answers (Set-Stage (New-ReadyBundle) 'answering') @($swollen)) $rules
Check 'closed after the battery answer: the swollen battery still counts' { $row.Verdict -eq 'FIX FIRST' -and $row.Reasons -match 'SWOLLEN BATTERY' }
Check 'and the note says only the answers given before are counted' { $row.Notes -match 'only the answers given before that are counted' }
$b = Set-Answers (Set-Stage (New-ReadyBundle) 'answering') @($flat)
$b.Manual | Add-Member -NotePropertyName Total -NotePropertyValue 11 -Force
$row = Get-AuditRow $b $rules
Check 'a run that recorded how many questions there were says how many were not reached' { $row.Notes -match '10 of 11 physical checks were not reached' }
Check 'and the battery question was answered, so it does not ask about it' { $row.Reasons -notmatch $batteryRecheck }

$b = Remove-Report (Set-Stage (New-ReadyBundle) 'started')
$b.Kit.ScanExitCode = $null
$row = Get-AuditRow $b $rules
Check 'closed during the scan with no report: CHECK AGAIN' { $row.Verdict -eq 'CHECK AGAIN' -and $row.Reasons -match 'closed before the scan was saved' }
Check 'it says the scan did not finish, with no blank exit code' { $row.Reasons -match 'scan did not finish' -and $row.Reasons -notmatch 'exit code \)' }
Check 'and does not blame the battery wear or Windows for a scan that never ran' { $row.Reasons -notmatch 'battery wear|Windows has not assessed' }
$b = Remove-Report (Set-Stage (New-ReadyBundle) 'started'); $b.Machine = $null
$row = Get-AuditRow $b $rules
Check 'a run closed before anything was read shows the battery as not read, not "No battery"' { $row.Battery -eq 'Not read' -and $row.Notes -notmatch 'SSD or a hard disk' }

$b = Set-Stage (New-ReadyBundle) 'started'; $b.Kit.ScanExitCode = $null   # a closed window never records it
$row = Get-AuditRow $b $rules
Check 'closed during the scan, the app finished it by itself: its report stands, and says so' { $row.Notes -match 'finished the scan by itself' -and $row.Reasons -notmatch 'closed before the scan was saved' }

$row = Get-AuditRow (Set-FromFolder (Set-Answers (Set-Stage (New-ReadyBundle) 'complete') @($flat))) $rules
Check 'a finished run whose final zip was not written: read from its folder, verdict stands' { $row.Verdict -eq 'READY' -and $row.Notes -match 'final zip was not written' }

$b = Set-FromFolder (New-ReadyBundle); $b.Kit = $null
$row = Get-AuditRow $b $rules
Check 'an older kit''s folder whose scan finished: the report stands, the missing answers are said' { $row.Notes -match 'the scan finished, but no answers were saved' }
Check 'and its unanswered battery question asks for a look' { $row.Verdict -eq 'CHECK AGAIN' -and $row.Reasons -match $batteryRecheck }
$b = Set-Answers (Set-FromFolder (New-ReadyBundle)) @($swollen); $b.Kit = $null
$row = Get-AuditRow $b $rules
Check 'a folder whose kit.json could not be read but which holds answers counts them' { $row.Reasons -match 'SWOLLEN BATTERY' -and $row.Notes -match 'only the answers given' }
$b = Remove-Report (Set-FromFolder (New-ReadyBundle)); $b.Kit = $null
$row = Get-AuditRow $b $rules
Check 'an older kit''s folder with no report: CHECK AGAIN' { $row.Verdict -eq 'CHECK AGAIN' -and $row.Reasons -match 'closed before the scan was saved' }

# Ctrl+C and errors run the kit's last step, which used to record "complete" whatever happened: Ctrl+C
# at the battery question then gave READY on a question nobody answered. It now records the stage
# reached and how the run stopped (StoppedEarly), and the battery rule looks at the answers alone.
function Set-Field($Object, [string]$Name, $Value) { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force }
function New-NotAsked($Bundle) { $Bundle.Manual = [pscustomobject]@{ Asked = $false; Items = @(); ExtraNote = '' }; return $Bundle }
$b = New-NotAsked (Set-Stage (New-ReadyBundle) 'scanned'); Set-Field $b.Kit 'StoppedEarly' 'interrupted'; Set-Field $b.Kit 'SkipQuestions' $false
$row = Get-AuditRow $b $rules
Check 'Ctrl+C at the battery question: CHECK AGAIN, and the note says Ctrl+C' {
    $row.Verdict -eq 'CHECK AGAIN' -and $row.Reasons -match $batteryRecheck -and $row.Notes -match 'stopped \(Ctrl\+C\) after the scan was saved'
}
$b = Remove-Report (New-NotAsked (Set-Stage (New-ReadyBundle) 'started')); $b.Kit.ScanExitCode = $null; Set-Field $b.Kit 'StoppedEarly' 'error'
$row = Get-AuditRow $b $rules
Check 'an error before the scan was saved says so' { $row.Reasons -match 'The kit stopped on an error before the scan was saved' }
$b = New-NotAsked (Set-Stage (New-ReadyBundle) 'complete'); Set-Field $b.Kit 'SkipQuestions' $true
$row = Get-AuditRow $b $rules
Check 'questions skipped on purpose (-SkipQuestions): READY, with the checks noted as not done' { $row.Verdict -eq 'READY' -and $row.Notes -match 'Physical checks not done' }
$b = New-NotAsked (Set-Stage (New-ReadyBundle) 'complete'); Set-Field $b.Kit 'SkipQuestions' $false
$row = Get-AuditRow $b $rules
Check 'a run recorded as complete that never asked the battery question is not READY' { $row.Verdict -eq 'CHECK AGAIN' -and $row.Reasons -match $batteryRecheck }
$row = Get-AuditRow (New-NotAsked (New-ReadyBundle)) $rules
Check 'a zip from before KIT-007 without answers reads as it always did' { $row.Verdict -eq 'READY' -and $row.Notes -match 'Physical checks not done' }
$b = Set-Answers (Set-Stage (New-ReadyBundle) 'scanned') @($swollen)
$row = Get-AuditRow $b $rules
Check 'an answer saved beside a kit.json still saying "scanned" counts, and is read as answering' {
    $row.Reasons -match 'SWOLLEN BATTERY' -and $row.Notes -match 'during the questions' -and $row.Notes -notmatch 'before any question'
}
Check 'and that folder ranks above the early zip of its run' { (Get-StageRank $b) -eq 3 -and (Get-StageRank (Set-Stage (New-ReadyBundle) 'scanned')) -eq 2 }
$standIn = [pscustomobject]@{ Kit = [pscustomobject]@{ Serial = 'X'; ComputerName = 'PC'; ResultName = 'r'; StartedAt = ''; AppVersion = '' }; Manual = $null; Report = $null }
Check 'an empty work folder, given the summary''s stand-in kit.json, has not finished anything' { (Get-StageRank $standIn) -eq 0 }
$b = Set-Answers (Set-Stage (New-ReadyBundle) 'answering') @($flat); Set-Field $b.Manual 'Total' 1; Set-Field $b.Kit 'StoppedEarly' 'interrupted'
$row = Get-AuditRow $b $rules
Check 'stopped after every question was answered: the note says so, not that answers are missing' { $row.Notes -match 'after every question was answered' -and $row.Notes -notmatch 'only the answers given' }
$b = Set-Stage (New-ReadyBundle) 'started'; Set-Field $b.Kit 'StoppedEarly' 'error'
$row = Get-AuditRow $b $rules
Check 'an error after the scan returned, before it was saved, is said as that, in one note' {
    $row.Notes -match 'stopped on an error after the scan finished, before it was saved' -and $row.Notes -notmatch 'scan; the app'
}
$b = New-NotAsked (Set-Stage (New-ReadyBundle) 'scanned'); Set-Field $b.Kit 'StoppedEarly' 'error'; Set-Field $b.Kit 'SkipQuestions' $true
$row = Get-AuditRow $b $rules
Check 'a run with the questions skipped on purpose is not said to have stopped before them' { $row.Notes -match 'after the scan was saved' -and $row.Notes -notmatch 'before any question' }

$tmpDir = Join-Path ([IO.Path]::GetTempPath()) ('kit-tests-tmp-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $tmpDir 'manual-checks.json.tmp'), (ConvertTo-Json -InputObject ([pscustomobject]@{ Asked = $true; Items = @($swollen); ExtraNote = '' }) -Depth 6))
    [IO.File]::WriteAllText((Join-Path $tmpDir 'kit.json'), '{"Stage": "answering"}')
    [IO.File]::WriteAllText((Join-Path $tmpDir 'kit.json.tmp'), '{"Stage": "compl')
    $folder = Read-AuditFolder $tmpDir
    Check 'a file caught between the delete and the rename of its rewrite is read from its .tmp' { @($folder.Manual.Items)[0].Answer -eq 'problem' }
    Check 'and a .tmp beside its complete file is left alone' { $folder.Kit.Stage -eq 'answering' }
} finally { Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue }

# What the fleet summary gives Get-AuditRow for a laptop checked more than once.
$b = New-ReadyBundle
Set-Field $b 'MergedAnswers' @([pscustomobject]@{ Id = 'battery_swelling'; Short = 'Battery swelling'; Question = 'q'; Answer = 'problem'; Note = 'case bulges'; From = '2026-10-26 09:00' })
$row = Get-AuditRow $b $rules
Check 'an answer from another check counts, and says which check' {
    $row.Verdict -eq 'FIX FIRST' -and $row.Reasons -match 'SWOLLEN BATTERY.*answered at the 2026-10-26 09:00 check' -and $row.Notes -match 'Answers from the 2026-10-26 09:00 check'
}
$b = Set-Answers (New-ReadyBundle) @($flat); Set-Field $b 'CarriedFix' @('Replace the drive: X (found by the 2026-10-26 11:00 check)')
$row = Get-AuditRow $b $rules
Check 'a reason carried from another check decides the verdict too' { $row.Verdict -eq 'FIX FIRST' -and $row.Reasons -match 'found by the 2026-10-26 11:00 check' }
$badSmart = New-Verdict 'smart.reallocated' 'Bad' 812 (Subject-For 'Internal SATA SSD' 240) 'Reallocated sectors'
$b = Set-Answers (New-ReadyBundle) @($swollen); $b.Report.Verdicts = @($b.Report.Verdicts | Where-Object { $_.Metric.Id -ne 'smart.reallocated' }) + @($badSmart)
$row = Get-AuditRow $b $rules
Check 'the scan''s own reasons are returned apart from the answers, for the summary to carry' {
    @($row.ScanFix | Where-Object { $_ -match '^Replace the drive' }).Count -eq 1 -and @($row.DriveFix).Count -eq 1 -and
        @($row.ScanFix | Where-Object { $_ -match 'SWOLLEN' }).Count -eq 0 -and $row.DriveRead
}

# -- The helpers the summary picks with ----------------------------------------

$b = New-ReadyBundle; $b.Kit = $null
Check 'progress without kit.json: answers saved means answering' { (Get-StageRank (Set-Answers $b @($flat))) -eq 3 }
$b = New-ReadyBundle; $b.Kit = $null; $b.Manual = $null
Check 'progress without kit.json: a report means the scan was saved' { (Get-StageRank $b) -eq 2 }
$b = Remove-Report (New-ReadyBundle); $b.Kit = $null; $b.Manual = $null
Check 'progress without kit.json and nothing else: lowest' { (Get-StageRank $b) -eq 0 }
Check 'a zip from an older kit, kit.json but no Stage, counts as finished' { (Get-StageRank (New-ReadyBundle)) -eq 4 }

$p = Get-ResultNameParts 'AB_12_PC-8_2026-10-26_0700' 'PC-8'
Check 'a serial containing "_" is read from the right of the name' { $p.Serial -eq 'AB_12' -and $p.Computer -eq 'PC-8' -and $p.StartedAt -eq '2026-10-26 07:00' }
$p = Get-ResultNameParts 'AB_12_PC-8_2026-10-26_0700' ''
Check 'and also when the computer name is not known' { $p.Serial -eq 'AB_12' -and $p.Computer -eq 'PC-8' }
$p = Get-ResultNameParts 'NO-SERIAL_PC-9_2026-10-26_130049' ''
Check 'NO-SERIAL means no serial, and a seconds suffix still dates the run' { $p.Serial -eq '' -and $p.Computer -eq 'PC-9' -and $p.StartedAt -eq '2026-10-26 13:00' }
$p = Get-ResultNameParts 'not-a-result-name' ''
Check 'a name the kit did not make gives no date rather than a wrong one' { $p.StartedAt -eq '' }

Check 'a typed serial and its file-name form are the same laptop' { (Get-SerialKey 'PF 0ABC12') -eq (Get-SerialKey 'pf-0abc12') }
Check 'a typed "?" or "#" names no laptop' { (Get-SerialKey '?') -eq '' -and (Get-SerialKey '#') -eq '' }
Check 'nor does a placeholder serial, in any letter case' { (Get-SerialKey 'Not Available') -eq '' -and (Get-SerialKey 'unknown') -eq '' -and (Get-SerialKey 'TO BE FILLED BY O.E.M.') -eq '' }
Check 'a result''s fingerprint does not depend on letter case, and gives away nothing of its name' {
    $h = Get-RunHash 'PF0ABC12_PC-1_2026-10-26_0900'
    $h -eq (Get-RunHash 'pf0abc12_pc-1_2026-10-26_0900') -and $h -match '^[0-9a-f]{16}$' -and $h -ne (Get-RunHash 'PF0ABC12_PC-1_2026-10-26_0901')
}
# Synthetic: a real laptop's UUID identifies it, and this repository is public.
$mach = [pscustomobject]@{ MachineUuid = 'abcdef01-2345-6789-abcd-ef0123456789' }
Check 'the hardware UUID comes from machine.json, in one letter case' { (Get-HardwareUuid $mach $null) -eq 'ABCDEF01-2345-6789-ABCD-EF0123456789' }
Check 'and from kit.json when the window was closed before machine.json was written' { (Get-HardwareUuid $null $mach) -eq 'ABCDEF01-2345-6789-ABCD-EF0123456789' }
Check 'a placeholder UUID names nothing' { (Get-HardwareUuid ([pscustomobject]@{ MachineUuid = 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF' }) $null) -eq '' }
Check 'and neither does a computer name alone' { (Get-HardwareUuid ([pscustomobject]@{ ComputerName = 'DESKTOP-IMG' }) $null) -eq '' }

# SerialSource 'firmware' or 'typed'; Recorded: the run's kit records a UUID field (KIT-007 on).
function New-GroupRun([string]$Name, [string]$SerialKey, [string]$Uuid, [string]$Source = 'typed', [bool]$Recorded = $true, [string]$Computer = '') {
    [pscustomobject]@{ Name = $Name; SerialKey = $SerialKey; SerialSource = $Source; HardwareUuid = $Uuid; UuidRecorded = $Recorded; Computer = $Computer }
}
function Format-Groups($Groups) { (@($Groups | ForEach-Object { (@($_ | ForEach-Object { $_.Name }) | Sort-Object) -join '+' }) | Sort-Object) -join ' | ' }
$grouped = Format-Groups (Group-AuditRuns @(
    (New-GroupRun 'a' 'S1' 'U1' 'firmware'), (New-GroupRun 'd' 'S1' 'U2' 'firmware'),
    (New-GroupRun 'x' 'S2' 'U3' 'firmware'), (New-GroupRun 'y' 'S3' 'U3' 'firmware'),
    (New-GroupRun 'p' 'S4' 'U4'), (New-GroupRun 'q' 'S5' 'U4'), (New-GroupRun 'r' '' 'U4'),
    (New-GroupRun 's' 'S6' 'U5'), (New-GroupRun 't' '' 'U5'),
    (New-GroupRun 'u' 'S7' 'U6' 'firmware'), (New-GroupRun 'v' '' 'U6'),
    (New-GroupRun 'w' 'S8' 'U7'), (New-GroupRun 'z' 'S8' '' 'typed' $false),
    (New-GroupRun 'm' 'S9' 'U8'), (New-GroupRun 'n' 'S9' '' 'typed' $true),
    (New-GroupRun 'e' 'SA' '' 'typed' $false), (New-GroupRun 'f' 'SA' '' 'typed' $false), (New-GroupRun 'g' '' ''),
    (New-GroupRun 'h' 'SB' 'U9' 'firmware'), (New-GroupRun 'i' 'SB' '' 'firmware' $false),
    (New-GroupRun 'j' 'SC' 'UA' 'firmware'), (New-GroupRun 'k' 'SC' 'UB' 'firmware'), (New-GroupRun 'l' 'SC' '' 'firmware' $false)))
Check 'one firmware serial on two system boards is two rows: nothing says a board was replaced rather than shared' { $grouped -match '(^| )a( |$)' -and $grouped -match '(^| )d( |$)' }
Check 'two firmware serials on one UUID are two laptops' { $grouped -match '(^| )x( |$)' -and $grouped -match '(^| )y( |$)' }
Check 'two typed serials on one UUID are two laptops, and a run of it with no serial stands alone' {
    $grouped -match '(^| )p( |$)' -and $grouped -match '(^| )q( |$)' -and $grouped -match '(^| )r( |$)'
}
Check 'one UUID is one laptop when one run had a typed serial and another none' { $grouped -match '(^| )s\+t( |$)' }
Check 'a run with no serial does not join a laptop whose firmware has one: that is another laptop sharing the UUID' { $grouped -match '(^| )u( |$)' -and $grouped -match '(^| )v( |$)' }
Check 'an older kit''s run joins the one laptop with a UUID that carries its serial, and no other' { $grouped -match '(^| )h\+i( |$)' -and $grouped -match '(^| )l( |$)' }
Check 'a run from a kit that recorded no UUID joins the one laptop carrying its serial' { $grouped -match '(^| )w\+z( |$)' }
Check 'but a run whose kit found no usable UUID is not another laptop given the same typed text' { $grouped -match '(^| )m( |$)' -and $grouped -match '(^| )n( |$)' }
$byName = Format-Groups (Group-AuditRuns @(
    (New-GroupRun 'o1' 'SD' 'UC' 'typed' $true 'PC-1'), (New-GroupRun 'o2' '' 'UC' 'typed' $true 'PC-2'), (New-GroupRun 'o3' '' 'UC' 'typed' $true 'PC-1'),
    (New-GroupRun 'q1' '' 'UD' 'typed' $true 'PC-3'), (New-GroupRun 'q2' '' 'UD' 'typed' $true 'PC-3'), (New-GroupRun 'q3' '' 'UD' 'typed' $true 'PC-4'),
    (New-GroupRun 'f1' 'SE' 'UE' 'firmware' $true 'PC-5'), (New-GroupRun 'f2' '' 'UE' 'typed' $true 'PC-6'), (New-GroupRun 'f3' '' 'UE' 'typed' $true 'PC-6')))
Check 'runs with no serial on one UUID are one laptop per computer name, and join a typed serial only under its name' {
    $byName -eq 'f1 | f2+f3 | o1+o3 | o2 | q1+q2 | q3'
}
Check 'all of it at once, with runs of one serial and no UUID together and a run with nothing alone' {
    $grouped -eq 'a | d | e+f | g | h+i | j | k | l | m | n | p | q | r | s+t | u | v | w+z | x | y'
}
Check 'every UUID in the kit and its tests is a placeholder or obviously made up' {
    $synthetic = '^([0-9A-F])\1{7}-(1111|2222)-2222-3333-444444444444$|^ABCDEF01-2345-6789-ABCD-EF0123456789$'
    $files = @(Get-ChildItem (Join-Path $PSScriptRoot '../../audit-kit') -Filter '*.ps1') + @(Get-ChildItem $PSScriptRoot -Filter '*.ps1')
    $found = @($files | ForEach-Object { [regex]::Matches((Get-Content -LiteralPath $_.FullName -Raw), '[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}') } | ForEach-Object { $_.Value.ToUpperInvariant() })
    $real = @($found | Where-Object { $script:PlaceholderUuids -notcontains $_ -and $_ -notmatch $synthetic })
    $found.Count -gt 5 -and $real.Count -eq 0
}

# -- What audit-laptop.ps1 does, held by its source ------------------------------

Check 'audit-laptop.ps1 saves the zip before it asks the questions' {
    $early = $laptop.IndexOf('Save-ResultZip -Early')
    $early -gt 0 -and $early -lt $laptop.IndexOf("Step 'Questions only a person can answer'")
}
Check 'and writes kit.json before the scan starts' {
    $first = $laptop.IndexOf("New-KitRecord 'started'")
    $first -gt 0 -and $first -lt $laptop.IndexOf("Step 'Running the health check'")
}
Check 'and removes the app''s folder from the laptop before the questions too' {
    $clean = $laptop.IndexOf('$left = Remove-AppTraces')
    $clean -gt 0 -and $clean -lt $laptop.IndexOf("Step 'Questions only a person can answer'")
}
Check 'a "no" is saved, and the fire warning shown, before its note is asked' {
    $added = $laptop.IndexOf('$items += $item')
    $saved = $laptop.IndexOf('Save-Answers', $added)
    $warned = $laptop.IndexOf('STOP: a swollen battery', $added)
    $asked = $laptop.IndexOf('What is wrong?', $added)
    $added -gt 0 -and $saved -gt $added -and $saved -lt $asked -and $warned -lt $asked
}
Check 'every progress file is written to a temporary name and moved into place' { $laptop -match 'Move-Item -LiteralPath "\$path\.tmp" -Destination \$path -Force' }
Check 'every zip is written under a partial name and renamed only once complete' {
    $laptop -match '-DestinationPath \$partial' -and $laptop -match 'Move-Item -LiteralPath \$partial -Destination \$zip -Force'
}
Check 'the verdict is shown only after the zip is saved' {
    $save = $laptop.IndexOf('try { Save-ResultZip } catch')
    $save -gt 0 -and $laptop.IndexOf('Step "Verdict:') -gt $save
}
Check 'a run in the same minute as an unfinished one gets a name of its own' { $laptop -match 'Join-Path \$ResultsDir "\.work-\$resultName"' }
Check 'the kit records the hardware UUID' { $laptop -match 'MachineUuid\s+= ' }
Check 'the app is told to keep its temporary files on the stick, and uses the value' {
    $laptop -match "'--temp-dir'" -and $headless -match 'Array\.IndexOf\(args, "--temp-dir"\)' -and $headless -match 'AppTempDir = appTempDir'
}
Check 'a question loop whose input has ended stops instead of spinning' { $laptop -match 'if \(\$null -eq \$raw\) \{ \$inputEnded = \$true; continue \}' }
Check 'the last step records "complete" only for a run that got to its end, and otherwise how it stopped' {
    $final = $laptop.IndexOf('New-KitRecord $(if ($runFinished) { ''complete'' } else { $lastStage })')
    $done = $laptop.IndexOf('$runFinished = $true')
    $final -gt 0 -and $done -gt $laptop.IndexOf("Step 'Questions only a person can answer'") -and $done -lt $laptop.IndexOf('$stoppedByError = $true') -and
        $laptop -match "\`$kit\['StoppedEarly'\]"
}
Check 'every saved answer moves the recorded stage on' { $laptop -match '\$script:lastStage = \$Stage' -and $laptop -match '\$lastStage = ''scanned''' }
Check 'a check is complete once its last question is answered, before the optional remark' {
    $complete = $laptop.IndexOf("Save-Answers 'complete'")
    $complete -gt 0 -and $complete -lt $laptop.IndexOf('Anything else worth noting')
}
Check 'kit.json names the laptop from the moment its work folder exists, made ready before it' {
    $folder = $laptop.IndexOf('New-Item -ItemType Directory -Force -Path $work')
    $ready = $laptop.IndexOf('$startedJson = ConvertTo-Json')
    $write = $laptop.IndexOf('Write-Utf8NoBom (Join-Path $work ''kit.json'') $startedJson')
    $ready -gt 0 -and $ready -lt $folder -and $write -gt $folder -and $write -lt $laptop.IndexOf('Start-Transcript -LiteralPath (Join-Path $work')
}
Check 'kit.json lists the results already on the stick, by fingerprint, read before the run adds its own' {
    $listed = $laptop.IndexOf('$earlierRuns = @($earlierNames | ForEach-Object { Get-RunHash $_ })')
    $listed -gt 0 -and $listed -lt $laptop.IndexOf('New-Item -ItemType Directory -Force -Path $work') -and $laptop -match 'EarlierRuns\s+= \$earlierRuns'
}
Check 'the summary never judges a laptop''s clock by its own' { $summary -notmatch 'AddDays' -and $summary -notmatch 'ClockLimit' }
Check 'Ctrl+C while the app is still scanning keeps the work folder the app writes its report into' {
    $laptop -match '\$keepWork = \$appStillRunning -or \(\$reportAfter -and -not \$reportBefore\)' -and $laptop -match 'if \(-not \$saveError -and -not \$keepWork\)'
}
Check 'a save that works again clears the warning that answers were lost' { $laptop -match '\$script:answersNotSaved = \$false' }
Check 'a clock set before the kit was made is said on screen' { $laptop -match 'before this kit was made' }
Check 'kit.json records the hardware UUID and whether the questions were skipped on purpose' {
    ([regex]::Matches($laptop, 'MachineUuid\s+= \$machineUuid')).Count -eq 2 -and $laptop -match 'SkipQuestions\s+= \[bool\]\$SkipQuestions'
}
Check 'a typed serial with no file-name form, or a placeholder, is refused' { $laptop -match 'return -not \(Test-PlaceholderSerial \$Value\)' -and (Test-PlaceholderSerial '?') -and (Test-PlaceholderSerial 'unknown') }
Check 'the results folder is a full path before the app is given folders inside it' {
    $resolve = $laptop.IndexOf('Resolve-Path -LiteralPath $ResultsDir')
    $resolve -gt 0 -and $resolve -lt $laptop.IndexOf('$work = Join-Path $ResultsDir')
}
Check 'answers the stick will not take are said out loud, at once and at the end' {
    $laptop -match 'Your answers are NOT being saved' -and $laptop -match 'Your answers did NOT reach the stick'
}
Check 'the app refuses a --temp-dir that is rooted but not a full path' { $headless -match 'IsPathFullyQualified\(value\)' -and $headless -notmatch 'IsPathRooted\(value\)' }

# -- The summary itself, end to end, in a child process as the owner runs it ----

$fleetDir = Join-Path ([IO.Path]::GetTempPath()) ('kit-tests-fleet-' + [guid]::NewGuid().ToString('N'))
$stageDir = Join-Path $fleetDir 'staging'
New-Item -ItemType Directory -Force -Path $fleetDir | Out-Null
try {
    function Write-RunFiles([string]$Dir, $Bundle, [switch]$NoKit, [switch]$NoReport, [switch]$HtmlOnly) {
        New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'report') | Out-Null
        $utf8 = New-Object Text.UTF8Encoding $false
        $files = @{ 'machine.json' = $Bundle.Machine; 'manual-checks.json' = $Bundle.Manual; 'disks.json' = @($Bundle.Disks) }
        if (-not $NoKit) { $files['kit.json'] = $Bundle.Kit }
        if (-not $NoReport -and -not $HtmlOnly) { $files['report/pc-health-report.json'] = $Bundle.Report }
        foreach ($name in $files.Keys) { [IO.File]::WriteAllText((Join-Path $Dir $name), (ConvertTo-Json -InputObject $files[$name] -Depth 12), $utf8) }
        if ($HtmlOnly) { [IO.File]::WriteAllText((Join-Path $Dir 'report/pc-health-report.html'), '<html></html>', $utf8) }
    }
    function New-FleetRun([string]$Serial, [string]$Stage, [string]$Computer = '', [string]$Uuid = '', [string]$At = '2026-10-26 09:00', $Answers = @($flat)) {
        $b = Set-Answers (Set-Stage (New-ReadyBundle) $Stage) $Answers
        $b.Kit.Serial = $Serial
        $b.Kit.StartedAt = $At
        if ($Computer) { $b.Kit.ComputerName = $Computer; $b.Machine.ComputerName = $Computer }
        if ($Uuid) { $b.Machine | Add-Member -NotePropertyName MachineUuid -NotePropertyValue $Uuid -Force }
        return $b
    }
    function Save-FleetZip([string]$Name, $Bundle) {
        $Bundle.Kit.ResultName = "$Name.zip"
        $dir = Join-Path $stageDir $Name
        Write-RunFiles $dir $Bundle
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath (Join-Path $fleetDir "$Name.zip")
    }
    function Save-FleetFolder([string]$Name, $Bundle, [switch]$NoKit, [switch]$NoReport, [switch]$HtmlOnly) {
        $Bundle.Kit.ResultName = "$Name.zip"
        Write-RunFiles (Join-Path $fleetDir ".work-$Name") $Bundle -NoKit:$NoKit -NoReport:$NoReport -HtmlOnly:$HtmlOnly
    }
    $brokenZip = [byte[]](80, 75, 3, 4, 0, 0)   # a zip header and nothing else: what an interrupted write leaves
    $noAnswers = @()

    # A: finished.
    Save-FleetZip 'AAA111_PC-ONE_2026-10-26_0900' (New-FleetRun 'AAA111' 'complete')
    # B: closed before any answer: the early zip and the work folder, both "scanned".
    Save-FleetZip 'BBB222_PC-TWO_2026-10-26_1000' (New-FleetRun 'BBB222' 'scanned' -At '2026-10-26 10:00' -Answers $noAnswers)
    Save-FleetFolder 'BBB222_PC-TWO_2026-10-26_1000' (New-FleetRun 'BBB222' 'scanned' -At '2026-10-26 10:00' -Answers $noAnswers)
    # C: an older kit closed at the questions (the T470p case): a folder, no zip, no kit.json.
    Save-FleetFolder 'CCC333_PC-THREE_2026-10-26_1111' (New-FleetRun 'CCC333' 'scanned' -Answers $noAnswers) -NoKit
    # D: closed during the scan: kit.json "started" with no exit code, and no report.
    $d = New-FleetRun 'DDD444' 'started' -At '2026-10-26 12:00' -Answers $noAnswers
    $d.Kit.ScanExitCode = $null
    Save-FleetFolder 'DDD444_PC-FOUR_2026-10-26_1200' $d -NoReport
    # E: closed during the scan, then checked again in the same minute (the kit adds the seconds).
    $e1 = New-FleetRun 'EEE555' 'started' -At '2026-10-26 13:00' -Answers $noAnswers
    $e1.Kit.ScanExitCode = $null
    Save-FleetFolder 'EEE555_PC-FIVE_2026-10-26_1300' $e1 -NoReport
    Save-FleetZip 'EEE555_PC-FIVE_2026-10-26_130049' (New-FleetRun 'EEE555' 'complete' -At '2026-10-26 13:00')
    # F: closed after the answers were saved, before the final zip: early zip "scanned", folder "complete".
    Save-FleetZip 'FFF666_PC-SIX_2026-10-26_1400' (New-FleetRun 'FFF666' 'scanned' -At '2026-10-26 14:00' -Answers $noAnswers)
    Save-FleetFolder 'FFF666_PC-SIX_2026-10-26_1400' (New-FleetRun 'FFF666' 'complete' -At '2026-10-26 14:00' -Answers @($swollen))
    # G: closed while an older kit wrote its final zip in place: a zip nothing can open, folder "complete".
    [IO.File]::WriteAllBytes((Join-Path $fleetDir 'GGG777_PC-SEVEN_2026-10-26_1410.zip'), $brokenZip)
    Save-FleetFolder 'GGG777_PC-SEVEN_2026-10-26_1410' (New-FleetRun 'GGG777' 'complete' -At '2026-10-26 14:10')
    # H: a zip nothing can open, and nothing else.
    [IO.File]::WriteAllBytes((Join-Path $fleetDir 'HHH888_PC-EIGHT_2026-10-26_1420.zip'), $brokenZip)
    # I: closed after the battery answer: early zip "scanned", folder "answering".
    Save-FleetZip 'III999_PC-NINE_2026-10-26_1430' (New-FleetRun 'III999' 'scanned' -At '2026-10-26 14:30' -Answers $noAnswers)
    Save-FleetFolder 'III999_PC-NINE_2026-10-26_1430' (New-FleetRun 'III999' 'answering' -At '2026-10-26 14:30' -Answers @($swollen))
    # J: a save cut short under its partial name: never a result.
    [IO.File]::WriteAllBytes((Join-Path $fleetDir '.partial-JJJ000_PC-TEN_2026-10-26_1500.zip'), $brokenZip)
    # K: no serial, one hardware UUID: closed during the scan, then checked to the end an hour later.
    $k1 = New-FleetRun '' 'started' 'PC-K' 'AAAAAAAA-1111-2222-3333-444444444444' '2026-10-26 16:00' $noAnswers
    $k1.Kit.ScanExitCode = $null
    Save-FleetFolder 'NO-SERIAL_PC-K_2026-10-26_1600' $k1 -NoReport
    Save-FleetZip 'NO-SERIAL_PC-K_2026-10-26_1700' (New-FleetRun '' 'complete' 'PC-K' 'AAAAAAAA-1111-2222-3333-444444444444' '2026-10-26 17:00')
    # L: checked to the end with a swollen battery, then rechecked and closed before any answer.
    Save-FleetZip 'LLL111_PC-L_2026-10-26_1300' (New-FleetRun 'LLL111' 'complete' -At '2026-10-26 13:00' -Answers @($swollen))
    Save-FleetZip 'LLL111_PC-L_2026-10-26_1400' (New-FleetRun 'LLL111' 'scanned' -At '2026-10-26 14:00' -Answers $noAnswers)
    Save-FleetFolder 'LLL111_PC-L_2026-10-26_1400' (New-FleetRun 'LLL111' 'scanned' -At '2026-10-26 14:00' -Answers $noAnswers)
    # M: two runs in one minute: the first closed before any answer (zip), the second, eight seconds
    # later, closed after the battery answer (folder only, its name with seconds).
    Save-FleetZip 'MMM222_PC-M_2026-10-26_1322' (New-FleetRun 'MMM222' 'scanned' -At '2026-10-26 13:22' -Answers $noAnswers)
    Save-FleetFolder 'MMM222_PC-M_2026-10-26_1322' (New-FleetRun 'MMM222' 'scanned' -At '2026-10-26 13:22' -Answers $noAnswers)
    Save-FleetFolder 'MMM222_PC-M_2026-10-26_132209' (New-FleetRun 'MMM222' 'answering' -At '2026-10-26 13:22' -Answers @($swollen))
    # N: two laptops cloned from one image: no serial, same computer name, different UUIDs. The one
    # with the swollen battery was closed after that answer; the other was checked to the end later.
    Save-FleetFolder 'NO-SERIAL_DESKTOP-IMG_2026-10-26_1300' (New-FleetRun '' 'answering' 'DESKTOP-IMG' 'BBBBBBBB-1111-2222-3333-444444444444' '2026-10-26 13:00' @($swollen))
    Save-FleetZip 'NO-SERIAL_DESKTOP-IMG_2026-10-26_1400' (New-FleetRun '' 'complete' 'DESKTOP-IMG' 'CCCCCCCC-1111-2222-3333-444444444444' '2026-10-26 14:00')
    # O: a typed serial with a space: its unreadable first zip is named with the safe form.
    [IO.File]::WriteAllBytes((Join-Path $fleetDir 'OO-001_PC-O_2026-10-26_1300.zip'), $brokenZip)
    Save-FleetZip 'OO-001_PC-O_2026-10-26_1400' (New-FleetRun 'OO 001' 'complete' -At '2026-10-26 14:00')
    # P: an unreadable zip of serial "AB_12" beside a different laptop "AB" with a swollen battery.
    [IO.File]::WriteAllBytes((Join-Path $fleetDir 'AB_12_PC-8_2026-10-26_1300.zip'), $brokenZip)
    Save-FleetZip 'AB_PC-OTHER_2026-10-26_1200' (New-FleetRun 'AB' 'complete' -At '2026-10-26 12:00' -Answers @($swollen))
    # Q: closed while kit.json was being rewritten after the battery answer: kit.json empty.
    Save-FleetZip 'QQQ333_PC-Q_2026-10-26_1500' (New-FleetRun 'QQQ333' 'scanned' -At '2026-10-26 15:00' -Answers $noAnswers)
    Save-FleetFolder 'QQQ333_PC-Q_2026-10-26_1500' (New-FleetRun 'QQQ333' 'answering' -At '2026-10-26 15:00' -Answers @($swollen))
    [IO.File]::WriteAllText((Join-Path $fleetDir '.work-QQQ333_PC-Q_2026-10-26_1500/kit.json'), '')
    # R: closed between the app writing the report's HTML and its JSON.
    Save-FleetFolder 'RRR444_PC-R_2026-10-26_1510' (New-FleetRun 'RRR444' 'started' -At '2026-10-26 15:10' -Answers $noAnswers) -HtmlOnly

    # The third review round: one run must never decide a laptop's row alone.
    function Set-BadDrive($Bundle) { $Bundle.Report.Verdicts = @($Bundle.Report.Verdicts | Where-Object { $_.Metric.Id -ne 'smart.reallocated' }) + @($badSmart); return $Bundle }
    function Set-Unelevated($Bundle) {
        $Bundle.Report.RanElevated = $false
        $Bundle.Report.Verdicts = @($Bundle.Report.Verdicts | Where-Object { $_.Metric.Id -notlike 'smart.*' })
        return $Bundle
    }
    function Set-Typed($Bundle) { $Bundle.Kit.SerialSource = 'typed'; return $Bundle }
    function Set-Recorded($Bundle, [string]$Uuid) { Set-Field $Bundle.Kit 'MachineUuid' $Uuid; Set-Field $Bundle.Machine 'MachineUuid' $Uuid; return $Bundle }
    function Set-KitDate($Bundle, [string]$At) { $Bundle.Kit.KitVersion = '2026-10-26.1'; $Bundle.Kit.StartedAt = $At; return $Bundle }
    $batteryOk = [pscustomobject]@{ Id = 'battery_swelling'; Short = 'Battery swelling'; Question = 'Is the case flat?'; Answer = 'ok'; Note = '' }
    $batterySkipped = [pscustomobject]@{ Id = 'battery_swelling'; Short = 'Battery swelling'; Question = 'Is the case flat?'; Answer = 'not checked'; Note = '' }
    # S: checked to the end, READY; checked again, "the battery bulges", window closed at the note.
    Save-FleetZip 'SSS111_PC-S_2026-10-26_0900' (New-FleetRun 'SSS111' 'complete' -At '2026-10-26 09:00')
    Save-FleetZip 'SSS111_PC-S_2026-10-26_1100' (New-FleetRun 'SSS111' 'scanned' -At '2026-10-26 11:00' -Answers $noAnswers)
    Save-FleetFolder 'SSS111_PC-S_2026-10-26_1100' (New-FleetRun 'SSS111' 'answering' -At '2026-10-26 11:00' -Answers @($swollen))
    # T: checked to the end, READY; checked again, the scan found the drive failing, closed at the first question.
    Save-FleetZip 'TTT222_PC-T_2026-10-26_0900' (New-FleetRun 'TTT222' 'complete' -At '2026-10-26 09:00')
    Save-FleetZip 'TTT222_PC-T_2026-10-26_1100' (Set-BadDrive (New-FleetRun 'TTT222' 'scanned' -At '2026-10-26 11:00' -Answers $noAnswers))
    Save-FleetFolder 'TTT222_PC-T_2026-10-26_1100' (Set-BadDrive (New-FleetRun 'TTT222' 'scanned' -At '2026-10-26 11:00' -Answers $noAnswers))
    # U: "the battery bulges", checked to the end; checked to the end again with the battery question skipped.
    Save-FleetZip 'UUU333_PC-U_2026-10-26_0900' (New-FleetRun 'UUU333' 'complete' -At '2026-10-26 09:00' -Answers @($swollen))
    Save-FleetZip 'UUU333_PC-U_2026-10-26_1100' (New-FleetRun 'UUU333' 'complete' -At '2026-10-26 11:00' -Answers @($batterySkipped))
    # V: the same, but the battery was replaced and the second check says the case is flat.
    Save-FleetZip 'VVV444_PC-V_2026-10-26_0900' (New-FleetRun 'VVV444' 'complete' -At '2026-10-26 09:00' -Answers @($swollen))
    Save-FleetZip 'VVV444_PC-V_2026-10-26_1100' (New-FleetRun 'VVV444' 'complete' -At '2026-10-26 11:00' -Answers @($batteryOk))
    # W: never finished: "the battery bulges" and closed at the note; then closed at the first question.
    Save-FleetZip 'WWW555_PC-W_2026-10-26_0900' (New-FleetRun 'WWW555' 'scanned' -At '2026-10-26 09:00' -Answers $noAnswers)
    Save-FleetFolder 'WWW555_PC-W_2026-10-26_0900' (New-FleetRun 'WWW555' 'answering' -At '2026-10-26 09:00' -Answers @($swollen))
    Save-FleetZip 'WWW555_PC-W_2026-10-26_1100' (New-FleetRun 'WWW555' 'scanned' -At '2026-10-26 11:00' -Answers $noAnswers)
    # X: never finished: the drive failing, closed at the first question; then the admin prompt declined.
    Save-FleetZip 'XXX666_PC-X_2026-10-26_0900' (Set-BadDrive (New-FleetRun 'XXX666' 'scanned' -At '2026-10-26 09:00' -Answers $noAnswers))
    Save-FleetZip 'XXX666_PC-X_2026-10-26_1100' (Set-Unelevated (New-FleetRun 'XXX666' 'scanned' -At '2026-10-26 11:00' -Answers $noAnswers))
    # Y: the drive failing, checked to the end; checked to the end again with the admin prompt declined.
    Save-FleetZip 'YYY777_PC-Y_2026-10-26_0900' (Set-BadDrive (New-FleetRun 'YYY777' 'complete' -At '2026-10-26 09:00'))
    Save-FleetZip 'YYY777_PC-Y_2026-10-26_1100' (Set-Unelevated (New-FleetRun 'YYY777' 'complete' -At '2026-10-26 11:00'))
    # Z: "the battery bulges" saved, window closed before kit.json said "answering".
    Save-FleetZip 'ZZZ888_PC-Z_2026-10-26_1000' (New-FleetRun 'ZZZ888' 'scanned' -At '2026-10-26 10:00' -Answers $noAnswers)
    Save-FleetFolder 'ZZZ888_PC-Z_2026-10-26_1000' (New-FleetRun 'ZZZ888' 'scanned' -At '2026-10-26 10:00' -Answers @($swollen))
    # ZA: window closed while the note was being saved: the answers only in manual-checks.json.tmp.
    Save-FleetZip 'ZAA999_PC-ZA_2026-10-26_1000' (New-FleetRun 'ZAA999' 'scanned' -At '2026-10-26 10:00' -Answers $noAnswers)
    Save-FleetFolder 'ZAA999_PC-ZA_2026-10-26_1000' (New-FleetRun 'ZAA999' 'answering' -At '2026-10-26 10:00' -Answers @($swollen))
    $za = Join-Path $fleetDir '.work-ZAA999_PC-ZA_2026-10-26_1000'
    Move-Item -LiteralPath (Join-Path $za 'manual-checks.json') -Destination (Join-Path $za 'manual-checks.json.tmp')
    # ZB: one laptop, no firmware serial: Enter at the prompt, "the battery bulges", closed; then its label serial
    # typed, and the battery question skipped.
    Save-FleetFolder 'NO-SERIAL_PC-ZB_2026-10-26_0900' (New-FleetRun '' 'answering' 'PC-ZB' 'DDDDDDDD-1111-2222-3333-444444444444' '2026-10-26 09:00' @($swollen))
    Save-FleetZip 'ZBB111_PC-ZB_2026-10-26_1000' (Set-Typed (New-FleetRun 'ZBB111' 'complete' 'PC-ZB' 'DDDDDDDD-1111-2222-3333-444444444444' '2026-10-26 10:00' @($batterySkipped)))
    # ZC: two laptops given the same typed text, "unknown"; one has a swollen battery.
    Save-FleetZip 'unknown_PC-ZC1_2026-10-26_0900' (Set-Typed (New-FleetRun 'unknown' 'complete' 'PC-ZC1' 'EEEEEEEE-1111-2222-3333-444444444444' '2026-10-26 09:00' @($swollen)))
    Save-FleetZip 'unknown_PC-ZC2_2026-10-26_1000' (Set-Typed (New-FleetRun 'unknown' 'complete' 'PC-ZC2' '99999999-1111-2222-3333-444444444444' '2026-10-26 10:00'))
    # ZD: no serial, closed before machine.json was written (kit.json alone); then checked to the end.
    $zd = Join-Path $fleetDir '.work-NO-SERIAL_PC-ZD_2026-10-26_0900'
    New-Item -ItemType Directory -Force -Path $zd | Out-Null
    $zdKit = (New-FleetRun '' 'started' 'PC-ZD' -At '2026-10-26 09:00').Kit
    Set-Field $zdKit 'MachineUuid' '77777777-1111-2222-3333-444444444444'
    $zdKit.ScanExitCode = $null; $zdKit.ResultName = 'NO-SERIAL_PC-ZD_2026-10-26_0900.zip'
    [IO.File]::WriteAllText((Join-Path $zd 'kit.json'), (ConvertTo-Json -InputObject $zdKit -Depth 6))
    Save-FleetZip 'NO-SERIAL_PC-ZD_2026-10-26_1000' (New-FleetRun '' 'complete' 'PC-ZD' '77777777-1111-2222-3333-444444444444' '2026-10-26 10:00')

    # The fourth review round: whose run is whose, and which came later, when hardware or clocks lie.
    # ZE: two laptops whose firmware reports one UUID; their firmware serials tell them apart.
    Save-FleetZip 'ZEA001_PC-ZEA_2026-10-26_0900' (Set-BadDrive (New-FleetRun 'ZEA001' 'complete' 'PC-ZEA' '55555555-1111-2222-3333-444444444444' '2026-10-26 09:00' @($swollen)))
    Save-FleetZip 'ZEB002_PC-ZEB_2026-10-26_1000' (New-FleetRun 'ZEB002' 'complete' 'PC-ZEB' '55555555-1111-2222-3333-444444444444' '2026-10-26 10:00')
    # ZF: two Dells with a blank service tag, so Dell's shared UUID, their label serials typed.
    Save-FleetZip 'ZFA001_PC-ZFA_2026-10-26_0900' (Set-Typed (New-FleetRun 'ZFA001' 'complete' 'PC-ZFA' '4C4C4544-0000-2010-8020-80C04F202020' '2026-10-26 09:00' @($swollen)))
    Save-FleetZip 'ZFB002_PC-ZFB_2026-10-26_1000' (Set-Typed (New-FleetRun 'ZFB002' 'complete' 'PC-ZFB' '4C4C4544-0000-2010-8020-80C04F202020' '2026-10-26 10:00'))
    # ZG: the same with a shared UUID that no list knows.
    Save-FleetZip 'ZGA001_PC-ZGA_2026-10-26_0900' (Set-Typed (New-FleetRun 'ZGA001' 'complete' 'PC-ZGA' '66666666-1111-2222-3333-444444444444' '2026-10-26 09:00' @($swollen)))
    Save-FleetZip 'ZGB002_PC-ZGB_2026-10-26_1000' (Set-Typed (New-FleetRun 'ZGB002' 'complete' 'PC-ZGB' '66666666-1111-2222-3333-444444444444' '2026-10-26 10:00'))
    # ZH: a laptop with a placeholder UUID, typed "unknown2", swollen; another, with a real UUID, typed the same later.
    Save-FleetZip 'unknown2_PC-ZHA_2026-10-26_0900' (Set-Recorded (Set-Typed (New-FleetRun 'unknown2' 'complete' 'PC-ZHA' '' '2026-10-26 09:00' @($swollen))) 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF')
    Save-FleetZip 'unknown2_PC-ZHB_2026-10-26_1000' (Set-Typed (New-FleetRun 'unknown2' 'complete' 'PC-ZHB' '88888888-1111-2222-3333-444444444444' '2026-10-26 10:00'))
    # ZI: swollen; then a new system board, a new UUID, the serial written back, and a finished recheck: repaired.
    Save-FleetZip 'ZII001_PC-ZI_2026-10-26_0900' (New-FleetRun 'ZII001' 'complete' 'PC-ZI' '11111111-1111-2222-3333-444444444444' '2026-10-26 09:00' @($swollen))
    Save-FleetZip 'ZII001_PC-ZI_2026-10-27_0900' (New-FleetRun 'ZII001' 'complete' 'PC-ZI' '22222222-1111-2222-3333-444444444444' '2026-10-27 09:00' @($batteryOk))
    # ZJ: checked to the end; then a window closed in a run's first second: an empty work folder.
    Save-FleetZip 'ZJJ001_PC-ZJ_2026-10-26_0900' (New-FleetRun 'ZJJ001' 'complete' -At '2026-10-26 09:00')
    New-Item -ItemType Directory -Force -Path (Join-Path $fleetDir '.work-ZJJ001_PC-ZJ_2026-10-26_1100') | Out-Null
    # ZK: READY; then checked to the end with the clock reset to 2018: swollen, and the drive failing.
    Save-FleetZip 'ZKK001_PC-ZK_2026-10-26_0900' (Set-KitDate (New-FleetRun 'ZKK001' 'complete' -At '2026-10-26 09:00' -Answers @($batteryOk)) '2026-10-26 09:00')
    Save-FleetZip 'ZKK001_PC-ZK_2018-01-01_0005' (Set-KitDate (Set-BadDrive (New-FleetRun 'ZKK001' 'complete' -At '2018-01-01 00:05' -Answers @($swollen))) '2018-01-01 00:05')
    # ZL: "the battery bulges", checked to the end; a later check said "flat" and was not finished.
    Save-FleetZip 'ZLL001_PC-ZL_2026-10-26_0900' (New-FleetRun 'ZLL001' 'complete' -At '2026-10-26 09:00' -Answers @($swollen))
    Save-FleetZip 'ZLL001_PC-ZL_2026-10-26_1000' (New-FleetRun 'ZLL001' 'scanned' -At '2026-10-26 10:00' -Answers $noAnswers)
    Save-FleetFolder 'ZLL001_PC-ZL_2026-10-26_1000' (New-FleetRun 'ZLL001' 'answering' -At '2026-10-26 10:00' -Answers @($batteryOk))
    # ZM: the drive failing; replaced, and read Good by a finished check; then a finished check without admin rights.
    Save-FleetZip 'ZMM001_PC-ZM_2026-10-26_0900' (Set-BadDrive (New-FleetRun 'ZMM001' 'complete' -At '2026-10-26 09:00'))
    Save-FleetZip 'ZMM001_PC-ZM_2026-10-26_1000' (New-FleetRun 'ZMM001' 'complete' -At '2026-10-26 10:00')
    Save-FleetZip 'ZMM001_PC-ZM_2026-10-26_1100' (Set-Unelevated (New-FleetRun 'ZMM001' 'complete' -At '2026-10-26 11:00'))
    # ZO: never finished, three times, the battery gauge a little lower each time.
    foreach ($o in @(@('0900', 41), @('1000', 40.8), @('1100', 40.5))) {
        $b = New-FleetRun 'ZOO001' 'scanned' -At "2026-10-26 $($o[0].Substring(0, 2)):00" -Answers $noAnswers
        $b.Report.Verdicts = @($b.Report.Verdicts | Where-Object { $_.Metric.Id -ne 'battery.capacity_pct' }) + @(New-Verdict 'battery.capacity_pct' 'Bad' $o[1] 'Battery' 'Battery capacity vs design')
        Save-FleetZip "ZOO001_PC-ZO_2026-10-26_$($o[0])" $b
    }

    # The fifth review round: checks put in order by the stick, not the clock, and UUIDs shared.
    function Set-Earlier($Bundle, [string[]]$Names) { Set-Field $Bundle.Kit 'EarlierRuns' @($Names | ForEach-Object { Get-RunHash $_ }); return $Bundle }
    # ZP: one UUID, two laptops: one with a firmware serial, one with none (Enter at the prompt) and swollen.
    Save-FleetZip 'NO-SERIAL_PC-ZPB_2026-10-26_0900' (New-FleetRun '' 'complete' 'PC-ZPB' '33333333-1111-2222-3333-444444444444' '2026-10-26 09:00' @($swollen))
    Save-FleetZip 'ZPA001_PC-ZPA_2026-10-26_1000' (New-FleetRun 'ZPA001' 'complete' 'PC-ZPA' '33333333-1111-2222-3333-444444444444' '2026-10-26 10:00')
    # ZQ: READY with the clock two days ahead; then, made after it on the stick, "the battery bulges" and closed at the note.
    Save-FleetZip 'ZQQ001_PC-ZQ_2026-10-28_1700' (Set-KitDate (New-FleetRun 'ZQQ001' 'complete' -At '2026-10-28 17:00' -Answers @($batteryOk)) '2026-10-28 17:00')
    Save-FleetZip 'ZQQ001_PC-ZQ_2026-10-26_1600' (Set-Earlier (Set-KitDate (New-FleetRun 'ZQQ001' 'scanned' -At '2026-10-26 16:00' -Answers $noAnswers) '2026-10-26 16:00') @('ZQQ001_PC-ZQ_2026-10-28_1700'))
    Save-FleetFolder 'ZQQ001_PC-ZQ_2026-10-26_1600' (Set-Earlier (Set-KitDate (New-FleetRun 'ZQQ001' 'answering' -At '2026-10-26 16:00' -Answers @($swollen)) '2026-10-26 16:00') @('ZQQ001_PC-ZQ_2026-10-28_1700'))
    # ZR: swollen and the drive failing, the clock reset to 2018; repaired, and checked to the end twice with the clock right.
    Save-FleetZip 'ZRR001_PC-ZR_2018-01-01_0005' (Set-KitDate (Set-BadDrive (New-FleetRun 'ZRR001' 'complete' -At '2018-01-01 00:05' -Answers @($swollen))) '2018-01-01 00:05')
    Save-FleetZip 'ZRR001_PC-ZR_2026-10-26_1000' (Set-Earlier (Set-KitDate (New-FleetRun 'ZRR001' 'complete' -At '2026-10-26 10:00' -Answers @($batteryOk)) '2026-10-26 10:00') @('ZRR001_PC-ZR_2018-01-01_0005'))
    Save-FleetZip 'ZRR001_PC-ZR_2026-10-26_1100' (Set-Earlier (Set-KitDate (New-FleetRun 'ZRR001' 'complete' -At '2026-10-26 11:00' -Answers @($batteryOk)) '2026-10-26 11:00') @('ZRR001_PC-ZR_2018-01-01_0005', 'ZRR001_PC-ZR_2026-10-26_1000'))
    # ZS: swollen, checked to the end; to the end again with the clock reset to 2018, all fine; then with the clock right.
    Save-FleetZip 'ZSS001_PC-ZS_2026-10-26_0900' (Set-KitDate (New-FleetRun 'ZSS001' 'complete' -At '2026-10-26 09:00' -Answers @($swollen)) '2026-10-26 09:00')
    Save-FleetZip 'ZSS001_PC-ZS_2018-01-01_0005' (Set-KitDate (New-FleetRun 'ZSS001' 'complete' -At '2018-01-01 00:05' -Answers @($batteryOk)) '2018-01-01 00:05')
    Save-FleetZip 'ZSS001_PC-ZS_2026-10-26_1100' (Set-KitDate (New-FleetRun 'ZSS001' 'complete' -At '2026-10-26 11:00' -Answers @($batteryOk)) '2026-10-26 11:00')
    # ZT: two internal drives: the first failing, checked to the end; later the second failing too, closed at the first question.
    $twoDrives = @((New-DiskAt '0' 'Internal SATA SSD' 'SATA'), (New-DiskAt '1' 'Internal NVMe' 'NVMe'))
    $badNvme = New-Verdict 'smart.pending_sectors' 'Bad' 8 (Subject-For 'Internal NVMe' 512) 'Pending sectors'
    $zt1 = Set-BadDrive (New-FleetRun 'ZTT001' 'complete' -At '2026-10-26 09:00'); $zt1.Disks = $twoDrives
    Save-FleetZip 'ZTT001_PC-ZT_2026-10-26_0900' $zt1
    $zt2 = Set-BadDrive (New-FleetRun 'ZTT001' 'scanned' -At '2026-10-26 11:00' -Answers $noAnswers); $zt2.Disks = $twoDrives
    $zt2.Report.Verdicts = @($zt2.Report.Verdicts) + @($badNvme)
    Save-FleetZip 'ZTT001_PC-ZT_2026-10-26_1100' $zt2
    # ZU: two laptops whose firmware reports the placeholder serial "Not Available"; one swollen.
    Save-FleetZip 'Not-Available_PC-ZUA_2026-10-26_0900' (New-FleetRun 'Not Available' 'complete' 'PC-ZUA' '44444444-1111-2222-3333-444444444444' '2026-10-26 09:00' @($swollen))
    Save-FleetZip 'Not-Available_PC-ZUB_2026-10-26_1000' (New-FleetRun 'Not Available' 'complete' 'PC-ZUB' 'FFFFFFFF-1111-2222-3333-444444444444' '2026-10-26 10:00')

    # The confirmation round.
    # ZV: two sticks: READY with the clock 20 hours ahead on one; "the battery bulges" and a failing drive,
    # really later, on the other. Each recorded its own stick, so neither lists the other.
    Save-FleetZip 'ZVV001_PC-ZV_2026-10-27_1400' (Set-Earlier (New-FleetRun 'ZVV001' 'complete' -At '2026-10-27 14:00' -Answers @($batteryOk)) @())
    Save-FleetZip 'ZVV001_PC-ZV_2026-10-26_1800' (Set-Earlier (Set-BadDrive (New-FleetRun 'ZVV001' 'complete' -At '2026-10-26 18:00' -Answers @($swollen))) @())
    # ZW: one UUID: a laptop with a firmware serial, and one with none, swollen, then checked again and fine.
    Save-FleetZip 'ZWA001_PC-ZWA_2026-10-26_0900' (New-FleetRun 'ZWA001' 'complete' 'PC-ZWA' '00000000-1111-2222-3333-444444444444' '2026-10-26 09:00')
    Save-FleetZip 'NO-SERIAL_PC-ZWB_2026-10-26_0930' (New-FleetRun '' 'complete' 'PC-ZWB' '00000000-1111-2222-3333-444444444444' '2026-10-26 09:30' @($swollen))
    Save-FleetZip 'NO-SERIAL_PC-ZWB_2026-10-26_1000' (Set-Earlier (New-FleetRun '' 'complete' 'PC-ZWB' '00000000-1111-2222-3333-444444444444' '2026-10-26 10:00' @($batteryOk)) @('ZWA001_PC-ZWA_2026-10-26_0900', 'NO-SERIAL_PC-ZWB_2026-10-26_0930'))
    # ZX: one UUID: a laptop with no serial, swollen; another, its label serial typed, under another computer name.
    Save-FleetZip 'NO-SERIAL_PC-ZXB_2026-10-26_0900' (New-FleetRun '' 'complete' 'PC-ZXB' '11111111-2222-2222-3333-444444444444' '2026-10-26 09:00' @($swollen))
    Save-FleetZip 'ZXA001_PC-ZXA_2026-10-26_1000' (Set-Typed (New-FleetRun 'ZXA001' 'complete' 'PC-ZXA' '11111111-2222-2222-3333-444444444444' '2026-10-26 10:00'))
    Remove-Item -LiteralPath $stageDir -Recurse -Force

    $summarize = Join-Path $PSScriptRoot '../../audit-kit/summarize-fleet.ps1'
    $null = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $summarize -ResultsDir $fleetDir -RulesFile (Join-Path $PSScriptRoot '../../audit-kit/audit-rules.json') 2>&1
    $fleet = @(Import-Csv -LiteralPath (Join-Path $fleetDir 'fleet-summary.csv'))
    function Get-FleetRow([string]$Serial) { @($fleet | Where-Object { $_.Serial -eq $Serial }) }

    Check 'one row per laptop, and nothing from the half-written save' {
        $fleet.Count -eq 60 -and @($fleet | Where-Object { $_.ResultFile -like '*JJJ000*' }).Count -eq 0
    }
    Check 'A: the finished run is READY' { (Get-FleetRow 'AAA111')[0].Verdict -eq 'READY' }
    Check 'B: closed before any answer: one row, and the battery question asks for a look' {
        $r = @(Get-FleetRow 'BBB222'); $r.Count -eq 1 -and $r[0].Verdict -eq 'CHECK AGAIN' -and $r[0].Reasons -match $batteryRecheck
    }
    Check 'C: an older unfinished folder is found, named and dated from its folder' {
        $r = @(Get-FleetRow 'CCC333'); $r.Count -eq 1 -and $r[0].ResultFile -eq '.work-CCC333_PC-THREE_2026-10-26_1111' -and $r[0].CheckedAt -eq '2026-10-26 11:11'
    }
    Check 'C: its scan finished, so the scan is reported, and the unanswered battery question is not passed' {
        $r = (Get-FleetRow 'CCC333')[0]; $r.Notes -match 'the scan finished, but no answers were saved' -and $r.Verdict -eq 'CHECK AGAIN'
    }
    Check 'D: closed during the scan: CHECK AGAIN, named by its folder, no noise' {
        $r = (Get-FleetRow 'DDD444')[0]
        $r.Verdict -eq 'CHECK AGAIN' -and $r.ResultFile -eq '.work-DDD444_PC-FOUR_2026-10-26_1200' -and
            $r.Reasons -match 'closed before the scan was saved' -and $r.Reasons -notmatch 'battery wear|exit code \)'
    }
    Check 'E: closed, then rechecked in the same minute: the finished run is shown' {
        $r = @(Get-FleetRow 'EEE555'); $r.Count -eq 1 -and $r[0].ResultFile -eq 'EEE555_PC-FIVE_2026-10-26_130049.zip' -and $r[0].Verdict -eq 'READY'
    }
    Check 'F: closed before the final zip: the answered swollen battery is not lost' {
        $r = (Get-FleetRow 'FFF666')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY' -and $r.Notes -match 'final zip was not written'
    }
    Check 'G: a zip nothing can open gives way to its work folder' {
        $r = @(Get-FleetRow 'GGG777'); $r.Count -eq 1 -and $r[0].Verdict -eq 'READY' -and $r[0].ResultFile -eq '.work-GGG777_PC-SEVEN_2026-10-26_1410'
    }
    Check 'H: an unreadable zip on its own is a row that says which laptop and which file' {
        $r = @(Get-FleetRow 'HHH888')
        $r.Count -eq 1 -and $r[0].Verdict -eq 'UNREADABLE' -and $r[0].Computer -eq 'PC-EIGHT' -and $r[0].CheckedAt -eq '2026-10-26 14:20' -and
            $r[0].Reasons -match 'HHH888_PC-EIGHT_2026-10-26_1420\.zip'
    }
    Check 'I: closed after the battery answer: it still counts' {
        $r = (Get-FleetRow 'III999')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY' -and $r.Notes -match 'only the answers given'
    }
    Check 'K: one hardware UUID, no serial: the abandoned attempt and the finished check are one laptop' {
        $r = @($fleet | Where-Object { $_.Computer -eq 'PC-K' }); $r.Count -eq 1 -and $r[0].Verdict -eq 'READY'
    }
    Check 'L: a later unfinished check does not erase a finished one''s swollen battery' {
        $r = @(Get-FleetRow 'LLL111')
        $r.Count -eq 1 -and $r[0].Verdict -eq 'FIX FIRST' -and $r[0].Reasons -match 'SWOLLEN BATTERY' -and $r[0].Notes -match 'later check \(2026-10-26 14:00\) was not finished'
    }
    Check 'M: in one minute, the later run''s answered folder beats the earlier run''s zip' {
        $r = @(Get-FleetRow 'MMM222'); $r.Count -eq 1 -and $r[0].Reasons -match 'SWOLLEN BATTERY' -and $r[0].ResultFile -eq '.work-MMM222_PC-M_2026-10-26_132209'
    }
    Check 'N: laptops cloned from one image are two laptops, the swollen one included' {
        $r = @($fleet | Where-Object { $_.Computer -eq 'DESKTOP-IMG' })
        $r.Count -eq 2 -and @($r | Where-Object { $_.Reasons -match 'SWOLLEN BATTERY' }).Count -eq 1
    }
    Check 'O: a typed serial and its file-name form are one laptop: the later readable check replaces the unreadable zip' {
        @($fleet | Where-Object { $_.Serial -eq 'OO 001' -or $_.Serial -eq 'OO-001' }).Count -eq 1 -and (Get-FleetRow 'OO 001')[0].Verdict -eq 'READY'
    }
    Check 'P: an unreadable "AB_12" zip is laptop AB_12, and laptop AB keeps its swollen battery' {
        (Get-FleetRow 'AB_12')[0].Verdict -eq 'UNREADABLE' -and (Get-FleetRow 'AB')[0].Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'Q: a kit.json caught mid-rewrite does not hide the answers saved beside it' {
        $r = (Get-FleetRow 'QQQ333')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'R: a report whose JSON was never written is not linked' {
        -not (Test-Path -LiteralPath (Join-Path $fleetDir 'fleet-reports/RRR444_PC-R_2026-10-26_1510.html'))
    }
    Check 'S: a later unfinished check''s swollen battery is not hidden behind an earlier READY' {
        $r = @(Get-FleetRow 'SSS111')
        $r.Count -eq 1 -and $r[0].Verdict -eq 'FIX FIRST' -and $r[0].Reasons -match 'SWOLLEN BATTERY' -and $r[0].Notes -match 'later check \(2026-10-26 11:00\) was not finished'
    }
    Check 'T: nor is the failing drive its scan found, and the row says which check found it' {
        $r = (Get-FleetRow 'TTT222')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'Replace the drive.*found by the 2026-10-26 11:00 check'
    }
    Check 'U: a later check that skipped the battery question keeps the earlier "the battery bulges"' {
        $r = (Get-FleetRow 'UUU333')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY.*answered at the 2026-10-26 09:00 check'
    }
    Check 'V: and a later "the case is flat" after a repair clears it' { (Get-FleetRow 'VVV444')[0].Verdict -eq 'READY' }
    Check 'W: with no check finished, an earlier check''s swollen battery is not hidden by a later one' {
        $r = (Get-FleetRow 'WWW555')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY' -and $r.Notes -match 'No check of this laptop was finished'
    }
    Check 'X: nor its failing drive, by a later check that could not read the drive' {
        $r = (Get-FleetRow 'XXX666')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'Replace the drive'
    }
    Check 'Y: a finished check that could not read the drive keeps the drive fault an earlier one found' {
        $r = (Get-FleetRow 'YYY777')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'Replace the drive.*could not read drive health'
    }
    Check 'Z: an answer saved just before kit.json said "answering" is not lost to the early zip' {
        $r = (Get-FleetRow 'ZZZ888')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY' -and $r.ResultFile -like '.work-*'
    }
    Check 'ZA: nor are answers caught between the delete and the rename of their file' {
        $r = (Get-FleetRow 'ZAA999')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'ZB: one hardware UUID is one laptop, though one run had a typed serial and one had none' {
        $r = @($fleet | Where-Object { $_.Computer -eq 'PC-ZB' }); $r.Count -eq 1 -and $r[0].Verdict -eq 'FIX FIRST' -and $r[0].Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'ZC: two laptops given the same typed serial are two laptops, the swollen one included' {
        $r = @(Get-FleetRow 'unknown'); $r.Count -eq 2 -and @($r | Where-Object { $_.Reasons -match 'SWOLLEN BATTERY' }).Count -eq 1
    }
    Check 'ZD: a run closed before machine.json was written is still its laptop' {
        $r = @($fleet | Where-Object { $_.Computer -eq 'PC-ZD' }); $r.Count -eq 1 -and $r[0].Verdict -eq 'READY'
    }
    Check 'T: and its Drive health column says a fault was found, and by which check' { (Get-FleetRow 'TTT222')[0].DriveHealth -eq 'Fault found by the 2026-10-26 11:00 check' }
    Check 'ZE: two laptops whose firmware reports one UUID are two laptops, the swollen one included' {
        @(Get-FleetRow 'ZEA001').Count -eq 1 -and @(Get-FleetRow 'ZEB002').Count -eq 1 -and (Get-FleetRow 'ZEA001')[0].Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'ZF: so are two Dells with a blank service tag, their label serials typed' {
        @(Get-FleetRow 'ZFA001').Count -eq 1 -and @(Get-FleetRow 'ZFB002').Count -eq 1 -and (Get-FleetRow 'ZFA001')[0].Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'ZG: and two laptops sharing a UUID that no list knows, their serials typed' {
        @(Get-FleetRow 'ZGA001').Count -eq 1 -and @(Get-FleetRow 'ZGB002').Count -eq 1 -and (Get-FleetRow 'ZGA001')[0].Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'ZH: a laptop with a placeholder UUID is not another laptop given the same typed text' {
        $r = @(Get-FleetRow 'unknown2'); $r.Count -eq 2 -and @($r | Where-Object { $_.Reasons -match 'SWOLLEN BATTERY' }).Count -eq 1
    }
    Check 'ZI: a new system board with the serial written back is a second row, each naming the other, the later READY' {
        $r = @(Get-FleetRow 'ZII001')
        $r.Count -eq 2 -and @($r | Where-Object { $_.Notes -match 'Another row has this serial too' }).Count -eq 2 -and
            @($r | Where-Object { $_.CheckedAt -eq '2026-10-27 09:00' })[0].Verdict -eq 'READY'
    }
    Check 'ZJ: an empty work folder from a window closed in its first second does not replace a finished check' {
        $r = @(Get-FleetRow 'ZJJ001'); $r.Count -eq 1 -and $r[0].Verdict -eq 'READY' -and $r[0].ResultFile -eq 'ZJJ001_PC-ZJ_2026-10-26_0900.zip'
    }
    Check 'ZK: a check with the clock reset to 2018 keeps its swollen battery and drive fault, and the row says why' {
        $r = (Get-FleetRow 'ZKK001')[0]
        $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY' -and $r.Reasons -match 'Replace the drive' -and $r.Notes -match 'clock was wrong'
    }
    Check 'ZL: an unfinished check''s "the case is flat" does not clear a finished check''s "the battery bulges"' {
        $r = (Get-FleetRow 'ZLL001')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY'
    }
    Check 'ZM: a drive replaced and read Good is not brought back by a later check that could not read it' {
        $r = (Get-FleetRow 'ZMM001')[0]; $r.Verdict -eq 'CHECK AGAIN' -and $r.Reasons -notmatch 'Replace the drive'
    }
    Check 'ZO: one battery fault found by three checks is listed once' {
        ([regex]::Matches((Get-FleetRow 'ZOO001')[0].Reasons, 'Replace the battery')).Count -eq 1
    }
    Check 'ZP: a laptop with no serial that shares a UUID with one that has a firmware serial is its own row' {
        $r = @($fleet | Where-Object { $_.Computer -eq 'PC-ZPB' }); $r.Count -eq 1 -and $r[0].Reasons -match 'SWOLLEN BATTERY' -and @(Get-FleetRow 'ZPA001').Count -eq 1
    }
    Check 'ZQ: a check dated two days ahead does not clear the swollen battery a check made after it on the stick found' {
        $r = (Get-FleetRow 'ZQQ001')[0]; $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY' -and $r.Notes -match 'clock was wrong'
    }
    Check 'ZR: a check dated 2018 is cleared by finished checks made after it on the stick: repaired, READY' { (Get-FleetRow 'ZRR001')[0].Verdict -eq 'READY' }
    Check 'ZS: a check dated 2018 does not stop two right clocks showing a repair' { (Get-FleetRow 'ZSS001')[0].Verdict -eq 'READY' }
    Check 'ZT: a second failing drive another check found is not dropped as a repeat of the first' {
        $r = (Get-FleetRow 'ZTT001')[0]; $r.Reasons -match 'Internal NVMe' -and $r.Reasons -match 'Internal SATA SSD'
    }
    Check 'ZU: two laptops reporting the placeholder serial "Not Available" are two laptops' {
        $r = @($fleet | Where-Object { $_.Computer -like 'PC-ZU*' }); $r.Count -eq 2 -and @($r | Where-Object { $_.Reasons -match 'SWOLLEN BATTERY' }).Count -eq 1
    }
    Check 'ZI: and the earlier row says the other was checked after it' {
        @(Get-FleetRow 'ZII001' | Where-Object { $_.CheckedAt -eq '2026-10-26 09:00' })[0].Notes -match 'checked after this one'
    }
    Check 'ZV: checks on two sticks are not put in order by their clocks: the swollen battery and the drive stay' {
        $r = (Get-FleetRow 'ZVV001')[0]
        $r.Verdict -eq 'FIX FIRST' -and $r.Reasons -match 'SWOLLEN BATTERY' -and $r.Reasons -match 'Replace the drive' -and $r.Notes -match 'could not be put in order'
    }
    Check 'ZW: a laptop with no serial on a shared UUID is one row of its own, its repair shown' {
        $r = @($fleet | Where-Object { $_.Computer -eq 'PC-ZWB' }); $r.Count -eq 1 -and $r[0].Verdict -eq 'READY' -and @(Get-FleetRow 'ZWA001').Count -eq 1
    }
    Check 'ZX: a laptop with no serial is not merged into a laptop typed under another computer name' {
        $r = @($fleet | Where-Object { $_.Computer -eq 'PC-ZXB' }); $r.Count -eq 1 -and $r[0].Reasons -match 'SWOLLEN BATTERY' -and @(Get-FleetRow 'ZXA001').Count -eq 1
    }
    Check 'the table never shows the columns the summary only uses to choose' {
        $columns = @($fleet[0].PSObject.Properties | ForEach-Object { $_.Name })
        @($columns | Where-Object { @('Bundle', 'ScanFix', 'ScanReplace', 'DriveFix', 'DriveRead', 'SerialKey', 'SerialSource', 'HardwareUuid', 'UuidRecorded', 'StageRank', 'RunName', 'RunHash', 'EarlierRuns', 'HasEarlierList', 'ClockWrong') -contains $_ }).Count -eq 0
    }
} finally {
    Remove-Item -LiteralPath $fleetDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Result -----------------------------------------------------------------

Write-Host ''
if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) of $($script:Ran) kit checks FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "All $($script:Ran) kit checks passed" -ForegroundColor Green
exit 0
