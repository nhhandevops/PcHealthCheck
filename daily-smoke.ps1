<#
.SYNOPSIS
    Daily smoke test for PC Health Check: is today's tree still the app we think it is?

.DESCRIPTION
    About two minutes, no administrator rights, no person at the keyboard. Checks the things that
    have actually broken on this project before, in the order a failure makes the rest pointless:

      1. Repository    Which commit is being tested, and whether GitHub has anything newer. A dirty
                       tree is recorded as "<sha>+dirty": a result stamped with a commit it was not
                       built from is the mislabelled release zip of 2026-10-17 all over again.
      2. Privacy gates No System.Net outside PcHealth.Net.Optional, checked twice. The source scan
                       is the one CI runs (with its pattern fixed - see PRIV-001 in
                       docs/warning_bug_and_solutions.md). The compiled scan reads the built
                       assemblies, because ImplicitUsings puts `global using System.Net.Http;` into
                       every project's obj\ folder, so `new HttpClient()` compiles anywhere without
                       the text "System.Net" appearing in any file a source scan reads.
      3. Build         Release with -warnaserror, so the locale analyzers stay errors.
      4. Tests         The whole suite. The one known flaky test (WARN-00x) is re-run alone before
                       it may fail the smoke, and is reported WARN either way so its rate gets
                       counted rather than shrugged off.
      5. Scan          The Release exe, --headless-scan --no-history --temp-dir, unelevated: the exe
                       must carry this commit's stamp, exit 0, have no check Failed or
                       PartiallyCompleted, and its report must still say it made 0 network
                       connections. Its console output must decode strictly as UTF-8 with the
                       progress lines' em dash intact (KIT-006: it was ANSI, and the laptops showed
                       every em dash as a u-grave). Every file its receipt says it wrote must be
                       inside --temp-dir (KIT-007: the audit kit relies on it to keep laptops clean).
      6. Backups       Only with -BackupDir: re-hash each folder against its MANIFEST-SHA256.txt.
                       A copy on a failing drive goes bad silently and looks fine until it is needed.

    Every run appends one row to docs/daily-smoke.md. Logs and the scan's reports go to
    %TEMP%\pchealth-smoke\<timestamp>; the newest 14 runs are kept.

    What it will NOT do: run the scan while DiskSpd is installed. With DiskSpd present the disk
    benchmark writes a test file to every physical drive, USB drives included, and a smoke test
    must not write to someone's backup drive every morning. The step reports SKIP and says why.

    Exit code: 0 when nothing FAILed (WARN and SKIP do not fail the run), 1 otherwise.

    -Push commits this run's row, and only docs/daily-smoke.md, then pushes it to origin/main, so
    the next machine sees it after a pull. A push rejected because another machine pushed first is
    retried once after a rebase. Anything else that fails leaves the row committed locally or on
    disk, says so, and does not change the smoke's result. The T470p runs it this way every
    morning from Task Scheduler (HANDOFF.md, "Daily smoke").

.EXAMPLE
    .\daily-smoke.ps1
    .\daily-smoke.ps1 -BackupDir E:\backup\pchealthcheck, F:\backup\pchealthcheck
    .\daily-smoke.ps1 -NoRecord        # try it without adding a row to docs\daily-smoke.md
    .\daily-smoke.ps1 -Push            # record the row, commit only that file, and push it
#>
[CmdletBinding()]
param(
    [string[]]$BackupDir = @(),
    [switch]$NoRecord,
    [switch]$Push
)

# git and dotnet write progress to stderr, and under 'Stop' Windows PowerShell 5.1 turns the first
# such line into a terminating error. Every native call below is judged by its exit code instead.
$ErrorActionPreference = 'Continue'

$root      = $PSScriptRoot
$solution  = Join-Path $root 'PcHealthCheck.slnx'
$logPath   = Join-Path $root 'docs\daily-smoke.md'
$appOut    = Join-Path $root 'src\PcHealth.App\bin\Release\net10.0-windows'
$exe       = Join-Path $appOut 'PcHealthCheck.exe'
$started   = Get-Date
$runsRoot  = Join-Path ([IO.Path]::GetTempPath()) 'pchealth-smoke'
$run       = Join-Path $runsRoot $started.ToString('yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Force -Path $run | Out-Null

# Known flaky tests, by method name, mapped to the entry that explains them. Anything not listed
# here that fails is a real failure.
$knownFlakes = @{ 'Worker_rejects_module_ids_that_are_not_in_its_own_registry' = 'WARN-00x' }

$steps = [ordered]@{}
function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Result([string]$name, [string]$status, [string]$detail) {
    $steps[$name] = [pscustomobject]@{ Status = $status; Detail = $detail }
    $color = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; SKIP = 'DarkGray' }[$status]
    Write-Host ('  {0}  {1}' -f $status, $detail) -ForegroundColor $color
}
# Runs a native command, keeps everything it printed in $log, returns its exit code.
function Invoke-Logged([string]$log, [string]$file, [string[]]$argList) {
    & $file @argList 2>&1 | ForEach-Object { "$_" } | Out-File -FilePath $log -Encoding utf8
    return $LASTEXITCODE
}

# ---------------------------------------------------------------------------------------------
Step 'Repository'
$sha    = (git -C $root rev-parse HEAD).Trim()
$branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
# The smoke log is the one file this script changes itself, so it does not count as dirt.
$dirty  = @(git -C $root status --porcelain | Where-Object { $_ -notmatch 'docs/daily-smoke\.md$' })
$commit = $sha.Substring(0, 7) + $(if ($dirty.Count) { '+dirty' } else { '' })

$problems = @()
git -C $root fetch --quiet origin 2>$null
if ($LASTEXITCODE -ne 0) {
    $problems += 'could not reach GitHub, so "up to date" is unknown'
} else {
    $counts = git -C $root rev-list --left-right --count "HEAD...origin/$branch" 2>$null
    if ($LASTEXITCODE -eq 0) {
        $ahead, $behind = -split $counts
        if ([int]$behind -gt 0) { $problems += "$behind commit(s) behind origin/$branch - GitHub has newer work" }
        if ([int]$ahead  -gt 0) { $problems += "$ahead commit(s) not pushed" }
    } else {
        $problems += "no origin/$branch to compare with"
    }
}
if ($dirty.Count) { $problems += "$($dirty.Count) uncommitted change(s) - this run tests the working tree, not $($sha.Substring(0, 7)) alone" }
Write-Host "  $branch @ $commit"
if ($problems) { Result 'Repository' 'WARN' ($problems -join '; ') }
else           { Result 'Repository' 'PASS' "$branch @ $commit, same as GitHub, nothing uncommitted" }

# ---------------------------------------------------------------------------------------------
Step 'Privacy gate - source'
# -SimpleMatch takes the pattern literally, so it must be 'System.Net' and not the regex-escaped
# 'System\.Net' - that version searches for a backslash no C# file contains, and finds nothing.
$leaks = Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Include *.cs, *.csproj |
    Where-Object { $_.FullName -notmatch 'PcHealth\.Net\.Optional' -and $_.FullName -notmatch '\\(obj|bin)\\' } |
    Select-String -Pattern 'System.Net' -SimpleMatch
if ($leaks) {
    $leaks | ForEach-Object { Write-Host "    $($_.Path):$($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Red }
    Result 'Gate (source)' 'FAIL' "$(@($leaks).Count) System.Net reference(s) outside PcHealth.Net.Optional"
} else {
    Result 'Gate (source)' 'PASS' 'no System.Net text outside the opt-in assembly'
}

# ---------------------------------------------------------------------------------------------
Step 'Build (Release, -warnaserror)'
$buildLog = Join-Path $run 'build.log'
$t = Get-Date
$buildExit = Invoke-Logged $buildLog 'dotnet' @('build', $solution, '-c', 'Release', '-warnaserror', '--nologo', '-v', 'q')
$buildSecs = [int]((Get-Date) - $t).TotalSeconds
$built = $buildExit -eq 0
if ($built) {
    Result 'Build' 'PASS' "0 warnings, 0 errors (${buildSecs}s)"
} else {
    Get-Content $buildLog | Select-String -Pattern 'error' | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Result 'Build' 'FAIL' "dotnet build exit $buildExit - see $buildLog"
}

# ---------------------------------------------------------------------------------------------
Step 'Privacy gate - compiled'
if (-not $built) {
    Result 'Gate (compiled)' 'SKIP' 'nothing was built'
} else {
    # Every type and assembly a DLL uses is named in its metadata as UTF-8, so a System.Net type
    # used anywhere - however it got into scope - leaves "System.Net" in the file. String literals
    # are stored as UTF-16 and cannot match. Latin-1 maps bytes to chars one-to-one, so an ordinal
    # IndexOf over the decoded file is a byte search.
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $assemblies = @(Get-ChildItem $appOut -File | Where-Object {
        ($_.Name -like 'PcHealth*.dll') -and ($_.Name -ne 'PcHealth.Net.Optional.dll')
    })
    # The Linux modules are not part of the Windows app, so their assembly is not in $appOut.
    # It is named here rather than globbed, so that losing it fails the gate instead of shrinking it.
    $linuxDll = Join-Path $root 'src\PcHealth.Modules.Linux\bin\Release\net10.0\PcHealth.Modules.Linux.dll'
    $linuxSeen = Test-Path $linuxDll
    if ($linuxSeen) { $assemblies += Get-Item $linuxDll }
    $optional = Join-Path $appOut 'PcHealth.Net.Optional.dll'
    $dirtyDlls = @($assemblies | Where-Object {
        $latin1.GetString([IO.File]::ReadAllBytes($_.FullName)).IndexOf('System.Net', [StringComparison]::Ordinal) -ge 0
    })
    # The check proves itself on every run: the one assembly that is allowed network code must
    # be seen to contain it, or the scan is not reading what it thinks it is reading.
    $controlSeen = (Test-Path $optional) -and
        ($latin1.GetString([IO.File]::ReadAllBytes($optional)).IndexOf('System.Net', [StringComparison]::Ordinal) -ge 0)
    if ($dirtyDlls) {
        Result 'Gate (compiled)' 'FAIL' "System.Net used in: $(($dirtyDlls | ForEach-Object Name) -join ', ')"
    } elseif ($assemblies.Count -lt 5 -or -not $controlSeen -or -not $linuxSeen) {
        Result 'Gate (compiled)' 'FAIL' "scan cannot be trusted: $($assemblies.Count) assemblies found, control assembly seen = $controlSeen, Linux modules seen = $linuxSeen"
    } else {
        Result 'Gate (compiled)' 'PASS' "$($assemblies.Count) assemblies clean; control (Net.Optional) detected"
    }
}

# ---------------------------------------------------------------------------------------------
Step 'Tests'
$testSummary = 'not run'
if (-not $built) {
    Result 'Tests' 'SKIP' 'nothing was built'
} else {
    $testLog = Join-Path $run 'tests.log'
    $t = Get-Date
    $testExit = Invoke-Logged $testLog 'dotnet' @('test', $solution, '-c', 'Release', '--no-build', '--nologo',
        '--results-directory', $run, '--logger', 'trx;LogFilePrefix=tests')
    $testSecs = [int]((Get-Date) - $t).TotalSeconds
    # One results file per test project, all of them read. Until 2026-10-23 every project wrote
    # tests.trx and only the first file found was read: with a second test project, its failures
    # could have gone unseen while the row said PASS.
    $trxFiles = @(Get-ChildItem $run -Filter *.trx -Recurse)
    $testProjects = @(Get-ChildItem (Join-Path $root 'tests') -Filter *.csproj -Recurse).Count
    if ($trxFiles.Count -eq 0) {
        Result 'Tests' 'FAIL' "dotnet test exit $testExit and no results file - see $testLog"
    } elseif ($trxFiles.Count -lt $testProjects) {
        Result 'Tests' 'FAIL' "results for $($trxFiles.Count) of $testProjects test projects - see $testLog"
    } else {
        $passed = 0; $total = 0; $failed = 0; $skipped = 0; $failedNames = @()
        foreach ($file in $trxFiles) {
            [xml]$trx = [IO.File]::ReadAllText($file.FullName)
            $c = $trx.TestRun.ResultSummary.Counters
            $passed += [int]$c.passed; $total += [int]$c.total; $failed += [int]$c.failed
            # Skipped tests are counted in total but never executed: the Linux-only live tests on Windows.
            $skipped += [int]$c.total - [int]$c.executed
            $failedNames += @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName })
        }
        $skipNote = if ($skipped -gt 0) { ", $skipped skipped" } else { '' }
        $testSummary = "$passed/$total$skipNote"
        $unknown = @($failedNames | Where-Object { $n = $_; -not ($knownFlakes.Keys | Where-Object { $n -like "*$_" }) })
        if ($failedNames.Count -eq 0 -and $testExit -eq 0) {
            Result 'Tests' 'PASS' "$passed/$total passed$skipNote (${testSecs}s)"
        } elseif ($failedNames.Count -eq 0) {
            # A host crash or an aborted run can leave a results file with no failures in it.
            Result 'Tests' 'FAIL' "dotnet test exit $testExit with no failed test recorded - see $testLog"
        } elseif ($unknown.Count -gt 0) {
            $unknown | ForEach-Object { Write-Host "    failed: $_" -ForegroundColor Red }
            Result 'Tests' 'FAIL' "$failed of $total failed - see $testLog"
        } else {
            # Only known flakes failed. Each must pass on its own, or it is not flaky, it is broken.
            $notes = @(); $stillFailing = @()
            foreach ($name in $failedNames) {
                $method = ($knownFlakes.Keys | Where-Object { $name -like "*$_" } | Select-Object -First 1)
                $rerunExit = Invoke-Logged (Join-Path $run "rerun-$method.log") 'dotnet' @('test', $solution, '-c', 'Release',
                    '--no-build', '--nologo', '--filter', "FullyQualifiedName~$method")
                if ($rerunExit -eq 0) { $notes += "$method failed in the full run, passed alone ($($knownFlakes[$method]))" }
                else { $stillFailing += $method }
            }
            $testSummary += ' (flake)'
            if ($stillFailing) { Result 'Tests' 'FAIL' "known flake failed alone too, so it is a real failure: $($stillFailing -join ', ')" }
            else { Result 'Tests' 'WARN' "$passed/$total passed; $($notes -join '; ')" }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# The audit kit is PowerShell, so dotnet test cannot see it, and a smoke that said PASS without
# running it would be claiming coverage it did not have. The kit decides what a manager is told
# to replace; it is not an optional extra.
Step 'Audit kit checks'
$kitTests = Join-Path $root 'tests/audit-kit/kit-tests.ps1'
if (-not (Test-Path -LiteralPath $kitTests)) {
    Result 'Audit kit checks' 'FAIL' 'tests/audit-kit/kit-tests.ps1 is missing'
} else {
    $kitLog = Join-Path $run 'kit-tests.log'
    $kitExit = Invoke-Logged $kitLog 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $kitTests)
    $kitText = if (Test-Path -LiteralPath $kitLog) { Get-Content -LiteralPath $kitLog -Raw } else { '' }
    $kitCount = if ($kitText -match 'All (\d+) kit checks passed') { [int]$Matches[1] } else { 0 }
    if ($kitExit -eq 0 -and $kitCount -gt 0) {
        Result 'Audit kit checks' 'PASS' "$kitCount kit checks passed"
        $testSummary += " + $kitCount kit"
    } elseif ($kitExit -eq 0) {
        # Exit 0 with no count line means the script changed shape, not that everything passed.
        Result 'Audit kit checks' 'FAIL' "kit tests exited 0 but printed no result line - see $kitLog"
        $testSummary += ' + kit UNKNOWN'
    } else {
        $kitFailed = if ($kitText -match '(\d+) of (\d+) kit checks FAILED') { $Matches[0] } else { "exit $kitExit" }
        Result 'Audit kit checks' 'FAIL' "$kitFailed - see $kitLog"
        $testSummary += ' + kit FAILED'
    }
}

# ---------------------------------------------------------------------------------------------
Step 'Scan (unelevated, headless)'
$scanSummary = 'not run'
$diskSpd = @(
    (Get-Command diskspd.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
    (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\diskspd.exe')
    (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Directory -Filter 'Microsoft.DiskSpd*' -ErrorAction SilentlyContinue | ForEach-Object FullName)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
$stamp = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.ProductVersion } else { $null }

if (-not $built) {
    Result 'Scan' 'SKIP' 'nothing was built'
} elseif ($diskSpd) {
    Result 'Scan' 'SKIP' "DiskSpd is installed ($diskSpd): the scan would write a test file to every physical drive, USB backup drives included"
} elseif (-not $stamp -or $stamp -notlike "*+$sha*") {
    # The build above should have produced this commit's exe. If the stamp says otherwise, running
    # it would test some other build and record the result against this commit.
    Result 'Scan' 'FAIL' "exe stamp '$stamp' does not name commit $sha - stale build"
} else {
    $scanDir = Join-Path $run 'scan'
    New-Item -ItemType Directory -Force -Path $scanDir | Out-Null
    # PcHealthCheck is a WinExe, so its console output only arrives through Start-Process redirection.
    $t = Get-Date
    # --temp-dir as the audit kit passes it: the report's receipt must then show every file the app
    # wrote inside that folder (KIT-007). On a machine with a battery that is the battery report.
    $scanTemp = Join-Path $run 'app-temp'
    $proc = Start-Process -FilePath $exe -ArgumentList @('--headless-scan', '--no-history', '--out', "`"$scanDir`"", '--temp-dir', "`"$scanTemp`"") `
        -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $run 'scan-console.txt') `
        -RedirectStandardError (Join-Path $run 'scan-errors.txt')
    $null = $proc.Handle   # without this, Windows PowerShell 5.1 can report ExitCode as empty
    if (-not $proc.WaitForExit(10 * 60 * 1000)) {
        $proc.Kill()
        Result 'Scan' 'FAIL' 'no result after 10 minutes - killed'
    } else {
        $scanSecs = [int]((Get-Date) - $t).TotalSeconds
        $exit = $proc.ExitCode
        $jsonPath = Join-Path $scanDir 'pc-health-report.json'
        $htmlPath = Join-Path $scanDir 'pc-health-report.html'
        if (-not (Test-Path $jsonPath) -or -not (Test-Path $htmlPath)) {
            Result 'Scan' 'FAIL' "exit $exit and the reports were not written - see $run\scan-console.txt"
        } else {
            $report = [IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json
            $byStatus = $report.Modules | Group-Object Status
            $count = @{}; $byStatus | ForEach-Object { $count[$_.Name] = $_.Count }
            $broken = @($report.Modules | Where-Object { $_.Status -in 'Failed', 'PartiallyCompleted' })
            $skipped = @($report.Modules | Where-Object { $_.Status -eq 'Skipped' } | ForEach-Object ModuleId)
            $networkLine = [IO.File]::ReadAllText($htmlPath).Contains('Network connections made by PcHealthCheck during this scan: 0.')
            $scanSummary = '{0} done / {1} skipped, {2}' -f [int]$count['Completed'], [int]$count['Skipped'], $report.OverallGrade
            Write-Host "  modules: $(($byStatus | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', ')"
            if ($skipped) { Write-Host "  skipped: $($skipped -join ', ')" }
            Write-Host "  machine graded $($report.OverallGrade) - recorded, not judged: a Bad PC is a true answer, not a failed run"

            $why = @()
            if ($exit -ne 0) { $why += "exit $exit (0 = every check finished)" }
            if ($broken) { $why += "did not finish: $(($broken | ForEach-Object { "$($_.ModuleId) $($_.Status)" }) -join ', ')" }
            if ($report.RanElevated) { $why += 'report claims an elevated run' }
            if (-not $networkLine) { $why += 'report no longer states 0 network connections' }
            $strayWrites = @(@($report.Receipt) | Where-Object { $_.'$type' -eq 'fileWrite' -and -not ([string]$_.Path).StartsWith($scanTemp, [StringComparison]::OrdinalIgnoreCase) })
            if ($strayWrites) { $why += "files written outside --temp-dir: $(($strayWrites | ForEach-Object Path) -join ', ')" }
            # The console text is UTF-8 since KIT-006. Decoding it strictly proves that on every run,
            # and the em dash in the progress lines must survive it: before the fix it was byte 0x97.
            $consoleUtf8 = $false
            try {
                $consoleText = (New-Object Text.UTF8Encoding($false, $true)).GetString([IO.File]::ReadAllBytes((Join-Path $run 'scan-console.txt')))
                $consoleUtf8 = $consoleText.Contains([string][char]0x2014)
            } catch { }
            if (-not $consoleUtf8) { $why += 'console output is not UTF-8 with its em dashes intact (KIT-006)' }
            if ($why) { Result 'Scan' 'FAIL' ($why -join '; ') }
            else { Result 'Scan' 'PASS' "exit 0, $scanSummary (${scanSecs}s)" }
        }
    }
}

# ---------------------------------------------------------------------------------------------
Step 'Backups'
$backupSummary = '-'
# `powershell -File` hands a comma-separated list over as one string, not an array.
$dirs = @($BackupDir | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (-not $dirs) {
    Result 'Backups' 'SKIP' 'no -BackupDir given'
} else {
    $fails = @(); $absent = @(); $okFiles = 0; $total = 0; $checked = 0
    foreach ($dir in $dirs) {
        # Path.Combine, not Join-Path: Join-Path throws on a drive letter that is not mounted, and an
        # unplugged backup drive is an ordinary morning that must read as "not checked", not crash.
        $manifest = [IO.Path]::Combine($dir, 'MANIFEST-SHA256.txt')
        if (-not (Test-Path -LiteralPath $manifest)) { $absent += $dir; continue }
        $checked++
        try {
            foreach ($line in [IO.File]::ReadAllLines($manifest)) {
                if (-not $line -or $line.StartsWith('#')) { continue }
                $hash, $rel = $line -split '  ', 2
                $total++
                $path = [IO.Path]::Combine($dir, ($rel -replace '/', '\'))
                if (-not (Test-Path -LiteralPath $path)) { $fails += "${dir}: missing $rel"; continue }
                if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash) { $fails += "${dir}: changed $rel" }
                else { $okFiles++ }
            }
        } catch {
            $fails += "${dir}: could not be read - $($_.Exception.Message)"
        }
    }
    $backupSummary = "$okFiles/$total in $checked of $($dirs.Count) folders"
    $absent | ForEach-Object { Write-Host "    not reachable, not checked: $_" -ForegroundColor Yellow }
    if ($fails) {
        $fails | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        Result 'Backups' 'FAIL' "$okFiles/$total files match; $($fails.Count) problem(s)"
    } elseif ($checked -eq 0 -or $total -eq 0) {
        # Nothing verified is not the same as nothing wrong.
        Result 'Backups' 'WARN' "nothing was verified: $($absent.Count) folder(s) not reachable, $total file(s) listed"
    } elseif ($absent) {
        Result 'Backups' 'WARN' "$okFiles/$total files match in $checked folder(s); $($absent.Count) not reachable, so not checked"
    } else {
        Result 'Backups' 'PASS' "$okFiles/$total files match across $checked folder(s)"
    }
}

# ---------------------------------------------------------------------------------------------
Step 'Summary'
$failed = @($steps.Values | Where-Object Status -eq 'FAIL').Count
$warned = @($steps.Values | Where-Object Status -eq 'WARN').Count
$overall = if ($failed) { 'FAIL' } elseif ($warned) { 'PASS with warnings' } else { 'PASS' }
$steps.GetEnumerator() | ForEach-Object { '  {0,-16} {1}' -f $_.Key, $_.Value.Status }
$elapsed = [int]((Get-Date) - $started).TotalSeconds
Write-Host ("`n  {0} in {1}s - logs: {2}" -f $overall, $elapsed, $run) -ForegroundColor $(if ($failed) { 'Red' } elseif ($warned) { 'Yellow' } else { 'Green' })

if (-not $NoRecord) {
    $utf8 = New-Object Text.UTF8Encoding $false
    if (-not (Test-Path $logPath)) {
        $header = @(
            '# Daily smoke log',
            '',
            'One row per run of `daily-smoke.ps1`, oldest first. The script header says what each column',
            'checks and why. WARN rows are kept, not tidied away: a flaky test or a smoke run on a dirty',
            'tree only means something once it has been counted.',
            '',
            '| When | Commit | Privacy gates | Build | Tests | Scan (unelevated) | Backups | Result |',
            '|---|---|---|---|---|---|---|---|'
        ) -join "`n"
        [IO.File]::WriteAllText($logPath, $header + "`n", $utf8)
    }
    $gates = '{0} / {1}' -f $steps['Gate (source)'].Status, $steps['Gate (compiled)'].Status
    $cell = { param($name, $text)
        switch ($steps[$name].Status) { 'PASS' { $text } 'SKIP' { 'SKIP' } default { "$($steps[$name].Status): $text" } } }
    $row = '| {0} | `{1}` | {2} | {3} | {4} | {5} | {6} | **{7}** |' -f `
        $started.ToString('yyyy-MM-dd HH:mm'), $commit, $gates, $steps['Build'].Status,
        (& $cell 'Tests' $testSummary), (& $cell 'Scan' $scanSummary), (& $cell 'Backups' $backupSummary), $overall
    # LF and no BOM, to match .editorconfig.
    [IO.File]::AppendAllText($logPath, $row + "`n", $utf8)
    Write-Host "  recorded in docs\daily-smoke.md"

    if ($Push) {
        # Only the log file is committed (a pathspec commit ignores anything else staged), so a
        # half-finished edit in the tree never rides along. Git failures are reported, never fatal.
        # One log per git step: Invoke-Logged overwrites its file, and a failure must keep its output.
        $pushLog = Join-Path $run 'push-*.log'
        $msg = 'Daily smoke {0} at {1}: {2}' -f $started.ToString('yyyy-MM-dd HH:mm'), $commit, $overall
        $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
        if ($branch -ne 'main') {
            Write-Host "  push: SKIP - on branch '$branch', not main; the row is on disk, not committed" -ForegroundColor Yellow
        } elseif ((Invoke-Logged (Join-Path $run 'push-commit.log') 'git' @('-C', $root, 'commit', '--quiet', '-m', $msg, '--', 'docs/daily-smoke.md')) -ne 0) {
            Write-Host "  push: FAILED to commit the row - see $pushLog; the row is on disk" -ForegroundColor Yellow
        } else {
            $pushed = (Invoke-Logged (Join-Path $run 'push-1.log') 'git' @('-C', $root, 'push', '--quiet', 'origin', 'main')) -eq 0
            if (-not $pushed) {
                # Usually another machine pushed its own row first. Rebase this one commit on top
                # and try once more; a conflict is aborted, never left half-done in the tree.
                if ((Invoke-Logged (Join-Path $run 'push-rebase.log') 'git' @('-C', $root, 'pull', '--rebase', '--autostash', '--quiet', 'origin', 'main')) -eq 0) {
                    $pushed = (Invoke-Logged (Join-Path $run 'push-2.log') 'git' @('-C', $root, 'push', '--quiet', 'origin', 'main')) -eq 0
                } else {
                    Invoke-Logged (Join-Path $run 'push-abort.log') 'git' @('-C', $root, 'rebase', '--abort') | Out-Null
                }
            }
            if ($pushed) { Write-Host "  pushed: $msg" }
            else { Write-Host "  push: FAILED - the row is committed locally, not pushed; see $pushLog" -ForegroundColor Yellow }
        }
    }
}

Get-ChildItem $runsRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip 14 |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

exit $(if ($failed) { 1 } else { 0 })
