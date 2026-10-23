using PcHealth.Modules.Linux.Support;
using Xunit;

namespace PcHealth.Tests.Linux;

/// <summary>
/// The pure readers behind the Linux modules (rule 8). Each case is one a development machine
/// does not produce: an AMD processor, a firmware placeholder, a btrfs root mounted twice, a
/// battery that counts charge instead of energy, a mount point with a space in it.
/// </summary>
public sealed class ParserTests
{
    [Fact]
    public void Meminfo_values_are_read_in_kB_and_odd_lines_skipped()
    {
        var info = ProcFiles.ParseMeminfo(Fixtures.Meminfo + "garbage line\nBroken:   abc kB\n");

        Assert.Equal(16222912, info["MemTotal"]);
        Assert.Equal(9874520, info["MemAvailable"]);
        Assert.Equal(0, info["HugePages_Total"]);
        Assert.False(info.ContainsKey("Broken"));
    }

    [Theory]
    [InlineData("356475.12 1342876.55\n", 356475.12)]
    [InlineData("12.00 3.00", 12.0)]
    public void Uptime_is_the_first_number(string text, double expected) =>
        Assert.Equal(expected, ProcFiles.ParseUptimeSeconds(text));

    [Fact]
    public void Uptime_that_is_not_a_number_is_not_read() => Assert.Null(ProcFiles.ParseUptimeSeconds("n/a"));

    [Fact]
    public void A_vmstat_counter_is_found_by_its_exact_name()
    {
        const string text = "oom_kill_extra 9\noom_kill 3\npgfault 12\n";
        Assert.Equal(3, ProcFiles.VmStatCounter(text, "oom_kill"));
        Assert.Null(ProcFiles.VmStatCounter("pgfault 12\n", "oom_kill"));
    }

    [Fact]
    public void Os_release_quoting_follows_the_shell_rules()
    {
        var fields = ProcFiles.ParseOsRelease("""
            # a comment
            NAME="Fedora Linux"
            VERSION='43 (Workstation Edition)'
            ID=fedora
            PRETTY_NAME="Say \"hi\" \\ \$HOME"
            not a field
            """);

        Assert.Equal("Fedora Linux", fields["NAME"]);
        Assert.Equal("43 (Workstation Edition)", fields["VERSION"]);
        Assert.Equal("fedora", fields["ID"]);
        Assert.Equal("Say \"hi\" \\ $HOME", fields["PRETTY_NAME"]);
        Assert.Equal(4, fields.Count);
    }

    [Fact]
    public void The_os_is_described_from_pretty_name_then_name_and_version()
    {
        Assert.Equal("Ubuntu 26.04 LTS (kernel 7.0.0-14-generic, X64)",
            ProcFiles.DescribeOs(ProcFiles.ParseOsRelease(Fixtures.OsRelease), "7.0.0-14-generic\n", "X64"));
        Assert.Equal("Debian GNU/Linux 13 (trixie) (Arm64)",
            ProcFiles.DescribeOs(new Dictionary<string, string> { ["NAME"] = "Debian GNU/Linux", ["VERSION"] = "13 (trixie)" }, null, "Arm64"));
        Assert.Equal("Linux (kernel 6.1.0, X64)", ProcFiles.DescribeOs(null, "6.1.0", "X64"));
    }

    [Theory]
    [InlineData("LENOVO", "20J6CTO1WW", "ThinkPad T470p", "LENOVO 20J6CTO1WW (ThinkPad T470p)")]
    [InlineData("Dell Inc.", "XPS 13 9310", "", "Dell Inc. XPS 13 9310")]
    [InlineData("System manufacturer", "System Product Name", "System Version", null)]
    [InlineData("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", "To Be Filled By O.E.M.", null)]
    [InlineData("HP", "HP EliteBook 840 G8 Notebook PC", "HP EliteBook", "HP EliteBook 840 G8 Notebook PC")]
    [InlineData(null, null, null, null)]
    public void Firmware_placeholders_never_pass_as_a_model(string? vendor, string? product, string? version, string? expected) =>
        Assert.Equal(expected, ProcFiles.DescribeModel(vendor, product, version));

    [Fact]
    public void The_processor_name_comes_from_the_first_model_name_line() =>
        Assert.Equal("AMD Ryzen 7 7840U w/ Radeon 780M Graphics",
            ProcFiles.CpuModelName("processor\t: 0\nmodel name\t: AMD Ryzen 7 7840U   w/ Radeon  780M Graphics\nmodel name\t: other\n"));

    [Fact]
    public void Mount_points_with_spaces_are_unescaped()
    {
        Assert.Equal("/media/an/My Passport", MountTable.Unescape(@"/media/an/My\040Passport"));
        Assert.Equal("a\tb\\c", MountTable.Unescape(@"a\011b\134c"));
        Assert.Equal(@"keep\9", MountTable.Unescape(@"keep\9"));
    }

    [Fact]
    public void Only_local_writable_disks_are_checked_once_each()
    {
        var removable = new HashSet<string>(StringComparer.Ordinal) { "/dev/sdb1" };
        var volumes = MountTable.LocalVolumes(MountTable.Parse(Fixtures.Mounts), removable.Contains);

        Assert.Equal(["/", "/boot/efi", "/media/an/My Passport"], volumes.Select(v => v.MountPoint));
        // The root's second mount is folded into the first, not reported as a second disk.
        Assert.Equal(["/var/snap/bind"], volumes[0].AlsoMountedAt);
        Assert.Equal("fuseblk", volumes[2].FsType);
    }

    [Fact]
    public void Network_filesystems_are_never_on_the_list()
    {
        // Asking these for free space would make a network connection; a scan makes none.
        foreach (var fs in new[] { "nfs", "nfs4", "cifs", "smb3", "fuse.sshfs", "fuse.rclone", "9p", "ceph", "glusterfs" })
            Assert.DoesNotContain(fs, MountTable.DiskFileSystems);
    }

    [Fact]
    public void Btrfs_subvolumes_and_zfs_datasets_count_once_per_pool()
    {
        var volumes = MountTable.LocalVolumes(MountTable.Parse("""
            /dev/nvme0n1p3 / btrfs rw,relatime,subvol=/@ 0 0
            /dev/nvme0n1p3 /home btrfs rw,relatime,subvol=/@home 0 0
            rpool/ROOT/ubuntu /srv zfs rw,xattr 0 0
            rpool/USERDATA/an /srv/an zfs rw,xattr 0 0
            tank/media /tank zfs rw,xattr 0 0
            """));

        Assert.Equal(["/", "/srv", "/tank"], volumes.Select(v => v.MountPoint));
        Assert.Equal(["/home"], volumes[0].AlsoMountedAt);
        Assert.Equal(["/srv/an"], volumes[1].AlsoMountedAt);
    }

    [Fact]
    public void Free_space_is_counted_like_df()
    {
        const long gb = 1L << 30;
        // 250 GB ext4, 30 GB free of which 12.5 GB is root's 5% reserve: df says 93% used.
        Assert.Equal(7.4, MountTable.FreePct(new VolumeSpace(250 * gb, 30 * gb, 30 * gb - 250 * gb / 20)));
        Assert.Equal(0, MountTable.FreePct(new VolumeSpace(100 * gb, 5 * gb, 0)));
        Assert.Null(MountTable.FreePct(new VolumeSpace(0, 0, 0)));
    }

    [Fact]
    public void Small_partitions_are_not_shown_as_zero_gigabytes()
    {
        // The first real kernel (CI, 2026-10-23) printed "0.1 GB free of 0 GB" for /boot/efi.
        Assert.Equal("106 MB", MountTable.FormatSize(111_149_056));
        Assert.Equal("144 GB", MountTable.FormatSize(144L << 30));
        Assert.Equal(2.5.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " GB",
            MountTable.FormatSize((5L << 30) / 2));
    }

    private static PowerSupplyEntry Supply(params (string Name, string Value)[] attributes) =>
        new("BAT0", attributes.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

    [Fact]
    public void Wear_uses_energy_and_falls_back_to_charge()
    {
        var energy = PowerSupply.Wear(Supply(("energy_full", "11960000"), ("energy_full_design", "23480000")));
        Assert.Equal(new BatteryWear(50.9, 11.96, 23.48, "Wh"), energy);

        var charge = PowerSupply.Wear(Supply(("charge_full", "3800000"), ("charge_full_design", "4400000")));
        Assert.Equal(new BatteryWear(86.4, 3800, 4400, "mAh"), charge);
    }

    [Fact]
    public void Wear_with_a_missing_or_zero_half_is_not_known()
    {
        Assert.Null(PowerSupply.Wear(Supply(("energy_full", "11960000"))));
        Assert.Null(PowerSupply.Wear(Supply(("energy_full", "0"), ("energy_full_design", "23480000"))));
        Assert.Null(PowerSupply.Wear(Supply()));
    }

    [Fact]
    public void A_peripheral_battery_is_not_the_computers()
    {
        var mouse = Supply(("type", "Battery"), ("scope", "Device"));
        var laptop = Supply(("type", "Battery"));
        var charger = Supply(("type", "Mains"));

        Assert.True(PowerSupply.IsPeripheralBattery(mouse));
        Assert.False(PowerSupply.IsSystemBattery(mouse));
        Assert.True(PowerSupply.IsSystemBattery(laptop));
        Assert.False(PowerSupply.IsSystemBattery(charger));
    }

    [Theory]
    [InlineData("Not charging", "79", "80", "79%, not charging; charge limit 80%")]
    [InlineData("Discharging", "41", null, "41%, on battery")]
    [InlineData("Charging", "62", "100", "62%, charging")]
    [InlineData("Unknown", "100", null, "100%")]
    public void Charge_state_reads_the_kernels_fixed_words(string status, string capacity, string? limit, string expected)
    {
        var attributes = new List<(string, string)> { ("status", status), ("capacity", capacity) };
        if (limit is not null) attributes.Add(("charge_control_end_threshold", limit));
        Assert.Equal(expected, PowerSupply.ChargeState(Supply([.. attributes])));
    }

    [Fact]
    public void The_serial_number_is_not_among_the_attributes_read() =>
        Assert.DoesNotContain("serial_number", PowerSupply.AttributesRead);

    private static HwmonChip Chip(string name, params (string? Label, double Celsius)[] temps) =>
        new("hwmon9", name, null, [.. temps.Select(t => new HwmonTemperature(t.Label, t.Celsius))]);

    [Fact]
    public void Intel_uses_the_package_reading_over_the_hottest_core()
    {
        var cpu = Temperatures.ProcessorSensor([Chip("coretemp", ("Package id 0", 71), ("Core 0", 69), ("Core 1", 73))], []);
        Assert.Equal(71, cpu?.Celsius);
        Assert.Equal("coretemp, package", cpu?.Source);

        var coresOnly = Temperatures.ProcessorSensor([Chip("coretemp", ("Core 0", 69), ("Core 1", 73))], []);
        Assert.Equal(73, coresOnly?.Celsius);
    }

    [Fact]
    public void Amd_prefers_tdie_and_takes_tctl_when_it_is_all_there_is()
    {
        Assert.Equal(58, Temperatures.ProcessorSensor([Chip("k10temp", ("Tctl", 78), ("Tdie", 58), ("Tccd1", 55))], [])?.Celsius);
        Assert.Equal(68.5, Temperatures.ProcessorSensor([Chip("k10temp", ("Tctl", 68.5), ("Tccd1", 60))], [])?.Celsius);
    }

    [Fact]
    public void A_driver_saying_no_reading_is_not_a_temperature()
    {
        Assert.Null(Temperatures.ProcessorSensor([Chip("coretemp", ("Package id 0", 0)), Chip("k10temp", ("Tctl", -128))], []));
        Assert.Equal(66, Temperatures.ProcessorSensor([Chip("coretemp", ("Package id 0", 255))],
            [new ThermalZone("thermal_zone1", "x86_pkg_temp", 66)])?.Celsius);
    }

    [Fact]
    public void The_acpi_zone_is_only_a_fallback_and_only_when_plausible()
    {
        ThermalZone[] zones = [new("thermal_zone0", "acpitz", 96), new("thermal_zone1", "acpitz", 27.8)];
        Assert.Null(Temperatures.ProcessorSensor([], zones));
        Assert.Equal(96, Temperatures.AcpiZone(zones)?.Celsius);
        // A fixed 27.8 °C placeholder alone is a reading nobody took.
        Assert.Null(Temperatures.AcpiZone([new("thermal_zone0", "acpitz", 27.8)]));
    }

    [Fact]
    public void Nvme_drives_report_their_composite_temperature()
    {
        var drives = Temperatures.NvmeDrives([
            new HwmonChip("hwmon3", "nvme", "nvme0", [new("Composite", 44.85), new("Sensor 1", 51.85)]),
            new HwmonChip("hwmon4", "nvme", null, [new(null, 39)]),
        ]);
        Assert.Equal([("nvme0", 44.85), ("hwmon4", 39.0)], drives.Select(d => (d.Subject, d.Celsius)));
    }

    [Fact]
    public void Amd_graphics_maps_edge_junction_and_memory()
    {
        var gpu = Temperatures.Graphics([Chip("amdgpu", ("edge", 55), ("junction", 62), ("mem", 70))]);
        Assert.Equal(
            [("gpu.temperature_c", 55.0), ("gpu.hotspot_c", 62.0), ("gpu.memory_junction_c", 70.0)],
            gpu.Select(g => (g.MetricId, g.Reading.Celsius)));
    }
}
