using PcHealth.Core.Advice;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The gate for Step B (STEP-B-SPEC 7, REMEDIES-PLAN principle 2): a healthy PC gets no
/// recommendation, and neither does a laptop just restored from the image. A recommendation on
/// either would be a threshold a healthy machine trips, which is a bug in the threshold
/// (CLAUDE.md rule 3), and a section that is always full teaches the reader to skip it.
///
/// The healthy PC is generated from the rule files, so a rule added later is in it on the day it
/// is added, at the very edge of its Good band.
/// </summary>
public sealed class HealthyPcAdviceTests
{
    [Fact]
    public void The_generated_healthy_PC_has_a_Good_reading_for_every_Windows_rule_with_a_Good_band()
    {
        var pc = AdvicePc.AllGood();
        var graded = pc.Verdicts.Where(v => !v.Metric.InfoOnly).ToList();

        Assert.All(graded, v => Assert.True(v.Grade == Grade.Good, $"{v.Metric.Id} = {v.Metric.Value} grades {v.Grade}"));
        var rulesWithGood = RulesLoader.LoadEmbedded("windows")
            .Where(r => r.Bands.Exists(b => string.Equals(b.Grade, "Good", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.MetricId);
        Assert.Equal(rulesWithGood.Order(StringComparer.Ordinal), graded.Select(v => v.Metric.Id).Order(StringComparer.Ordinal));
        Assert.All(pc.Modules, m => Assert.Equal(ModuleStatus.Completed, m.Status));
    }

    [Fact]
    public void A_healthy_PC_that_finished_every_check_gets_nothing_at_all()
    {
        var pc = AdvicePc.AllGood();

        var section = pc.Advice();

        Assert.Empty(section.Recommendations);
        Assert.Empty(section.NotMeasured);
        Assert.Empty(section.Notes);
        Assert.Equal(0, section.OtherFindings);
        Assert.Empty(section.ItemErrors);
        // Its uptime is the Good band's edge, 30 days: more than S1's 7, so the value half of S1 holds
        // and only a graded reading could make that card.
        Assert.Equal(30, pc.Verdicts.Single(v => v.Metric.Id == "os.uptime_days").Metric.Value.Number);
        Assert.True(30 > AdviceThresholds.RestartAfterDays);
    }

    [Fact]
    public void A_healthy_PC_scanned_without_administrator_rights_gets_one_grey_note_and_nothing_else()
    {
        var pc = AdvicePc.AllGood().Unelevated();
        Assert.DoesNotContain(pc.Modules, m => m.ModuleId is "disk.smart" or "disk.reliability" or "sensors");

        var section = pc.Advice();

        Assert.Empty(section.Recommendations);
        Assert.Empty(section.NotMeasured);
        Assert.Equal("Drive health was not part of this scan: its checks need administrator rights.", Assert.Single(section.Notes).Text);
        Assert.Equal(0, section.OtherFindings);
        Assert.Empty(section.ItemErrors);

        // Through the report a real scan builds: the note changes no module's status, so the "some
        // checks could not finish" banner stays off.
        var report = pc.Report(ranElevated: false);
        Assert.Equal(pc.Modules.Select(m => (m.ModuleId, m.Status)), report.Modules.Select(m => (m.ModuleId, m.Status)));
        Assert.DoesNotContain(report.Modules, m => m.Status is ModuleStatus.Failed or ModuleStatus.PartiallyCompleted);
        Assert.Empty(report.Advice!.Recommendations);
        Assert.Equal(["advice.note.drive-health-not-included"], report.Advice.Notes.Select(n => n.Key));
        Assert.Equal(Grade.Good, report.OverallGrade);
    }

    [Fact]
    public void A_healthy_PC_scanned_as_administrator_with_the_drive_checks_left_out_is_not_told_it_lacked_the_rights()
    {
        // A window started with Run as administrator and both drive checks unticked: the report says
        // the scan was elevated, and the note must not send anyone to fix rights it already had.
        var pc = AdvicePc.AllGood().Module("disk.smart", null).Module("disk.reliability", null);

        var report = pc.Report(ranElevated: true);

        Assert.Empty(report.Advice!.Recommendations);
        Assert.Empty(report.Advice.NotMeasured);
        Assert.Equal(["advice.note.drive-health-not-selected"], report.Advice.Notes.Select(n => n.Key));
        Assert.Equal(["advice.note.drive-health-not-included"], pc.Report(ranElevated: false).Advice!.Notes.Select(n => n.Key));
    }

    [Fact]
    public void A_freshly_reimaged_Windows_10_laptop_gets_no_recommendation()
    {
        var section = AdvicePc.FreshReimage().Advice();

        Assert.Empty(section.Recommendations);
        Assert.Empty(section.ItemErrors);
        // Updates waiting, Windows 10 out of support, old definitions, no encryption, no backup, a
        // restart pending, Windows 11 blocked by the processor, and the auditor's USB stick: each is
        // in its own row with its own tips, and the section counts them rather than repeating them.
        Assert.True(section.OtherFindings >= 9, $"{section.OtherFindings} other findings");
    }

    [Fact]
    public void A_reimaged_laptop_whose_only_Windows_11_blocker_is_the_TPM_gets_exactly_that_card() =>
        Assert.Equal(["O1"], AdvicePc.FreshReimage(blockers: "Tpm").Advice().Recommendations.Select(c => c.Item));

    [Fact]
    public void A_reimaged_laptop_left_on_for_nine_days_with_a_restart_pending_gets_exactly_that_card()
    {
        var section = AdvicePc.FreshReimage(uptimeDays: 9).Advice();

        var card = Assert.Single(section.Recommendations);
        Assert.Equal("S1", card.Item);
        Assert.DoesNotContain(card.Lines, l => l.Key.StartsWith("advice.s4.", StringComparison.Ordinal));
        Assert.Empty(section.ItemErrors);
    }

    [Fact]
    public void Acceptance_row_K2_ticks_the_admin_check_whose_declined_prompt_gives_the_amber_drive_health_line()
    {
        // A declined UAC prompt fails only the admin checks that were ticked; the others are not in the
        // scan at all. Only a drive check that broke gives K2's amber line, and any other admin check
        // leaves drive health out, which is K3's grey note. K2 is blocking, and it once said "Tick an
        // admin check", so it could fail on correct behaviour (review round, 2026-10-28).
        static AdvicePc Declined(string moduleId) => AdvicePc.AllGood().Unelevated().Module(moduleId, ModuleStatus.Failed);
        var step = File.ReadLines(Path.Combine(AdviceCatalogTests.RepoRoot(), "ACCEPTANCE.md"))
            .Single(l => l.StartsWith("| K2 |", StringComparison.Ordinal)).Split('|')[2];
        var admin = AdvicePc.Manifests.Where(m => m.NeedsAdmin).ToList();

        var ticked = Assert.Single(admin, m => step.Contains(m.Name, StringComparison.Ordinal));
        var pc = Declined(ticked.Id);
        var section = pc.Advice();
        Assert.Empty(section.Recommendations);
        Assert.StartsWith("Drive health was not fully checked: ", Assert.Single(section.NotMeasured).Text.Text, StringComparison.Ordinal);
        Assert.Equal(["Nothing to suggest from the checks that ran.", "1 check did not finish (listed above), so this is not an all-clear."],
            AdvicePresentation.View(section, pc.Modules, AdvicePresentation.NothingHidden).EmptyState);

        foreach (var other in admin.Where(m => m.Id is not ("disk.smart" or "disk.reliability")))
        {
            var left = Declined(other.Id).Advice();
            Assert.DoesNotContain(left.NotMeasured, n => n.Area == "drive-health");
            Assert.Equal(["advice.note.drive-health-not-included"], left.Notes.Select(n => n.Key));
        }
    }
}
