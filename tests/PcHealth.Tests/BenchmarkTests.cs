using System.Globalization;
using System.Runtime.InteropServices;
using PcHealth.Modules.Windows.Benchmarks;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The benchmark's job is to produce a number that is true. These tests cover the three
/// places it could quietly stop being true: reading DiskSpd's results, deciding what kind of
/// drive is being measured, and choosing which drives to touch at all.
/// </summary>
public class BenchmarkTests
{
    /// <summary>
    /// Shaped exactly like DiskSpd's own XmlResultParser emits it — two threads, one target
    /// each, which is why the totals have to be summed rather than read from one place.
    /// </summary>
    private const string TwoThreadResults = """
        <Results>
          <System><ComputerName>TESTPC</ComputerName></System>
          <Profile><Progress>0</Progress></Profile>
          <TimeSpan>
            <TestTimeSeconds>10.00</TestTimeSeconds>
            <ThreadCount>2</ThreadCount>
            <ProcCount>12</ProcCount>
            <Thread>
              <Id>0</Id>
              <Target>
                <Path>D:\PcHealthCheck-bench\bench.tmp</Path>
                <BytesCount>10000000000</BytesCount>
                <FileSize>1073741824</FileSize>
                <IOCount>10000</IOCount>
                <ReadBytes>10000000000</ReadBytes>
                <ReadCount>10000</ReadCount>
                <WriteBytes>0</WriteBytes>
                <WriteCount>0</WriteCount>
                <Latency>
                  <AverageReadLatencyMilliseconds>0.800</AverageReadLatencyMilliseconds>
                  <ReadLatencyStdev>0.100</ReadLatencyStdev>
                </Latency>
              </Target>
            </Thread>
            <Thread>
              <Id>1</Id>
              <Target>
                <Path>D:\PcHealthCheck-bench\bench.tmp</Path>
                <BytesCount>20000000000</BytesCount>
                <FileSize>1073741824</FileSize>
                <IOCount>30000</IOCount>
                <ReadBytes>20000000000</ReadBytes>
                <ReadCount>30000</ReadCount>
                <WriteBytes>0</WriteBytes>
                <WriteCount>0</WriteCount>
                <Latency>
                  <AverageReadLatencyMilliseconds>1.200</AverageReadLatencyMilliseconds>
                  <ReadLatencyStdev>0.200</ReadLatencyStdev>
                </Latency>
              </Target>
            </Thread>
          </TimeSpan>
        </Results>
        """;

    [Fact]
    public void Totals_are_summed_across_every_thread()
    {
        var reading = DiskSpdOutputReader.Parse(TwoThreadResults, out var error);

        Assert.Null(error);
        Assert.NotNull(reading);
        // 30 GB in 10 s = 3,000 MB/s. Reading one thread only would report a third of that.
        Assert.Equal(3000, reading.ReadMegabytesPerSecond, 1);
        Assert.Equal(4000, reading.ReadIops, 1);
    }

    [Fact]
    public void Latency_is_weighted_by_how_much_each_thread_actually_did()
    {
        // 10k reads at 0.8 ms and 30k at 1.2 ms: the honest average is 1.10 ms.
        // A plain mean of the two thread averages would say 1.00 ms.
        var reading = DiskSpdOutputReader.Parse(TwoThreadResults, out _);

        Assert.NotNull(reading);
        Assert.Equal(1.10, reading.AverageLatencyMs, 3);
    }

    [Fact]
    public void An_interrupted_run_reports_diskspds_own_error_rather_than_a_number()
    {
        const string interrupted = """
            <Results><TimeSpan>
              <Error>The test was interrupted before the measurements began. No results are displayed.</Error>
            </TimeSpan></Results>
            """;

        var reading = DiskSpdOutputReader.Parse(interrupted, out var error);

        Assert.Null(reading);
        Assert.Contains("interrupted", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<Results></Results>")]
    [InlineData("<Results><TimeSpan><TestTimeSeconds>0.00</TestTimeSeconds></TimeSpan></Results>")]
    public void Anything_unreadable_yields_no_number_and_an_explanation(string xml)
    {
        var reading = DiskSpdOutputReader.Parse(xml, out var error);

        Assert.Null(reading);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_run_that_recorded_no_reads_is_not_reported_as_zero_speed()
    {
        // Zero MB/s would be graded as a catastrophically failing drive. It is a failed
        // measurement, and has to be said as one.
        const string noReads = """
            <Results><TimeSpan><TestTimeSeconds>10.00</TestTimeSeconds>
              <Thread><Id>0</Id><Target><ReadBytes>0</ReadBytes><ReadCount>0</ReadCount></Target></Thread>
            </TimeSpan></Results>
            """;

        Assert.Null(DiskSpdOutputReader.Parse(noReads, out var error));
        Assert.Contains("no reads", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_are_read_the_same_way_on_a_comma_decimal_locale()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // On vi-VN or de-DE, current-culture parsing turns "10.00" into 1000.
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var reading = DiskSpdOutputReader.Parse(TwoThreadResults, out _);

            Assert.NotNull(reading);
            Assert.Equal(10, reading.TestSeconds, 3);
            Assert.Equal(3000, reading.ReadMegabytesPerSecond, 1);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_spinning_drive_in_a_usb_caddy_is_never_graded_as_an_ssd()
    {
        // Measured on real hardware: a Seagate 2.5" hard disk behind a USB bridge reports
        // MediaType 4 (SSD). Trusting that would grade a healthy 100 MB/s drive against a
        // 400 MB/s floor and call it broken.
        Assert.Equal(DriveClass.External, DriveClassifier.Classify(mediaType: 4, busType: 7));
        Assert.Null(DriveClassifier.Expectations(DriveClass.External).SequentialFloorMbps);
    }

    [Theory]
    [InlineData(3, 11, DriveClass.Hdd)]         // SATA hard disk
    [InlineData(4, 17, DriveClass.NvmeSsd)]     // NVMe SSD
    [InlineData(4, 11, DriveClass.SataSsd)]     // SATA SSD
    [InlineData(0, 17, DriveClass.NvmeSsd)]     // media type unset, but the bus gives it away
    [InlineData(4, 12, DriveClass.External)]    // SD card
    [InlineData(null, null, DriveClass.Unknown)]
    public void Drives_are_classified_from_media_and_bus_type(int? media, int? bus, DriveClass expected)
    {
        // int? rather than ushort? only because xUnit's InlineData cannot narrow an int literal.
        Assert.Equal(expected, DriveClassifier.Classify((ushort?)media, (ushort?)bus));
    }

    [Fact]
    public void Every_gradeable_class_has_a_floor_and_every_ungradeable_one_says_why()
    {
        foreach (var driveClass in Enum.GetValues<DriveClass>())
        {
            var expectation = DriveClassifier.Expectations(driveClass);
            Assert.False(string.IsNullOrWhiteSpace(expectation.TypicalRange));
            Assert.False(string.IsNullOrWhiteSpace(expectation.Note));
            if (driveClass is DriveClass.External or DriveClass.Unknown)
            {
                Assert.Null(expectation.SequentialFloorMbps);
            }
            else
            {
                Assert.True(expectation.SequentialFloorMbps > 0);
            }
        }
    }

    [Theory]
    [InlineData(38.3)]   // measured on this PC: an NVMe SSD enclosure running at USB 2.0 speed
    [InlineData(28)]
    public void An_external_drive_stuck_at_usb_2_speed_is_pointed_out(double mbps)
    {
        var hint = DriveClassifier.DescribeExternalLink(mbps);

        Assert.NotNull(hint);
        Assert.Contains("USB 2.0", hint, StringComparison.Ordinal);
        Assert.Contains("port or the cable", hint, StringComparison.Ordinal);
        // Stated as an observation, not a fault: a 30 MB/s memory stick is working fine.
        Assert.Contains("simply normal", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(114.1)]  // the USB hard disk on this PC — the drive is the limit, not the link
    [InlineData(450)]
    [InlineData(0)]      // nothing measured
    public void A_healthy_external_link_says_nothing_at_all(double mbps)
    {
        Assert.Null(DriveClassifier.DescribeExternalLink(mbps));
    }

    [Fact]
    public void Two_volumes_on_one_drive_are_measured_once()
    {
        // C: and E: on the same SSD would give two near-identical numbers, take twice as
        // long, and write twice as much for nothing.
        var (targets, _) = BenchmarkTargetSelector.Choose(
            [Volume("C:", 1, 200), Volume("E:", 1, 400)],
            [Nvme(1)],
            GigabytesToBytes(1));

        var target = Assert.Single(targets);
        Assert.Equal("E:", target.DriveLetter);   // the roomier of the two
    }

    [Fact]
    public void Each_physical_drive_gets_its_own_measurement()
    {
        var (targets, _) = BenchmarkTargetSelector.Choose(
            [Volume("C:", 1, 400), Volume("D:", 0, 900)],
            [Nvme(1), new PhysicalDiskInfo(0, "ST2000DM008", DriveClass.Hdd)],
            GigabytesToBytes(1));

        Assert.Equal(["D:", "C:"], targets.OrderBy(t => t.DiskNumber).Select(t => t.DriveLetter));
    }

    [Fact]
    public void A_drive_without_comfortable_headroom_is_skipped_and_said_so()
    {
        // Filling someone's disk to benchmark it is the opposite of looking after their data.
        var (targets, skipped) = BenchmarkTargetSelector.Choose(
            [Volume("C:", 1, freeGb: 1)], [Nvme(1)], GigabytesToBytes(1));

        Assert.Empty(targets);
        var reason = Assert.Single(skipped);
        Assert.Contains("C:", reason, StringComparison.Ordinal);
        Assert.Contains("free to test safely", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_volume_whose_drive_windows_cannot_identify_is_skipped_not_guessed_at()
    {
        var (targets, skipped) = BenchmarkTargetSelector.Choose(
            [Volume("Z:", 7, 500)], [Nvme(1)], GigabytesToBytes(1));

        Assert.Empty(targets);
        Assert.Contains("Z:", Assert.Single(skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void The_measured_pass_can_never_write_and_the_shown_command_is_the_real_one()
    {
        var sequential = DiskSpdRunner.Arguments(
            DiskProfile.Sequential, @"D:\bench.tmp", DiskSpdRunner.DefaultFileBytes, deepQueue: true);
        var random = DiskSpdRunner.Arguments(
            DiskProfile.Random4K, @"D:\bench.tmp", createBytes: null, deepQueue: true);

        // -w0 is the whole read-only promise; -Sh is what stops this measuring RAM.
        Assert.Contains("-w0", sequential, StringComparison.Ordinal);
        Assert.Contains("-w0", random, StringComparison.Ordinal);
        Assert.Contains("-Sh", sequential, StringComparison.Ordinal);
        Assert.DoesNotContain("-w100", sequential, StringComparison.Ordinal);

        // Only the first pass creates the file; the second must reuse it.
        Assert.Contains("-c1073741824", sequential, StringComparison.Ordinal);
        Assert.DoesNotContain("-c", random, StringComparison.Ordinal);

        Assert.EndsWith(@"""D:\bench.tmp""", sequential, StringComparison.Ordinal);
    }

    /// <summary>7-Zip's benchmark table, in the fixed-width shape it actually prints.</summary>
    private const string SevenZipOutput = """
        7-Zip 26.02 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-01-14

        Windows 11 (26200)
        Intel(R) Core(TM) i5-12400F

        Compressing  | Decompressing
        Dict     Speed Usage    R/U Rating  | Speed Usage    R/U Rating
                 KiB/s     %   MIPS   MIPS  |  KiB/s     %   MIPS   MIPS

        22:       36886   624   5397  33707  |  391605   793   4568  36230
        23:       35123   618   5401  33390  |  388991   790   4571  36112
        ----------------------------------------------------------------
        Avr:      36004   621   5399  33548  |  390298   791   4569  36171
        Tot:              706   4984  34859
        """;

    [Fact]
    public void The_overall_processor_rating_comes_from_the_total_row()
    {
        var reading = SevenZipOutputReader.Parse(SevenZipOutput, out var error);

        Assert.Null(error);
        Assert.NotNull(reading);
        Assert.Equal(34859, reading.OverallRating);
        // The rating columns, not the speed or usage columns beside them.
        Assert.Equal(33548, reading.CompressRating);
        Assert.Equal(36171, reading.DecompressRating);
    }

    [Fact]
    public void A_table_with_unexpected_columns_yields_no_compression_figures_rather_than_wrong_ones()
    {
        // A number read out of the wrong column looks perfectly plausible and is worse
        // than no number, so a short row simply produces nothing for those two metrics.
        const string shortRows = """
            Avr:      36004   621   33548
            Tot:              706   4984  34859
            """;

        var reading = SevenZipOutputReader.Parse(shortRows, out _);

        Assert.NotNull(reading);
        Assert.Equal(34859, reading.OverallRating);
        Assert.Equal(0, reading.CompressRating);
        Assert.Equal(0, reading.DecompressRating);
    }

    [Theory]
    [InlineData("")]
    [InlineData("7-Zip 26.02\nUnsupported command")]
    [InlineData("Avr:      36004   621   5399  33548")]
    [InlineData("Tot:              706   4984  0")]
    public void Output_without_a_usable_total_row_is_an_error_not_a_score(string output)
    {
        Assert.Null(SevenZipOutputReader.Parse(output, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void The_processor_benchmark_is_read_the_same_way_on_a_comma_decimal_locale()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("vi-VN");
            var reading = SevenZipOutputReader.Parse(SevenZipOutput, out _);

            Assert.NotNull(reading);
            Assert.Equal(34859, reading.OverallRating);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_portable_seven_zip_counts_as_seven_zip()
    {
        // The installer is the only part that needs a UAC prompt. Someone who unpacked
        // 7-Zip instead has 7za.exe and would otherwise be told the check is unavailable.
        Assert.Equal("7z.exe", SevenZipBenchmark.ToolName);
        Assert.Equal("7za.exe", SevenZipBenchmark.StandaloneToolName);
    }

    [Fact]
    public void The_processor_benchmark_never_asks_seven_zip_to_touch_a_file()
    {
        // "b" runs entirely in memory. Any path or archive argument would make this check
        // something quite different from what its consent row promises.
        Assert.Equal("b", SevenZipBenchmark.BenchmarkArguments);
    }

    /// <summary>
    /// Found by installing DiskSpd for real: its winget package ships amd64, arm64 and x86
    /// builds side by side, and a plain recursive search returns whichever the file system
    /// hands back first. Running the arm64 build on an x64 PC fails with an opaque Windows
    /// error that would look like a broken benchmark rather than a wrong pick.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X64, @"C:\pkg\amd64\diskspd.exe")]
    [InlineData(Architecture.Arm64, @"C:\pkg\arm64\diskspd.exe")]
    [InlineData(Architecture.X86, @"C:\pkg\x86\diskspd.exe")]
    public void The_build_matching_this_processor_is_the_one_chosen(Architecture architecture, string expected)
    {
        string[] candidates = [@"C:\pkg\amd64\diskspd.exe", @"C:\pkg\arm64\diskspd.exe", @"C:\pkg\x86\diskspd.exe"];

        Assert.Equal(expected, ToolLocator.PickBuildFor(candidates, architecture));
    }

    [Fact]
    public void A_package_with_one_build_for_everyone_is_used_as_is()
    {
        Assert.Equal(@"C:\pkg\7z.exe",
            ToolLocator.PickBuildFor([@"C:\pkg\7z.exe"], Architecture.X64));
    }

    [Fact]
    public void A_package_holding_only_other_architectures_is_refused_rather_than_guessed_at()
    {
        // Better to report the tool as missing than to run something that cannot start.
        Assert.Null(ToolLocator.PickBuildFor(
            [@"C:\pkg\arm64\diskspd.exe", @"C:\pkg\x86\diskspd.exe"], Architecture.X64));
    }

    [Fact]
    public void Nothing_found_stays_nothing()
    {
        Assert.Null(ToolLocator.PickBuildFor([], Architecture.X64));
    }

    /// <summary>
    /// A hundred and seven I/O retries appeared in the Windows event log in one evening on
    /// the machine this was written on — every one on a spinning drive behind a USB bridge,
    /// every one at the moment this benchmark ran, and the app's own event check then graded
    /// the PC Bad because of them. The measurement must never manufacture the finding, so
    /// deep queues are reserved for drives built to absorb them.
    /// </summary>
    [Theory]
    [InlineData(DriveClass.NvmeSsd, true)]
    [InlineData(DriveClass.SataSsd, true)]
    [InlineData(DriveClass.Hdd, false)]
    [InlineData(DriveClass.External, false)]
    [InlineData(DriveClass.Unknown, false)]
    public void Only_solid_state_drives_are_asked_for_a_deep_queue(DriveClass driveClass, bool expected)
    {
        Assert.Equal(expected, DiskSpdRunner.DeepQueueSuits(driveClass));
    }

    /// <summary>
    /// Measured on real hardware, on the same USB drive, minutes apart: creating a 1 GiB
    /// test file produced 25 Windows I/O retries and a 256 MiB one produced 4, while the
    /// read passes over either produced none at all. The write is the whole problem, so it
    /// is the write that shrinks.
    /// </summary>
    [Theory]
    [InlineData(DriveClass.External, 256L * 1024 * 1024)]
    [InlineData(DriveClass.NvmeSsd, 1024L * 1024 * 1024)]
    [InlineData(DriveClass.Hdd, 1024L * 1024 * 1024)]
    public void External_drives_are_asked_to_absorb_a_smaller_write(DriveClass driveClass, long expected)
    {
        Assert.Equal(expected, DiskSpdRunner.TestFileBytes(driveClass));
    }

    [Fact]
    public void Even_the_smaller_file_is_far_bigger_than_any_drive_cache()
    {
        // 256 MiB against a drive cache measured in tens of megabytes: the measurement is
        // still of the drive, not of its buffer.
        Assert.True(DiskSpdRunner.ExternalFileBytes >= 128L * 1024 * 1024);
    }

    [Fact]
    public void The_gentle_profile_asks_a_hard_disk_only_for_what_one_head_can_chase()
    {
        var gentle = DiskSpdRunner.Arguments(DiskProfile.Random4K, @"D:\bench.tmp", null, deepQueue: false);
        var deep = DiskSpdRunner.Arguments(DiskProfile.Random4K, @"D:\bench.tmp", null, deepQueue: true);

        Assert.Contains("-o4 -t1", gentle, StringComparison.Ordinal);
        Assert.Contains("-o32 -t4", deep, StringComparison.Ordinal);
        // Read-only either way — that promise is not up for negotiation.
        Assert.Contains("-w0", gentle, StringComparison.Ordinal);
        Assert.Contains("-Sh", gentle, StringComparison.Ordinal);
    }

    private static VolumeInfo Volume(string letter, int disk, long freeGb) =>
        new(letter, disk, GigabytesToBytes(freeGb));

    private static PhysicalDiskInfo Nvme(int number) => new(number, "CT1000P3PSSD8", DriveClass.NvmeSsd);

    private static long GigabytesToBytes(long gigabytes) => gigabytes * 1024 * 1024 * 1024;
}
