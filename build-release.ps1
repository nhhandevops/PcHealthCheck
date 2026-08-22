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

# The app is meant to travel with the documents that explain what it does and promises.
foreach ($doc in 'README.md', 'PRIVACY.md', 'THIRD-PARTY-NOTICES.md', 'USER-GUIDE.md') {
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
$sums = Join-Path $OutputDir 'SHA256SUMS.txt'
Get-ChildItem $OutputDir -File -Filter '*.zip' |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name } |
    Tee-Object -FilePath $sums
Get-ChildItem $staging -File -Filter '*.exe' |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name } |
    Tee-Object -FilePath $sums -Append

Step 'Done'
Write-Host "  $zip"
Write-Host "  $sums"
Write-Host "`nNot yet code-signed: Windows will show a SmartScreen warning on first run." -ForegroundColor Yellow
Write-Host "See RELEASING.md for the signing and publishing steps." -ForegroundColor Yellow
