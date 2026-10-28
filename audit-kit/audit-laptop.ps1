<#
.SYNOPSIS
    Checks one laptop before it is handed to a new employee, and saves the result on the USB stick.

.DESCRIPTION
    Started by "1 - Check this laptop.cmd". In order:
      1. Reads which laptop this is: serial number, hardware UUID, model, processor, memory, disks,
         Windows.
      2. Runs PC Health Check with every check, administrator ones included (one Windows prompt).
         Nothing is installed, no benchmark runs, and no history is kept on this laptop.
      3. Asks about what only a person can check: battery swelling, screen, keyboard, ports...
      4. Packs everything into Results\<serial>_<computer>_<date>.zip next to this script, and
         shows the verdict the fleet summary will give this laptop.

    WHAT THE ZIP CONTAINS: the app's HTML and JSON reports (they name this computer, its drives,
    its startup programs with their paths, its browser extensions and the programs using the most
    memory), the app's console output, this laptop's serial number, hardware UUID (a fixed ID its
    firmware reports), model, BIOS version, memory, disks and Windows version, your answers to the
    questions, and short fingerprints (not the names) of the results already on the stick. The
    kit's own log (audit-transcript.txt) is in it too, and Windows PowerShell starts that log with
    the computer name and the Windows user name the kit ran under. It contains no passwords, no
    product keys and no file contents: the app never reads them.

    WHAT IS LEFT ON THE LAPTOP: nothing of the kit's or the app's. All work happens on the USB
    stick, the app's temporary files included, and the app's own folder in %LOCALAPPDATA% is
    removed again if this run created it. Only if the stick cannot be written does the result go
    to this laptop's Desktop, and the kit says so.

.PARAMETER NoAdmin
    Skip the checks that need administrator rights. Drive health (SMART) is then NOT checked, and
    the laptop's verdict will be CHECK AGAIN.

.PARAMETER SkipQuestions
    Do not ask about the laptop's physical condition. Its verdict then notes the checks as not done.

.PARAMETER ResultsDir
    Where to save the zip. Default: the Results folder next to this script.
#>
[CmdletBinding()]
param(
    [switch]$NoAdmin,
    [switch]$SkipQuestions,
    [string]$ResultsDir = ''
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty while Windows PowerShell 5.1 evaluates parameter defaults (PKG-003).
$kitDir = $PSScriptRoot
. (Join-Path $kitDir 'audit-common.ps1')
if (-not $ResultsDir) { $ResultsDir = Join-Path $kitDir 'Results' }
$exe = Join-Path $kitDir 'PcHealthCheck.exe'
# Elapsed time is measured with a Stopwatch, never by subtracting two clock readings: Windows steps
# the clock on a laptop that has been switched off for months as soon as it finds a network, and on
# the first fleet audit that landed mid-scan and recorded a scan of -316 seconds (KIT-004). The wall
# clock is still read, and the difference between the two is kept as ClockJumpSeconds.
$started = Get-Date
$runWatch = [Diagnostics.Stopwatch]::StartNew()
$scriptErrors = New-Object System.Collections.Generic.List[string]

function Step([string]$Text) { Write-Host ''; Write-Host "=== $Text ===" -ForegroundColor Cyan }
function Say([string]$Text, [string]$Color = 'Gray') { Write-Host "  $Text" -ForegroundColor $Color }

# Written to a temporary name, then moved over the real file, so a window closed mid-write never leaves
# a half-written file (KIT-007): kit.json and the answers are rewritten many times a run. The move is
# not atomic: Move-Item -Force in Windows PowerShell 5.1 deletes the old file and then renames the new
# one, so for a moment only the complete .tmp exists, and the summary reads that (Read-KitFile).
# [IO.File]::Replace, which is atomic, does not work on the audit stick.
function Save-Json([string]$Name, $Object) {
    $path = Join-Path $work $Name
    Write-Utf8NoBom "$path.tmp" (ConvertTo-Json -InputObject $Object -Depth 6)
    Move-Item -LiteralPath "$path.tmp" -Destination $path -Force
}

# Each fact on its own, so one WMI class that fails on an odd laptop does not lose the rest.
function Try-Read([string]$What, [scriptblock]$Block) {
    try { return (& $Block) } catch { $scriptErrors.Add("${What}: $($_.Exception.Message)"); return $null }
}

# Firmware fills the serial field with placeholders on some machines, and a person may type one
# ("unknown", "?"). Those must never become a file name, or two different laptops would share one.
# The list is shared with the fleet summary (Test-PlaceholderSerial in audit-common.ps1).
function Test-RealSerial([string]$Value) {
    if ($null -eq $Value) { return $false }
    return -not (Test-PlaceholderSerial $Value)
}

# Prints the lines the app has written since the last call. The output file is still open in the
# app, so it is opened with ReadWrite sharing; the last line may be half-written and waits.
# The app writes UTF-8. This decoded with the console's code page until KIT-006, which turned the
# em dash of every progress line into a "u" with a grave accent on the laptops.
function Show-NewLines([string]$Path, [int]$Already) {
    if (-not (Test-Path -LiteralPath $Path)) { return $Already }
    try {
        $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $text = (New-Object IO.StreamReader($stream, (New-Object Text.UTF8Encoding $false))).ReadToEnd() } finally { $stream.Dispose() }
    } catch { return $Already }
    $lines = $text -split "`r?`n"
    $complete = $lines.Count - 1
    for ($i = $Already; $i -lt $complete; $i++) { if ($lines[$i]) { Write-Host "    $($lines[$i])" -ForegroundColor DarkGray } }
    return [Math]::Max($Already, $complete)
}

Write-Host ''
Write-Host '  PC Health Check - laptop audit' -ForegroundColor White
Write-Host "  Kit $script:AuditKitVersion. Takes about 3 minutes, plus the questions." -ForegroundColor White
# A laptop whose clock battery is flat comes up years in the past. Its result is still saved, but the
# fleet summary cannot then tell which of the laptop's checks came last (KIT-007).
try {
    $kitMade = [datetime]::ParseExact($script:AuditKitVersion.Substring(0, 10), 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    if ($started -lt $kitMade) {
        Write-Host "  This laptop's clock says $($started.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)), before this kit was made. Set the date and time first if you can." -ForegroundColor Yellow
    }
} catch { }

if (-not (Test-Path -LiteralPath $exe)) {
    Say 'PcHealthCheck.exe is not next to this script. Copy the whole kit folder to the USB stick, not just this file.' 'Red'
    exit 1
}

# --- Where the result goes: the stick, unless it cannot be written to.
try {
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    $probe = Join-Path $ResultsDir '.write-test'
    Set-Content -LiteralPath $probe -Value 'ok'
    Remove-Item -LiteralPath $probe -Force
} catch {
    $fallback = [Environment]::GetFolderPath('Desktop')
    Say "Cannot write to $ResultsDir ($($_.Exception.Message))." 'Yellow'
    Say "The result will be saved on this laptop's Desktop instead: copy it off before handing the laptop over." 'Yellow'
    $ResultsDir = $fallback
}
# A full path from here on: the app is given folders inside it, and refuses a relative one.
try { $ResultsDir = (Resolve-Path -LiteralPath $ResultsDir).ProviderPath } catch { }

Step 'Which laptop is this'
$bios    = Try-Read 'BIOS' { Get-CimInstance Win32_BIOS }
$cs      = Try-Read 'Computer system' { Get-CimInstance Win32_ComputerSystem }
$product = Try-Read 'Computer product' { Get-CimInstance Win32_ComputerSystemProduct }
$os      = Try-Read 'Windows version' { Get-CimInstance Win32_OperatingSystem }
$cpu     = Try-Read 'Processor' { Get-CimInstance Win32_Processor | Select-Object -First 1 }
# The hardware UUID tells laptops apart when the firmware has no serial: the fleet summary groups runs
# by it, never by computer name, which cloned laptops share (KIT-007). Recorded in kit.json from the
# first moment, so even a run closed before machine.json is written says which laptop it was.
$machineUuid = if ($product -and $product.UUID) { ([string]$product.UUID).Trim() } else { '' }

$serial = $null; $serialSource = 'none'
foreach ($candidate in @($(if ($bios) { $bios.SerialNumber }), $(if ($product) { $product.IdentifyingNumber }))) {
    if (Test-RealSerial $candidate) { $serial = ([string]$candidate).Trim(); $serialSource = 'firmware'; break }
}
if (-not $serial -and -not $SkipQuestions) {
    Say 'This laptop does not report a serial number.' 'Yellow'
    $typed = Read-Host '  Type the serial number from the label underneath (or press Enter to skip)'
    if (Test-RealSerial $typed) { $serial = $typed.Trim(); $serialSource = 'typed' }
}
$serialPart = if ($serial) { ConvertTo-SafeName $serial } else { 'NO-SERIAL' }
$resultName = '{0}_{1}_{2}' -f $serialPart, (ConvertTo-SafeName $env:COMPUTERNAME), $started.ToString('yyyy-MM-dd_HHmm')
# A second run in the same minute gets the seconds too, whether the first left a zip or only its work
# folder (closed early): reusing that folder would mix two scans, and the first run's app, which
# outlives a closed window, would still hold files in it open (KIT-007).
if ((Test-Path -LiteralPath (Join-Path $ResultsDir "$resultName.zip")) -or
    (Test-Path -LiteralPath (Join-Path $ResultsDir ".work-$resultName"))) { $resultName += $started.ToString('ss') }

$appData = Join-Path $env:LOCALAPPDATA 'PcHealthCheck'
$appDataExisted = Test-Path -LiteralPath $appData
$exit = $null
$scanSeconds = $null
$zip = Join-Path $ResultsDir "$resultName.zip"
$appVersion = ''
try { $appVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } catch { }

# The results already on the stick, which this run comes after whatever any clock says: the fleet
# summary orders a laptop's checks by them (Get-RunHash in audit-common.ps1). Keep the Results folder
# on the stick until the audit is over, or the summary has only the laptops' clocks to go by.
$earlierNames = @(Get-ResultNames $ResultsDir)
$earlierRuns = @($earlierNames | ForEach-Object { Get-RunHash $_ })
# An earlier check of this laptop dated after this laptop's clock: one of the two clocks is wrong.
$startedText = $started.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
$aheadOfClock = @($earlierNames | ForEach-Object { Get-ResultNameParts $_ $env:COMPUTERNAME } | Where-Object {
    $_.StartedAt -and [string]::CompareOrdinal($_.StartedAt, $startedText) -gt 0 -and
        ((ConvertTo-SafeName $_.Computer) -eq (ConvertTo-SafeName $env:COMPUTERNAME) -or ($serial -and (Get-SerialKey $_.Serial) -eq (Get-SerialKey $serial))) })
if ($aheadOfClock.Count -gt 0) {
    Say "An earlier check of this laptop on this stick is dated $($aheadOfClock[0].StartedAt), after this laptop's clock ($startedText). Set the date and time if you can." 'Yellow'
}

# kit.json, written at every stage: now ("started"), once the scan is saved ("scanned"), after every
# answer ("answering") and at the very end ("complete"). Closing the window kills PowerShell outright,
# so no finally block runs; whatever was written last is what an interrupted run leaves, and it says
# how far the run got (KIT-007). Ctrl+C and an error do run the last step, which then records the
# stage the run had reached, not "complete", and how it stopped.
$lastStage = 'started'
$runFinished = $false
$stoppedByError = $false
$answersNotSaved = $false
$answersWarned = $false
$proc = $null
function New-KitRecord([string]$Stage, [string]$Left) {
    # One wall-clock reading and one Stopwatch reading, taken together, so the two times below and
    # the step between them all describe the same instant.
    # InvariantCulture because ':' in a custom format string is the culture's time separator, not a
    # literal: under fi-FI the same format writes "16.51", and these strings are sorted as text to
    # pick a laptop's latest run.
    $finished = Get-Date
    [ordered]@{
        KitVersion       = $script:AuditKitVersion
        AppVersion       = $appVersion
        Serial           = if ($serial) { $serial } else { '' }
        SerialSource     = $serialSource
        MachineUuid      = $machineUuid
        ComputerName     = $env:COMPUTERNAME
        ResultName       = "$resultName.zip"
        EarlierRuns      = $earlierRuns
        Stage            = $Stage
        StartedAt        = $started.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
        FinishedAt       = $finished.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
        AdminRequested   = -not $NoAdmin
        SkipQuestions    = [bool]$SkipQuestions
        ScanExitCode     = $exit
        ScanSeconds      = $scanSeconds
        RunSeconds       = [int]$runWatch.Elapsed.TotalSeconds
        ClockJumpSeconds = Measure-ClockJump $started $finished $runWatch.Elapsed.TotalSeconds
        LeftOnLaptop     = $Left
        ScriptErrors     = @($scriptErrors)
    }
}

# The work folder sits next to the results, on the stick, so this laptop's disk is not written to.
# Its kit.json is made ready before the folder exists and written the moment it does, before even the
# transcript starts: a window closed in the run's first second still leaves a file that says which
# laptop this was (KIT-007). Building it took a third of a second, the first ConvertTo-Json of a session.
$work = Join-Path $ResultsDir ".work-$resultName"
$startedJson = ConvertTo-Json -InputObject (New-KitRecord 'started' 'not known yet: the run had not finished') -Depth 6
New-Item -ItemType Directory -Force -Path $work | Out-Null
try { Write-Utf8NoBom (Join-Path $work 'kit.json') $startedJson } catch { $scriptErrors.Add("kit.json: $($_.Exception.Message)") }
try { Start-Transcript -LiteralPath (Join-Path $work 'audit-transcript.txt') | Out-Null } catch { $scriptErrors.Add("Transcript: $($_.Exception.Message)") }

# The result zip, from the work folder as it is now. It is written as ".partial-<name>.zip", which
# the fleet summary ignores, and renamed only once complete. Compress-Archive writes the whole file
# at its end, so a window closed while it ran used to leave a zip nothing could open, and the early
# zip had already been deleted to make room for it (KIT-007). The transcript is held open for writing
# while the kit runs, and Compress-Archive cannot read it then ("being used by another process"), so
# an early save pauses it and resumes it with -Append. Windows PowerShell 5.1 writes subfolder
# entries as "report\...", which Expand-Archive reads.
function Save-ResultZip([switch]$Early) {
    $partial = Join-Path $ResultsDir ".partial-$resultName.zip"
    if ($Early) { try { Stop-Transcript | Out-Null } catch { } }
    try {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        Compress-Archive -Path (Join-Path $work '*') -DestinationPath $partial -CompressionLevel Optimal
        # Replaces the old zip. The work folder holds everything meanwhile, and the summary reads it
        # if a zip is missing or cannot be opened.
        Move-Item -LiteralPath $partial -Destination $zip -Force
    } finally {
        if ($Early) {
            try { Start-Transcript -LiteralPath (Join-Path $work 'audit-transcript.txt') -Append | Out-Null } catch { }
        }
    }
}

# Leave the laptop as it was: the app's own folder goes if this run created it. Called right after
# the scan, so a window closed during the questions leaves nothing behind either, and again at the
# end in case the scan itself broke off. (The consent file in %APPDATA% is written only by the app's
# window, never by this headless scan.)
function Remove-AppTraces {
    if ($appDataExisted) { return "$appData existed before this run and was left as it was" }
    if (Test-Path -LiteralPath $appData) {
        try { Remove-Item -LiteralPath $appData -Recurse -Force } catch { return "could not remove $($appData): $($_.Exception.Message)" }
    }
    return 'nothing'
}


try {
    # A friendly model: Lenovo puts the machine type in Model ("20J7S08T03") and the name people
    # know in SystemFamily ("ThinkPad T470p"); other makers put the name in Model.
    $family = if ($cs -and $cs.SystemFamily -and $cs.SystemFamily -notmatch '^(Default string|To be filled.*|Not Applicable)$') { [string]$cs.SystemFamily } else { '' }
    $friendly = if ($cs) { if ($family -and $family -ne $cs.Model) { "$($cs.Manufacturer) $family ($($cs.Model))" } else { "$($cs.Manufacturer) $($cs.Model)" } } else { '' }

    $modules = @(Try-Read 'Memory modules' { Get-CimInstance Win32_PhysicalMemory })
    $arrays  = @(Try-Read 'Memory slots' { Get-CimInstance Win32_PhysicalMemoryArray })
    $installedGb = $null
    if ($modules.Count -gt 0 -and $modules[0]) { $installedGb = [math]::Round((($modules | Measure-Object -Property Capacity -Sum).Sum) / 1GB, 1) }
    $slotsTotal = $null
    if ($arrays.Count -gt 0 -and $arrays[0]) { $slotsTotal = ($arrays | Measure-Object -Property MemoryDevices -Sum).Sum }

    # The disk that holds Windows, and whether it is an SSD. MediaType 3 = HDD, 4 = SSD; an NVMe
    # drive is always an SSD even when MediaType says "Unspecified", as it does on older Windows.
    $physical = @(Try-Read 'Physical disks' { Get-PhysicalDisk | Select-Object DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus })
    $systemDisk = $null
    $systemDiskNumber = Try-Read 'System disk' { (Get-Partition -DriveLetter ($env:SystemDrive.Substring(0, 1)) -ErrorAction Stop).DiskNumber }
    if ($null -ne $systemDiskNumber) { $systemDisk = $physical | Where-Object { [string]$_.DeviceId -eq [string]$systemDiskNumber } | Select-Object -First 1 }
    $mediaType = 'Unknown'
    if ($systemDisk) {
        $mediaType = [string]$systemDisk.MediaType
        if ($mediaType -notin @('SSD', 'HDD') -and [string]$systemDisk.BusType -eq 'NVMe') { $mediaType = 'SSD' }
        if ($mediaType -notin @('SSD', 'HDD')) { $mediaType = 'Unknown' }
    }

    $machine = [ordered]@{
        ComputerName        = $env:COMPUTERNAME
        Manufacturer        = if ($cs) { [string]$cs.Manufacturer } else { '' }
        Model               = if ($cs) { [string]$cs.Model } else { '' }
        SystemFamily        = $family
        FriendlyModel       = $friendly
        IsLaptop            = [bool]($cs -and $cs.PCSystemType -eq 2)
        MachineUuid         = $machineUuid
        BiosVersion         = if ($bios) { [string]$bios.SMBIOSBIOSVersion } else { '' }
        BiosDate            = if ($bios -and $bios.ReleaseDate) { $bios.ReleaseDate.ToString('yyyy-MM-dd') } else { '' }
        Windows             = if ($os) { "$($os.Caption) (build $($os.BuildNumber))" } else { '' }
        WindowsBuild        = if ($os) { [int]$os.BuildNumber } else { 0 }
        WindowsInstalled    = if ($os -and $os.InstallDate) { $os.InstallDate.ToString('yyyy-MM-dd') } else { '' }
        Cpu                 = if ($cpu) { ([string]$cpu.Name).Trim() } else { '' }
        CpuCores            = if ($cpu) { "$($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads" } else { '' }
        InstalledRamGB      = $installedGb
        RamModules          = @($modules | Where-Object { $_ } | ForEach-Object { "$([math]::Round($_.Capacity / 1GB)) GB $($_.Speed) MT/s" })
        RamSlotsTotal       = $slotsTotal
        RamSlotsUsed        = @($modules | Where-Object { $_ }).Count
        SystemDrive         = $env:SystemDrive
        SystemDiskMediaType = $mediaType
        SystemDiskBusType   = if ($systemDisk) { [string]$systemDisk.BusType } else { '' }
        SystemDiskModel     = if ($systemDisk) { [string]$systemDisk.FriendlyName } else { '' }
        SystemDiskSizeGB    = if ($systemDisk) { [math]::Round($systemDisk.Size / 1GB) } else { $null }
        BatteryCount        = @(Try-Read 'Battery' { Get-CimInstance Win32_Battery }) | Where-Object { $_ } | Measure-Object | ForEach-Object Count
        Culture             = (Get-Culture).Name
        DecimalSeparator    = (Get-Culture).NumberFormat.NumberDecimalSeparator
    }
    Say "Serial      $(if ($serial) { "$serial ($serialSource)" } else { 'none reported' })"
    Say "Model       $($machine.FriendlyModel)"
    Say "Processor   $($machine.Cpu)"
    Say "Memory      $($machine.InstalledRamGB) GB$(if ($slotsTotal) { ", $($machine.RamSlotsUsed) of $slotsTotal slots used" })"
    Say "Windows     $($machine.Windows)"
    Say "Disk        $mediaType $($machine.SystemDiskSizeGB) GB $($machine.SystemDiskModel)"
    Save-Json 'machine.json' $machine
    Save-Json 'disks.json' @($physical | Where-Object { $_ })
    Save-Json 'volumes.json' @(Try-Read 'Volumes' { Get-CimInstance Win32_LogicalDisk -Filter 'DriveType = 3' |
        Select-Object DeviceID, VolumeName, FileSystem, @{ n = 'SizeGB'; e = { [math]::Round($_.Size / 1GB, 1) } }, @{ n = 'FreeGB'; e = { [math]::Round($_.FreeSpace / 1GB, 1) } } })

    $usbDrives = @($physical | Where-Object { $_ -and [string]$_.BusType -eq 'USB' })
    if ($usbDrives.Count -gt 1) {
        Say "$($usbDrives.Count) USB drives are plugged in. Unplug all but this stick next time: an external drive's health would be mixed into this laptop's." 'Yellow'
    }

    Step 'Running the health check'
    $reportDir = Join-Path $work 'report'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    # Quoted: Start-Process joins the arguments with spaces, so an unquoted path with a space in it
    # (a stick named "My USB") would be cut in two.
    # --skip-benchmarks: a benchmark runs only where its tool happens to be installed, which would
    # make laptops incomparable, and the disk one writes test files to every drive.
    # --temp-dir on the stick: the app writes nothing to this laptop's disk, even if the window is
    # closed during the scan, when the app keeps running and no cleanup of the kit's would run.
    $scanArgs = @('--headless-scan', '--no-history', '--skip-benchmarks', '--out', "`"$reportDir`"",
                  '--temp-dir', "`"$(Join-Path $work 'app-temp')`"")
    if (-not $NoAdmin) {
        $scanArgs += '--include-admin'
        Say 'Windows will ask for administrator permission once: click Yes (or type the admin password).' 'Yellow'
        Say 'Without it, drive health cannot be checked.' 'Yellow'
    }
    $stdout = Join-Path $work 'console-output.txt'
    $stderr = Join-Path $work 'console-errors.txt'
    $scanWatch = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $exe -ArgumentList $scanArgs -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $null = $proc.Handle   # without this, Windows PowerShell 5.1 can report ExitCode as empty
    $shown = 0
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 700
        $shown = Show-NewLines $stdout $shown
    }
    $proc.WaitForExit()
    $shown = Show-NewLines $stdout $shown
    $exit = $proc.ExitCode
    $scanSeconds = [int]$scanWatch.Elapsed.TotalSeconds
    switch ($exit) {
        0 { Say "Finished in $scanSeconds s; every check ran." 'Green' }
        2 { Say "Finished in $scanSeconds s, but some checks could not finish (listed above). The zip is still worth sending." 'Yellow' }
        default { Say "The scan did not run properly (exit code $exit). The zip will hold what it said; send it." 'Red' }
    }

    # Saved now, before the questions, so closing the window during them still leaves this laptop's
    # result. Until KIT-007 the zip was written only in the last step, and a window closed at the
    # first question lost a complete elevated scan. The final step replaces this zip.
    Save-Json 'manual-checks.json' ([ordered]@{ Asked = $false; Items = @(); ExtraNote = '' })
    $left = Remove-AppTraces
    Save-Json 'kit.json' (New-KitRecord 'scanned' $left)
    $lastStage = 'scanned'
    try {
        Save-ResultZip -Early
        Say "Scan saved: $zip" 'Green'
    } catch {
        $scriptErrors.Add("Early save: $($_.Exception.Message)")
        Say "Could not save the scan yet ($($_.Exception.Message)); it will be saved at the end." 'Yellow'
    }

    # --- The questions only a person can answer
    $questions = @(
        @{ Id = 'battery_swelling'; Short = 'Battery swelling'; Text = 'Is the case flat: no bulge under the keyboard or touchpad, and the laptop does not rock on a table?' },
        @{ Id = 'screen'; Short = 'Screen'; Text = 'Screen has no dead pixels, lines, dark patches or flicker (look at a white page and a black one)?' },
        @{ Id = 'keyboard'; Short = 'Keyboard'; Text = 'Every key types (try each row in Notepad)?' },
        @{ Id = 'touchpad'; Short = 'Touchpad'; Text = 'Touchpad and its buttons work?' },
        @{ Id = 'usb_ports'; Short = 'USB ports'; Text = 'Every USB port works (try a mouse in each; leave this stick where it is)?' },
        @{ Id = 'wifi'; Short = 'Wi-Fi'; Text = 'Wi-Fi finds and joins a network?' },
        @{ Id = 'camera_mic'; Short = 'Camera / microphone'; Text = 'Webcam and microphone work (Camera app; Settings > Sound > microphone test)?' },
        @{ Id = 'audio'; Short = 'Speakers / headphones'; Text = 'Speakers and the headphone jack give sound?' },
        @{ Id = 'charger'; Short = 'Charger'; Text = 'Charger is the right wattage for this laptop (see its label), undamaged, and the laptop charges?' },
        @{ Id = 'body'; Short = 'Body / hinges'; Text = 'Hinges are firm, no cracks, no missing screws or rubber feet?' },
        @{ Id = 'locks'; Short = 'Firmware locks'; Text = 'No unknown BIOS password, and the laptop is not locked to another company or account?' }
    )
    $manual = [ordered]@{ Asked = $false; Items = @(); ExtraNote = '' }
    if (-not $SkipQuestions) {
        Step 'Questions only a person can answer'
        Say 'Type y if it is fine, n if there is a problem, s to skip a check you cannot do now.'
        Say 'Each answer is saved to the stick as you give it. Keep this window open until it says "Saved".'
        $manual.Asked = $true
        $manual.Total = @($questions | Where-Object { $_.Id -ne 'battery_swelling' -or $machine.IsLaptop }).Count
        $items = @()
        $inputEnded = $false
        # Saved after every answer, and a "no" before its note is asked: the answers used to live only
        # in memory, so a swollen battery answered first and a window closed at the next prompt read
        # as READY (KIT-007).
        # A stick that fills up or turns read-only would otherwise drop every answer without a word. A
        # later save that works holds every answer so far, so the warning at the end is only for a
        # stick that is still refusing them.
        function Save-Answers([string]$Stage = 'answering') {
            try {
                $manual.Items = $items
                Save-Json 'manual-checks.json' $manual
                $script:lastStage = $Stage
                Save-Json 'kit.json' (New-KitRecord $Stage $left)
                $script:answersNotSaved = $false
            } catch {
                $scriptErrors.Add("Saving an answer: $($_.Exception.Message)")
                $script:answersNotSaved = $true
                if (-not $script:answersWarned) {
                    $script:answersWarned = $true
                    Say "Your answers are NOT being saved to the stick: $($_.Exception.Message)" 'Red'
                    Say 'Keep answering, and write down every answer that is "n": the stick may be full or write-protected.' 'Red'
                }
            }
        }
        foreach ($q in $questions) {
            if ($q.Id -eq 'battery_swelling' -and -not $machine.IsLaptop) { continue }
            $answer = $null
            while (-not $answer) {
                if ($inputEnded) { $answer = 'not checked'; break }
                $raw = Read-Host "  $($q.Text) [y/n/s]"
                # $null means the input itself has ended, which happens only when it is redirected,
                # as in a test. Asked again it would stay $null and this loop would never end.
                if ($null -eq $raw) { $inputEnded = $true; continue }
                $typed = ([string]$raw).Trim().ToLowerInvariant()
                switch ($typed) {
                    'y' { $answer = 'ok' }
                    'n' { $answer = 'problem' }
                    's' { $answer = 'not checked' }
                    default { Say 'Please type y, n or s.' 'Yellow' }
                }
            }
            $item = [ordered]@{ Id = $q.Id; Short = $q.Short; Question = $q.Text; Answer = $answer; Note = '' }
            $items += $item
            Save-Answers
            if ($answer -eq 'problem') {
                if ($q.Id -eq 'battery_swelling') {
                    Say 'STOP: a swollen battery can catch fire. Unplug the charger, do not charge it again, and mark it for battery replacement.' 'Red'
                }
                if (-not $inputEnded) {
                    $note = Read-Host '    What is wrong? (one line, or Enter)'
                    if ($null -eq $note) { $inputEnded = $true }
                    else { $item.Note = ([string]$note).Trim(); Save-Answers }
                }
            }
        }
        # Every question has its answer: the check is complete, and the closing remark is optional.
        # Recorded before the remark is asked, so a re-check closed at that prompt still counts as a
        # finished check, the one that can show a repair was made.
        Save-Answers 'complete'
        if (-not $inputEnded) {
            $manual.ExtraNote = ([string](Read-Host '  Anything else worth noting about this laptop? (one line, or Enter)')).Trim()
        }
    }
    Save-Json 'manual-checks.json' $manual
    $answersNotSaved = $false
    # The last statement of the run: only a run that got here, or past its last question, is "complete".
    $runFinished = $true
} catch {
    $stoppedByError = $true
    $scriptErrors.Add("Audit script: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)")
    Say "Something went wrong: $($_.Exception.Message)" 'Red'
    Say 'Whatever was collected is still saved; send the zip, it shows what happened.' 'Red'
} finally {
    # Ctrl+C runs this block too, without the catch: a run stopped at the battery question must not be
    # recorded as complete, or the laptop reads READY on a question nobody answered (KIT-007).
    $kit = New-KitRecord $(if ($runFinished) { 'complete' } else { $lastStage }) (Remove-AppTraces)
    if (-not $runFinished -and $lastStage -ne 'complete') { $kit['StoppedEarly'] = if ($stoppedByError) { 'error' } else { 'interrupted' } }
    # Ctrl+C during the scan does not stop the app, a windowed program: it goes on writing its report
    # into the work folder. That folder is kept then, whatever the zip below holds, or a finished scan
    # would be deleted with it.
    $appStillRunning = ($null -ne $proc) -and -not $proc.HasExited
    $reportBefore = @(Get-ChildItem -LiteralPath (Join-Path $work 'report') -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt 0
    $clockJump = $kit.ClockJumpSeconds
    try { Save-Json 'kit.json' $kit } catch { }

    # Said out loud here as well as in the zip: the operator is the only person who can tell whether
    # the file name they just wrote down is the time they think it is.
    $clockNote = Get-ClockJumpText $clockJump
    if ($clockNote) { Say "$clockNote." 'Yellow' }

    # The verdict, computed by the same code the fleet summary uses. It is worked out now but shown
    # only after the zip is saved: the old order showed it first, and an operator who read it and
    # closed the window cut the save short (KIT-007).
    # From the stick, which is what the fleet summary will read; when the stick refused the answers,
    # from the answers as given, so the screen still tells the operator what they found.
    $row = $null; $verdictError = ''
    try {
        $bundle = Read-AuditFolder $work
        if ($answersNotSaved) {
            $bundle.Manual = ConvertTo-Json -InputObject $manual -Depth 6 | ConvertFrom-Json
            $bundle.Kit = ConvertTo-Json -InputObject $kit -Depth 6 | ConvertFrom-Json
        }
        $row = Get-AuditRow $bundle (Read-AuditRules (Join-Path $kitDir 'audit-rules.json'))
    } catch { $verdictError = $_.Exception.Message }
    if ($row) {
        # Kept in kit.json too: the transcript stops before the verdict reaches the screen.
        $kit['Verdict'] = $row.Verdict
        $kit['VerdictReasons'] = $row.Reasons
        try { Save-Json 'kit.json' $kit } catch { }
    }

    Say 'Saving the result to the stick...'
    try { Stop-Transcript | Out-Null } catch { }
    $saveError = ''
    try { Save-ResultZip } catch { $saveError = $_.Exception.Message }
    # A report that appeared while the zip was written is in the folder, perhaps not in the zip.
    $reportAfter = @(Get-ChildItem -LiteralPath (Join-Path $work 'report') -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt 0
    $keepWork = $appStillRunning -or ($reportAfter -and -not $reportBefore)
    if (-not $saveError -and -not $keepWork) {
        # The zip holds everything now. If the folder cannot be removed, the summary prefers the zip.
        try { Remove-Item -LiteralPath $work -Recurse -Force } catch { }
    }

    if ($row) {
        Step "Verdict: $($row.Verdict)"
        $color = switch ($row.Verdict) { 'READY' { 'Green' } 'CHECK AGAIN' { 'Yellow' } default { 'Red' } }
        Say "$($row.Model), $($row.Cpu), $($row.RamGB) GB, $($row.SystemDisk)" $color
        Say "Drive health: $($row.DriveHealth)   Battery: $($row.Battery)   Windows 11: $($row.Windows11)" $color
        foreach ($reason in ($row.Reasons -split '; ' | Where-Object { $_ })) { Say "- $reason" $color }
        if ($row.Notes) { foreach ($n in ($row.Notes -split '; ')) { Say "  note: $n" } }
    } else {
        Say "Could not compute the verdict here ($verdictError); the fleet summary will." 'Yellow'
    }
    if ($answersNotSaved) {
        $problems = @(@($manual.Items) | Where-Object { $_ -and $_.Answer -eq 'problem' } | ForEach-Object { if ($_.Note) { "$($_.Short): $($_.Note)" } else { [string]$_.Short } })
        Say 'Your answers did NOT reach the stick, so the fleet summary will not have them.' 'Red'
        if ($problems.Count -gt 0) { Say "Write these down and pass them on with the results: $($problems -join '; ')" 'Red' }
    }
    $stillRunning = ($null -ne $proc) -and -not $proc.HasExited
    if ($keepWork) {
        if ($stillRunning) { Say "The app is still finishing its scan into $work. Leave that folder on the stick: the fleet summary reads it." 'Yellow' }
        else { Say "The app finished its scan into $work after the zip was made. Leave that folder on the stick: the fleet summary reads it." 'Yellow' }
    }
    if (-not $saveError) {
        Step 'Saved'
        Say $zip 'Green'
        if ($stillRunning) { Say 'Wait a minute before unplugging the stick: the app is still writing to it.' 'Yellow' }
        else { Say 'You can close this window and unplug the stick.' }
    } else {
        Say "Could not create the zip: $saveError. The files are in $work, and the fleet summary reads them from there." 'Red'
    }
}
