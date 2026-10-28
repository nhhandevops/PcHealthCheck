using System.Globalization;
using System.Text.Json;
using PcHealth.Core.Advice;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Reporting;
using PcHealth.Core.Text;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests;

public class ReportExportTests
{
    private static ScanReport Report(
        IReadOnlyList<TrendFinding>? trends = null,
        HistoryCoverage? history = null,
        IReadOnlyList<Verdict>? verdicts = null)
    {
        verdicts ??= [new Verdict(
            new Metric("storage.volume.free_pct", "Free space", 8.5, HealthCategory.Storage, Unit: "%", Subject: "C:"),
            Grade.Bad, "rule.free", ["Free up space"], "It will get worse")];

        return new ScanReport
        {
            GeneratedAt = DateTimeOffset.Now,
            MachineName = "TESTPC",
            OsDescription = "Windows 11",
            AppVersion = "0.1.0",
            RanElevated = false,
            Modules = [new ModuleRunSummary("volumes", "Volumes", ModuleStatus.Completed, TimeSpan.FromSeconds(1), null, [])],
            Verdicts = verdicts,
            Receipt = [],
            Trends = trends ?? [],
            History = history,
            CategoryGrades = ScanReport.ComputeCategoryGrades(verdicts),
            OverallGrade = ScanReport.Worst(verdicts.Select(v => v.Grade)),
        };
    }

    [Fact]
    public void Renders_trend_findings_with_their_tips()
    {
        var trend = new TrendFinding(
            TrendKind.CounterGrowth, "smart.pending_sectors", "ST2000 — 2000 GB", "Pending sectors",
            Grade.Bad, "This is new: sectors have become unstable.", 0, 3,
            DateTimeOffset.Now.AddDays(-7), ["BACK UP THIS DRIVE NOW."]);

        var html = HtmlReportExporter.ToHtml(Report([trend]));

        Assert.Contains("What changed since your last scan", html, StringComparison.Ordinal);
        Assert.Contains("Pending sectors", html, StringComparison.Ordinal);
        Assert.Contains("ST2000 — 2000 GB", html, StringComparison.Ordinal);
        Assert.Contains("BACK UP THIS DRIVE NOW.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_so_plainly_when_nothing_moved()
    {
        var html = HtmlReportExporter.ToHtml(
            Report(history: new HistoryCoverage(4, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now)));

        Assert.Contains("Nothing has moved", html, StringComparison.Ordinal);
        Assert.Contains("3 earlier scan(s)", html, StringComparison.Ordinal); // 4 recorded, 3 to compare against
        Assert.Contains("over 30 days", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_that_a_first_scan_has_nothing_to_compare()
    {
        var html = HtmlReportExporter.ToHtml(
            Report(history: new HistoryCoverage(1, DateTimeOffset.Now, DateTimeOffset.Now)));

        Assert.Contains("first recorded scan", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing has moved", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scan_that_recorded_no_history_does_not_claim_to_be_the_first()
    {
        // --no-history: nothing was stored, so "first recorded scan, run again" was untrue (HW-001).
        var html = HtmlReportExporter.ToHtml(Report(history: HistoryOutcome.Disabled.Coverage));

        Assert.DoesNotContain("first recorded scan", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not added to the history", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Escapes_content_so_a_device_name_cannot_inject_markup()
    {
        var verdicts = new[]
        {
            new Verdict(
                new Metric("startup.count", "Startup items", 3, HealthCategory.Security,
                    Subject: "<script>alert('x')</script>", Detail: "a & b < c"),
                Grade.Good, null, [], null),
        };

        var html = HtmlReportExporter.ToHtml(Report(verdicts: verdicts));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("a &amp; b &lt; c", html, StringComparison.Ordinal);
    }

    [Fact]
    public void States_the_zero_network_promise_and_stays_self_contained()
    {
        var html = HtmlReportExporter.ToHtml(Report());

        Assert.Contains("Network connections made by PcHealthCheck during this scan: 0", html, StringComparison.Ordinal);
        // A self-contained file: no external stylesheets, scripts, or images.
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Checks_that_could_not_finish_are_called_out_before_the_results()
    {
        // The dangerous failure mode: a check silently drops out, and its absence reads
        // as "nothing wrong here". The report must say so, above the findings.
        var report = Report() with
        {
            Modules =
            [
                new ModuleRunSummary("malware.defender", "Malware history & defences",
                    ModuleStatus.PartiallyCompleted, TimeSpan.FromSeconds(1), null,
                    ["Threat history could not be read"]),
                new ModuleRunSummary("volumes", "Volumes", ModuleStatus.Completed, TimeSpan.Zero, null, []),
            ],
        };

        var html = HtmlReportExporter.ToHtml(report);

        Assert.Contains("Some checks could not finish", html, StringComparison.Ordinal);
        Assert.Contains("Malware history &amp; defences", html, StringComparison.Ordinal);
        Assert.Contains("Threat history could not be read", html, StringComparison.Ordinal);
        Assert.Contains("not the same as being healthy", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Found in acceptance testing, and the fourth time this app has presented unchecked as
    /// clean: the user selected an admin check, declined the Windows prompt, and the report
    /// said "GOOD" with nothing to indicate an entire check had never run. The headline
    /// grade is a verdict on the checks that ran, and has to say so when some did not.
    /// </summary>
    [Fact]
    public void The_headline_grade_is_marked_incomplete_when_a_check_did_not_run()
    {
        var report = Report(verdicts:
        [
            new Verdict(new Metric("storage.volume.free_pct", "Free space", 90, HealthCategory.Storage,
                Unit: "%", Subject: "C:"), Grade.Good, null, [], null),
        ]) with
        {
            Modules =
            [
                new ModuleRunSummary("volumes", "Volumes", ModuleStatus.Completed, TimeSpan.Zero, null, []),
                new ModuleRunSummary("disk.smart", "Drive SMART health", ModuleStatus.Failed, TimeSpan.Zero,
                    "Administrator access was declined, so the admin checks were skipped.", []),
            ],
        };

        var html = HtmlReportExporter.ToHtml(report);

        Assert.Contains("INCOMPLETE", html, StringComparison.Ordinal);
        Assert.Contains("Some checks could not finish", html, StringComparison.Ordinal);
        Assert.Contains("Administrator access was declined", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_scan_is_not_labelled_incomplete()
    {
        Assert.DoesNotContain("INCOMPLETE", HtmlReportExporter.ToHtml(Report()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fully_successful_scan_shows_no_incomplete_warning()
    {
        var html = HtmlReportExporter.ToHtml(Report());
        Assert.DoesNotContain("Some checks could not finish", html, StringComparison.Ordinal);
    }

    [Fact]
    public void No_sentinel_number_reaches_the_machine_readable_report()
    {
        // "Never scanned" used to serialize as 1.7976931348623157e+308. Nothing downstream could
        // tell that from a PC last scanned 1.8e308 days ago.
        var verdict = new Verdict(
            new Metric("malware.quick_scan_age_days", "Days since last quick scan", "never run",
                HealthCategory.Security, Detail: "No quick scan has ever been recorded on this PC."),
            Grade.Bad, "rule.malware.quick-scan-age", ["Run one now"], null);

        var json = JsonReportExporter.ToJson(Report(verdicts: [verdict]));

        Assert.Contains("\"Text\": \"never run\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("e+308", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1.7976931348623157", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_carries_the_advice_section_with_its_typed_arguments()
    {
        // What Step D's kit will read: Item, Urgency, Key and the argument names, as .Args.count.Number.
        var item = new TestAdviceItem("T", "drive-health", (facts, _) =>
        [
            new AdviceCard("T:disk0", "T", "drive-health", AdviceUrgency.ActNow, 10) { Subject = "MODEL-A — 240 GB", DiskNumber = 0 }
                .Title("advice.section.title")
                .Line(AdviceLineRole.Finding, "advice.section.other-findings", ("count", AdviceArg.Count(4)))
                .Because(facts.First("smart.reallocated")!, countInFingerprint: true)
                .Build(),
        ]);
        var rules = new VerdictEngine([new Rule
        {
            Id = "r", MetricId = "smart.reallocated", Bands = [new Band { Grade = "Good", Lte = 0 }, new Band { Grade = "Bad", Always = true }],
        }]);
        ModuleResult[] results =
        [
            new()
            {
                ModuleId = "disk.smart", Status = ModuleStatus.Completed,
                Metrics = [new Metric("smart.reallocated", "Reallocated sectors", 5, HealthCategory.Storage, Subject: "MODEL-A — 240 GB", DiskNumber: 0)],
            },
        ];

        var json = JsonReportExporter.ToJson(new ScanOrchestrator(rules, new AdviceEngine([item])).BuildReport(results, [], false));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("1.1", document.RootElement.GetProperty("SchemaVersion").GetString());
        var advice = document.RootElement.GetProperty("Advice");
        var card = Assert.Single(advice.GetProperty("Recommendations").EnumerateArray());
        Assert.Equal(("T:disk0", "T", "drive-health", "ActNow", "Bad", 10, 0),
            (card.GetProperty("Id").GetString(), card.GetProperty("Item").GetString(), card.GetProperty("Area").GetString(),
             card.GetProperty("Urgency").GetString(), card.GetProperty("Severity").GetString(),
             card.GetProperty("Rank").GetInt32(), card.GetProperty("DiskNumber").GetInt32()));
        Assert.Equal("advice.section.title", card.GetProperty("Title").GetProperty("Key").GetString());
        var line = Assert.Single(card.GetProperty("Lines").EnumerateArray());
        Assert.Equal("Finding", line.GetProperty("Role").GetString());
        Assert.Equal("advice.section.other-findings", line.GetProperty("Key").GetString());
        var count = line.GetProperty("Args").GetProperty("count");
        Assert.Equal(4, count.GetProperty("Number").GetDouble());
        Assert.Equal("Count", count.GetProperty("Unit").GetString());
        Assert.False(count.TryGetProperty("Text", out _));
        var evidence = Assert.Single(card.GetProperty("Evidence").EnumerateArray());
        Assert.Equal(("smart.reallocated", 0, "Bad"),
            (evidence.GetProperty("MetricId").GetString(), evidence.GetProperty("DiskNumber").GetInt32(), evidence.GetProperty("Grade").GetString()));
        Assert.Equal(0, advice.GetProperty("OtherFindings").GetInt32());
        foreach (var list in new[] { "NotMeasured", "Notes", "ItemErrors" })
            Assert.Equal(JsonValueKind.Array, advice.GetProperty(list).ValueKind);
        Assert.DoesNotContain("Fingerprint", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_whose_advice_was_not_worked_out_has_none_in_its_json() =>
        // Hand-built reports, older reports and Linux reports: no section, rather than an empty one.
        Assert.DoesNotContain("\"Advice\"", JsonReportExporter.ToJson(Report()), StringComparison.Ordinal);

    // --- "What you can do" in the HTML report (STEP-B-SPEC 6) -----------------------------------------

    private const string AdviceHeading = "<h2>What you can do</h2>";

    private static int Count(string text, string part) =>
        text.Split(part).Length - 1;

    [Fact]
    public void The_html_shows_five_cards_and_the_rest_inside_a_details_element()
    {
        var html = HtmlReportExporter.ToHtml(Report() with { Advice = AdvicePresentationTests.Cards(7) });

        Assert.Equal(7, Count(html, "<div class=\"card rec\">"));
        var more = html.IndexOf("<details><summary>2 more</summary>", StringComparison.Ordinal);
        Assert.True(more > 0);
        var end = html.IndexOf("</details>", more, StringComparison.Ordinal);
        Assert.Equal(5, Count(html[..more], "<div class=\"card rec\">"));
        Assert.Equal(2, Count(html[more..end], "<div class=\"card rec\">"));
        // Native HTML, so the report stays script-free and offline.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Five_cards_need_no_details_element()
    {
        var html = HtmlReportExporter.ToHtml(Report() with { Advice = AdvicePresentationTests.Cards(5) });

        Assert.Equal(5, Count(html, "<div class=\"card rec\">"));
        Assert.DoesNotContain("<details>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_section_comes_after_the_banner_and_before_the_trends()
    {
        var trend = new TrendFinding(TrendKind.CounterGrowth, "smart.pending_sectors", "ST2000 — 2000 GB", "Pending sectors",
            Grade.Bad, "New.", 0, 3, DateTimeOffset.Now.AddDays(-7), []);
        var report = Report([trend]) with
        {
            Modules = [new ModuleRunSummary("disk.smart", "Drive SMART health", ModuleStatus.Failed, TimeSpan.Zero, "Declined.", [])],
            Advice = AdvicePresentationTests.Cards(1),
        };

        var html = HtmlReportExporter.ToHtml(report);

        var banner = html.IndexOf("Some checks could not finish", StringComparison.Ordinal);
        var advice = html.IndexOf(AdviceHeading, StringComparison.Ordinal);
        var trends = html.IndexOf("What changed since your last scan", StringComparison.Ordinal);
        Assert.True(banner > 0 && banner < advice && advice < trends, $"banner {banner}, advice {advice}, trends {trends}");
    }

    [Fact]
    public void A_card_shows_its_urgency_title_subject_and_lines_with_those_for_IT_marked()
    {
        var html = HtmlReportExporter.ToHtml(Report() with { Advice = AdvicePresentationTests.Cards(1, 1) });

        Assert.Contains("<span class=\"chip g-Bad\">Act now</span> <strong>What you can do</strong> <span class=\"subj\">MODEL-1 — 240 GB</span>",
            html, StringComparison.Ordinal);
        Assert.Contains("<li>First, copy your files off", html, StringComparison.Ordinal);
        Assert.Contains("<li><strong>For IT:</strong> When the drive is retired", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_s_words_are_escaped_like_everything_else_in_the_report()
    {
        var card = AdvicePresentationTests.Cards(1).Recommendations[0] with { Subject = "<b>MODEL</b> & \"co\"" };

        var html = HtmlReportExporter.ToHtml(Report() with { Advice = new AdviceSection([card], [], [], 0, []) });

        Assert.Contains("<span class=\"subj\">&lt;b&gt;MODEL&lt;/b&gt; &amp; &quot;co&quot;</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>MODEL</b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_scan_says_there_is_nothing_to_suggest_without_any_alarm()
    {
        var report = AdvicePc.AllGood().Report(ranElevated: true);

        var html = HtmlReportExporter.ToHtml(report);

        Assert.NotNull(report.Advice);
        Assert.Empty(report.Advice.ItemErrors);
        Assert.Contains(AdviceHeading, html, StringComparison.Ordinal);
        Assert.Contains("<p>Nothing to suggest from the checks that ran.</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not an all-clear", html, StringComparison.Ordinal);
        Assert.DoesNotContain("INCOMPLETE", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Some checks could not finish", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not the same as being healthy", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_incomplete_scan_with_nothing_to_suggest_says_it_is_not_an_all_clear()
    {
        // The drive checks tried and failed: an empty section must not read as "no failing drive".
        var report = new AdvicePc().Module("disk.smart", ModuleStatus.Failed).Report(ranElevated: true);

        var html = HtmlReportExporter.ToHtml(report);

        Assert.Contains("<p class=\"amber\">Drive health was not fully checked: Drive SMART health did not finish", html, StringComparison.Ordinal);
        Assert.Contains("<p>Nothing to suggest from the checks that ran. 1 check did not finish (listed above), so this is not an all-clear.</p>",
            html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_standard_scan_gets_a_grey_note_about_drive_health_and_no_amber_line()
    {
        // CLAUDE.md rule 2: checks deliberately left out of a scan without admin rights are a note.
        var report = AdvicePc.AllGood().Unelevated().Report(ranElevated: false);

        var html = HtmlReportExporter.ToHtml(report);

        Assert.Contains("<p class=\"muted\">Drive health was not part of this scan: its checks need administrator rights.</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"amber\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not an all-clear", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_html_names_other_findings_and_item_errors()
    {
        var error = AdviceCatalog.English.Text(AdviceLineRole.Finding, "advice.section.item-error",
            ("area", AdviceArg.Words("drive health")), ("error", AdviceArg.Words("FormatException")))!;
        var html = HtmlReportExporter.ToHtml(Report() with { Advice = new AdviceSection([], [], [], 3, [error]) });

        Assert.Contains("<p class=\"amber\">Advice about drive health could not be worked out (FormatException). The readings below are complete.</p>",
            html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"muted\">3 other readings are graded Normal or Action needed; their tips are in their sections below.</p>",
            html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_whose_advice_was_not_worked_out_has_no_section_in_its_html() =>
        Assert.DoesNotContain("What you can do", HtmlReportExporter.ToHtml(Report()), StringComparison.Ordinal);

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("de-DE")]
    public void The_html_section_reads_the_same_whatever_the_PC_s_regional_format(string culture)
    {
        var report = Report() with { Advice = AdvicePresentationTests.Cards(7, 2) with { OtherFindings = 1234 } };
        string Section()
        {
            var html = HtmlReportExporter.ToHtml(report);
            var start = html.IndexOf(AdviceHeading, StringComparison.Ordinal);
            return html[start..html.IndexOf("<h2>", start + AdviceHeading.Length, StringComparison.Ordinal)];
        }

        var (saved, savedUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        string invariant, local;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            invariant = Section();
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            local = Section();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (saved, savedUi);
        }

        Assert.Equal(invariant, local);
        Assert.Contains("1234 other readings", local, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_lists_every_card_whatever_was_hidden_in_the_window()
    {
        // The JSON is the full section: no cap, and no "Don't show again".
        var json = JsonReportExporter.ToJson(Report() with { Advice = AdvicePresentationTests.Cards(7) });

        using var document = JsonDocument.Parse(json);
        Assert.Equal(7, document.RootElement.GetProperty("Advice").GetProperty("Recommendations").GetArrayLength());
    }

    [Fact]
    public void A_value_that_is_not_a_number_reads_as_words_in_the_html()
    {
        // The HTML did NOT suppress the sentinel: two of the first fleet audit's reports printed
        // 309 digits in this cell, under a red "Action needed" chip.
        var verdict = new Verdict(
            new Metric("malware.quick_scan_age_days", "Days since last quick scan", "never run",
                HealthCategory.Security),
            Grade.Bad, "rule.malware.quick-scan-age", [], null);

        var html = HtmlReportExporter.ToHtml(Report(verdicts: [verdict]));

        Assert.Contains("<td>never run</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("17976931348623", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_reading_still_prints_its_number_and_unit()
    {
        var verdict = new Verdict(
            new Metric("malware.quick_scan_age_days", "Days since last quick scan", 3d,
                HealthCategory.Security, Unit: "days"),
            Grade.Good, "rule.malware.quick-scan-age", [], null);

        var html = HtmlReportExporter.ToHtml(Report(verdicts: [verdict]));

        Assert.Contains("<td>3 days</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_export_round_trips_the_report()
    {
        var trend = new TrendFinding(
            TrendKind.Projection, "storage.wear_pct", "SSD", "SSD endurance used",
            Grade.Normal, "reaching rated endurance around March 2028", 10, 30,
            DateTimeOffset.Now.AddDays(-100), ["Plan a replacement"]);

        var json = JsonReportExporter.ToJson(Report([trend]));

        Assert.Contains("\"Trends\"", json, StringComparison.Ordinal);
        Assert.Contains("Projection", json, StringComparison.Ordinal);
        Assert.Contains("storage.wear_pct", json, StringComparison.Ordinal);
    }
}
