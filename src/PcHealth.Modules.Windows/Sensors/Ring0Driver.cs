using Microsoft.Win32;

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

    public static string MissingDriverAdvice =>
        "CPU and motherboard sensors need PawnIO, a small free open-source driver that " +
        "LibreHardwareMonitor uses for low-level access (it is the same component FanControl " +
        "and OpenRGB moved to). It is not installed on this PC, which is why graphics card and " +
        "memory readings appear but processor temperature and case fan speeds do not. " +
        "Install it from pawnio.eu, or with: winget install namazso.PawnIO";
}
