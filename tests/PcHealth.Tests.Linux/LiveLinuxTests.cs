using System.Globalization;
using System.Text;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Linux;
using PcHealth.Modules.Linux.Support;
using Xunit;
using Xunit.Abstractions;

namespace PcHealth.Tests.Linux;

/// <summary>Runs only on Linux; elsewhere it shows as skipped, never as passed.</summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Reads a real Linux kernel's /proc and /sys; runs in CI's ubuntu-latest job.";
    }
}

/// <summary>
/// The fixtures are written from the kernel's documentation; these tests check the same code
/// against a real kernel. They print every reading, so the CI log shows what a real machine
/// produced, and they assert only what holds on any Linux machine, a CI virtual machine included.
/// </summary>
public sealed class LiveLinuxTests(ITestOutputHelper output)
{
    [LinuxOnlyFact]
    public async Task A_real_scan_completes_and_every_check_reads_something_or_says_why_not()
    {
        var modules = new LinuxModuleProvider().CreateModules();
        var orchestrator = new ScanOrchestrator(new VerdictEngine(RulesLoader.LoadEmbedded()));
        var context = FakeLinux.Context();

        var report = await orchestrator.RunAsync(modules, context);

        var dump = new StringBuilder();
        foreach (var module in report.Modules)
        {
            dump.AppendLine(CultureInfo.InvariantCulture, $"[{module.Status}] {module.ModuleId} ({module.Elapsed.TotalMilliseconds:0} ms) {module.Error}");
            foreach (var note in module.Notes) dump.AppendLine(CultureInfo.InvariantCulture, $"    note: {note}");
        }
        foreach (var verdict in report.Verdicts)
            dump.AppendLine(CultureInfo.InvariantCulture,
                $"  {verdict.Grade,-7} {verdict.Metric.Id} [{verdict.Metric.Subject}] = {verdict.Metric.Value} {verdict.Metric.Unit} | {verdict.Metric.Detail}");
        output.WriteLine(dump.ToString());

        Assert.Equal(5, report.Modules.Count);
        Assert.All(report.Modules, m => Assert.NotEqual(ModuleStatus.Failed, m.Status));
        Assert.Empty(context.Receipt.Snapshot());
        Assert.Equal("linux", RulesLoader.CurrentOs);

        var osName = ProcFiles.ParseOsRelease(await File.ReadAllTextAsync("/etc/os-release")).GetValueOrDefault("NAME");
        var build = report.Verdicts.Single(v => v.Metric.Id == "os.build").Metric.Value.Text;
        Assert.Contains(osName ?? "Linux", build, StringComparison.Ordinal);

        var used = report.Verdicts.Single(v => v.Metric.Id == "memory.used_pct");
        Assert.InRange(used.Metric.Value.Number!.Value, 0.1, 100);
        Assert.NotEqual(Grade.Unknown, used.Grade);

        var volumes = report.Verdicts.Where(v => v.Metric.Id == "storage.volume.free_pct").ToList();
        Assert.NotEmpty(volumes);
        Assert.All(volumes, v => Assert.InRange(v.Metric.Value.Number!.Value, 0, 100));
    }

    [LinuxOnlyFact]
    public void Class_entries_are_links_and_are_still_listed_as_directories()
    {
        // Every entry of /sys/class/block is a symbolic link to a directory. If the listing did
        // not follow links, the battery and sensor checks would find nothing on a real machine
        // while passing every fixture test, where the entries are plain directories.
        var files = new SystemFiles("/");
        var devices = files.TryListDirectories("/sys/class/block", out var error);
        output.WriteLine($"/sys/class/block: {string.Join(", ", devices ?? [])} {error}");

        Assert.NotNull(devices);
        Assert.NotEmpty(devices);

        // A partition has no "removable" of its own: the check reaches its disk through the link.
        foreach (var partition in devices.Where(d => File.Exists($"/sys/class/block/{d}/partition")))
        {
            var disk = files.LinkTargetParentName($"/sys/class/block/{partition}");
            output.WriteLine($"{partition} -> disk {disk}, removable={files.TryRead($"/sys/class/block/{disk}/removable")?.Trim()}");
            Assert.NotNull(disk);
            Assert.NotNull(files.TryRead($"/sys/class/block/{disk}/removable"));
        }
    }
}
