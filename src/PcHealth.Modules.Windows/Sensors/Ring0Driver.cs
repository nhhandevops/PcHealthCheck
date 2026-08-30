using Microsoft.Win32;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Sensors;

/// <summary>
/// LibreHardwareMonitorLib does not ship a kernel driver of its own. For CPU and
/// motherboard sensors it talks to PawnIO — a separate, free, open-source, signed
/// driver the user installs themselves. Graphics cards and memory need no driver,
/// which is why those readings work regardless.
///
/// Knowing whether PawnIO is present turns a vague "sensors unavailable" into an
/// actionable "install this and CPU temperatures will appear".
/// </summary>
internal static class Ring0Driver
{
    public static bool IsPawnIoInstalled()
    {
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        if (service is not null) return true;

        using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PawnIO");
        return uninstall is not null;
    }

    /// <summary>
    /// Whether the PawnIO driver is actually loaded and running, as opposed to merely registered.
    ///
    /// The distinction is load-bearing and the registry key cannot make it. LibreHardwareMonitor
    /// builds and activates a processor's temperature sensors from CPUID in the CPU constructor;
    /// only their VALUES need the ring0 driver. Measured on an i7-7820HQ with PawnIO absent, the
    /// library offers eleven CPU temperature sensors whose every value is null. So "a temperature
    /// sensor exists but read back empty" is what an ordinary healthy PC looks like, not what a
    /// broken read looks like, and treating it as a failure would pin the "some checks could not
    /// finish" banner on permanently for the majority of machines.
    ///
    /// A driver that is registered, running, and still produced no reading is the case that
    /// genuinely tried and broke. Anything else was never attempted.
    /// </summary>
    public static bool IsPawnIoRunning()
    {
        // A query that itself fails tells us nothing, so it is treated as "not running": the
        // caller's fallback is a non-degrading note, which is the safe direction. Claiming a
        // broken driver on the strength of a broken query would be this app's own recurring bug.
        var rows = Wmi.TryQuery(@"root\cimv2",
            "SELECT State FROM Win32_SystemDriver WHERE Name = 'PawnIO'", out var error);
        if (error is not null) return false;

        return rows.Any(row => string.Equals(
            Wmi.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase));
    }

    public static string MissingDriverAdvice =>
        "CPU and motherboard sensors need PawnIO, a small free open-source driver that " +
        "LibreHardwareMonitor uses for low-level access (it is the same component FanControl " +
        "and OpenRGB moved to). It is not installed on this PC, which is why graphics card and " +
        "memory readings appear but processor temperature and case fan speeds do not. " +
        "Install it from pawnio.eu, or with: winget install namazso.PawnIO";
}
