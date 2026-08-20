using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Modules;

namespace PcHealth.Modules.Windows;

/// <summary>
/// The compiled-in registry of Windows modules. This list is the authority on both
/// sides of the privilege boundary: the UI runs the modules that need no admin rights
/// in-process, and the elevated worker matches requested module ids against this same
/// list — never against anything an unprivileged process wrote to disk.
/// </summary>
public sealed class WindowsModuleProvider : IModuleProvider
{
    public IReadOnlyList<IHealthModule> CreateModules() =>
    [
        // Runs unelevated.
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
        new MalwareDefenderModule(),
        new NetworkHealthModule(),
        new DiskSpeedModule(),
        new CpuBenchmarkModule(),

        // Requires the elevated worker (one UAC prompt, only if selected).
        new DiskSmartModule(),
        new StorageReliabilityModule(),
        new BackupPostureModule(),
        new SecurityPlatformModule(),
        new IntegrityQuickModule(),
        new SensorsModule(),
    ];
}
