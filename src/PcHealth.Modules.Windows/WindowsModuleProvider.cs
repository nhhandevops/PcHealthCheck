using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Modules;

namespace PcHealth.Modules.Windows;

/// <summary>
/// The compiled-in registry of Windows modules (M1: the unelevated set).
/// The elevated worker (M2) will validate requested module ids against this list —
/// never against anything an unprivileged process wrote to disk.
/// </summary>
public sealed class WindowsModuleProvider : IModuleProvider
{
    public IReadOnlyList<IHealthModule> CreateModules() =>
    [
        new SysOverviewModule(),
        new VolumesModule(),
        new SecurityPostureModule(),
        new StartupModule(),
        new LicenseModule(),
        new UpdatesModule(),
        new EventsModule(),
        new StabilityModule(),
        new DriversModule(),
        new BatteryModule(),
        new TimeSyncModule(),
        new CrashDumpModule(),
    ];
}
