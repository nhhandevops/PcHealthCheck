using System.Globalization;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Modules.Linux;
using PcHealth.Modules.Linux.Support;

namespace PcHealth.Tests.Linux;

/// <summary>
/// A fake /proc and /sys in a temp folder, so the real modules run unchanged on any OS. The
/// file contents in <see cref="Fixtures"/> follow the kernel's documented formats; LiveLinuxTests
/// checks the same code against a real kernel in CI.
/// </summary>
internal sealed class FakeLinux : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "pchealth-fake-linux-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public Dictionary<string, VolumeSpace> Spaces { get; } = new(StringComparer.Ordinal);

    public FakeLinux() => Directory.CreateDirectory(Root);

    public FakeLinux File(string path, string content)
    {
        var full = Path.Combine(Root, path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    public FakeLinux Dir(string path)
    {
        Directory.CreateDirectory(Path.Combine(Root, path.TrimStart('/')));
        return this;
    }

    /// <summary>Writes each attribute as its own file, the way sysfs presents them.</summary>
    public FakeLinux Attributes(string dir, params (string Name, string Value)[] attributes)
    {
        foreach (var (name, value) in attributes) File($"{dir}/{name}", value + "\n");
        return this;
    }

    public LinuxModuleProvider Provider() =>
        new(Root, mount => Spaces.TryGetValue(mount, out var space) ? space : throw new IOException($"{mount}: not in the fixture"),
            sensorSamples: 1, sensorInterval: TimeSpan.Zero);

    public static ModuleContext Context()
    {
        var receipt = new ReceiptLog();
        return new ModuleContext { Processes = new ProcessRunner(receipt), Receipt = receipt, AppTempDir = Path.GetTempPath() };
    }

    public async Task<ModuleResult> Run(string moduleId, ModuleContext? context = null)
    {
        var module = Provider().CreateModules().Single(m => m.Manifest.Id == moduleId);
        return await module.RunAsync(context ?? Context(), null, CancellationToken.None);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Fixture machines, written in the formats the kernel documents.</summary>
internal static class Fixtures
{
    public const string SerialNumber = "SERIAL-MUST-NEVER-BE-REPORTED";

    public const string Meminfo = """
        MemTotal:       16222912 kB
        MemFree:          812344 kB
        MemAvailable:    9874520 kB
        Buffers:          402116 kB
        Cached:          8102236 kB
        SwapCached:         1024 kB
        SwapTotal:       4194300 kB
        SwapFree:        3145724 kB
        Dirty:               412 kB
        CommitLimit:    12305756 kB
        Committed_AS:   14876512 kB
        VmallocTotal:   34359738367 kB
        HugePages_Total:       0
        Hugepagesize:       2048 kB

        """;

    public const string OsRelease = """
        PRETTY_NAME="Ubuntu 26.04 LTS"
        NAME="Ubuntu"
        VERSION_ID="26.04"
        VERSION="26.04 LTS (Resolute Raccoon)"
        VERSION_CODENAME=resolute
        ID=ubuntu
        ID_LIKE=debian

        """;

    public const string Mounts = """
        sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
        proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
        udev /dev devtmpfs rw,nosuid,relatime,size=8075264k,mode=755,inode64 0 0
        tmpfs /run tmpfs rw,nosuid,nodev,noexec,relatime,size=1622292k,mode=755,inode64 0 0
        /dev/nvme0n1p2 / ext4 rw,relatime 0 0
        /dev/loop0 /snap/core22/1612 squashfs ro,nodev,relatime,errors=continue,threads=single 0 0
        /dev/loop1 /snap/firefox/5091 squashfs ro,nodev,relatime,errors=continue,threads=single 0 0
        /dev/nvme0n1p1 /boot/efi vfat rw,relatime,fmask=0077,dmask=0077,codepage=437,errors=remount-ro 0 0
        /dev/sda1 /media/an/My\040Passport fuseblk rw,nosuid,nodev,relatime,user_id=0,group_id=0,allow_other 0 0
        /dev/sdb1 /media/an/USB\040STICK vfat rw,nosuid,nodev,relatime,uid=1000,gid=1000 0 0
        //nas/share /mnt/nas cifs rw,relatime,vers=3.1.1 0 0
        nas:/export /mnt/nfs nfs4 rw,relatime 0 0
        tmpfs /run/user/1000 tmpfs rw,nosuid,nodev,relatime,size=1622288k,mode=700,uid=1000,gid=1000 0 0
        gvfsd-fuse /run/user/1000/gvfs fuse.gvfsd-fuse rw,nosuid,nodev,relatime,user_id=1000,group_id=1000 0 0
        /dev/nvme0n1p2 /var/snap/bind ext4 rw,relatime 0 0
        /dev/sdc1 /mnt/recovery ext4 ro,relatime 0 0
        overlay /var/lib/docker/overlay2/abc/merged overlay rw,relatime,lowerdir=/x,upperdir=/y,workdir=/z 0 0

        """;

    private const long Gb = 1L << 30;

    /// <summary>
    /// A ThinkPad T470p running Ubuntu: two batteries (one worn out), a wireless mouse, Intel
    /// sensors, an NVMe drive, a nearly full root filesystem, a USB stick and two network shares.
    /// </summary>
    public static FakeLinux ThinkPad()
    {
        var fake = new FakeLinux()
            .File("/etc/os-release", OsRelease)
            .File("/proc/sys/kernel/osrelease", "7.0.0-14-generic\n")
            .File("/proc/uptime", "356475.12 1342876.55\n")
            .File("/sys/class/dmi/id/sys_vendor", "LENOVO\n")
            .File("/sys/class/dmi/id/product_name", "20J6CTO1WW\n")
            .File("/sys/class/dmi/id/product_version", "ThinkPad T470p\n")
            .File("/proc/meminfo", Meminfo)
            .File("/proc/sys/vm/overcommit_memory", "0\n")
            .File("/proc/vmstat", "nr_free_pages 203086\npgfault 99312\noom_kill 0\nnr_dirty 103\n")
            .File("/proc/self/mounts", Mounts)
            .File("/sys/class/block/sdb1/removable", "1\n")
            .File("/sys/class/block/sda1/removable", "0\n")
            .File("/proc/cpuinfo", "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Core(TM) i7-7820HQ CPU @ 2.90GHz\n\nprocessor\t: 1\n")
            .Attributes("/sys/class/power_supply/AC", ("type", "Mains"), ("online", "1"))
            .Attributes("/sys/class/power_supply/BAT0",
                ("type", "Battery"), ("present", "1"), ("status", "Not charging"), ("capacity", "79"),
                ("energy_full", "11960000"), ("energy_full_design", "23480000"), ("cycle_count", "712"),
                ("manufacturer", "SMP"), ("model_name", "01AV427"), ("technology", "Li-poly"),
                ("charge_control_end_threshold", "80"), ("serial_number", SerialNumber))
            .Attributes("/sys/class/power_supply/BAT1",
                ("type", "Battery"), ("present", "1"), ("status", "Unknown"), ("capacity", "100"),
                ("energy_full", "18040000"), ("energy_full_design", "23200000"), ("cycle_count", "0"),
                ("manufacturer", "SANYO"), ("model_name", "45N1775"), ("technology", "Li-ion"), ("serial_number", SerialNumber))
            .Attributes("/sys/class/power_supply/hidpp_battery_0",
                ("type", "Battery"), ("scope", "Device"), ("status", "Discharging"), ("capacity", "55"), ("model_name", "MX Master 3"))
            .Attributes("/sys/class/hwmon/hwmon0", ("name", "acpitz"), ("temp1_input", "52000"))
            .Attributes("/sys/class/hwmon/hwmon2", ("name", "coretemp"),
                ("temp1_label", "Package id 0"), ("temp1_input", "71000"),
                ("temp2_label", "Core 0"), ("temp2_input", "69000"),
                ("temp3_label", "Core 1"), ("temp3_input", "73000"))
            .Attributes("/sys/class/hwmon/hwmon3", ("name", "nvme"),
                ("temp1_label", "Composite"), ("temp1_input", "44850"),
                ("temp2_label", "Sensor 1"), ("temp2_input", "51850"))
            .Attributes("/sys/class/thermal/thermal_zone0", ("type", "acpitz"), ("temp", "52000"))
            .Attributes("/sys/class/thermal/thermal_zone1", ("type", "x86_pkg_temp"), ("temp", "71000"))
            .Attributes("/sys/class/thermal/thermal_zone2", ("type", "iwlwifi_1"), ("temp", "40000"))
            .Attributes("/sys/class/thermal/cooling_device0", ("type", "Processor"), ("cur_state", "0"));

        fake.Spaces["/"] = new VolumeSpace(250 * Gb, 30 * Gb, 30 * Gb - (250 * Gb / 20));
        fake.Spaces["/boot/efi"] = new VolumeSpace(512L << 20, 480L << 20, 480L << 20);
        fake.Spaces["/media/an/My Passport"] = new VolumeSpace(1000 * Gb, 400 * Gb, 400 * Gb);
        return fake;
    }
}
