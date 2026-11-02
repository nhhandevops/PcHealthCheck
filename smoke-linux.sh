#!/usr/bin/env bash
# Linux smoke test for PC Health Check: does the Linux provider still read a real machine?
#
# Run it from the repository on any Linux PC, VM or live USB session:
#     bash smoke-linux.sh                   # needs the .NET 10 SDK already installed
#     bash smoke-linux.sh --install-dotnet  # first run: puts .NET 10 into ~/.dotnet (no sudo)
#
# What it checks, in order:
#   1. Machine     Which Linux this is (/etc/os-release, uname), whether it is a virtual machine,
#                  and whether it has a battery and temperature sensors. A VM or WSL usually has
#                  neither, so there those checks must say why they read nothing; on a laptop
#                  they must read something.
#   2. .NET        The .NET 10 SDK. --install-dotnet runs Microsoft's dotnet-install.sh into
#                  ~/.dotnet: nothing outside your home folder, no sudo.
#   3. Repository  Which commit is tested, and whether the tree is dirty.
#   4. Build       tests/PcHealth.Tests.Linux, Release, -warnaserror, as CI's linux job does.
#   5. Tests       The fixture tests plus the live tests. The live ones scan this machine
#                  through LinuxModuleProvider and print every reading. On Windows they are
#                  skipped; here a skipped live test is a FAIL, because it means nothing was read.
#
# There is no Linux app to run yet (phase 2, PcHealth.Cli, see NEXT-STEPS M5-LNX), so the live
# test IS the Linux scan for now. Read-only apart from bin/obj, the log folder and ~/.dotnet.
# Logs go to ${TMPDIR:-/tmp}/pchealth-linux-smoke/<timestamp>. Nothing is committed or pushed:
# paste the last line ("Linux smoke ...") back to whoever keeps docs/daily-smoke.md.
#
# Exit: 0 when nothing FAILed, 1 otherwise, 2 when the .NET 10 SDK is missing.

set -u
cd "$(dirname "$0")" || exit 1
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

install_dotnet=0
for arg in "$@"; do
    case "$arg" in
        --install-dotnet) install_dotnet=1 ;;
        *) echo "unknown option: $arg (only --install-dotnet)"; exit 1 ;;
    esac
done

started=$(date '+%Y-%m-%d %H:%M')
run="${TMPDIR:-/tmp}/pchealth-linux-smoke/$(date '+%Y%m%d-%H%M%S')"
mkdir -p "$run"
project=tests/PcHealth.Tests.Linux/PcHealth.Tests.Linux.csproj
failed=0
warned=0
step()   { printf '\n=== %s ===\n' "$1"; }
pass()   { printf '  PASS  %s\n' "$1"; }
warn()   { printf '  WARN  %s\n' "$1"; warned=1; }
fail()   { printf '  FAIL  %s\n' "$1"; failed=1; }

if [ "$(uname -s)" != "Linux" ]; then
    echo "This is $(uname -s), not Linux. On Windows run smoke-windows10.ps1 or smoke-windows11.ps1 instead."
    exit 1
fi

# ---------------------------------------------------------------------------------------------
step 'Machine'
distro=$( . /etc/os-release 2>/dev/null && echo "${PRETTY_NAME:-$NAME}" )
[ -n "$distro" ] || distro='unknown Linux (no /etc/os-release)'
kernel="$(uname -r) $(uname -m)"
if grep -qi microsoft /proc/sys/kernel/osrelease 2>/dev/null; then
    virt=wsl
elif command -v systemd-detect-virt >/dev/null 2>&1; then
    virt=$(systemd-detect-virt 2>/dev/null) || virt=none   # prints "none" and exits 1 on bare metal
else
    virt=unknown
fi
model=$(cat /sys/class/dmi/id/product_version 2>/dev/null)
[ -n "$model" ] && [ "$model" != "None" ] || model=$(cat /sys/class/dmi/id/product_name 2>/dev/null)
has_battery=no
for t in /sys/class/power_supply/*/type; do
    [ -r "$t" ] && [ "$(cat "$t")" = "Battery" ] && has_battery=yes
done
has_sensors=no
ls /sys/class/thermal/thermal_zone* >/dev/null 2>&1 && has_sensors=yes
ls /sys/class/hwmon/hwmon* >/dev/null 2>&1 && has_sensors=yes
echo "  $distro, kernel $kernel"
echo "  virtualization: $virt; model: ${model:-unknown}; battery: $has_battery; temperature sensors: $has_sensors"

# ---------------------------------------------------------------------------------------------
step '.NET 10 SDK'
find_dotnet() {
    for d in dotnet "$HOME/.dotnet/dotnet"; do
        if command -v "$d" >/dev/null 2>&1 && "$d" --list-sdks 2>/dev/null | grep -q '^10\.'; then
            echo "$d"; return 0
        fi
    done
    return 1
}
dotnet=$(find_dotnet)
if [ -z "$dotnet" ] && [ "$install_dotnet" = 1 ]; then
    echo "  installing .NET 10 into $HOME/.dotnet (log: $run/dotnet-install.log)"
    url=https://dot.net/v1/dotnet-install.sh
    if command -v curl >/dev/null 2>&1; then curl -fsSL "$url" -o "$run/dotnet-install.sh"
    else wget -q "$url" -O "$run/dotnet-install.sh"; fi
    bash "$run/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet" >"$run/dotnet-install.log" 2>&1
    dotnet=$(find_dotnet)
fi
if [ -z "$dotnet" ]; then
    fail "no .NET 10 SDK found. Run again with --install-dotnet, or install dotnet-sdk-10.0"
    exit 2
fi
case "$dotnet" in "$HOME"/*) export DOTNET_ROOT="$HOME/.dotnet" ;; esac
sdk=$("$dotnet" --version 2>/dev/null)
pass "SDK $sdk ($dotnet)"

# ---------------------------------------------------------------------------------------------
step 'Repository'
sha=$(git rev-parse --short=7 HEAD 2>/dev/null || echo unknown)
if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
    commit="$sha+dirty"
    warn "$commit: uncommitted changes, so this tests the working tree, not $sha alone"
else
    commit=$sha
    pass "$(git rev-parse --abbrev-ref HEAD 2>/dev/null) @ $commit"
fi

# ---------------------------------------------------------------------------------------------
step 'Build (Release, -warnaserror)'
if "$dotnet" build "$project" -c Release -warnaserror >"$run/build.log" 2>&1; then
    pass 'tests/PcHealth.Tests.Linux built'
    built=1
else
    fail "build failed, see $run/build.log"
    grep -E 'error [A-Z]+[0-9]+' "$run/build.log" | sort -u | head -10 | sed 's/^/    /'
    built=0
fi

# ---------------------------------------------------------------------------------------------
step 'Tests, fixtures and this machine'
tests='not run'
if [ "$built" = 1 ]; then
    "$dotnet" test "$project" -c Release --no-build --logger 'console;verbosity=detailed' \
        --logger 'trx;LogFileName=linux.trx' --results-directory "$run" >"$run/tests.log" 2>&1
    trx="$run/linux.trx"
    if [ ! -f "$trx" ]; then
        fail "no results file, see $run/tests.log"
    else
        counters=$(grep -o '<Counters [^>]*' "$trx")
        num() { echo "$counters" | sed -n "s/.* $1=\"\([0-9]*\)\".*/\1/p"; }
        total=$(num total); executed=$(num executed); passed=$(num passed); nfailed=$(num failed)
        live_all=$(grep -o 'testName="[^"]*LiveLinuxTests[^"]*"' "$trx" | wc -l)
        live_ok=$(grep -o 'testName="[^"]*LiveLinuxTests[^"]*"[^>]*outcome="Passed"' "$trx" | wc -l)
        tests="$passed/$total"
        echo
        echo '  What this machine read (from the live scan):'
        grep -E '^\s*(\[(Completed|Skipped|Failed|PartiallyCompleted)\]|note:|(Good|Normal|Bad|Unknown) +[a-z])' "$run/tests.log" \
            | sed 's/^\s*/    /' | head -60
        echo
        if [ "${nfailed:-1}" != 0 ]; then
            fail "$nfailed of $total tests failed, see $run/tests.log"
            grep -o 'testName="[^"]*"[^>]*outcome="Failed"' "$trx" | sed 's/testName="\([^"]*\)".*/    failed: \1/'
        elif [ "$live_all" = 0 ] || [ "$live_ok" != "$live_all" ]; then
            fail "$live_ok of $live_all live tests ran and passed: on Linux none may be skipped"
        elif [ "$executed" != "$total" ]; then
            fail "$executed of $total tests ran: on Linux none may be skipped"
        else
            pass "$passed/$total passed, $live_ok of them live on this machine"
            tests="$passed/$total, $live_ok live"
        fi
        # A laptop's battery must come out as a reading or a note, never as nothing at all.
        if [ "$has_battery" = yes ] && ! grep -qE '(Good|Normal|Bad|Unknown) +battery\.' "$run/tests.log"; then
            warn 'a battery is in /sys/class/power_supply, but the scan produced no battery reading; read the notes above'
        fi
    fi
fi

# ---------------------------------------------------------------------------------------------
step 'Summary'
if [ "$failed" = 1 ]; then overall=FAIL; elif [ "$warned" = 1 ]; then overall='PASS with warnings'; else overall=PASS; fi
echo "  logs: $run"
echo
echo "Linux smoke $started at $commit: $distro, kernel $kernel, $virt, battery $has_battery: tests $tests: $overall"
[ "$failed" = 1 ] && exit 1
exit 0
