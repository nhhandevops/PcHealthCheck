using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests.Linux;

/// <summary>
/// The Linux readings reuse the Windows metric ids where they mean the same thing, and get
/// Linux rules where the Windows tips would send the reader to a tool that does not exist
/// there. These tests keep both halves honest.
/// </summary>
public sealed class RuleTests
{
    private static readonly VerdictEngine Linux = new(RulesLoader.LoadEmbedded("linux"));

    /// <summary>Words that only make sense on Windows. None may reach a Linux report.</summary>
    private static readonly string[] WindowsOnly =
    [
        "Windows", "Task Manager", "Storage Sense", "Disk Cleanup", "Recycle Bin", "WinDirStat",
        "PawnIO", "page file", "Event Viewer", "Defender", "Settings >", "fsutil", "powercfg",
    ];

    private static async Task<List<ModuleResult>> ScanEveryFixture()
    {
        var results = new List<ModuleResult>();
        using (var thinkPad = Fixtures.ThinkPad())
            foreach (var module in thinkPad.Provider().CreateModules())
                results.Add(await module.RunAsync(FakeLinux.Context(), null, CancellationToken.None));
        using (var strict = Fixtures.ThinkPad().File("/proc/sys/vm/overcommit_memory", "2\n"))
            results.Add(await strict.Run("linux.memory"));
        using (var zoneOnly = new FakeLinux().Dir("/sys/class/hwmon")
                   .Attributes("/sys/class/thermal/thermal_zone0", ("type", "acpitz"), ("temp", "96000")))
            results.Add(await zoneOnly.Run("linux.sensors"));
        using (var amd = new FakeLinux().Dir("/sys/class/thermal")
                   .Attributes("/sys/class/hwmon/hwmon1", ("name", "amdgpu"),
                       ("temp1_label", "edge"), ("temp1_input", "55000"),
                       ("temp2_label", "junction"), ("temp2_input", "62000"),
                       ("temp3_label", "mem"), ("temp3_input", "70000")))
            results.Add(await amd.Run("linux.sensors"));
        return results;
    }

    [Fact]
    public void Each_os_gets_one_rule_per_metric()
    {
        _ = new VerdictEngine(RulesLoader.LoadEmbedded("linux"));   // throws on a duplicate
        _ = new VerdictEngine(RulesLoader.LoadEmbedded("windows"));
    }

    [Fact]
    public void Every_rule_names_an_os_the_loader_knows_or_none()
    {
        // A typo such as "linx" would silently drop the rule on every OS.
        Assert.All(RulesLoader.LoadAllEmbedded(), r =>
            Assert.True(r.Os is null || RulesLoader.KnownOs.Contains(r.Os), $"{r.Id} has os '{r.Os}'"));
    }

    [Theory]
    [InlineData("storage.volume.free_pct", "rule.linux.free-space", "rule.storage.free-space")]
    [InlineData("memory.used_pct", "rule.linux.memory-used", "rule.sensors.memory-used")]
    [InlineData("memory.commit_pct", "rule.linux.memory-commit", "rule.memory.commit")]
    [InlineData("cpu.zone_temperature_c", "rule.linux.cpu-zone-temp", "rule.sensors.cpu-zone-temp")]
    public void A_shared_metric_gets_its_own_tips_on_each_os(string metricId, string linuxRule, string windowsRule)
    {
        Assert.Equal(linuxRule, Assert.Single(RulesLoader.LoadEmbedded("linux"), r => r.MetricId == metricId).Id);
        Assert.Equal(windowsRule, Assert.Single(RulesLoader.LoadEmbedded("windows"), r => r.MetricId == metricId).Id);
    }

    [Fact]
    public async Task Every_graded_linux_reading_has_a_rule_with_linux_advice()
    {
        var rules = RulesLoader.LoadEmbedded("linux").ToDictionary(r => r.MetricId, StringComparer.Ordinal);
        var graded = (await ScanEveryFixture()).SelectMany(r => r.Metrics).Where(m => !m.InfoOnly)
            .Select(m => m.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(graded);
        foreach (var id in graded)
        {
            // A graded metric without a rule comes out Unknown: measured, then silently not judged.
            Assert.True(rules.TryGetValue(id, out var rule), $"{id} is graded on Linux but has no rule");
            var advice = (rule.Tips?.Values.SelectMany(t => t) ?? []).Append(rule.Forecast ?? "");
            foreach (var text in advice)
                foreach (var word in WindowsOnly)
                    Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{rule.Id} tells a Linux user: \"{text}\"");
        }
    }

    [Fact]
    public async Task The_thinkpad_fixture_grades_as_its_owner_would_expect()
    {
        var verdicts = Linux.Evaluate((await ScanEveryFixture()).Take(5).SelectMany(r => r.Metrics));
        Grade GradeOf(string id, string? subject = null) =>
            verdicts.Single(v => v.Metric.Id == id && (subject is null || v.Metric.Subject == subject)).Grade;

        Assert.Equal(Grade.Bad, GradeOf("storage.volume.free_pct", "/"));
        Assert.Equal(Grade.Good, GradeOf("storage.volume.free_pct", "/boot/efi"));
        Assert.Equal(Grade.Bad, GradeOf("battery.capacity_pct", "BAT0"));
        Assert.Equal(Grade.Normal, GradeOf("battery.capacity_pct", "BAT1"));
        Assert.Equal(Grade.Good, GradeOf("memory.used_pct"));
        Assert.Equal(Grade.Unknown, GradeOf("memory.commit_pct"));
        Assert.Equal(Grade.Good, GradeOf("memory.oom_kills_since_boot"));
        Assert.Equal(Grade.Good, GradeOf("cpu.temperature_c"));
        Assert.Equal(Grade.Good, GradeOf("os.uptime_days"));

        var rootTips = verdicts.Single(v => v.Metric.Id == "storage.volume.free_pct" && v.Metric.Subject == "/").Tips;
        Assert.Contains(rootTips, t => t.Contains("package cache", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_oom_kills_are_bad() =>
        Assert.Equal(Grade.Bad, Linux.EvaluateOne(new Metric("memory.oom_kills_since_boot", "x", 3, HealthCategory.Memory)).Grade);
}
