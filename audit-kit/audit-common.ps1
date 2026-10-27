# Shared by audit-laptop.ps1 and summarize-fleet.ps1, so the verdict shown on a laptop's screen
# and the one in the fleet table are computed by the same code and cannot disagree.
# Windows PowerShell 5.1 compatible and ASCII only: 5.1 reads a script without a byte-order mark
# in the ANSI code page, so any non-ASCII character here would be garbled on some laptops.

$script:AuditKitVersion = '2026-10-26.1'

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

# One of the kit's own files, which it rewrites as a run goes on (KIT-007). It writes "<name>.tmp" in
# full and then moves it over "<name>", and Move-Item in Windows PowerShell 5.1 deletes the old file
# before it renames the new one. A window closed in that gap leaves only the .tmp, complete by then;
# reading nothing would turn an answered "the battery bulges" into no answer at all.
function Read-KitFile([string]$Path) {
    $data = Read-JsonFile $Path
    if ($null -eq $data) { $data = Read-JsonFile "$Path.tmp" }
    return $data
}

# Everything one laptop's run produced, from its unpacked folder.
function Read-AuditFolder([string]$Dir) {
    $reportJson = Get-ChildItem -LiteralPath (Join-Path $Dir 'report') -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    $reportHtml = Get-ChildItem -LiteralPath (Join-Path $Dir 'report') -Filter '*.html' -ErrorAction SilentlyContinue | Select-Object -First 1
    [pscustomobject]@{
        Dir        = $Dir
        Kit        = Read-KitFile (Join-Path $Dir 'kit.json')
        Machine    = Read-KitFile (Join-Path $Dir 'machine.json')
        Manual     = Read-KitFile (Join-Path $Dir 'manual-checks.json')
        Disks      = Read-KitFile (Join-Path $Dir 'disks.json')
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

# Disk errors in the event log above this many in 90 days are a fault, at or below it a note. It
# mirrors rule.storage.disk-events in src/PcHealth.Core/Rules/storage.json (Normal up to 5), and
# kit-tests.ps1 fails if the two ever disagree.
$script:DiskErrorsNormalMax = 5

# The event log's disk errors split by the drive that logged them. The app writes the split into
# the metric's Detail, in fixed English that is never translated (DiskEventAttribution.Describe):
#   "By drive: on the disk Windows runs from (disk 0): 0; on other drives: disk 1: 34, disk 2: 13."
# Returns $null when there is no split, or when its numbers do not add up to the total: the caller
# then counts every error against the laptop, because a sentence that cannot be read must never
# make errors disappear.
function Get-DiskErrorSplit($Verdict) {
    $detail = [string](Get-JsonField $Verdict.Metric 'Detail')
    $total = Get-JsonField (Get-JsonField $Verdict.Metric 'Value') 'Number'
    if (-not $detail -or $null -eq $total) { return $null }
    $byDisk = @{}
    $systemDisk = $null
    $match = [regex]::Match($detail, 'on the disk Windows runs from \(disk (\d+)\): (\d+)')
    if ($match.Success) {
        $systemDisk = [int]$match.Groups[1].Value
        $byDisk[$systemDisk] = [int]$match.Groups[2].Value
    }
    foreach ($list in [regex]::Matches($detail, '(?:on other drives|by disk number): ([^;.]+)')) {
        foreach ($pair in [regex]::Matches($list.Groups[1].Value, 'disk (\d+): (\d+)')) {
            $n = [int]$pair.Groups[1].Value
            $byDisk[$n] = [int]$byDisk[$n] + [int]$pair.Groups[2].Value
        }
    }
    $unattributed = 0
    $match = [regex]::Match($detail, 'no disk named: (\d+)')
    if ($match.Success) { $unattributed = [int]$match.Groups[1].Value }
    if ($byDisk.Count -eq 0 -and $unattributed -eq 0) { return $null }
    $sum = $unattributed
    foreach ($count in $byDisk.Values) { $sum += $count }
    if ($sum -ne [int]$total) { return $null }
    return [pscustomobject]@{ ByDisk = $byDisk; SystemDisk = $systemDisk; Unattributed = $unattributed }
}

# Which of those errors are this laptop's. The disk Windows runs from always is, and so is any disk
# that is attached now over an internal bus. A disk attached over USB is the audit drive or someone's
# backup drive, and a disk number not attached now is a drive that is not in the laptop as it will be
# handed over: both are reported but not counted. Errors the app could not tie to any disk are
# counted, because nothing shows they came from elsewhere (the KIT-003 rule: unknown stays in).
# Returns $null when the drive list is missing, so nothing is excluded on no evidence.
function Get-DiskErrorsAgainstLaptop($Verdict, $Disks) {
    $split = Get-DiskErrorSplit $Verdict
    $diskList = @($Disks | Where-Object { $_ })
    if (-not $split -or $diskList.Count -eq 0) { return $null }
    $busByNumber = @{}
    foreach ($disk in $diskList) { $busByNumber[[string]$disk.DeviceId] = [string]$disk.BusType }
    $own = [int]$split.Unattributed
    $ownParts = New-Object System.Collections.Generic.List[string]
    $external = New-Object System.Collections.Generic.List[string]
    $detached = New-Object System.Collections.Generic.List[string]
    foreach ($n in @($split.ByDisk.Keys | Sort-Object)) {
        $count = [int]$split.ByDisk[$n]
        if ($count -le 0) { continue }
        $text = "disk ${n}: $count"
        if ($n -eq $split.SystemDisk) { $own += $count; $ownParts.Add("$text (the disk Windows runs from)") }
        elseif (-not $busByNumber.ContainsKey([string]$n)) { $detached.Add($text) }
        elseif ($script:ExternalBusTypes -contains $busByNumber[[string]$n]) { $external.Add($text) }
        else { $own += $count; $ownParts.Add($text) }
    }
    if ($split.Unattributed -gt 0) { $ownParts.Add("$($split.Unattributed) not tied to any drive") }
    return [pscustomobject]@{ Own = $own; OwnParts = $ownParts; External = $external; Detached = $detached }
}

# How far a run got, from its kit.json, for choosing between a run's zip and its work folder: the
# fleet summary uses whichever got further. A zip from a kit older than KIT-007 has no Stage, and
# such a kit wrote its zip only at the very end, so it counts as complete. A work folder from such
# a kit is complete if it has a kit.json (written only at the end) and counts for nothing if not.
$script:StageRank = @{ 'started' = 1; 'scanned' = 2; 'answering' = 3; 'complete' = 4 }
# The files beside kit.json count too, whatever it says: the kit saves an answer before it rewrites
# kit.json, so a window closed between the two leaves a kit.json still saying "scanned" beside the
# answer, and that folder must not lose to the early zip, which has no answers. Saved answers mean
# the run was answering; a report means the scan was saved. Nothing at all ranks lowest.
function Get-StageRank($Bundle) {
    $kit = $Bundle.Kit
    $stage = [string](Get-JsonField $kit 'Stage')
    $rank = 0
    if ($stage -and $script:StageRank.ContainsKey($stage)) { $rank = $script:StageRank[$stage] }
    # A real kit.json from before KIT-007, never the summary's stand-in for a missing one: an empty
    # work folder, left by a window closed in the run's first second, has not finished anything.
    elseif (Get-JsonField $kit 'KitVersion') { return 4 }
    if ((Get-JsonField $Bundle.Manual 'Asked') -and @(Get-JsonField $Bundle.Manual 'Items' | Where-Object { $_ }).Count -gt 0) { $rank = [Math]::Max($rank, 3) }
    elseif ($Bundle.Report) { $rank = [Math]::Max($rank, 2) }
    return $rank
}

# A serial or computer name as it appears in a result name: anything but letters, digits, '.', '_'
# and '-' becomes '-', at most 40 characters. The kit names its files with it, and the fleet summary
# groups runs with it, so a typed serial "PF 0ABC12" and its file name "PF-0ABC12" are one laptop.
function ConvertTo-SafeName([string]$Value, [int]$Max = 40) {
    $safe = ($Value.Trim() -replace '[^A-Za-z0-9._-]', '-').Trim('-')
    if ($safe.Length -gt $Max) { $safe = $safe.Substring(0, $Max) }
    return $safe
}

# Which laptop a run belongs to (KIT-007). Two facts can say so: the serial, in its file-name form,
# and the hardware UUID the firmware reports, which kits from KIT-007 on record in kit.json and
# machine.json. Never the computer name: laptops cloned from one image share it. Neither fact can be
# trusted alone: firmware reports placeholder serials, and some UUIDs are shared by many machines.
$script:PlaceholderUuids = @('00000000-0000-0000-0000-000000000000', 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',
    '03000200-0400-0500-0006-000700080009', '00020003-0004-0005-0006-000700080009',
    '4C4C4544-0000-2010-8020-80C04F202020')
# Serials firmware fills in, or a person types, that name no laptop. Compared without letter case.
$script:PlaceholderSerials = @('', '0', 'None', 'Default', 'Default string', 'To be filled by O.E.M.', 'To be filled by O.E.M',
    'System Serial Number', 'Serial Number', 'SerialNumber', 'Chassis Serial Number', 'Base Board Serial Number',
    'Not Specified', 'Not Applicable', 'Not Available', 'N/A', 'NA', 'TBD', 'Unknown', 'OEM', 'INVALID',
    '123456789', '0123456789', '1234567890', '12345678')
function Test-PlaceholderSerial([string]$Value) {
    $v = ([string]$Value).Trim()
    return ($script:PlaceholderSerials -contains $v) -or ($v -match '^[0\s.*xX-]+$') -or -not (ConvertTo-SafeName $v)
}
function Get-SerialKey([string]$Serial) {
    if (Test-PlaceholderSerial $Serial) { return '' }
    return (ConvertTo-SafeName $Serial).ToUpperInvariant()
}
# From machine.json, else kit.json, which has it from the first moment of a run: a window closed
# before machine.json was written must still say which laptop it was.
function Get-HardwareUuid($Machine, $Kit) {
    foreach ($source in @($Machine, $Kit)) {
        $uuid = ([string](Get-JsonField $source 'MachineUuid')).Trim().ToUpperInvariant()
        if ($uuid -and $script:PlaceholderUuids -notcontains $uuid) { return $uuid }
    }
    return ''
}

# The order of a laptop's checks, without its clock (KIT-007). An old laptop's clock comes up reset
# to some year long gone, or set ahead, and a check dated wrongly would otherwise count as the
# earlier or the later one. So each run records, in kit.json EarlierRuns, a short fingerprint of the
# name of every result already on the stick when it started: a run that lists another came after it,
# whatever either clock said. Fingerprints, not names, so a zip carries no other laptop's serial.
function Get-RunHash([string]$Name) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Name.ToUpperInvariant())) } finally { $sha.Dispose() }
    return (($bytes[0..7] | ForEach-Object { $_.ToString('x2') }) -join '')
}
# The results in a folder, by name: "<name>.zip" and ".work-<name>", never ".partial-*.zip".
function Get-ResultNames([string]$Dir) {
    @(Get-ChildItem -LiteralPath $Dir -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PSIsContainer) { if ($_.Name.StartsWith('.work-')) { $_.Name.Substring(6) } }
        elseif ($_.Extension -eq '.zip' -and -not $_.Name.StartsWith('.')) { $_.BaseName }
    } | Where-Object { $_ } | Sort-Object -Unique)
}

# Runs into laptops. Each run carries SerialKey (empty for a placeholder), SerialSource ('firmware',
# 'typed', or '' when not known), HardwareUuid (empty for a placeholder), and UuidRecorded (its kit
# records a UUID field at all: every kit from KIT-007 on, even when the firmware gave none usable).
# A wrong merge hides a laptop, a wrong split shows one twice, so when in doubt, runs stay apart:
# 1. Runs with one usable UUID are one laptop, unless they carry different serials, when several
#    laptops share that UUID: then one laptop per serial. A run with no serial joins only a laptop
#    whose serial was typed (a laptop with no serial in its firmware, typed on one run and skipped
#    on another), and only under the same computer name; a laptop whose firmware has a serial never
#    gives a run without one. Runs with no serial are otherwise one laptop per computer name.
# 2. A run with a serial and no usable UUID joins the one laptop of step 1 carrying its serial, but
#    only if it comes from a kit too old to record a UUID, or is a zip nothing could read: a run
#    whose kit did record one and found none usable is not a laptop that has one. Otherwise, runs of
#    one serial with no UUID are one laptop.
# A run with no serial and no UUID stands alone. The same serial on two system boards (a board
# replaced, or two laptops reporting one serial) gives two laptops, which the fleet summary names on
# each row. Returns a list of lists.
# "foreach ($run in $Runs)", never "@($Runs)": in Windows PowerShell 5.1, @() of a List[object] made
# with New-Object throws "Argument types do not match" (TOOL-004).
function Group-AuditRuns($Runs) {
    $groups = New-Object System.Collections.Generic.List[object]
    function New-Group($First) {
        $group = New-Object System.Collections.Generic.List[object]
        if ($First) { $group.Add($First) }
        $groups.Add($group)
        return ,$group
    }
    $byUuid = @{}; $rest = New-Object System.Collections.Generic.List[object]
    foreach ($run in $Runs) {
        if ($run.HardwareUuid) {
            if (-not $byUuid.ContainsKey($run.HardwareUuid)) { $byUuid[$run.HardwareUuid] = New-Object System.Collections.Generic.List[object] }
            $byUuid[$run.HardwareUuid].Add($run)
        } else { $rest.Add($run) }
    }
    # Runs with no serial and one UUID: the same laptop when they carry the same computer name. One
    # with another name is another laptop sharing that UUID, or this one renamed; two rows then.
    function Get-NameKey($Run) { (ConvertTo-SafeName ([string](Get-JsonField $Run 'Computer'))).ToUpperInvariant() }
    function Add-ByName($Runs, $Into) {
        $byName = @{}
        foreach ($run in $Runs) {
            $name = Get-NameKey $run
            if ($Into -and @($Into | Where-Object { (Get-NameKey $_) -eq $name }).Count -gt 0) { $Into.Add($run); continue }
            if (-not $byName.ContainsKey($name)) { $byName[$name] = New-Group $null }
            $byName[$name].Add($run)
        }
    }
    foreach ($uuid in @($byUuid.Keys)) {
        $serials = @($byUuid[$uuid] | ForEach-Object { $_.SerialKey } | Where-Object { $_ } | Sort-Object -Unique)
        $firmware = @($byUuid[$uuid] | Where-Object { $_.SerialKey -and $_.SerialSource -eq 'firmware' }).Count -gt 0
        $noSerial = New-Object System.Collections.Generic.List[object]
        foreach ($run in $byUuid[$uuid]) { if (-not $run.SerialKey) { $noSerial.Add($run) } }
        if ($serials.Count -eq 0) { Add-ByName $noSerial $null; continue }
        $bySerialHere = @{}
        foreach ($run in $byUuid[$uuid]) {
            if (-not $run.SerialKey) { continue }
            if (-not $bySerialHere.ContainsKey($run.SerialKey)) { $bySerialHere[$run.SerialKey] = New-Group $null }
            $bySerialHere[$run.SerialKey].Add($run)
        }
        # One typed serial: its runs without a serial, under the same computer name, are the same laptop.
        $into = $null   # assigned, not "= if ...": an if-expression would unroll the list
        if ($serials.Count -eq 1 -and -not $firmware) { $into = $bySerialHere[$serials[0]] }
        if ($noSerial.Count -gt 0) { Add-ByName $noSerial $into }
    }
    # Which laptops carry each serial, for step 2: decided before any run of step 2 joins one.
    $owners = @{}
    foreach ($group in $groups) {
        foreach ($key in @($group | ForEach-Object { $_.SerialKey } | Where-Object { $_ } | Sort-Object -Unique)) {
            if (-not $owners.ContainsKey($key)) { $owners[$key] = New-Object System.Collections.Generic.List[object] }
            $owners[$key].Add($group)
        }
    }
    $bySerial = @{}
    foreach ($run in $rest) {
        if (-not $run.SerialKey) { $null = New-Group $run; continue }
        if (-not $run.UuidRecorded -and $owners.ContainsKey($run.SerialKey) -and $owners[$run.SerialKey].Count -eq 1) {
            $owners[$run.SerialKey][0].Add($run)
            continue
        }
        if (-not $bySerial.ContainsKey($run.SerialKey)) { $bySerial[$run.SerialKey] = New-Group $null }
        $bySerial[$run.SerialKey].Add($run)
    }
    return ,$groups
}

# Serial, computer and start time from a result name, <serial>_<computer>_<yyyy-MM-dd>_<HHmm[ss]>,
# read from the right, because ConvertTo-SafeName keeps "_" and a serial may contain one. The
# computer part is matched against the computer name when it is known. Used only when kit.json is
# missing (kits older than KIT-007) or unreadable.
function Get-ResultNameParts([string]$Name, [string]$ComputerName) {
    $parts = $Name -split '_'
    $result = [ordered]@{ Serial = ''; Computer = ''; StartedAt = '' }
    if ($parts.Count -lt 3) { return [pscustomobject]$result }
    $date = $parts[-2]; $time = $parts[-1]
    if ($date -match '^\d{4}-\d{2}-\d{2}$' -and $time -match '^\d{4}') {
        $result.StartedAt = '{0} {1}:{2}' -f $date, $time.Substring(0, 2), $time.Substring(2, 2)
        $prefix = ($parts[0..($parts.Count - 3)]) -join '_'
    } else {
        $prefix = $Name
    }
    $safeComputer = ''
    if ($ComputerName) { $safeComputer = ConvertTo-SafeName $ComputerName }
    if ($safeComputer -and $prefix.EndsWith('_' + $safeComputer)) {
        $result.Serial = $prefix.Substring(0, $prefix.Length - $safeComputer.Length - 1)
        $result.Computer = $ComputerName.Trim()
    } else {
        # The computer is the last part: a Windows computer name practically never holds "_", while
        # a typed serial may. Taking the first part as the serial turned "AB_12" into "AB".
        $cut = $prefix.LastIndexOf('_')
        if ($cut -gt 0) {
            $result.Serial = $prefix.Substring(0, $cut)
            $result.Computer = $prefix.Substring($cut + 1)
        } else {
            $result.Serial = $prefix
        }
    }
    if ($result.Serial -eq 'NO-SERIAL') { $result.Serial = '' }
    return [pscustomobject]$result
}

# The checks whose failure makes a verdict unsafe. Any other check that did not finish is a note.
$script:CriticalModules = @('sys.overview', 'volumes', 'battery', 'disk.smart', 'disk.reliability')

# One row of the fleet table: the facts, and the verdict with every reason for it.
# READY       - nothing found that stops a new employee using it.
# FIX FIRST   - usable after a repair or upgrade the reasons name (battery, drive, memory...).
# REPLACE     - cannot meet the rules at any repair cost (today: a processor Windows 11 refuses).
# CHECK AGAIN - something important was not checked, so no verdict would be honest.
#
# Used on one run, and by the fleet summary on the run it shows for a laptop checked more than once.
# The summary then adds to the bundle: MergedAnswers, the latest real answer to each question from
# any of the laptop's runs (an item with From names the check it came from); CarriedReplace,
# CarriedFix and CarriedRecheck, reasons other runs found that this one could not supersede. The row
# also returns the reasons that came from the scan (ScanReplace, ScanFix, DriveFix) and DriveRead, for
# the summary to carry; it drops them from the table.
function Get-AuditRow($Bundle, $Rules) {
    $kit = $Bundle.Kit; $m = $Bundle.Machine; $report = $Bundle.Report; $manual = $Bundle.Manual
    $replace  = New-Object System.Collections.Generic.List[string]
    $physical = New-Object System.Collections.Generic.List[string]
    $fix      = New-Object System.Collections.Generic.List[string]
    $driveFix = New-Object System.Collections.Generic.List[string]
    $recheck  = New-Object System.Collections.Generic.List[string]
    $notes    = New-Object System.Collections.Generic.List[string]
    $verdicts = @()
    if ($report) { $verdicts = @($report.Verdicts) }

    # --- Physical checks first: a swollen battery outranks everything else on the list.
    $merged = Get-JsonField $Bundle 'MergedAnswers'
    $ownItems = @()
    if ((Get-JsonField $manual 'Asked')) { $ownItems = @(Get-JsonField $manual 'Items' | Where-Object { $_ }) }
    $items = @(if ($null -ne $merged) { $merged | Where-Object { $_ } } else { $ownItems })
    $manualProblems = @()
    if ($items.Count -gt 0 -or (Get-JsonField $manual 'Asked')) {
        $fromOthers = [ordered]@{}
        foreach ($item in $items) {
            $from = [string](Get-JsonField $item 'From')
            if ($from) {
                if (-not $fromOthers.Contains($from)) { $fromOthers[$from] = @() }
                $fromOthers[$from] += [string]$item.Short
            }
            if ($item.Answer -eq 'problem') {
                $text = if ($item.Note) { "$($item.Short): $($item.Note)" } else { [string]$item.Short }
                $tag = if ($from) { " (answered at the $from check)" } else { '' }
                $manualProblems += "$text$tag"
                if ($item.Id -eq 'battery_swelling') { $physical.Insert(0, "SWOLLEN BATTERY: do not charge or use it; replace the battery first$tag") }
                else { $physical.Add("Physical check failed - $text$tag") }
            }
        }
        foreach ($from in $fromOthers.Keys) { $notes.Add("Answers from the $from check of this laptop: $($fromOthers[$from] -join ', ')") }
        $skipped = @($items | Where-Object { $_.Answer -eq 'not checked' }).Count
        if ($skipped -gt 0) { $notes.Add("$skipped physical check(s) skipped") }
        if ((Get-JsonField $manual 'ExtraNote')) { $notes.Add("Remark: $($manual.ExtraNote)") }
    } else {
        $notes.Add('Physical checks not done')
    }

    # --- The clock. A step does not make any reading wrong, only the times recorded beside them,
    # so it is a note and never a recheck: a laptop is not re-audited because Windows fixed its
    # clock, and a zip from before the kit measured this says nothing at all.
    $clockNote = Get-ClockJumpNote $kit
    if ($clockNote) { $notes.Add($clockNote) }

    # --- A run that did not reach its last step (KIT-007). The kit writes kit.json at every stage:
    # "started", "scanned" (the scan is whole, before any answer), "answering" (saved after each
    # answer), "complete". FromWorkFolder is set by the fleet summary when it read the run's work
    # folder because that got further than the zip, or because there was no readable zip.
    # Whether the scan finished is answered by the report itself, not by the stage: the app is a
    # windowed program and keeps running after the kit's window is closed, so it can finish and write
    # its report after the kit recorded "started". A report on disk is whole: it is written once, at
    # the end of the scan.
    $stage = [string](Get-JsonField $kit 'Stage')
    $fromFolder = [bool](Get-JsonField $Bundle 'FromWorkFolder')
    $realKit = [bool](Get-JsonField $kit 'KitVersion')   # the summary's stand-in for a missing kit.json has none
    # How the run stopped. A closed window leaves no trace of its own; Ctrl+C and an error let the
    # kit's last step run, and it records which (StoppedEarly), with the stage the run had reached.
    $stoppedEarly = [string](Get-JsonField $kit 'StoppedEarly')
    $how = if ($stoppedEarly -eq 'error') { 'The kit stopped on an error' } elseif ($stoppedEarly) { 'The kit was stopped (Ctrl+C)' } else { 'The kit window was closed' }
    $closedBeforeScan = "$how before the scan was saved: check this laptop again"
    $answered = $ownItems
    # The kit saves an answer before it rewrites kit.json, so a window closed between the two leaves
    # "scanned" beside a saved answer: that run was answering.
    if ($stage -eq 'scanned' -and $answered.Count -gt 0) { $stage = 'answering' }
    # Notes never contain "; ": the table splits a row's notes on it.
    $answeringNote = "$how during the questions: only the answers given before that are counted"
    $total = Get-JsonField $manual 'Total'
    if ($total -and [int]$total -gt $answered.Count) {
        $answeringNote = "$how during the questions: $([int]$total - $answered.Count) of $total physical checks were not reached"
    } elseif ($total -and $answered.Count -ge [int]$total) {
        # A run is "complete" once its last answer is saved, so this one stopped at that answer's
        # note, or on an error while saving.
        $last = $answered[$answered.Count - 1]
        if ([string](Get-JsonField $last 'Answer') -eq 'problem' -and -not [string](Get-JsonField $last 'Note')) {
            $answeringNote = "$how at the last question, before its note was given"
        } else { $answeringNote = "$how after every question was answered" }
    }
    if ($stage -eq 'scanned') {
        if (Get-JsonField $kit 'SkipQuestions') { $notes.Add("$how after the scan was saved") }
        else { $notes.Add("$how after the scan was saved, before any question was answered") }
    }
    elseif ($stage -eq 'answering') { $notes.Add($answeringNote) }
    elseif ($stage -eq 'started') {
        if ($report -and $null -ne (Get-JsonField $kit 'ScanExitCode')) { $notes.Add("$how after the scan finished, before it was saved or any question was asked") }
        elseif ($report) { $notes.Add("$how during the scan, and the app finished the scan by itself, but no question was asked") }
        else { $recheck.Add($closedBeforeScan) }
    } elseif ($fromFolder -and $realKit) {
        # Reached its end (a kit older than KIT-007 wrote kit.json only at the end), but no finished
        # zip was saved: the window was closed during the final save, or the save failed.
        $notes.Add('The final zip was not written (the window was closed or the save failed), so this result was read from its work folder on the stick')
    } elseif ($fromFolder) {
        # No readable kit.json: a kit older than KIT-007 closed before its end, or a window closed
        # while kit.json was being rewritten. The other files say how far it got.
        if ($answered.Count -gt 0) { $notes.Add($answeringNote) }
        elseif ($report) { $notes.Add('The kit window was closed before its last step: the scan finished, but no answers were saved') }
        else { $recheck.Add($closedBeforeScan) }
    }
    # The battery question is a fire risk, so a laptop must not read READY on a swelling question
    # nobody answered: that is a reason to look again, not a note. Decided by the answers themselves,
    # whatever the stage, because a closed window, Ctrl+C and an error all stop a run at different
    # stages. Not asked on purpose (-SkipQuestions) is a note; so is a kit older than KIT-007, which
    # had no stages and asked every question it was going to ask.
    $batteryAnswered = @($items | Where-Object { [string](Get-JsonField $_ 'Id') -eq 'battery_swelling' }).Count -gt 0
    $skippedOnPurpose = [bool](Get-JsonField $kit 'SkipQuestions')
    $olderKit = $realKit -and -not [string](Get-JsonField $kit 'Stage')
    if ((Get-JsonField $m 'IsLaptop') -and -not $batteryAnswered -and -not $skippedOnPurpose -and -not $olderKit) {
        $recheck.Add('The battery swelling question was not answered: look at the case before handing this laptop over')
    }

    # --- Did the scan itself run, and with administrator rights?
    if (-not $report) {
        $exitCode = Get-JsonField $kit 'ScanExitCode'
        if ($null -eq $exitCode) { $recheck.Add('The scan did not finish, so there is no report') }
        else { $recheck.Add("The scan produced no report (exit code $exitCode)") }
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
    if ($driveGrade -eq 'Bad') { $driveFix.Add("Replace the drive: $($driveIssues -join ', ')") }
    elseif ($driveGrade -eq 'Normal') {
        if ($Rules.driveWarningsNeedFix) { $driveFix.Add("Drive warnings: $($driveIssues -join ', ')") }
        else { $notes.Add("Drive shows early warnings, worth watching: $($driveIssues -join ', ')") }
    }
    # Disk errors in the event log count against the laptop only when they came from its own drives.
    # The app's grade is on the total of every drive Windows ever saw, so on the first fleet audit a
    # T470p was sent for a drive check over 47 errors, all of them on two USB drives, while its own
    # SSD had logged none (KIT-005). Where the split cannot be read, the total still counts.
    foreach ($eventVerdict in $eventVerdicts) {
        $split = Get-DiskErrorsAgainstLaptop $eventVerdict $Bundle.Disks
        if (-not $split) {
            if ($eventVerdict.Grade -eq 'Bad') { $driveFix.Add("Disk errors in the event log, check that drive: $(Format-Subject $eventVerdict)") }
            elseif ($eventVerdict.Grade -eq 'Normal') { $notes.Add("A few disk errors in the event log: $(Format-Subject $eventVerdict)") }
            continue
        }
        $outside = @()
        if ($split.External.Count -gt 0) { $outside += "on USB drives ($($split.External -join ', '))" }
        if ($split.Detached.Count -gt 0) { $outside += "on drives not attached during the audit ($($split.Detached -join ', '))" }
        if ($outside.Count -gt 0) { $notes.Add("Not counted against this laptop, disk errors in the event log $($outside -join ' and ')") }
        if ($split.Own -gt $script:DiskErrorsNormalMax) {
            $driveFix.Add("Disk errors in the event log from this laptop's own drive: $($split.Own) in 90 days ($($split.OwnParts -join ', ')), check that drive")
        } elseif ($split.Own -gt 0) {
            $notes.Add("A few disk errors in the event log from this laptop's own drive: $($split.Own) in 90 days ($($split.OwnParts -join ', '))")
        }
    }

    # --- Drive type: Windows 11 on a hard disk is slow enough to cost an employee real time.
    $diskType = if ($m) { [string]$m.SystemDiskMediaType } else { '' }
    $diskText = if ($m -and $m.SystemDiskModel) { "$diskType $($m.SystemDiskSizeGB) GB ($($m.SystemDiskModel))" } else { $diskType }
    if ($Rules.requireSsd -and $diskType -eq 'HDD') { $fix.Add('Replace the hard disk with an SSD') }
    elseif ($m -and $diskType -ne 'SSD' -and $diskType -ne 'HDD') { $notes.Add('The system drive did not say whether it is an SSD or a hard disk') }

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
    } elseif (($m -and $m.IsLaptop) -or -not $report -or -not $m) {
        # A laptop whose battery was not read, or a run that read nothing at all: "No battery" would
        # claim a fact nobody established. Without a report, the no-report reason already says why.
        $batteryText = 'Not read'
        if ($report -and $m -and $m.IsLaptop) { $recheck.Add('Laptop, but battery wear was NOT read') }
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
    } elseif (-not $report) {
        $win11 = 'Not read'
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

    # What the scan found, kept apart for the fleet summary to carry between runs, then everything
    # other runs of the laptop found that this one did not supersede.
    $scanReplace = @($replace)
    $scanFix = @($driveFix) + @($fix)
    foreach ($r in @(Get-JsonField $Bundle 'CarriedReplace' | Where-Object { $_ })) { $replace.Add([string]$r) }
    foreach ($r in @(Get-JsonField $Bundle 'CarriedFix' | Where-Object { $_ })) { $fix.Add([string]$r) }
    foreach ($r in @(Get-JsonField $Bundle 'CarriedRecheck' | Where-Object { $_ })) { $recheck.Add([string]$r) }
    $allFix = @($physical) + @($driveFix) + @($fix)

    $verdict = if ($replace.Count) { 'REPLACE' } elseif ($allFix.Count) { 'FIX FIRST' } elseif ($recheck.Count) { 'CHECK AGAIN' } else { 'READY' }
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
        Reasons            = (@($replace) + $allFix + @($recheck)) -join '; '
        Notes              = @($notes) -join '; '
        PhysicalProblems   = $manualProblems -join '; '
        CheckedAt          = Get-AuditCheckedAt $kit
        KitVersion         = if ($kit) { [string]$kit.KitVersion } else { '' }
        AppVersion         = if ($kit) { [string]$kit.AppVersion } elseif ($report) { [string]$report.AppVersion } else { '' }
        ResultFile         = if ($kit) { [string]$kit.ResultName } else { '' }
        ScanReplace        = $scanReplace
        ScanFix            = $scanFix
        DriveFix           = @($driveFix)
        DriveRead          = $driveRead
    }
}
