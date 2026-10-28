using PcHealth.Core.Advice;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Benchmarks;
using PcHealth.Modules.Windows.Storage;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// REMEDIES-PLAN Step A rows 1, 2, 4, 5 and the encryption half of row 11: the PC is graded on its
/// own drives, not on the USB drives plugged into it (D1, H3, row 11); an SSD's temperature is
/// graded as an SSD's and TRIM only where an SSD is (H7); and the disk benchmark never writes to
/// a drive this scan already calls failing (H2). Each is held in both directions: the false alarm
/// is gone, and the real finding on the PC's own drive still stands. Step B adds the rows that tell
/// the advice which disk is which (STEP-B-SPEC 2.4) and the one list of damage readings (2.5).
/// </summary>
public sealed class UsbAndDriveKindTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded());

    private static Grade GradeOf(Metric metric) => Engine.EvaluateOne(metric).Grade;

    // The pilot T470p as the kit found it: its SATA SSD on disk 0 (Windows runs from it), a laptop
    // hard disk in a USB caddy on disk 1 and the audit drive on disk 2.
    private static readonly AttachedDisk[] T470p =
    [
        new(0, BusType: 11, IsBoot: true),
        new(1, BusType: DiskBus.Usb, IsBoot: false),
        new(2, BusType: DiskBus.Usb, IsBoot: false),
    ];

    private static DriveMap T470pMap() => new(T470p,
        [KeyValuePair.Create("C", 0), KeyValuePair.Create("E:", 1), KeyValuePair.Create(@"F:\", 2)], @"C:\");

    private static readonly string[] TwoUsbDrives = ["disk 1: 34", "disk 2: 13"];

    private static string[] Errors(int disk, int count) => [.. Enumerable.Repeat($@"\Device\Harddisk{disk}\DR{disk}", count)];

    // --- D1: a full USB drive is not this PC's disk nearly full ----------------------------------

    [Fact]
    public void A_nearly_full_usb_drive_is_shown_but_not_graded()
    {
        // The pilot's only "disk nearly full" on three laptops: the auditor's drive at 18% free.
        var metric = VolumeReadings.FreeSpace("F: (An)", 966_367_641_600, 173_626_408_960, DiskBus.Usb);

        Assert.True(metric.InfoOnly);
        Assert.Equal(Grade.Unknown, GradeOf(metric));
        Assert.Contains("does not count toward this PC's health", metric.Detail, StringComparison.Ordinal);
        Assert.Contains("161.7 GB free of 900 GB", metric.Detail, StringComparison.Ordinal);
        Assert.Contains("Recycle Bin", metric.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nearly_full_internal_drive_is_still_graded()
    {
        var metric = VolumeReadings.FreeSpace("C:", 966_367_641_600, 48_318_382_080, externalBus: null);

        Assert.False(metric.InfoOnly);
        Assert.Equal(Grade.Bad, GradeOf(metric));
    }

    [Fact]
    public void Volume_sizes_are_written_the_same_way_on_a_comma_decimal_locale()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("vi-VN");
            var metric = VolumeReadings.FreeSpace("E:", 966_367_641_600, 173_626_408_960, DiskBus.Usb);
            Assert.Contains("161.7 GB", metric.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData("E:", DiskBus.Usb)]
    [InlineData("f", DiskBus.Usb)]
    public void A_volume_on_a_usb_disk_is_external(string letter, ushort expected) =>
        Assert.Equal(expected, T470pMap().ExternalBusOf(letter));

    [Theory]
    [InlineData("C:")]   // on the internal SSD
    [InlineData("Z:")]   // a letter on no disk Windows listed
    [InlineData(null)]
    public void A_volume_not_known_to_be_external_counts_as_this_pcs(string? letter) =>
        Assert.Null(T470pMap().ExternalBusOf(letter));

    [Fact]
    public void The_drive_windows_runs_from_is_always_graded_whatever_its_connection()
    {
        // Windows started from a USB drive: its volumes are this PC's, and so is another volume on it.
        var map = new DriveMap([new AttachedDisk(3, DiskBus.Usb, IsBoot: true)],
            [KeyValuePair.Create("C:", 3), KeyValuePair.Create("D:", 3)], @"C:\");

        Assert.Null(map.ExternalBusOf("C:"));
        Assert.Null(map.ExternalBusOf("D:"));
    }

    [Theory]
    [InlineData((ushort)0)]    // unknown bus
    [InlineData((ushort)8)]    // RAID
    [InlineData((ushort)17)]   // NVMe
    public void An_unknown_or_internal_bus_counts_as_this_pcs(ushort bus)
    {
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(1, bus, false)], [KeyValuePair.Create("D:", 1)], "C:");

        Assert.Null(map.ExternalBusOf("D:"));
    }

    [Theory]
    [InlineData(DiskBus.Sd, "a memory card")]
    [InlineData(DiskBus.Firewire, "a FireWire drive")]
    public void The_kits_other_external_buses_count_as_external_too(ushort bus, string name)
    {
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(1, bus, false)], [KeyValuePair.Create("D:", 1)], "C:");

        Assert.Equal(bus, map.ExternalBusOf("D:"));
        Assert.Contains(name, VolumeReadings.NotCounted(bus), StringComparison.Ordinal);
    }

    // --- Row 11: a USB drive's encryption is not this PC's --------------------------------------

    // Win32_EncryptableVolume.ConversionStatus
    private const uint FullyDecrypted = 0, FullyEncrypted = 1, EncryptionInProgress = 2, DecryptionInProgress = 3,
        EncryptionPaused = 4, DecryptionPaused = 5;

    [Fact]
    public void An_unencrypted_usb_drive_is_shown_but_not_graded()
    {
        var metric = VolumeReadings.Encryption("F:", 0, FullyDecrypted, DiskBus.Usb);

        Assert.True(metric.InfoOnly);
        Assert.Equal(Grade.Unknown, GradeOf(metric));
        Assert.Contains("not encrypted", metric.Detail, StringComparison.Ordinal);
        Assert.Contains("does not count toward this PC's health", metric.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Principle 10: every reading is named on the consent row. The platform-security check began
    /// reading which disk each drive letter is on for this (SEC-001) without saying so, while the
    /// event-log and volume checks, given the same reading, did. And the SMART check's row claimed a
    /// serial-number read the app never makes (review of 2026-10-28).
    /// </summary>
    [Fact]
    public void Every_check_that_reads_how_drives_are_connected_says_so_and_none_claims_a_serial()
    {
        var modules = new WindowsModuleProvider().CreateModules().ToDictionary(m => m.Manifest.Id, StringComparer.Ordinal);

        foreach (var id in new[] { "security.platform", "volumes", "events.errors" })
            Assert.Contains(modules[id].Manifest.Reads, r => r.Contains("connection", StringComparison.Ordinal));
        Assert.All(modules.Values, m =>
            Assert.DoesNotContain(m.Manifest.Reads, r => r.Contains("serial", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(0u, FullyDecrypted, Grade.Normal)]
    [InlineData(0u, null, Grade.Normal)]          // Windows did not say how far encryption got: still not protected
    [InlineData(1u, FullyEncrypted, Grade.Good)]
    [InlineData(1u, null, Grade.Good)]
    public void The_internal_drives_encryption_is_graded_as_before(uint protection, uint? conversion, Grade expected)
    {
        var metric = VolumeReadings.Encryption("C:", protection, conversion, externalBus: null);

        Assert.False(metric.InfoOnly);
        Assert.Equal(expected, GradeOf(metric));
        Assert.DoesNotContain("for information", metric.Detail, StringComparison.Ordinal);
    }

    // --- I1 (GOLDEN-IMAGE-PLAN): encrypted with its key in the clear is not "not encrypted" -----

    [Theory]
    [InlineData(null)]         // an internal drive
    [InlineData(DiskBus.Usb)]  // a suspended BitLocker To Go stick
    public void A_fully_encrypted_drive_with_its_key_in_the_clear_says_so(ushort? bus)
    {
        // What Windows 11 device encryption leaves on a PC with only local accounts, and what a
        // suspended BitLocker looks like: ProtectionStatus 0, ConversionStatus fully encrypted.
        var metric = VolumeReadings.Encryption("C:", 0, FullyEncrypted, bus);

        Assert.Equal(false, metric.Value.Flag);
        Assert.Equal(bus is null ? Grade.Normal : Grade.Unknown, GradeOf(metric)); // not protected, like an unencrypted drive
        Assert.Contains("encrypted, but its key is in the clear (device encryption waiting, or BitLocker suspended)",
            metric.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("not encrypted", metric.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Engine.EvaluateOne(metric).Tips, t => t.Contains("not encrypted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0u, EncryptionInProgress, "has started and is not finished")]
    [InlineData(1u, EncryptionInProgress, "has started and is not finished")]
    [InlineData(0u, EncryptionPaused, "is paused")]
    [InlineData(0u, DecryptionInProgress, "decrypting")]
    [InlineData(0u, DecryptionPaused, "decrypting")]
    public void Encryption_or_decryption_under_way_is_not_finished(uint protection, uint conversion, string expected)
    {
        var metric = VolumeReadings.Encryption("C:", protection, conversion, externalBus: null);

        Assert.Equal(false, metric.Value.Flag);
        Assert.Equal(Grade.Normal, GradeOf(metric));
        Assert.Contains(expected, metric.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2u, FullyEncrypted)]   // a locked BitLocker data drive
    [InlineData(2u, null)]
    [InlineData(null, null)]           // the value was missing
    [InlineData(7u, FullyDecrypted)]   // a value Windows does not document
    public void An_encryption_state_windows_could_not_tell_is_not_graded_and_does_not_read_as_no(uint? protection, uint? conversion)
    {
        var metric = VolumeReadings.Encryption("D:", protection, conversion, externalBus: null);

        Assert.True(metric.InfoOnly);
        Assert.Equal(Grade.Unknown, GradeOf(metric));
        Assert.Null(metric.Value.Flag);
        Assert.Equal("unknown", metric.Value.ToString());
        Assert.Contains("could not be determined", metric.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("not encrypted", metric.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_encryption_tip_sends_home_users_where_device_encryption_lives_and_no_further()
    {
        // The old tip offered "Device Encryption (Home)" as if every Home PC had it. Most older
        // laptops, the pilot's T470p among them, do not.
        var tips = Engine.EvaluateOne(VolumeReadings.Encryption("C:", 0, FullyDecrypted, null)).Tips;

        Assert.Contains(tips, t => t.Contains("ms-settings:deviceencryption", StringComparison.Ordinal)
                                   && t.Contains("Home has no BitLocker", StringComparison.Ordinal)
                                   && t.Contains("needs Pro", StringComparison.Ordinal));
        Assert.DoesNotContain(tips, t => t.Contains("Device Encryption (Home)", StringComparison.Ordinal));
    }

    // --- H3: disk errors logged by USB drives are not this PC's ---------------------------------

    [Fact]
    public void Errors_only_on_usb_drives_leave_this_pc_graded_good()
    {
        // KIT-005's real case: 47 errors, 34 and 13 on two USB drives, none on the internal SSD.
        var share = DiskEventAttribution.Share([.. Errors(1, 34), .. Errors(2, 13)], T470p);

        Assert.Equal(47, share.Total);
        Assert.Equal(0, share.AgainstThisPc);
        Assert.Equal(TwoUsbDrives, share.OnExternalDrives);
        Assert.Equal(Grade.Good, GradeOf(new Metric("events.disk_errors_90d", "x", share.AgainstThisPc, HealthCategory.Storage)));
        Assert.Contains("Counted against this PC: 0 of 47.", DiskEventAttribution.DescribeShare(share), StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_on_the_drive_windows_runs_from_still_count_and_usb_ones_are_not_added()
    {
        var share = DiskEventAttribution.Share([.. Errors(0, 12), .. Errors(2, 13)], T470p);

        Assert.Equal(12, share.AgainstThisPc);
        Assert.Equal(Grade.Bad, GradeOf(new Metric("events.disk_errors_90d", "x", share.AgainstThisPc, HealthCategory.Storage)));
    }

    [Fact]
    public void Errors_on_a_second_internal_drive_and_errors_naming_no_drive_count()
    {
        AttachedDisk[] desktop = [new(0, DiskBus.Nvme, true), new(1, 11, false), new(2, 0, false)];

        var share = DiskEventAttribution.Share([.. Errors(1, 4), .. Errors(2, 1), "C:", null], desktop);

        Assert.Equal(7, share.AgainstThisPc);
        Assert.Equal("", DiskEventAttribution.DescribeShare(share));
    }

    [Fact]
    public void Errors_on_a_disk_number_nothing_is_attached_at_are_reported_not_counted()
    {
        var share = DiskEventAttribution.Share(Errors(3, 8), T470p);

        Assert.Equal(0, share.AgainstThisPc);
        Assert.Equal("disk 3: 8", Assert.Single(share.OnDetachedDrives));
        Assert.Contains("not attached now (disk 3: 8)", DiskEventAttribution.DescribeShare(share), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_list_of_attached_disks_every_error_counts()
    {
        var share = DiskEventAttribution.Share([.. Errors(1, 34), .. Errors(2, 13)], attached: null);

        Assert.Equal(47, share.AgainstThisPc);
        Assert.Equal("", DiskEventAttribution.DescribeShare(share));
    }

    [Fact]
    public void The_share_sentence_never_uses_the_words_the_kit_reads_the_split_from()
    {
        // The kit adds up "on other drives:", "by disk number:" and "no disk named:" lists; the
        // share sentence names the same disks again and must not be counted twice.
        var text = DiskEventAttribution.DescribeShare(DiskEventAttribution.Share([.. Errors(1, 2), .. Errors(3, 1)], T470p));

        Assert.DoesNotContain("on other drives:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("by disk number:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no disk named:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows runs from", text, StringComparison.Ordinal);
    }

    // --- H7: an SSD is graded as an SSD; TRIM only where an SSD is ------------------------------

    [Theory]
    [InlineData(40, Grade.Good)]    // pilot laptop A's SATA SSD, Normal on hard-disk bands
    [InlineData(70, Grade.Good)]
    [InlineData(75, Grade.Normal)]
    [InlineData(85, Grade.Bad)]
    public void An_ssds_temperature_is_graded_on_ssd_bands(double celsius, Grade expected)
    {
        var metric = SmartTemperature.Reading("Example SSD — 240 GB", 0, celsius, DriveClass.SataSsd);

        Assert.Equal(expected, GradeOf(metric));
        Assert.DoesNotContain("Mechanical", metric.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DriveClass.Hdd)]
    [InlineData(DriveClass.External)]   // a USB caddy that calls its hard disk an SSD
    [InlineData(DriveClass.Unknown)]
    public void A_hard_disk_or_a_drive_windows_did_not_describe_keeps_the_hard_disk_bands(DriveClass driveClass)
    {
        var metric = SmartTemperature.Reading("Example drive — 1000 GB", 1, 45, driveClass);

        Assert.Equal("smart.temperature_c", metric.Id);
        Assert.Equal(Grade.Normal, GradeOf(metric));
        Assert.Contains("40 °C", metric.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pc_with_only_hard_disks_inside_has_no_trim_row()
    {
        // A USB SSD does not make TRIM this PC's question: enclosures misreport what is inside.
        Assert.False(DiskBus.AnyInternalSsd([(3, 11), (4, DiskBus.Usb)]));
    }

    [Theory]
    [InlineData((ushort)4, (ushort)11)]   // SATA SSD
    [InlineData((ushort)0, (ushort)17)]   // NVMe that did not give its media type
    public void A_pc_with_an_ssd_inside_keeps_the_trim_row(ushort media, ushort bus) =>
        Assert.True(DiskBus.AnyInternalSsd([(3, 11), (media, bus)]));

    [Fact]
    public void A_drive_that_did_not_say_what_it_is_keeps_the_trim_row()
    {
        Assert.Null(DiskBus.AnyInternalSsd([(3, 11), (0, 11)]));
        Assert.Null(DiskBus.AnyInternalSsd([(4, DiskBus.Usb)]));
        Assert.Null(DiskBus.AnyInternalSsd([]));
    }

    // --- H2: the benchmark never writes to a drive this scan calls failing ----------------------

    private static Verdict Finding(string id, MetricValue value, int disk) =>
        Engine.EvaluateOne(new Metric(id, id switch
        {
            "smart.reallocated" => "Reallocated sectors",
            "smart.predict_failure" => "Drive self-assessment",
            _ => id,
        }, value, HealthCategory.Storage, Subject: "Example SSD — 240 GB", DiskNumber: disk));

    private static VolumeInfo Volume(string letter, int disk) => new(letter, disk, 400L * 1024 * 1024 * 1024);

    [Fact]
    public void Of_two_identical_drives_only_the_failing_one_is_skipped()
    {
        // Same model, same size, same subject: only the disk number tells them apart.
        var failing = FailingDrives.Find(
            [Finding("smart.reallocated", 5, disk: 1), Finding("smart.reallocated", 0, disk: 2)], [], [], []);

        var (targets, skipped) = BenchmarkTargetSelector.Choose(
            [Volume("D:", 1), Volume("E:", 2)],
            [new PhysicalDiskInfo(1, "Example SSD", DriveClass.SataSsd), new PhysicalDiskInfo(2, "Example SSD", DriveClass.SataSsd)],
            1L << 30, failing);

        Assert.Equal("E:", Assert.Single(targets).DriveLetter);
        var reason = Assert.Single(skipped);
        Assert.Contains("D:", reason, StringComparison.Ordinal);
        Assert.Contains("\"Reallocated sectors: 5\" Bad", reason, StringComparison.Ordinal);
        Assert.Contains("backed up and left alone", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drive_whose_own_firmware_predicts_failure_is_skipped()
    {
        var failing = FailingDrives.Find([Finding("smart.predict_failure", false, disk: 0)], [], [], []);

        Assert.Equal("this scan's own drive check graded \"Drive self-assessment\" Bad", failing[0]);
    }

    // The T470p's drives as the benchmark reads them: C: on the internal SSD, E: on the USB caddy,
    // F: on the audit drive.
    private static readonly VolumeInfo[] T470pVolumes = [Volume("C:", 0), Volume("E:", 1), Volume("F:", 2)];

    private static readonly PhysicalDiskInfo[] T470pDrives =
    [
        new(0, "Example SSD", DriveClass.SataSsd),
        new(1, "Example caddy", DriveClass.External),
        new(2, "Example stick", DriveClass.External),
    ];

    [Fact]
    public void Disk_errors_in_the_event_log_naming_a_drive_skip_it()
    {
        var failing = FailingDrives.Find([], Errors(2, 13), T470pVolumes, T470pDrives);

        Assert.Equal(2, Assert.Single(failing.Keys));
        Assert.Contains("13 disk errors", failing[2], StringComparison.Ordinal);
        Assert.Contains("name this drive's number", failing[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Disk_errors_that_name_no_drive_keep_the_benchmark_off_every_drive_inside_the_pc()
    {
        // A controller reset (storahci/stornvme 129) names the controller, \Device\RaidPort0, not a
        // disk. This scan counts such errors against the PC and grades them Bad ("back up now"), so
        // the drive inside it may be the failing one (project rule 4). USB drives are still measured.
        string?[] resets = [.. Enumerable.Repeat(@"\Device\RaidPort0", 12), null];
        Assert.Equal(13, DiskEventAttribution.Share(resets, T470p).AgainstThisPc);

        var failing = FailingDrives.Find([], resets, T470pVolumes, T470pDrives);
        var (targets, skipped) = BenchmarkTargetSelector.Choose(T470pVolumes, T470pDrives, 1L << 30, failing);

        Assert.Equal(0, Assert.Single(failing.Keys));
        Assert.Equal(["E:", "F:"], targets.Select(t => t.DriveLetter));
        var reason = Assert.Single(skipped);
        Assert.StartsWith("C: (Example SSD): the event log holds 13 disk errors from the last 90 days that name no drive",
            reason, StringComparison.Ordinal);
        Assert.Contains("backed up and left alone", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("E:", 1)]   // Ntfs 55 names the volume: the USB caddy's, so only the caddy is left alone
    [InlineData("c:", 0)]
    [InlineData(@"C:\", 0)]
    public void File_system_errors_skip_the_drive_their_volume_is_on(string volume, int disk)
    {
        var failing = FailingDrives.Find([], [volume, volume], T470pVolumes, T470pDrives);

        Assert.Equal(disk, Assert.Single(failing.Keys));
        Assert.Contains("2 disk errors", failing[disk], StringComparison.Ordinal);
        Assert.Contains("on this drive's volume", failing[disk], StringComparison.Ordinal);
    }

    [Fact]
    public void A_volume_that_is_not_there_now_counts_as_naming_no_drive()
    {
        // Its letter may have moved: nothing shows which drive it was, so every drive inside the PC.
        var failing = FailingDrives.Find([], ["Z:", @"\\?\Volume{00000000-0000-0000-0000-000000000000}"], T470pVolumes, T470pDrives);

        Assert.Equal(0, Assert.Single(failing.Keys));
        Assert.Contains("2 disk errors", failing[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_that_name_only_usb_drives_leave_the_drive_inside_the_pc_measured()
    {
        var failing = FailingDrives.Find([], [.. Errors(1, 34), .. Errors(2, 13), "F:"], T470pVolumes, T470pDrives);
        var (targets, _) = BenchmarkTargetSelector.Choose(T470pVolumes, T470pDrives, 1L << 30, failing);

        Assert.Equal([1, 2], failing.Keys);
        Assert.Equal("C:", Assert.Single(targets).DriveLetter);
    }

    [Fact]
    public void Cable_errors_and_temperature_do_not_stop_a_measurement()
    {
        // They say nothing about whether writing to the drive harms it (REMEDIES-PLAN H2).
        var failing = FailingDrives.Find(
            [Finding("smart.crc_errors", 500, disk: 0), Finding("smart.command_timeout", 500, disk: 0),
             Finding("smart.ssd_temperature_c", 90, disk: 0)], [], [], []);

        Assert.Empty(failing);
    }

    [Fact]
    public void A_healthy_drive_is_measured_and_a_drive_never_read_is_measured_with_a_note()
    {
        Verdict[] findings = [Finding("smart.reallocated", 0, disk: 0)];

        Assert.Empty(FailingDrives.Find(findings, [], [], []));
        Assert.True(FailingDrives.HealthWasRead(findings, 0));
        Assert.False(FailingDrives.HealthWasRead(findings, 1));
    }

    /// <summary>
    /// STEP-B-SPEC 2.5: the benchmark's "leave this drive alone" and the advice's "back this drive
    /// up" read one list, so they cannot disagree about which drive is failing.
    /// </summary>
    [Fact]
    public void The_benchmark_and_the_advice_read_one_list_of_damage_readings()
    {
        Assert.Equal(
            ["smart.attributes_at_threshold", "smart.offline_uncorrectable", "smart.pending_sectors", "smart.predict_failure",
             "smart.reallocated", "smart.reported_uncorrectable", "storage.read_errors_uncorrected", "storage.write_errors_uncorrected"],
            DriveDamage.MetricIds.Order(StringComparer.Ordinal));

        foreach (var id in DriveDamage.MetricIds)
        {
            var damaged = new Verdict(new Metric(id, id, 1, HealthCategory.Storage, DiskNumber: 3), Grade.Bad, null, [], null);
            Assert.True(FailingDrives.Find([damaged], [], [], []).ContainsKey(3), id);
        }

        // The advice also gathers the two wear readings, which the benchmark does not refuse.
        Assert.True(DriveDamage.MetricIds.IsSubsetOf(AdviceFacts.DriveReadingIds));
        Assert.Equal(["smart.ssd_percent_consumed", "storage.wear_pct"],
            AdviceFacts.DriveReadingIds.Except(DriveDamage.MetricIds).Order(StringComparer.Ordinal));
    }

    // --- STEP-B-SPEC 2.4: where each disk is, so advice can tell this PC's drive from a USB drive ---

    private const ulong Gib = 1UL << 30;

    private static (string Letter, ulong Size, ulong Free) Measured(string letter, double sizeGb, double freeGb) =>
        (letter, (ulong)(sizeGb * Gib), (ulong)(freeGb * Gib));

    private static Metric RoleRow(IReadOnlyList<Metric> rows, int disk) =>
        Assert.Single(rows, r => r.Id == "storage.disk.role" && r.DiskNumber == disk);

    private static Metric? UsedRow(IReadOnlyList<Metric> rows, int disk) =>
        rows.SingleOrDefault(r => r.Id == "storage.disk.used_gb" && r.DiskNumber == disk);

    [Fact]
    public void Every_attached_disk_gets_a_role_and_a_usb_disk_is_external()
    {
        // The T470p: C: on the internal SSD (disk 0), E: on a USB caddy, F: on the audit drive.
        var rows = DiskRoles.From(T470pMap(), [Measured("C", 237, 120), Measured("E:", 931, 500), Measured("F:", 900, 161.7)]);

        Assert.Equal(["system", "external", "external"], Enumerable.Range(0, 3).Select(d => RoleRow(rows, d).Value.Text));
        Assert.Equal(["C:", "E:", "F:"], Enumerable.Range(0, 3).Select(d => RoleRow(rows, d).Subject));
        Assert.All(rows, r =>
        {
            Assert.True(r.InfoOnly);
            Assert.Equal(Grade.Unknown, GradeOf(r));
            Assert.Equal(HealthCategory.Storage, r.Category);
        });
        Assert.Equal("The disk Windows runs from. It holds C:.", RoleRow(rows, 0).Detail);
        Assert.Equal("Plugged in from outside this PC: a USB drive. It holds F:.", RoleRow(rows, 2).Detail);
        Assert.Equal("Disk role", RoleRow(rows, 0).Label);
    }

    [Theory]
    [InlineData(true, null)]       // MSFT_Disk says Windows started from it, and the Windows folder was not read
    [InlineData(false, @"C:\")]    // it did not say so, but it holds the drive Windows runs from
    public void The_disk_windows_runs_from_is_the_system_disk_even_on_usb(bool isBoot, string? systemDrive)
    {
        var map = new DriveMap([new AttachedDisk(3, DiskBus.Usb, isBoot), new AttachedDisk(4, DiskBus.Usb, false)],
            [KeyValuePair.Create("C:", 3), KeyValuePair.Create("D:", 3), KeyValuePair.Create("E:", 4)], systemDrive);

        var rows = DiskRoles.From(map, []);

        Assert.Equal("system", RoleRow(rows, 3).Value.Text);
        Assert.Equal("C:, D:", RoleRow(rows, 3).Subject);
        Assert.Equal("The disk Windows runs from. It is a USB drive, and counts as this PC's own. It holds C:, D:.", RoleRow(rows, 3).Detail);
        Assert.Equal("external", RoleRow(rows, 4).Value.Text);
    }

    [Theory]
    [InlineData((ushort)0, "Counted as inside this PC: Windows did not say how it is connected.")]
    [InlineData(null, "Counted as inside this PC: Windows did not say how it is connected.")]
    [InlineData((ushort)8, "Inside this PC.")]     // RAID
    [InlineData((ushort)17, "Inside this PC.")]    // NVMe
    [InlineData((ushort)11, "Inside this PC.")]    // SATA
    public void An_unknown_raid_or_nvme_bus_is_internal(ushort? bus, string where)
    {
        var map = new DriveMap([new AttachedDisk(0, DiskBus.Nvme, true), new AttachedDisk(1, bus, false)],
            [KeyValuePair.Create("C:", 0), KeyValuePair.Create("D:", 1)], "C:");

        var row = RoleRow(DiskRoles.From(map, []), 1);

        Assert.Equal("internal", row.Value.Text);
        Assert.Equal($"{where} It holds D:.", row.Detail);
    }

    [Theory]
    [InlineData(DiskBus.Sd)]
    [InlineData(DiskBus.Mmc)]
    [InlineData(DiskBus.Firewire)]
    public void The_kits_other_external_buses_give_an_external_disk(ushort bus)
    {
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(1, bus, false)], [KeyValuePair.Create("D:", 1)], "C:");

        Assert.Equal("external", RoleRow(DiskRoles.From(map, []), 1).Value.Text);
    }

    [Fact]
    public void A_disk_with_no_drive_letter_is_named_by_its_number_and_has_no_space_in_use()
    {
        // A blank disk, or one holding only another system's partitions: nothing this check measured,
        // which is not the same as nothing in use.
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(2, 11, false)], [KeyValuePair.Create("C:", 0)], "C:");

        var rows = DiskRoles.From(map, [Measured("C:", 237, 120)]);

        Assert.Equal("Disk 2", RoleRow(rows, 2).Subject);
        Assert.Equal("Inside this PC. It has no drive letter.", RoleRow(rows, 2).Detail);
        Assert.Null(UsedRow(rows, 2));
        Assert.NotNull(UsedRow(rows, 0));
    }

    [Fact]
    public void The_space_in_use_is_added_up_over_the_disks_lettered_drives()
    {
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(1, DiskBus.Usb, false)],
            [KeyValuePair.Create("D:", 0), KeyValuePair.Create("C:", 0), KeyValuePair.Create("F:", 1)], "C:");

        var rows = DiskRoles.From(map, [Measured("c", 237, 120.5), Measured(@"D:\", 100, 40), Measured("F:", 900, 161.7)]);

        var used = UsedRow(rows, 0)!;
        Assert.Equal(176.5, used.Value.Number);   // 116.5 GB on C: and 60 GB on D:
        Assert.Equal("GB", used.Unit);
        Assert.Equal("C:, D:", used.Subject);
        Assert.True(used.InfoOnly);
        Assert.Equal(Grade.Unknown, GradeOf(used));
        Assert.Equal("Space in use", used.Label);
        Assert.Equal(738.3, UsedRow(rows, 1)!.Value.Number);
    }

    [Fact]
    public void Only_the_disk_Windows_runs_from_is_said_to_hold_Windows_and_programs()
    {
        // The HTML report prints every row's detail: "Windows and programs included" on the auditor's
        // USB drive, or on a data disk, would say Windows is on it.
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(1, 11, false), new AttachedDisk(2, DiskBus.Usb, false)],
            [KeyValuePair.Create("C:", 0), KeyValuePair.Create("D:", 1), KeyValuePair.Create("F:", 2)], "C:");

        var rows = DiskRoles.From(map, [Measured("C:", 237, 120), Measured("D:", 100, 40), Measured("F:", 900, 161.7)]);

        Assert.Equal("Added up over this disk's drives (C:), Windows and programs included.", UsedRow(rows, 0)!.Detail);
        Assert.Equal("Added up over this disk's drives (D:).", UsedRow(rows, 1)!.Detail);
        Assert.Equal("Added up over this disk's drives (F:).", UsedRow(rows, 2)!.Detail);
    }

    [Fact]
    public void The_space_in_use_is_left_out_rather_than_added_up_over_part_of_a_disk()
    {
        // D: was not measured (its volume did not answer, or it is a removable volume this check
        // skips): 116.5 GB would read as the whole disk's use.
        var map = new DriveMap([new AttachedDisk(0, 11, true)], [KeyValuePair.Create("C:", 0), KeyValuePair.Create("D:", 0)], "C:");

        Assert.Null(UsedRow(DiskRoles.From(map, [Measured("C:", 237, 120.5)]), 0));
        Assert.Null(UsedRow(DiskRoles.From(map, [Measured("C:", 237, 120.5), ("D:", 0, 0)]), 0));          // a size of zero
        Assert.Null(UsedRow(DiskRoles.From(map, [Measured("C:", 237, 120.5), Measured("D:", 100, 101)]), 0)); // more free than its size
        Assert.NotNull(RoleRow(DiskRoles.From(map, []), 0));
    }

    [Fact]
    public void A_disk_row_never_carries_a_volume_label()
    {
        // The volume rows are named "D: (label)", and a label can be a person's name. The disk rows
        // take their letters from the drive map, which has none.
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(1, DiskBus.Usb, false), new AttachedDisk(5, 0, false)],
            [KeyValuePair.Create("C:", 0), KeyValuePair.Create("D:", 0), KeyValuePair.Create("F:", 1)], "C:");
        var label = VolumeReadings.FreeSpace("D: (Holiday photos)", 100 * Gib, 40 * Gib, null).Subject!;

        var rows = DiskRoles.From(map, [Measured("C:", 237, 120), Measured("D:", 100, 40), Measured("F:", 900, 161.7)]);

        Assert.Equal(5, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Matches(@"^([A-Z]:(, [A-Z]:)*|Disk [0-9]+)$", r.Subject);
            Assert.DoesNotContain("Holiday", r.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("(", r.Subject, StringComparison.Ordinal);
        });
        Assert.Contains("Holiday", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_drive_map_there_are_no_disk_rows_and_the_status_is_unchanged()
    {
        List<Metric> metrics = [VolumeReadings.FreeSpace("C:", 237 * Gib, 120 * Gib, null)];

        Assert.Equal(ModuleStatus.Completed, DiskRoles.AddAfterStatus(metrics, failures: 0, map: null, [Measured("C:", 237, 120)]));
        Assert.Single(metrics);
        Assert.Equal(ModuleStatus.PartiallyCompleted, DiskRoles.AddAfterStatus(metrics, failures: 1, map: null, []));
        Assert.Single(metrics);
    }

    [Fact]
    public void Disk_rows_never_make_a_volume_check_that_measured_nothing_read_as_finished()
    {
        // The other direction: the drive map answered, but no volume did. The check still failed,
        // and the rows about the disks are kept, so advice still knows the audit drive is external.
        List<Metric> metrics = [];

        var status = DiskRoles.AddAfterStatus(metrics, failures: 1, T470pMap(), []);

        Assert.Equal(ModuleStatus.Failed, status);
        Assert.Equal(3, metrics.Count);
        Assert.All(metrics, m => Assert.Equal("storage.disk.role", m.Id));
        Assert.Equal(ModuleStatus.Failed, DiskRoles.AddAfterStatus([], failures: 0, T470pMap(), []));
    }

    [Fact]
    public void The_advice_reads_the_disk_rows_back_as_the_same_disks()
    {
        // The contract with AdviceFacts (STEP-B-SPEC 3.3): the role's Text and the letters in the
        // Subject are what the advice joins drive readings on.
        var map = new DriveMap([new AttachedDisk(0, 11, true), new AttachedDisk(1, 11, false), new AttachedDisk(2, DiskBus.Usb, false)],
            [KeyValuePair.Create("C:", 0), KeyValuePair.Create("D:", 1), KeyValuePair.Create("E:", 1), KeyValuePair.Create("F:", 2)], "C:");
        var rows = DiskRoles.From(map, [Measured("C:", 237, 120.5), Measured("D:", 100, 40), Measured("E:", 100, 50)]);

        var facts = AdviceFacts.Build(Engine.Evaluate(rows), [], [], new AdviceContext("C:"));

        var groups = facts.DiskGroups.ToDictionary(g => g.DiskNumber!.Value);
        Assert.Equal(DiskRole.System, groups[0].Role);
        Assert.Equal(DiskRole.Internal, groups[1].Role);
        Assert.Equal(DiskRole.External, groups[2].Role);
        Assert.Equal("C:", groups[0].Letters);
        Assert.Equal("D:, E:", groups[1].Letters);
        Assert.Equal(116.5, groups[0].UsedGb);
        Assert.Equal(110.0, groups[1].UsedGb);
        Assert.Null(groups[2].UsedGb);   // F:, the audit drive, was not measured here
    }
}
