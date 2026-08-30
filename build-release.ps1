<#
.SYNOPSIS
    Builds a release of PC Health Check: one self-contained .exe, a zip, and checksums.

.DESCRIPTION
    Produces exactly what a person downloads and runs: a zip that extracts to one folder.
    Deliberately not an installer — nothing goes into Program Files, nothing is added to the
    registry, and uninstalling means deleting the folder. That is the same promise the app
    makes about the machine it inspects.

    The build is self-contained, so the .NET runtime travels with it and the user installs
    nothing first. It is NOT a single file: the graphics, text-shaping and SQLite native
    libraries sit beside the exe. A true single-file build is possible, but the .NET host
    then silently unpacks those libraries into a temporary folder on first run — an
    undisclosed write, from an app whose entire point is disclosing every write it makes.
    Seven visible files are the more honest answer.

    Run the tests before shipping whatever this produces. `-SkipTests` exists for iterating
    on the packaging itself, not for releasing.

.EXAMPLE
    .\build-release.ps1
    .\build-release.ps1 -OutputDir D:\releases -SkipTests
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "$PSScriptRoot\release",
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'PcHealthCheck.slnx'
$appProject = Join-Path $PSScriptRoot 'src\PcHealth.App\PcHealth.App.csproj'

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

Step 'Version'
# Single source of truth: whatever the project file says. Anything else drifts.
[xml]$csproj = Get-Content $appProject
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $appProject" }
Write-Host "  $version"

if (-not $SkipTests) {
    Step 'Tests'
    dotnet test $solution -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not packaging a build that does not pass.' }
}

Step 'Privacy gate'
# The same check CI runs. A release that quietly gained network code somewhere outside the
# opt-in assembly would break the app's central promise, so it is verified here too rather
# than trusted to have been caught earlier.
$leaks = Get-ChildItem (Join-Path $PSScriptRoot 'src'), (Join-Path $PSScriptRoot 'tests') -Recurse -Include *.cs, *.csproj |
    Where-Object { $_.FullName -notmatch 'PcHealth\.Net\.Optional' -and $_.FullName -notmatch '\\(obj|bin)\\' } |
    Select-String -Pattern 'System\.Net' -SimpleMatch
if ($leaks) {
    $leaks | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)" -ForegroundColor Red }
    throw 'System.Net referenced outside PcHealth.Net.Optional.'
}
Write-Host '  OK - no network code outside the opt-in assembly.'

Step 'Publish'
$staging = Join-Path $OutputDir "PcHealthCheck-$version-win-x64"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

dotnet publish $appProject -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The app is meant to travel with the documents that explain what it does and promises, and with
# the script that collects a run for comparison - the point of carrying it to another machine is
# bringing the result back, and a collector left behind in the repo is one that never gets used.
foreach ($doc in 'README.md', 'PRIVACY.md', 'THIRD-PARTY-NOTICES.md', 'USER-GUIDE.md',
                 'TRYING-IT-OUT.md', 'collect-results.ps1') {
    $path = Join-Path $PSScriptRoot $doc
    if (Test-Path $path) { Copy-Item $path $staging }
}

Step 'Contents'
Get-ChildItem $staging | Sort-Object Length -Descending |
    ForEach-Object { '  {0,-38} {1,10:N0} bytes' -f $_.Name, $_.Length }

Step 'Package'
$zip = Join-Path $OutputDir "PcHealthCheck-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $zip -CompressionLevel Optimal

Step 'Checksums'
# Published so a downloader can prove the file is the one that was built. Until the binary
# is code-signed this is the only integrity check available to them, so it ships with it.
#
# The zip and only the zip. It is what gets downloaded, and it is the one file that exists in
# the directory where someone runs `sha256sum -c SHA256SUMS.txt`. Listing the exe here too
# reads as more thorough, but the exe lives inside the archive, so that line can never be
# checked from where this file sits: the tool reports FAILED and exits 1 on a release where
# nothing is wrong. An integrity check that cries tampering at a healthy download is worse
# than one that covers less.
$sums = Join-Path $OutputDir 'SHA256SUMS.txt'
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
$exeHash = (Get-FileHash (Join-Path $staging 'PcHealthCheck.exe') -Algorithm SHA256).Hash
$lines = @('{0}  {1}' -f $zipHash, (Split-Path $zip -Leaf))

# No byte-order mark and LF line endings, both for the same reason: `sha256sum -c` must be
# able to read this file. A BOM makes the first line unreadable; CRLF is worse, because the
# trailing carriage return becomes part of every filename and so every line fails to verify.
# Set-Content does both wrong by default on Windows PowerShell, which is why this writes the
# bytes itself rather than asking a cmdlet to.
[IO.File]::WriteAllText($sums, ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
$lines | ForEach-Object { Write-Host "  $_" }

# Prove the file just written is actually readable, rather than trusting that it is. Both
# faults this guards against — a BOM, then CRLF — shipped once each, and both times the
# hashes themselves were correct, so comparing them by eye said everything was fine. The
# only check that would have caught either is the one a downloader actually runs.
$bytes = [IO.File]::ReadAllBytes($sums)
if ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    throw "$sums has a byte-order mark; `sha256sum -c` cannot read its first line."
}
if ($bytes -contains 0x0D) {
    throw "$sums has CRLF line endings; `sha256sum -c` reads the carriage return as part of the filename."
}
# Git for Windows ships GNU sha256sum, but under usr\bin, which is not on PowerShell's PATH.
# Looking only at PATH means this check quietly downgrades to the byte heuristics above on
# every machine that has the tool sitting right there - and a warning printed by every build
# is a warning nobody reads.
$sha256sum = (Get-Command sha256sum -ErrorAction SilentlyContinue).Source
if (-not $sha256sum) {
    $git = (Get-Command git -ErrorAction SilentlyContinue).Source
    if ($git) {
        $candidate = Join-Path (Split-Path (Split-Path $git)) 'usr\bin\sha256sum.exe'
        if (Test-Path $candidate) { $sha256sum = $candidate }
    }
}
if ($sha256sum) {
    Push-Location $OutputDir
    try {
        $check = & $sha256sum -c 'SHA256SUMS.txt'
        if ($LASTEXITCODE -ne 0) { throw "sha256sum -c rejected ${sums}: $check" }
        Write-Host "  verified by $sha256sum - $check" -ForegroundColor Green
    } finally { Pop-Location }
} else {
    Write-Host '  No sha256sum found - format checked, but not run through a real checksum tool.' -ForegroundColor Yellow
}
Write-Host "`n  Not in SHA256SUMS.txt, but needed for release notes and the winget manifest:"
Write-Host "    zip  $zipHash"
Write-Host "    exe  $exeHash"

Step 'Done'
Write-Host "  $zip"
Write-Host "  $sums"
Write-Host "`nNot yet code-signed: Windows will show a SmartScreen warning on first run." -ForegroundColor Yellow
Write-Host "See RELEASING.md for the signing and publishing steps." -ForegroundColor Yellow
