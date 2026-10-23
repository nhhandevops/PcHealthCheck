using PcHealth.Core.Modules;
using PcHealth.Modules.Linux.Modules;
using PcHealth.Modules.Linux.Support;

namespace PcHealth.Modules.Linux;

/// <summary>
/// The compiled-in registry of Linux modules. Unelevated only, by the owner's decision of
/// 2026-10-23: every module reads files under /proc, /sys and /etc, runs no program and needs no
/// root. Checks that would need root (SMART, the full system journal) are not here yet; when they
/// come, they are reported as "not checked, needs root", never as healthy.
/// </summary>
public sealed class LinuxModuleProvider : IModuleProvider
{
    private readonly SystemFiles _files;
    private readonly Func<string, VolumeSpace> _statfs;
    private readonly int _sensorSamples;
    private readonly TimeSpan _sensorInterval;

    public LinuxModuleProvider() : this("/", StatVfs, 3, TimeSpan.FromSeconds(1)) { }

    /// <summary>For the fixture tests: a fake root, a fake statvfs, and no waiting between sensor samples.</summary>
    internal LinuxModuleProvider(string root, Func<string, VolumeSpace> statfs, int sensorSamples, TimeSpan sensorInterval)
    {
        _files = new SystemFiles(root);
        _statfs = statfs;
        _sensorSamples = sensorSamples;
        _sensorInterval = sensorInterval;
    }

    public IReadOnlyList<IHealthModule> CreateModules() =>
    [
        new LinuxOverviewModule(_files),
        new LinuxMemoryModule(_files),
        new LinuxVolumesModule(_files, _statfs),
        new LinuxBatteryModule(_files),
        new LinuxSensorsModule(_files, _sensorSamples, _sensorInterval),
    ];

    /// <summary>statvfs through .NET, which asks the kernel for the filesystem mounted at that path.</summary>
    private static VolumeSpace StatVfs(string mountPoint)
    {
        var drive = new DriveInfo(mountPoint);
        return new VolumeSpace(drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace);
    }
}
