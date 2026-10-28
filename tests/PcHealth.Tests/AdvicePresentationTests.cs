using System.Globalization;
using System.Text.Json;
using PcHealth.Core.Advice;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using PcHealth.Core.Text;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// How every surface lays the section out (STEP-B-SPEC 3.6 and 6): five cards, then "N more", and
/// every cap says what it dropped. A card hidden with "Don't show again" is taken out before the cap
/// and counted, and the hidden line says when an "Act now" card is among them. Lines about checks
/// that did not run never count toward the cap, and an empty section never reads as an all-clear
/// when a check did not finish.
/// </summary>
public sealed class AdvicePresentationTests
{
    private static ModuleRunSummary Module(string id, ModuleStatus status) => new(id, id, status, TimeSpan.Zero, null, []);

    private static readonly IReadOnlyList<ModuleRunSummary> AllFinished = [Module("disk.smart", ModuleStatus.Completed)];

    /// <summary>
    /// A section of <paramref name="count"/> cards, "T:1" first, each about its own failing drive.
    /// Every card has a "Do" line and a line for IT. <paramref name="actNow"/> names the cards marked
    /// Act now; the rest are general advice.
    /// </summary>
    internal static AdviceSection Cards(int count, params int[] actNow)
    {
        var verdicts = Enumerable.Range(1, count)
            .Select(i => AdviceEngineTests.V("smart.reallocated", 5, Grade.Bad, "MODEL-" + i.ToString(CultureInfo.InvariantCulture) + " — 240 GB", i))
            .ToList();
        var item = new TestAdviceItem("T", "drive-health", (facts, _) =>
        [
            .. verdicts.Select((v, at) =>
            {
                var number = at + 1;
                var urgency = actNow.Contains(number) ? AdviceUrgency.ActNow : AdviceUrgency.Advice;
                return new AdviceCard("T:" + number.ToString(CultureInfo.InvariantCulture), "T", "drive-health", urgency, 10 + number)
                    {
                        Subject = v.Metric.Subject, DiskNumber = v.Metric.DiskNumber,
                    }
                    .Title("advice.section.title")
                    .Line(AdviceLineRole.Do, "advice.h1.do.copy")
                    .Line(AdviceLineRole.ForIt, "advice.h1.for-it.retire")
                    .Because(Assert.Single(facts.All("smart.reallocated", v.Metric.Subject)), countInFingerprint: true)
                    .Build();
            }),
        ]);
        var section = AdviceEngineTests.Derive([item], verdicts);
        Assert.Empty(section.ItemErrors);
        Assert.Equal(count, section.Recommendations.Count);
        return section;
    }

    private static HashSet<string> Hide(AdviceSection section, params string[] ids) =>
        [.. section.Recommendations.Where(c => ids.Contains(c.Id)).Select(c => AdvicePresentation.DismissKey(c)!)];

    private static List<string> Ids(IEnumerable<Recommendation> cards) => [.. cards.Select(c => c.Id)];

    // --- The cap ------------------------------------------------------------------------------------

    [Fact]
    public void Seven_cards_give_five_shown_and_two_more_in_the_section_s_order()
    {
        var section = Cards(7);

        var (shown, more, hidden) = AdvicePresentation.Arrange(section, AdvicePresentation.NothingHidden);

        Assert.Equal(["T:1", "T:2", "T:3", "T:4", "T:5"], Ids(shown));
        Assert.Equal(["T:6", "T:7"], Ids(more));
        Assert.Empty(hidden);
        var view = AdvicePresentation.View(section, AllFinished, AdvicePresentation.NothingHidden);
        Assert.Equal("2 more", view.MoreHeader);
        Assert.Null(view.HiddenLine);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void Five_cards_or_fewer_are_all_shown_with_no_more_line(int count)
    {
        var section = count == 0 ? new AdviceSection([], [], [], 0, []) : Cards(count);

        var view = AdvicePresentation.View(section, AllFinished, AdvicePresentation.NothingHidden);

        Assert.Equal(count, view.Shown.Count);
        Assert.Empty(view.More);
        Assert.Null(view.MoreHeader);
    }

    [Fact]
    public void Hidden_cards_are_taken_out_counted_and_do_not_count_toward_the_five()
    {
        var section = Cards(7);

        var (shown, more, hidden) = AdvicePresentation.Arrange(section, Hide(section, "T:2", "T:7"));

        Assert.Equal(["T:1", "T:3", "T:4", "T:5", "T:6"], Ids(shown));
        Assert.Empty(more);
        Assert.Equal(["T:2", "T:7"], Ids(hidden));
        Assert.Equal("2 hidden", AdvicePresentation.View(section, AllFinished, Hide(section, "T:2", "T:7")).HiddenLine);
    }

    [Fact]
    public void The_hidden_line_says_when_an_Act_now_card_is_among_the_hidden()
    {
        var section = Cards(4, 1);

        string? HiddenLine(params string[] ids) => AdvicePresentation.View(section, AllFinished, Hide(section, ids)).HiddenLine;

        Assert.Equal("1 hidden", HiddenLine("T:2"));
        Assert.Equal("1 hidden, marked Act now", HiddenLine("T:1"));
        Assert.Equal("3 hidden, at least one of them marked Act now", HiddenLine("T:1", "T:2", "T:3"));
        Assert.Equal("2 hidden", HiddenLine("T:2", "T:3"));
    }

    [Fact]
    public void Hiding_every_card_shows_the_hidden_line_and_not_the_empty_state()
    {
        // "Nothing to suggest" would be untrue: there are suggestions, and someone chose to hide them.
        var section = Cards(2);

        var view = AdvicePresentation.View(section, AllFinished, Hide(section, "T:1", "T:2"));

        Assert.Empty(view.Shown);
        Assert.Equal("2 hidden", view.HiddenLine);
        Assert.Empty(view.EmptyState);
    }

    [Fact]
    public void A_key_kept_from_a_card_that_has_since_changed_hides_nothing()
    {
        var section = Cards(3);

        var (shown, _, hidden) = AdvicePresentation.Arrange(section, new HashSet<string>(["0123456789ABCDEF"], StringComparer.Ordinal));

        Assert.Equal(3, shown.Count);
        Assert.Empty(hidden);
    }

    [Fact]
    public void A_card_without_a_fingerprint_can_never_be_hidden()
    {
        var card = Cards(1).Recommendations[0] with { Fingerprint = "" };
        var section = new AdviceSection([card], [], [], 0, []);

        Assert.Null(AdvicePresentation.DismissKey(card));
        Assert.Single(AdvicePresentation.Arrange(section, new HashSet<string>([""], StringComparer.Ordinal)).Shown);
    }

    [Fact]
    public void The_key_is_the_first_sixteen_hex_digits_of_the_fingerprint()
    {
        var card = Cards(1).Recommendations[0];

        Assert.Equal(card.Fingerprint[..16], AdvicePresentation.DismissKey(card));
        Assert.Matches("^[0-9A-F]{16}$", AdvicePresentation.DismissKey(card));
    }

    [Fact]
    public void Lines_about_checks_that_did_not_run_never_count_toward_the_cap()
    {
        var cards = Cards(5);
        var line = AdviceCatalog.English.Text(AdviceLineRole.Finding, "advice.note.drive-health-not-included")!;
        var section = cards with
        {
            NotMeasured = [new NotMeasured("space", ["volumes"], line), new NotMeasured("heat", ["sensors"], line)],
            Notes = [line, line],
            ItemErrors = [line],
            OtherFindings = 9,
        };

        var view = AdvicePresentation.View(section, AllFinished, AdvicePresentation.NothingHidden);

        Assert.Equal(5, view.Shown.Count);
        Assert.Null(view.MoreHeader);
        Assert.Equal(2, view.NotMeasured.Count);
        Assert.Equal(2, view.Notes.Count);
        Assert.Single(view.Errors);
    }

    [Fact]
    public void A_negative_cap_is_a_fault_in_the_caller() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvicePresentation.Arrange(Cards(1), AdvicePresentation.NothingHidden, -1));

    // --- The words ----------------------------------------------------------------------------------

    [Fact]
    public void A_card_is_shown_with_its_urgency_title_subject_and_lines_for_IT_labelled()
    {
        var view = AdvicePresentation.View(Cards(1, 1), AllFinished, AdvicePresentation.NothingHidden);

        var card = Assert.Single(view.Shown);
        Assert.Equal(("Act now", "What you can do", "MODEL-1 — 240 GB"), (card.Urgency, card.Title, card.Subject));
        Assert.Equal([AdviceLineRole.Do, AdviceLineRole.ForIt], card.Lines.Select(l => l.Role));
        Assert.Null(card.Lines[0].Label);
        Assert.Equal("For IT:", card.Lines[1].Label);
        Assert.StartsWith("When the drive is retired", card.Lines[1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdviceUrgency.ActNow, "Act now")]
    [InlineData(AdviceUrgency.CheckAgain, "Check again")]
    [InlineData(AdviceUrgency.Plan, "Plan")]
    [InlineData(AdviceUrgency.Advice, "Advice")]
    public void Every_urgency_has_its_words(AdviceUrgency urgency, string words)
    {
        var card = Cards(1).Recommendations[0] with { Urgency = urgency };

        var view = AdvicePresentation.View(new AdviceSection([card], [], [], 0, []), AllFinished, AdvicePresentation.NothingHidden);

        Assert.Equal(words, Assert.Single(view.Shown).Urgency);
    }

    [Fact]
    public void An_empty_section_after_a_complete_scan_says_there_is_nothing_to_suggest()
    {
        var view = AdvicePresentation.View(new AdviceSection([], [], [], 0, []), AllFinished, AdvicePresentation.NothingHidden);

        Assert.Equal(["Nothing to suggest from the checks that ran."], view.EmptyState);
        Assert.Null(view.OtherFindings);
    }

    [Theory]
    [InlineData(ModuleStatus.Failed, 1, "1 check did not finish (listed above), so this is not an all-clear.")]
    [InlineData(ModuleStatus.PartiallyCompleted, 2, "2 checks did not finish (listed above), so this is not an all-clear.")]
    public void An_empty_section_after_an_incomplete_scan_says_it_is_not_an_all_clear(ModuleStatus status, int broken, string line)
    {
        IReadOnlyList<ModuleRunSummary> modules =
        [
            Module("volumes", ModuleStatus.Completed), Module("disk.smart", status),
            .. Enumerable.Range(1, broken - 1).Select(i => Module("m" + i.ToString(CultureInfo.InvariantCulture), status)),
        ];

        var view = AdvicePresentation.View(new AdviceSection([], [], [], 0, []), modules, AdvicePresentation.NothingHidden);

        Assert.Equal(["Nothing to suggest from the checks that ran.", line], view.EmptyState);
    }

    [Fact]
    public void A_check_skipped_on_purpose_does_not_make_the_empty_section_incomplete()
    {
        // CLAUDE.md rule 2: "deliberately not checked here" must not raise the alarm on a healthy PC.
        IReadOnlyList<ModuleRunSummary> modules = [Module("volumes", ModuleStatus.Completed), Module("battery", ModuleStatus.Skipped)];

        var view = AdvicePresentation.View(new AdviceSection([], [], [], 0, []), modules, AdvicePresentation.NothingHidden);

        Assert.Equal(["Nothing to suggest from the checks that ran."], view.EmptyState);
    }

    [Fact]
    public void A_section_with_cards_has_no_empty_state_even_after_an_incomplete_scan()
    {
        var view = AdvicePresentation.View(Cards(1), [Module("disk.smart", ModuleStatus.Failed)], AdvicePresentation.NothingHidden);

        Assert.Empty(view.EmptyState);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 other reading is graded Normal or Action needed; its tips are in its section below.")]
    [InlineData(4, "4 other readings are graded Normal or Action needed; their tips are in their sections below.")]
    public void Other_findings_are_counted_in_one_line(int count, string? line)
    {
        var view = AdvicePresentation.View(new AdviceSection([], [], [], count, []), AllFinished, AdvicePresentation.NothingHidden);

        Assert.Equal(line, view.OtherFindings);
    }

    [Fact]
    public void The_section_s_words_never_borrow_the_banner_s()
    {
        // ReportExportTests checks a clean report for exactly these, and a banner that fires on a
        // healthy PC is a banner nobody reads (CLAUDE.md rule 2).
        var section = Cards(7, 1) with { OtherFindings = 3 };
        IReadOnlyList<ModuleRunSummary> broken = [Module("disk.smart", ModuleStatus.Failed)];
        var views = new[]
        {
            AdvicePresentation.View(section, broken, Hide(section, "T:1")),
            AdvicePresentation.View(new AdviceSection([], [], [], 2, []), broken, AdvicePresentation.NothingHidden),
        };

        foreach (var words in views.Select(WordsOf))
        {
            Assert.DoesNotContain("INCOMPLETE", words, StringComparison.Ordinal);
            Assert.DoesNotContain("Some checks could not finish", words, StringComparison.Ordinal);
            Assert.DoesNotContain("not the same as being healthy", words, StringComparison.Ordinal);
            Assert.DoesNotContain("{", words, StringComparison.Ordinal);
        }
    }

    /// <summary>Everything a view says, one sentence per line.</summary>
    private static string WordsOf(AdviceView view)
    {
        var words = new List<string> { view.Title, view.Intro, view.MoreHeader ?? "", view.HiddenLine ?? "", view.OtherFindings ?? "" };
        words.AddRange(view.EmptyState.Concat(view.NotMeasured).Concat(view.Notes).Concat(view.Errors));
        foreach (var card in view.Shown.Concat(view.More).Concat(view.Hidden))
            words.AddRange(card.Lines.Select(l => l.Text).Prepend(card.Title).Prepend(card.Urgency));
        return string.Join("\n", words);
    }

    [Fact]
    public void Sentences_are_rendered_from_their_key_and_arguments_so_another_catalog_can_word_them()
    {
        // Step E passes the Vietnamese catalog here; the JSON's Text stays English.
        var english = AdviceCatalog.English;
        var entries = english.Keys.ToDictionary(k => k, k => english.Templates(k) switch
        {
            [var one] => (object)one,
            [var one, var other] => new Dictionary<string, string> { ["one"] = one, ["other"] = other },
            _ => throw new InvalidOperationException(k),
        });
        entries["advice.section.title"] = "Que faire";
        entries["advice.section.more"] = "{count} de plus";
        var other = AdviceCatalog.Parse(JsonSerializer.Serialize(entries));
        var section = Cards(6);

        var view = AdvicePresentation.View(section, AllFinished, AdvicePresentation.NothingHidden, catalog: other);

        Assert.Equal("Que faire", view.Title);
        Assert.Equal("Que faire", view.Shown[0].Title);
        Assert.Equal("1 de plus", view.MoreHeader);
        Assert.Equal("What you can do", section.Recommendations[0].Title.Text);
    }

    [Fact]
    public void A_catalog_that_cannot_word_the_section_gives_one_line_instead_of_failing_the_scan()
    {
        // The engine turns a broken catalog into an item error so the scan still finishes; a surface
        // that then threw on the same catalog would crash the headless scan before it wrote a report.
        var view = AdvicePresentation.View(Cards(3), AllFinished, AdvicePresentation.NothingHidden, catalog: AdviceCatalog.Parse("{}"));

        Assert.Empty(view.Shown);
        Assert.Equal(["This section could not be put into words (KeyNotFoundException). The readings below are complete."], view.Errors);
        Assert.Equal("What you can do", view.Title);
    }

    // --- The headless console -------------------------------------------------------------------------

    [Fact]
    public void The_console_lists_five_titles_and_says_how_many_more_the_report_holds()
    {
        var section = Cards(7, 1) with { OtherFindings = 4 };

        var lines = AdvicePresentation.ConsoleLines(section, AllFinished);

        Assert.Equal(
        [
            "What you can do (7):",
            "  1. [Act now] What you can do (MODEL-1 — 240 GB)",
            "  2. [Advice] What you can do (MODEL-2 — 240 GB)",
            "  3. [Advice] What you can do (MODEL-3 — 240 GB)",
            "  4. [Advice] What you can do (MODEL-4 — 240 GB)",
            "  5. [Advice] What you can do (MODEL-5 — 240 GB)",
            "  … and 2 more in the report.",
            "  4 other readings are graded Normal or Action needed; see the report.",
        ], lines);
    }

    [Fact]
    public void The_console_says_nothing_to_suggest_and_names_what_was_not_measured_and_not_included()
    {
        var notMeasured = AdviceCatalog.English.Text(AdviceLineRole.Finding, "advice.not-measured.drive-health",
            ("modules", AdviceArg.Words("Drive SMART health")))!;
        var note = AdviceCatalog.English.Text(AdviceLineRole.Finding, "advice.note.clock-not-included")!;
        var section = new AdviceSection([], [new NotMeasured("drive-health", ["disk.smart"], notMeasured)], [note], 1, []);

        var lines = AdvicePresentation.ConsoleLines(section, [Module("disk.smart", ModuleStatus.Failed)]);

        Assert.Equal(
        [
            "What you can do: nothing to suggest from the checks that ran.",
            "  1 check did not finish (listed below), so this is not an all-clear.",
            "  Not measured: " + notMeasured.Text,
            "  Note: " + note.Text,
            "  1 other reading is graded Normal or Action needed; see the report.",
        ], lines);
    }

    [Fact]
    public void A_clean_console_block_is_one_line()
    {
        Assert.Equal(["What you can do: nothing to suggest from the checks that ran."],
            AdvicePresentation.ConsoleLines(new AdviceSection([], [], [], 0, []), AllFinished));
    }

    [Fact]
    public void The_privacy_page_and_the_readme_say_what_the_console_prints()
    {
        // Both once said the headless console lists every card (review round, 2026-10-28). It prints
        // the first five titles and how many more the report holds; the HTML and the JSON list every
        // card. What all three share is that none of them hides one.
        Assert.Equal(5, AdvicePresentation.Cap);
        var lines = AdvicePresentation.ConsoleLines(Cards(7), AllFinished);
        Assert.Equal(5, lines.Count(l => l.Contains(". [", StringComparison.Ordinal)));
        Assert.Contains("  … and 2 more in the report.", lines);

        foreach (var doc in new[] { "PRIVACY.md", "README.md" })
        {
            var text = File.ReadAllText(Path.Combine(AdviceCatalogTests.RepoRoot(), doc));
            Assert.DoesNotContain("headless console always list every card", text, StringComparison.Ordinal);
            Assert.Contains("the headless console prints the first five and how many more the report holds", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_console_never_hides_a_card()
    {
        // "Don't show again" is the window's; the console takes no hidden set at all.
        var lines = AdvicePresentation.ConsoleLines(Cards(3), AllFinished);

        Assert.Equal("What you can do (3):", lines[0]);
        Assert.Equal(3, lines.Count(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Contains(". [", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("de-DE")]
    public void The_section_reads_the_same_whatever_the_PC_s_regional_format(string culture)
    {
        var section = Cards(12, 3) with { OtherFindings = 1234 };
        (IReadOnlyList<string> Console, string View) Render() =>
            (AdvicePresentation.ConsoleLines(section, AllFinished), JsonSerializer.Serialize(AdvicePresentation.View(section, AllFinished, AdvicePresentation.NothingHidden)));

        var invariant = Under(CultureInfo.InvariantCulture, Render);
        var local = Under(new CultureInfo(culture), Render);

        Assert.Equal(invariant.Console, local.Console);
        Assert.Equal(invariant.View, local.View);
        Assert.Contains("  1234 other readings are graded Normal or Action needed; see the report.", local.Console);
    }

    private static T Under<T>(CultureInfo culture, Func<T> work)
    {
        var (saved, savedUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return work();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }

    // --- With the real items ----------------------------------------------------------------------------

    [Fact]
    public void A_PC_with_a_card_of_every_kind_shows_the_hardware_first_and_the_rest_under_more()
    {
        var pc = new AdvicePc()
            .Disk(0, "system", "C:", 104.4).Drive("storage.wear_pct", 80)
            .Disk(1, "internal", "D:").Drive("smart.pending_sectors", 1, 1, AdvicePc.DataDisk)
            .Read("storage.volume.free_pct", 8, "C:")
            .Read("cpu.temperature_c", 91, AdvicePc.Cpu).Read("bench.cpu.clock_pct_of_base", 78, AdvicePc.Cpu)
            .Read("security.firewall.enabled", false, "Public profile")
            .Read("os.win11_ready", false).Info("os.win11_blockers", "Tpm")
            .Read("os.uptime_days", 9).Read("os.pending_reboot", true);
        var section = pc.Advice();

        var view = AdvicePresentation.View(section, pc.Modules, AdvicePresentation.NothingHidden);

        Assert.Empty(section.ItemErrors);
        Assert.Equal(["H1:disk1", "H1:disk0", "O3", "O4", "SPACE:C:"], view.Shown.Select(c => c.Card.Id));
        Assert.Equal(["O1", "S1"], view.More.Select(c => c.Card.Id));
        Assert.Equal("2 more", view.MoreHeader);
        Assert.Equal(["Act now", "Plan", "Advice"], view.Shown.Take(3).Select(c => c.Urgency));
        Assert.Contains(view.Shown[0].Lines, l => l.Label == "For IT:");
        Assert.Empty(view.EmptyState);
    }
}
