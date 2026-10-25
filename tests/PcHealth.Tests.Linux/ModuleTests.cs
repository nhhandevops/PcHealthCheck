using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using Xunit;

namespace PcHealth.Tests.Linux;

/// <summary>
/// The real Linux modules run against fake machines. What these lock: a check that could not
/// read must say so (rule 7), a desktop is skipped rather than failed, and nothing a module
/// does leaves a trace in the receipt, because these modules only read files.
/// </summary>
public sealed class ModuleTests
{
    private static readonly string[] ModuleIds = ["linux.overview", "linux.memory", "linux.volumes", "linux.battery", "linux.sensors"];
    private static readonly string[] CheckedVolumes = ["/", "/boot/efi", "/media/an/My Passport"];
    private static readonly string[] BothBatteries = ["BAT0", "BAT1"];

    private static Metric Only(ModuleResult result, string id, string? subject = null) =>
        Assert.Single(result.Metrics, m => m.Id == id && (subject is null || m.Subject == subject));

    [Fact]
    public void Every_linux_module_reads_only_needs_no_root_and_loads_nothing()
    {
        // The owner's decision of 2026-10-23, as code: unelevated only.
        using var fake = Fixtures.ThinkPad();
        var modules = fake.Provider().CreateModules();

        Assert.Equal(ModuleIds, modules.Select(m => m.Manifest.Id));
        Assert.All(modules, m =>
        {
            Assert.False(m.Manifest.NeedsAdmin);
            Assert.False(m.Manifest.NeedsInternet);
            Assert.False(m.Manifest.LoadsDriver);
            Assert.Null(m.Manifest.Writes);
            Assert.NotEmpty(m.Manifest.Reads);
        });
        // Rule 11: the temperature check must not measure the other checks.
        Assert.True(modules.Single(m => m.Manifest.Id == "linux.sensors").Manifest.RunsAlone);
    }

    [Fact]
    public async Task A_full_scan_runs_no_program_and_writes_no_file()
    {
        using var fake = Fixtures.ThinkPad();
        var context = FakeLinux.Context();

        foreach (var id in ModuleIds) await fake.Run(id, context);

        Assert.Empty(context.Receipt.Snapshot());
    }

    [Fact]
    public async Task Overview_names_the_distribution_kernel_model_and_uptime()
    {
        using var fake = Fixtures.ThinkPad();
        var result = await fake.Run("linux.overview");

        Assert.Equal(ModuleStatus.Completed, result.Status);
        Assert.StartsWith("Ubuntu 26.04 LTS (kernel 7.0.0-14-generic, ", Only(result, "os.build").Value.Text);
        Assert.Equal("LENOVO 20J6CTO1WW (ThinkPad T470p)", Only(result, "hw.model").Value.Text);
        Assert.Equal(4.1, Only(result, "os.uptime_days").Value.Number);
    }

    [Fact]
    public async Task Memory_grades_use_but_not_commit_on_an_overcommitting_system()
    {
        using var fake = Fixtures.ThinkPad();
        var result = await fake.Run("linux.memory");

        Assert.Equal(ModuleStatus.Completed, result.Status);
        Assert.Equal(39.1, Only(result, "memory.used_pct").Value.Number);
        // 121% promised is normal under the default heuristic overcommit, so it is shown, not graded.
        var commit = Only(result, "memory.commit_pct");
        Assert.Equal(121, commit.Value.Number);
        Assert.True(commit.InfoOnly);
        Assert.Equal("4.0 GB, 25% in use", Only(result, "memory.swap").Value.Text);
        Assert.Equal(0, Only(result, "memory.oom_kills_since_boot").Value.Number);
    }

    [Fact]
    public async Task Commit_is_graded_under_strict_overcommit()
    {
        using var fake = Fixtures.ThinkPad().File("/proc/sys/vm/overcommit_memory", "2\n");
        var commit = Only(await fake.Run("linux.memory"), "memory.commit_pct");

        Assert.False(commit.InfoOnly);
    }

    [Fact]
    public async Task An_unreadable_overcommit_setting_is_said_not_assumed()
    {
        using var fake = Fixtures.ThinkPad();
        File.Delete(Path.Combine(fake.Root, "proc/sys/vm/overcommit_memory"));
        var result = await fake.Run("linux.memory");

        Assert.Equal(ModuleStatus.PartiallyCompleted, result.Status);
        Assert.True(Only(result, "memory.commit_pct").InfoOnly);
    }

    [Fact]
    public async Task Without_meminfo_the_memory_check_failed_rather_than_passed()
    {
        using var fake = new FakeLinux();
        var result = await fake.Run("linux.memory");

        Assert.Equal(ModuleStatus.Failed, result.Status);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task A_kernel_without_the_oom_counter_says_it_was_not_checked()
    {
        using var fake = Fixtures.ThinkPad().File("/proc/vmstat", "nr_free_pages 203086\n");
        var result = await fake.Run("linux.memory");

        Assert.DoesNotContain(result.Metrics, m => m.Id == "memory.oom_kills_since_boot");
        Assert.Contains(result.Notes, n => n.Contains("NOT checked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Volumes_checks_local_disks_once_and_never_the_shares_or_the_usb_stick()
    {
        using var fake = Fixtures.ThinkPad();
        var result = await fake.Run("linux.volumes");

        Assert.Equal(ModuleStatus.Completed, result.Status);
        Assert.Equal(CheckedVolumes, result.Metrics.Select(m => m.Subject));
        Assert.Equal(7.4, Only(result, "storage.volume.free_pct", "/").Value.Number);
        Assert.Contains("/var/snap/bind", Only(result, "storage.volume.free_pct", "/").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_volume_whose_size_cannot_be_read_degrades_the_check()
    {
        using var fake = Fixtures.ThinkPad();
        fake.Spaces.Remove("/boot/efi");
        var result = await fake.Run("linux.volumes");

        Assert.Equal(ModuleStatus.PartiallyCompleted, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("/boot/efi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inside_a_container_no_disk_is_not_reported_as_all_disks_fine()
    {
        using var fake = new FakeLinux().File("/proc/self/mounts", "overlay / overlay rw,relatime 0 0\nproc /proc proc rw 0 0\n");
        var result = await fake.Run("linux.volumes");

        Assert.Equal(ModuleStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Battery_reports_each_pack_and_never_the_serial_number()
    {
        using var fake = Fixtures.ThinkPad();
        var result = await fake.Run("linux.battery");

        Assert.Equal(ModuleStatus.Completed, result.Status);
        Assert.Equal(BothBatteries, result.Metrics.Where(m => m.Id == "battery.capacity_pct").Select(m => m.Subject));
        Assert.Equal(50.9, Only(result, "battery.capacity_pct", "BAT0").Value.Number);
        Assert.Equal(77.8, Only(result, "battery.capacity_pct", "BAT1").Value.Number);
        Assert.Equal("79%, not charging; charge limit 80%", Only(result, "battery.charge_state", "BAT0").Value.Text);
        Assert.Equal(712, Only(result, "battery.cycles", "BAT0").Value.Number);
        // BAT1 reports 0 cycles, which means "not counted", not "new".
        Assert.DoesNotContain(result.Metrics, m => m.Id == "battery.cycles" && m.Subject == "BAT1");
        // The wireless mouse is mentioned, not graded.
        Assert.DoesNotContain(result.Metrics, m => m.Subject == "hidpp_battery_0");
        Assert.Contains(result.Notes, n => n.Contains("hidpp_battery_0", StringComparison.Ordinal));

        var everything = string.Join('\n', result.Metrics.Select(m => $"{m.Value} {m.Detail}").Concat(result.Notes));
        Assert.DoesNotContain(Fixtures.SerialNumber, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gauge_claiming_more_than_the_pack_was_built_to_hold_is_not_graded()
    {
        // The Windows side of this was found on a real laptop in the first fleet audit: a pack
        // reading 109.7% of design was graded Good. The kernel's figures are no safer, so the
        // same guard has to hold here, or the bug simply moves to the other operating system.
        using var fake = new FakeLinux()
            .Attributes("/sys/class/power_supply/BAT0",
                ("type", "Battery"), ("present", "1"), ("status", "Charging"), ("capacity", "2"),
                ("energy_full", "25800000"), ("energy_full_design", "23510000"));
        var result = await fake.Run("linux.battery");

        var capacity = Only(result, "battery.capacity_pct");
        Assert.Equal(109.7, capacity.Value.Number);
        // Shown, so nobody thinks the check was skipped - but InfoOnly, so no rule can grade it.
        Assert.True(capacity.InfoOnly);
        Assert.Contains("does not measure wear", capacity.Detail, StringComparison.Ordinal);
        // And the scan says out loud that wear is still unknown.
        Assert.Equal(ModuleStatus.PartiallyCompleted, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("NOT established", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ordinary_worn_pack_is_still_graded()
    {
        // The other direction, and the one that matters as much: if this guard ever starts
        // firing on normal packs, every healthy laptop reads "could not finish" and the warning
        // stops meaning anything.
        using var fake = new FakeLinux()
            .Attributes("/sys/class/power_supply/BAT0",
                ("type", "Battery"), ("present", "1"), ("status", "Full"), ("capacity", "100"),
                ("energy_full", "23980000"), ("energy_full_design", "23510000"));
        var result = await fake.Run("linux.battery");

        var capacity = Only(result, "battery.capacity_pct");
        Assert.Equal(102, capacity.Value.Number);
        Assert.False(capacity.InfoOnly);
        Assert.Equal(ModuleStatus.Completed, result.Status);
    }

    [Fact]
    public async Task One_pack_in_two_slots_is_just_the_battery()
    {
        using var fake = Fixtures.ThinkPad().File("/sys/class/power_supply/BAT1/present", "0\n");
        var result = await fake.Run("linux.battery");

        Assert.Equal("Battery", Only(result, "battery.capacity_pct").Subject);
        Assert.Contains(result.Notes, n => n.Contains("BAT1: the battery slot is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_desktop_with_a_wireless_mouse_is_skipped_not_failed()
    {
        using var fake = new FakeLinux()
            .Attributes("/sys/class/power_supply/hidpp_battery_0", ("type", "Battery"), ("scope", "Device"), ("capacity", "55"));
        var result = await fake.Run("linux.battery");

        Assert.Equal(ModuleStatus.Skipped, result.Status);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task A_kernel_without_power_supply_support_has_no_battery()
    {
        using var fake = new FakeLinux().Dir("/sys/class/block");
        Assert.Equal(ModuleStatus.Skipped, (await fake.Run("linux.battery")).Status);
    }

    [Fact]
    public async Task Without_sysfs_whether_there_is_a_battery_is_unknown()
    {
        using var fake = new FakeLinux();
        var result = await fake.Run("linux.battery");

        Assert.Equal(ModuleStatus.Failed, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("NOT checked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_battery_without_capacity_figures_is_not_passed_as_healthy()
    {
        using var fake = new FakeLinux()
            .Attributes("/sys/class/power_supply/BAT0", ("type", "Battery"), ("present", "1"), ("status", "Discharging"), ("capacity", "64"));
        var result = await fake.Run("linux.battery");

        Assert.Equal(ModuleStatus.PartiallyCompleted, result.Status);
        Assert.DoesNotContain(result.Metrics, m => m.Id == "battery.capacity_pct");
    }

    [Fact]
    public async Task Sensors_read_the_processor_package_and_the_nvme_drive()
    {
        using var fake = Fixtures.ThinkPad();
        var result = await fake.Run("linux.sensors");

        Assert.Equal(ModuleStatus.Completed, result.Status);
        var cpu = Only(result, "cpu.temperature_c");
        Assert.Equal(71, cpu.Value.Number);
        Assert.Equal("Intel(R) Core(TM) i7-7820HQ CPU @ 2.90GHz", cpu.Subject);
        Assert.Equal(45, Only(result, "storage.temperature_c").Value.Number);
        Assert.Equal(HealthCategory.Storage, Only(result, "storage.temperature_c").Category);
        // The processor's own sensor was found, so the ACPI zone is not reported beside it.
        Assert.DoesNotContain(result.Metrics, m => m.Id == "cpu.zone_temperature_c");
    }

    [Fact]
    public async Task Without_a_processor_sensor_the_acpi_zone_stands_in()
    {
        using var fake = new FakeLinux()
            .Dir("/sys/class/hwmon")
            .Attributes("/sys/class/thermal/thermal_zone0", ("type", "acpitz"), ("temp", "96000"));
        var result = await fake.Run("linux.sensors");

        Assert.Equal(96, Only(result, "cpu.zone_temperature_c").Value.Number);
    }

    [Fact]
    public async Task A_virtual_machine_without_sensors_says_the_processor_was_not_read()
    {
        using var fake = new FakeLinux().Dir("/sys/class/hwmon").Dir("/sys/class/thermal");
        var result = await fake.Run("linux.sensors");

        Assert.Equal(ModuleStatus.Completed, result.Status);
        var notRead = Only(result, "sensors.cpu_temperature_read");
        Assert.False(notRead.Value.Flag);
        Assert.True(notRead.InfoOnly);
    }

    [Fact]
    public async Task Without_sysfs_the_sensor_check_failed()
    {
        using var fake = new FakeLinux();
        Assert.Equal(ModuleStatus.Failed, (await fake.Run("linux.sensors")).Status);
    }
}
