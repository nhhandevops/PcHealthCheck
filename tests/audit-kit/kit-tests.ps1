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

# --- Result -----------------------------------------------------------------

Write-Host ''
if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) of $($script:Ran) kit checks FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "All $($script:Ran) kit checks passed" -ForegroundColor Green
exit 0
