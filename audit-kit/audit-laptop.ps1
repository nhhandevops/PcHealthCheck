<#
.SYNOPSIS
    Checks one laptop before it is handed to a new employee, and saves the result on the USB stick.

.DESCRIPTION
    Started by "1 - Check this laptop.cmd". In order:
      1. Reads which laptop this is: serial number, model, processor, memory, disks, Windows.
      2. Runs PC Health Check with every check, administrator ones included (one Windows prompt).
         Nothing is installed, no benchmark runs, and no history is kept on this laptop.
      3. Asks about what only a person can check: battery swelling, screen, keyboard, ports...
      4. Packs everything into Results\<serial>_<computer>_<date>.zip next to this script, and
         shows the verdict the fleet summary will give this laptop.

    WHAT THE ZIP CONTAINS: the app's HTML and JSON reports (they name this computer, its drives,
    its startup programs with their paths, its browser extensions and the programs using the most
    memory), the app's console output, this laptop's serial number, model, BIOS version, memory,
    disks and Windows version, and your answers to the questions. It contains no passwords, no
    product keys and no file contents: the app never reads them.

    WHAT IS LEFT ON THE LAPTOP: nothing. The app's own folder in %LOCALAPPDATA% is removed again
    if this run created it, and all work happens on the USB stick.

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

function Save-Json([string]$Name, $Object) {
    Write-Utf8NoBom (Join-Path $work $Name) (ConvertTo-Json -InputObject $Object -Depth 6)
}

# Each fact on its own, so one WMI class that fails on an odd laptop does not lose the rest.
function Try-Read([string]$What, [scriptblock]$Block) {
    try { return (& $Block) } catch { $scriptErrors.Add("${What}: $($_.Exception.Message)"); return $null }
}

# Firmware fills the serial field with placeholders on some machines. Those must never become a
# file name, or two different laptops would share one.
$placeholderSerials = @('', '0', 'None', 'Default string', 'To be filled by O.E.M.', 'System Serial Number',
    'Not Specified', 'Not Applicable', 'Chassis Serial Number', 'INVALID', '123456789', '0123456789', 'N/A')
function Test-RealSerial([string]$Value) {
    if ($null -eq $Value) { return $false }
    $v = $Value.Trim()
    return ($placeholderSerials -notcontains $v) -and ($v -notmatch '^[0\s.*xX-]+$')
}

function ConvertTo-SafeName([string]$Value, [int]$Max = 40) {
    $safe = ($Value.Trim() -replace '[^A-Za-z0-9._-]', '-').Trim('-')
    if ($safe.Length -gt $Max) { $safe = $safe.Substring(0, $Max) }
    return $safe
}

# Prints the lines the app has written since the last call. The output file is still open in the
# app, so it is opened with ReadWrite sharing; the last line may be half-written and waits.
function Show-NewLines([string]$Path, [int]$Already) {
    if (-not (Test-Path -LiteralPath $Path)) { return $Already }
    try {
        $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $text = (New-Object IO.StreamReader($stream, [Console]::OutputEncoding)).ReadToEnd() } finally { $stream.Dispose() }
    } catch { return $Already }
    $lines = $text -split "`r?`n"
    $complete = $lines.Count - 1
    for ($i = $Already; $i -lt $complete; $i++) { if ($lines[$i]) { Write-Host "    $($lines[$i])" -ForegroundColor DarkGray } }
    return [Math]::Max($Already, $complete)
}

Write-Host ''
Write-Host '  PC Health Check - laptop audit' -ForegroundColor White
Write-Host "  Kit $script:AuditKitVersion. Takes about 3 minutes, plus the questions." -ForegroundColor White

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

Step 'Which laptop is this'
$bios    = Try-Read 'BIOS' { Get-CimInstance Win32_BIOS }
$cs      = Try-Read 'Computer system' { Get-CimInstance Win32_ComputerSystem }
$product = Try-Read 'Computer product' { Get-CimInstance Win32_ComputerSystemProduct }
$os      = Try-Read 'Windows version' { Get-CimInstance Win32_OperatingSystem }
$cpu     = Try-Read 'Processor' { Get-CimInstance Win32_Processor | Select-Object -First 1 }

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
if (Test-Path -LiteralPath (Join-Path $ResultsDir "$resultName.zip")) { $resultName += $started.ToString('ss') }

# The work folder sits next to the results, on the stick, so this laptop's disk is not written to.
$work = Join-Path $ResultsDir ".work-$resultName"
New-Item -ItemType Directory -Force -Path $work | Out-Null
try { Start-Transcript -LiteralPath (Join-Path $work 'audit-transcript.txt') | Out-Null } catch { $scriptErrors.Add("Transcript: $($_.Exception.Message)") }

$appData = Join-Path $env:LOCALAPPDATA 'PcHealthCheck'
$appDataExisted = Test-Path -LiteralPath $appData
$exit = $null
$scanSeconds = $null

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
    $scanArgs = @('--headless-scan', '--no-history', '--skip-benchmarks', '--out', "`"$reportDir`"")
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
        $manual.Asked = $true
        $items = @()
        foreach ($q in $questions) {
            if ($q.Id -eq 'battery_swelling' -and -not $machine.IsLaptop) { continue }
            $answer = $null; $note = ''
            while (-not $answer) {
                $typed = ([string](Read-Host "  $($q.Text) [y/n/s]")).Trim().ToLowerInvariant()
                switch ($typed) {
                    'y' { $answer = 'ok' }
                    'n' { $answer = 'problem'; $note = [string](Read-Host '    What is wrong? (one line, or Enter)') }
                    's' { $answer = 'not checked' }
                    default { Say 'Please type y, n or s.' 'Yellow' }
                }
            }
            if ($q.Id -eq 'battery_swelling' -and $answer -eq 'problem') {
                Say 'STOP: a swollen battery can catch fire. Unplug the charger, do not charge it again, and mark it for battery replacement.' 'Red'
            }
            $items += [ordered]@{ Id = $q.Id; Short = $q.Short; Question = $q.Text; Answer = $answer; Note = $note.Trim() }
        }
        $manual.Items = $items
        $manual.ExtraNote = ([string](Read-Host '  Anything else worth noting about this laptop? (one line, or Enter)')).Trim()
    }
    Save-Json 'manual-checks.json' $manual
} catch {
    $scriptErrors.Add("Audit script: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)")
    Say "Something went wrong: $($_.Exception.Message)" 'Red'
    Say 'Whatever was collected is still saved; send the zip, it shows what happened.' 'Red'
} finally {
    # Leave the laptop as it was: the app's own folder goes if this run created it. (The consent
    # file in %APPDATA% is written only by the app's window, never by this headless scan.)
    $left = 'nothing'
    if ($appDataExisted) { $left = "$appData existed before this run and was left as it was" }
    elseif (Test-Path -LiteralPath $appData) {
        try { Remove-Item -LiteralPath $appData -Recurse -Force } catch { $left = "could not remove $($appData): $($_.Exception.Message)" }
    }

    $appVersion = ''
    try { $appVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } catch { }

    # One wall-clock reading and one Stopwatch reading, taken together, so the two times below and
    # the step between them all describe the same instant.
    # InvariantCulture because ':' in a custom format string is the culture's time separator, not a
    # literal: under fi-FI the same format writes "16.51", and these strings are sorted as text to
    # pick a laptop's latest run.
    $finished = Get-Date
    $clockJump = Measure-ClockJump $started $finished $runWatch.Elapsed.TotalSeconds
    $kit = [ordered]@{
        KitVersion       = $script:AuditKitVersion
        AppVersion       = $appVersion
        Serial           = if ($serial) { $serial } else { '' }
        SerialSource     = $serialSource
        ComputerName     = $env:COMPUTERNAME
        ResultName       = "$resultName.zip"
        StartedAt        = $started.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
        FinishedAt       = $finished.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
        AdminRequested   = -not $NoAdmin
        ScanExitCode     = $exit
        ScanSeconds      = $scanSeconds
        RunSeconds       = [int]$runWatch.Elapsed.TotalSeconds
        ClockJumpSeconds = $clockJump
        LeftOnLaptop     = $left
        ScriptErrors     = @($scriptErrors)
    }
    try { Save-Json 'kit.json' $kit } catch { }

    # Said out loud here as well as in the zip: the operator is the only person who can tell whether
    # the file name they just wrote down is the time they think it is.
    $clockNote = Get-ClockJumpText $clockJump
    if ($clockNote) { Say "$clockNote." 'Yellow' }

    # The verdict, computed by the same code the fleet summary uses.
    try {
        $row = Get-AuditRow (Read-AuditFolder $work) (Read-AuditRules (Join-Path $kitDir 'audit-rules.json'))
        Step "Verdict: $($row.Verdict)"
        $color = switch ($row.Verdict) { 'READY' { 'Green' } 'CHECK AGAIN' { 'Yellow' } default { 'Red' } }
        Say "$($row.Model), $($row.Cpu), $($row.RamGB) GB, $($row.SystemDisk)" $color
        Say "Drive health: $($row.DriveHealth)   Battery: $($row.Battery)   Windows 11: $($row.Windows11)" $color
        foreach ($reason in ($row.Reasons -split '; ' | Where-Object { $_ })) { Say "- $reason" $color }
        if ($row.Notes) { foreach ($n in ($row.Notes -split '; ')) { Say "  note: $n" } }
    } catch {
        Say "Could not compute the verdict here ($($_.Exception.Message)); the fleet summary will." 'Yellow'
    }

    try { Stop-Transcript | Out-Null } catch { }
    $zip = Join-Path $ResultsDir "$resultName.zip"
    try {
        Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -CompressionLevel Optimal
        Remove-Item -LiteralPath $work -Recurse -Force
        Step 'Saved'
        Say $zip 'Green'
        Say 'You can close this window and unplug the stick.'
    } catch {
        Say "Could not create the zip: $($_.Exception.Message). The files are in $work." 'Red'
    }
}
