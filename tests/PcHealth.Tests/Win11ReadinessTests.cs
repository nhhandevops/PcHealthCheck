using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Windows 11 readiness from Windows' own appraiser verdict. The first case is the real registry
/// of a ThinkPad T470p (i7-7820HQ), read on 2026-10-23; the rest are the cases a machine that can
/// already upgrade never shows (rule 8).
/// </summary>
public sealed class Win11ReadinessTests
{
    private static readonly VerdictEngine Engine = new(RulesLoader.LoadEmbedded("windows"));
    private static readonly string[] OnlyCpu = ["CpuFms"];
    private static readonly string[] TpmAndSecureBoot = ["Tpm", "UefiSecureBoot"];

    private static Dictionary<string, string?> Values(string upgEx, string build, string redReason = "None",
        string epoch = "1792719668", string gated = "None") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["UpgEx"] = upgEx, ["DestBuildNum"] = build, ["RedReason"] = redReason,
            ["TimestampEpochString"] = epoch, ["GatedBlockReason"] = gated,
        };

    private static UpgradeAssessment A(string target, string upgEx, string build, string redReason = "None", string gated = "None") =>
        Win11Readiness.FromValues(target, Values(upgEx, build, redReason, gated: gated))!;

    [Fact]
    public void The_thinkpad_t470p_is_blocked_by_its_processor()
    {
        // The keys as they are on that laptop: Windows 10 21H2 Green, every Windows 11 target Red.
        var verdict = Win11Readiness.Assess([
            A("21H2", "Green", "19044"),
            A("CO21H2", "Red", "22000", "CpuFms"),
            A("GE25H2", "Red", "26200", "CpuFms"),
            A("GE26H2", "Red", "26300", "CpuFms"),
        ]);

        Assert.NotNull(verdict);
        Assert.False(verdict.Ready);
        Assert.Equal("GE26H2", verdict.Assessment.Target);
        Assert.Equal(OnlyCpu, verdict.BlockerCodes);
        Assert.True(verdict.ProcessorBlocked);
        Assert.Contains("supported list", Assert.Single(verdict.Blockers), StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2026, 10, 23), DateOnly.FromDateTime(verdict.Assessment.AssessedAt!.Value.UtcDateTime));
    }

    [Fact]
    public void The_unv_key_and_windows_10_targets_do_not_count()
    {
        Assert.Null(Win11Readiness.FromValues("UNV", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DataRelDate"] = "10/15/2026", ["Version"] = "12",
        }));
        Assert.Null(Win11Readiness.Assess([A("21H2", "Green", "19044")]));
        Assert.Null(Win11Readiness.Assess([]));
    }

    [Fact]
    public void Fixable_blockers_are_not_called_a_processor_block()
    {
        var verdict = Win11Readiness.Assess([A("NI23H2", "Red", "22631", "Tpm UefiSecureBoot")])!;

        Assert.Equal(TpmAndSecureBoot, verdict.BlockerCodes);
        Assert.Equal(2, verdict.Blockers.Count);
        Assert.False(verdict.ProcessorBlocked);
    }

    [Fact]
    public void A_missing_instruction_is_a_processor_block()
    {
        Assert.True(Win11Readiness.Assess([A("GE24H2", "Red", "26100", "PopCnt")])!.ProcessorBlocked);
    }

    [Fact]
    public void An_unknown_code_is_shown_verbatim_not_guessed()
    {
        var verdict = Win11Readiness.Assess([A("GE25H2", "Red", "26200", "SomethingNew")])!;

        Assert.Contains("'SomethingNew'", Assert.Single(verdict.Blockers), StringComparison.Ordinal);
    }

    [Fact]
    public void Green_is_ready_and_a_safeguard_hold_is_kept()
    {
        var verdict = Win11Readiness.Assess([A("GE25H2", "Green", "26200", gated: "Safeguard 12345")])!;

        Assert.True(verdict.Ready);
        Assert.Empty(verdict.BlockerCodes);
        Assert.Equal("Safeguard 12345", verdict.Assessment.GatedBlockReason);
    }

    [Fact]
    public void A_verdict_this_app_does_not_know_is_not_graded()
    {
        Assert.Null(Win11Readiness.Assess([A("GE25H2", "Yellow", "26200")])!.Ready);
    }

    [Fact]
    public void A_red_verdict_without_a_reason_still_says_no()
    {
        var verdict = Win11Readiness.Assess([A("GE25H2", "Red", "26200")])!;

        Assert.False(verdict.Ready);
        Assert.Single(verdict.Blockers);
    }

    [Fact]
    public void The_filetime_stamp_is_used_when_the_epoch_one_is_missing()
    {
        var values = Values("Red", "26200", "CpuFms", epoch: "");
        values["Timestamp"] = "134371932691467491";

        var assessed = Win11Readiness.FromValues("GE25H2", values)!.AssessedAt;

        Assert.Equal(new DateOnly(2026, 10, 23), DateOnly.FromDateTime(assessed!.Value.UtcDateTime));
    }

    [Fact]
    public void A_multi_string_registry_value_reads_as_its_codes()
    {
        // RedReason is REG_MULTI_SZ on a real machine; "System.String[]" was the first live result.
        Assert.Equal("CpuFms", Win11Readiness.RegistryText(OnlyCpu));
        Assert.Equal("Tpm UefiSecureBoot", Win11Readiness.RegistryText(TpmAndSecureBoot));
        Assert.Equal("22000", Win11Readiness.RegistryText(22000));
        Assert.Null(Win11Readiness.RegistryText(null));
    }

    [Theory]
    [InlineData(true, Grade.Good)]
    [InlineData(false, Grade.Normal)]
    public void Not_being_able_to_upgrade_is_a_plan_not_a_fault(bool ready, Grade expected) =>
        // Normal, not Bad: the PC works today. Whether a company replaces it is the fleet rules' call.
        Assert.Equal(expected, Engine.EvaluateOne(new Metric("os.win11_ready", "x", ready, HealthCategory.SoftwareOs)).Grade);
}
