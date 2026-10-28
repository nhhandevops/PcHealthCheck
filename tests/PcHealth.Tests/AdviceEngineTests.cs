using PcHealth.Core.Advice;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests;

/// <summary>An advice item made for a test: it runs whatever the test hands it.</summary>
internal sealed class TestAdviceItem(
    string id, string area, Func<AdviceFacts, IReadOnlyList<Recommendation>, IReadOnlyList<Recommendation>> evaluate)
    : IAdviceItem
{
    public string Id => id;
    public string Area => area;
    public string Os => "windows";
    public int Rank => 10;
    public IReadOnlyList<string> Needs => [];

    public IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier) => evaluate(facts, earlier);

    /// <summary>One card per drive group that has a reading graded Normal or Bad, resting on those readings.</summary>
    public static TestAdviceItem CardPerFailingDrive() => new("T", "drive-health", (facts, _) =>
    [
        .. facts.DiskGroups
            .Where(g => g.Verdicts.Any(AdviceEvidence.IsProblem) || g.Trends.Any(AdviceEvidence.IsProblem))
            .Select(g =>
            {
                var card = new AdviceCard("T:" + g.Key, "T", "drive-health", AdviceUrgency.ActNow, 10)
                {
                    Subject = g.Subject, DiskNumber = g.DiskNumber,
                }.Title("advice.section.title");
                foreach (var v in g.Verdicts.Where(AdviceEvidence.IsProblem)) card.Because(v, countInFingerprint: true);
                foreach (var t in g.Trends.Where(AdviceEvidence.IsProblem)) card.Because(t);
                return card.Build();
            }),
    ]);

    /// <summary>One card resting on every reading with this id, whatever its grade; the card is checked when built.</summary>
    public static TestAdviceItem CardOn(string metricId, string id = "T", string area = "drive-health", int rank = 10,
        AdviceUrgency urgency = AdviceUrgency.ActNow) => new(id, area, (facts, _) =>
    {
        var card = new AdviceCard(id, id, area, urgency, rank).Title("advice.section.title");
        foreach (var v in facts.All(metricId)) card.Because(v);
        return [card.Build()];
    });
}

/// <summary>
/// The engine's own rules, with items made for the tests (STEP-B-SPEC 1, 3 and 7). The rule that
/// matters most: a card exists only because a reading was graded Normal or Bad. A card resting on an
/// information-only row, a Good reading or a Good forecast is refused, and the refusal is said, not
/// swallowed.
/// </summary>
public sealed class AdviceEngineTests
{
    private const string Model = "MODEL-A — 240 GB";

    internal static Verdict V(string id, MetricValue value, Grade grade, string? subject = null, int? disk = null,
        bool infoOnly = false, string? detail = null, HealthCategory category = HealthCategory.Storage) =>
        new(new Metric(id, id, value, category, Subject: subject, InfoOnly: infoOnly, DiskNumber: disk, Detail: detail),
            grade, infoOnly ? null : "rule." + id, [], null);

    internal static TrendFinding Trend(TrendKind kind, string id, string? subject, Grade grade, double from, double to,
        HealthCategory category = HealthCategory.Storage) =>
        new(kind, id, subject, id, grade, "message", from, to, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), [], category);

    internal static AdviceSection Derive(IEnumerable<IAdviceItem> items, IReadOnlyList<Verdict> verdicts,
        IReadOnlyList<TrendFinding>? trends = null, IReadOnlyList<ModuleRunSummary>? modules = null, string? systemDrive = "C:") =>
        new AdviceEngine(items).Derive(verdicts, trends ?? [], modules ?? [], new AdviceContext(systemDrive))!;

    private static AdviceFacts Facts(IReadOnlyList<Verdict> verdicts, IReadOnlyList<TrendFinding>? trends = null, string? systemDrive = "C:") =>
        AdviceFacts.Build(verdicts, trends ?? [], [], new AdviceContext(systemDrive));

    // --- Invariant 1: a card rests on a graded problem ----------------------------------------------

    [Fact]
    public void A_card_resting_on_a_graded_problem_is_kept()
    {
        var section = Derive([TestAdviceItem.CardOn("smart.reallocated")], [V("smart.reallocated", 5, Grade.Bad, Model, 0)]);

        var card = Assert.Single(section.Recommendations);
        Assert.Equal(Grade.Bad, card.Severity);
        Assert.Equal("What you can do", card.Title.Text);
        Assert.Matches("^[0-9A-F]{64}$", card.Fingerprint);
        Assert.Empty(section.ItemErrors);
    }

    public static TheoryData<string, Verdict?, TrendFinding?> NotAProblem => new()
    {
        // An information-only row is never a problem, whatever grade a hand-built verdict claims.
        { "an information-only row", V("storage.disk.used_gb", 104.4, Grade.Bad, "C:", 0, infoOnly: true), null },
        { "a Good reading", V("smart.reallocated", 0, Grade.Good, Model, 0), null },
        { "an unknown reading", V("smart.reallocated", "n/a", Grade.Unknown, Model, 0), null },
        // TrendAnalyzer.BuildProjection grades a limit two years or more away Good.
        { "a Good projection", null, Trend(TrendKind.Projection, "storage.wear_pct", Model, Grade.Good, 10, 12) },
    };

    [Theory]
    [MemberData(nameof(NotAProblem))]
    public void A_card_resting_only_on_what_is_not_a_problem_is_refused_and_said(string what, Verdict? verdict, TrendFinding? trend)
    {
        var item = new TestAdviceItem("T", "drive-health", (_, _) =>
        {
            var card = new AdviceCard("T", "T", "drive-health", AdviceUrgency.ActNow, 10).Title("advice.section.title");
            if (verdict is not null) card.Because(verdict);
            if (trend is not null) card.Because(trend);
            return [card.Build()];
        });

        var section = Derive([item], verdict is null ? [] : [verdict], trend is null ? [] : [trend]);

        Assert.True(section.Recommendations.Count == 0, what);
        var error = Assert.Single(section.ItemErrors);
        Assert.Equal("Advice about drive health could not be worked out (AdviceInvariantException). The readings below are complete.", error.Text);
        Assert.Equal("advice.section.item-error", error.Key);
    }

    [Fact]
    public void A_card_made_without_the_builder_is_checked_again_and_only_the_bad_one_goes()
    {
        var good = V("smart.reallocated", 0, Grade.Good, Model, 0);
        var bad = V("smart.pending_sectors", 2, Grade.Bad, Model, 0);
        var title = new AdviceText(AdviceLineRole.Title, "advice.section.title", new Dictionary<string, AdviceArg>(), "What you can do");
        var item = new TestAdviceItem("T", "drive-health", (_, _) =>
        [
            // Claims a Bad grade for a reading this report graded Good.
            new Recommendation("T:claims", "T", "drive-health", AdviceUrgency.ActNow, Grade.Bad, 10, null, 0, title, [],
                [new AdviceEvidence("smart.reallocated", Model, 0, Grade.Bad, null)]) { Fingerprint = "X" },
            // Rests on a reading that is not in this report at all.
            new Recommendation("T:made-up", "T", "drive-health", AdviceUrgency.ActNow, Grade.Bad, 10, null, 0, title, [],
                [new AdviceEvidence("smart.reallocated", "OTHER", 7, Grade.Bad, null)]) { Fingerprint = "X" },
            // One real problem, padded with a reading that is not in this report.
            new Recommendation("T:padded", "T", "drive-health", AdviceUrgency.ActNow, Grade.Bad, 10, null, 0, title, [],
                [AdviceEvidence.From(bad), new AdviceEvidence("smart.offline_uncorrectable", Model, 0, Grade.Bad, null)]) { Fingerprint = "X" },
            // Never went through AdviceCard, so nothing was checked and it has no fingerprint.
            new Recommendation("T:unchecked", "T", "drive-health", AdviceUrgency.ActNow, Grade.Bad, 10, null, 0, title, [],
                [AdviceEvidence.From(bad)]),
            new AdviceCard("T:sound", "T", "drive-health", AdviceUrgency.ActNow, 10).Title("advice.section.title").Because(bad).Build(),
        ]);

        var section = Derive([item], [good, bad]);

        Assert.Equal("T:sound", Assert.Single(section.Recommendations).Id);
        Assert.Single(section.ItemErrors);
    }

    [Fact]
    public void A_card_claiming_another_item_or_area_is_refused()
    {
        var bad = V("smart.reallocated", 5, Grade.Bad, Model, 0);
        var item = new TestAdviceItem("T", "drive-health", (_, _) =>
        [
            new AdviceCard("A", "OTHER", "drive-health", AdviceUrgency.ActNow, 10).Title("advice.section.title").Because(bad).Build(),
            new AdviceCard("B", "T", "security", AdviceUrgency.ActNow, 10).Title("advice.section.title").Because(bad).Build(),
        ]);

        var section = Derive([item], [bad]);

        Assert.Empty(section.Recommendations);
        Assert.Single(section.ItemErrors);
    }

    [Fact]
    public void Two_cards_with_one_id_are_refused()
    {
        var bad = V("smart.reallocated", 5, Grade.Bad, Model, 0);

        var section = Derive([TestAdviceItem.CardOn("smart.reallocated"), TestAdviceItem.CardOn("smart.reallocated")], [bad]);

        Assert.Single(section.Recommendations);
        Assert.Single(section.ItemErrors);
    }

    [Fact]
    public void A_sentence_missing_an_argument_makes_the_card_an_error_never_a_gap()
    {
        var bad = V("smart.reallocated", 5, Grade.Bad, Model, 0);
        var careless = new TestAdviceItem("T", "drive-health", (_, _) =>
            [new AdviceCard("T", "T", "drive-health", AdviceUrgency.ActNow, 10).Title("advice.section.title")
                .Line(AdviceLineRole.Finding, "advice.section.other-findings").Because(bad).Build()]);
        var careful = new TestAdviceItem("U", "space", (_, _) =>
        {
            var card = new AdviceCard("U", "U", "space", AdviceUrgency.Advice, 45).Title("advice.section.title").Because(bad);
            Assert.False(card.TryLine(AdviceLineRole.Finding, "advice.section.other-findings"));
            Assert.True(card.TryLine(AdviceLineRole.Finding, "advice.section.other-findings", ("count", AdviceArg.Count(2))));
            return [card.Build()];
        });

        var section = Derive([careless, careful], [bad]);

        var kept = Assert.Single(section.Recommendations);
        Assert.Equal("U", kept.Id);
        Assert.Equal("2 other readings are graded Normal or Action needed; their tips are in their sections below.", Assert.Single(kept.Lines).Text);
        Assert.Contains("drive health", Assert.Single(section.ItemErrors).Text, StringComparison.Ordinal);
        Assert.DoesNotContain(section.Recommendations.SelectMany(c => c.Lines.Append(c.Title)), l => l.Text.Contains('{', StringComparison.Ordinal));
    }

    // --- Failure handling ---------------------------------------------------------------------------

    [Fact]
    public void An_item_that_throws_gives_one_line_and_the_items_after_it_still_run()
    {
        var bad = V("smart.reallocated", 5, Grade.Bad, Model, 0);
        var broken = new TestAdviceItem("B", "security", (_, _) => throw new InvalidOperationException("C:\\Users\\Example\\secret"));

        var section = Derive([broken, TestAdviceItem.CardOn("smart.reallocated")], [bad]);

        Assert.Single(section.Recommendations);
        var error = Assert.Single(section.ItemErrors);
        Assert.Equal("Advice about security settings could not be worked out (InvalidOperationException). The readings below are complete.", error.Text);
        Assert.DoesNotContain("secret", error.Text, StringComparison.Ordinal); // the type, never the message
    }

    [Fact]
    public void An_item_of_an_area_the_catalog_does_not_name_still_reports_its_error()
    {
        var section = Derive([new TestAdviceItem("Z", "zz-area", (_, _) => throw new FormatException())], []);

        Assert.Contains("Advice about zz-area could not be worked out (FormatException)", Assert.Single(section.ItemErrors).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_item_sees_the_cards_of_the_items_before_it()
    {
        var bad = V("smart.reallocated", 5, Grade.Bad, Model, 0);
        IReadOnlyList<Recommendation>? seen = null;
        var last = new TestAdviceItem("S1", "startup", (_, earlier) => { seen = earlier; return []; });

        Derive([TestAdviceItem.CardOn("smart.reallocated", id: "H1"), last], [bad]);

        Assert.Equal("H1", Assert.Single(seen!).Item);
    }

    // --- Which OS gets a section --------------------------------------------------------------------

    [Fact]
    public void Windows_gets_a_section_even_with_nothing_to_say_and_Linux_gets_none()
    {
        IReadOnlyList<Verdict> verdicts = [V("smart.reallocated", 0, Grade.Good, Model, 0)];
        var context = new AdviceContext("C:");

        var windows = AdviceEngine.ForOs("windows").Derive(verdicts, [], [], context);

        Assert.NotNull(windows);
        Assert.Empty(windows.Recommendations);
        Assert.Empty(windows.ItemErrors);
        Assert.Equal(0, windows.OtherFindings);
        Assert.Null(AdviceEngine.ForOs("linux").Derive(verdicts, [], [], context));
        Assert.Null(AdviceEngine.None.Derive(verdicts, [], [], context));
    }

    // --- Other findings -----------------------------------------------------------------------------

    [Fact]
    public void Other_findings_count_the_graded_problems_no_card_used_and_nothing_else()
    {
        IReadOnlyList<Verdict> verdicts =
        [
            V("smart.reallocated", 5, Grade.Bad, Model, 0),                  // used by the card
            V("updates.pending_count", 3, Grade.Normal, category: HealthCategory.SoftwareOs), // counted
            V("battery.capacity_pct", 55, Grade.Bad, "BAT0", category: HealthCategory.Battery), // counted
            V("smart.pending_sectors", 0, Grade.Good, Model, 0),
            V("storage.disk.role", "system", Grade.Bad, "C:", 0, infoOnly: true), // hand-graded, still information only
            V("os.win11_blockers", "none", Grade.Unknown, infoOnly: true),
        ];
        IReadOnlyList<TrendFinding> trends =
        [
            Trend(TrendKind.CounterGrowth, "smart.reallocated", Model, Grade.Bad, 3, 5), // used by the card
            Trend(TrendKind.Projection, "storage.wear_pct", "OTHER", Grade.Normal, 40, 60), // counted
            Trend(TrendKind.Projection, "battery.capacity_pct", "BAT0", Grade.Good, 70, 69, HealthCategory.Battery),
        ];

        var section = Derive([TestAdviceItem.CardPerFailingDrive()], verdicts, trends);

        Assert.Single(section.Recommendations);
        Assert.Equal(3, section.OtherFindings);
        Assert.Empty(section.ItemErrors);
    }

    [Fact]
    public void With_no_cards_every_graded_problem_is_another_finding()
    {
        var section = AdviceEngine.ForOs("windows").Derive(
            [V("updates.pending_count", 3, Grade.Normal), V("os.supported", false, Grade.Bad)],
            [Trend(TrendKind.CounterGrowth, "smart.crc_errors", Model, Grade.Bad, 1, 2)], [], new AdviceContext("C:"))!;

        Assert.Equal(3, section.OtherFindings);
    }

    // --- Order --------------------------------------------------------------------------------------

    [Fact]
    public void Cards_come_out_by_rank_then_Bad_before_Normal_then_drive_and_the_order_never_depends_on_the_input()
    {
        IReadOnlyList<Verdict> verdicts =
        [
            V("a", 1, Grade.Normal, "x", 1), V("b", 1, Grade.Bad, "x", 2), V("c", 1, Grade.Bad, "x", 0),
            V("d", 1, Grade.Bad), V("e", 1, Grade.Normal), V("f", 1, Grade.Bad),
        ];
        IAdviceItem[] items =
        [
            TestAdviceItem.CardOn("f", id: "S1", area: "startup", rank: 60),
            TestAdviceItem.CardOn("a", id: "H1n1", rank: 10),
            TestAdviceItem.CardOn("e", id: "SPACE", area: "space", rank: 45),
            TestAdviceItem.CardOn("b", id: "H1b2", rank: 10),
            TestAdviceItem.CardOn("d", id: "O3", area: "heat", rank: 25),
            TestAdviceItem.CardOn("c", id: "H1b0", rank: 12),
        ];
        string[] expected = ["H1b2", "H1n1", "H1b0", "O3", "SPACE", "S1"];
        // Disk numbers come from the evidence only in CardPerFailingDrive; set them here through the card.
        var withDisks = items.Select(i => (IAdviceItem)new TestAdviceItem(i.Id, i.Area, (facts, earlier) =>
            [.. i.Evaluate(facts, earlier).Select(c => c with { DiskNumber = c.Evidence[0].DiskNumber })])).ToList();

        var random = new Random(20261028);
        for (var round = 0; round < 100; round++)
        {
            var shuffled = withDisks.OrderBy(_ => random.Next()).ToList();
            var section = Derive(shuffled, [.. verdicts.OrderBy(_ => random.Next())]);
            Assert.Equal(expected, section.Recommendations.Select(c => c.Id));
            Assert.Empty(section.ItemErrors);
        }
    }

    // --- The facts: disk groups ---------------------------------------------------------------------

    [Fact]
    public void Drive_readings_are_grouped_by_disk_number_and_take_their_role_letters_and_space_from_the_disk_rows()
    {
        var facts = Facts(
        [
            V("smart.reallocated", 5, Grade.Bad, Model, 0),
            V("smart.temperature_c", 38, Grade.Good, Model, 0),
            V("storage.disk.role", "system", Grade.Unknown, "C:, D:", 0, infoOnly: true),
            V("storage.disk.used_gb", 104.4, Grade.Unknown, "C:, D:", 0, infoOnly: true),
            V("storage.disk.role", "internal", Grade.Unknown, "Disk 3", 3, infoOnly: true),
            V("storage.wear_pct", 12, Grade.Good, "MODEL-B — 1000 GB", 2),
        ]);

        Assert.Equal(["disk0", "disk2", "disk3"], facts.DiskGroups.Select(g => g.Key));
        var system = facts.DiskGroups[0];
        Assert.Equal((0, Model, DiskRole.System, "C:, D:", 104.4), (system.DiskNumber!.Value, system.Subject, system.Role, system.Letters, system.UsedGb!.Value));
        Assert.Equal(4, system.Verdicts.Count);
        Assert.All(facts.DiskGroups, g => Assert.False(g.SharesHistory)); // three models: a series each
        Assert.Equal(DiskRole.Unknown, facts.DiskGroups[1].Role); // no role row: counts as inside the PC
        Assert.Null(facts.DiskGroups[1].UsedGb);
        Assert.Null(facts.DiskGroups[2].Letters);                 // "Disk 3" is not a drive letter
        Assert.Equal(DiskRole.Internal, facts.DiskGroups[2].Role);
    }

    [Theory]
    [InlineData("external", DiskRole.External)]
    [InlineData("SYSTEM", DiskRole.System)]
    [InlineData(" internal ", DiskRole.Internal)]
    [InlineData("usb-thing", DiskRole.Unknown)]
    public void A_role_row_s_value_names_the_role(string value, DiskRole expected) =>
        Assert.Equal(expected, Facts([V("storage.disk.role", value, Grade.Unknown, "E:", 1, infoOnly: true)]).DiskGroups.Single().Role);

    [Fact]
    public void A_drive_reading_that_names_no_disk_gets_a_group_of_its_own_by_subject()
    {
        var facts = Facts(
        [
            V("smart.reallocated", 5, Grade.Bad, "MODEL-B — 1000 GB"),
            V("smart.pending_sectors", 0, Grade.Good, "MODEL-B — 1000 GB"),
            V("smart.reallocated", 1, Grade.Normal, Model),
            V("smart.temperature_c", 40, Grade.Good, Model),       // not a damage or wear reading
            V("battery.capacity_pct", 80, Grade.Good, "BAT0", category: HealthCategory.Battery),
        ]);

        Assert.Equal(["unknown-1", "unknown-2"], facts.DiskGroups.Select(g => g.Key));
        Assert.All(facts.DiskGroups, g => Assert.Equal((DiskRole.Unknown, (int?)null, (string?)null), (g.Role, g.DiskNumber, g.Letters)));
        Assert.Equal(Model, facts.DiskGroups[0].Subject);
        Assert.Single(facts.DiskGroups[0].Verdicts);
        Assert.Equal(2, facts.DiskGroups[1].Verdicts.Count);
    }

    [Fact]
    public void A_trend_joins_the_drive_whose_reading_has_its_id_and_subject()
    {
        var facts = Facts(
            [V("smart.reallocated", 5, Grade.Bad, Model, 1), V("smart.reallocated", 0, Grade.Good, "MODEL-B — 1000 GB", 0)],
            [Trend(TrendKind.CounterGrowth, "smart.reallocated", Model, Grade.Bad, 3, 5)]);

        Assert.Empty(facts.DiskGroups.Single(g => g.DiskNumber == 0).Trends);
        Assert.Single(facts.DiskGroups.Single(g => g.DiskNumber == 1).Trends);
        Assert.All(facts.DiskGroups, g => Assert.False(g.SharesHistory));
    }

    [Fact]
    public void Two_identical_drives_only_one_failing_give_one_card_on_the_right_disk()
    {
        // Two drives of one model share a Subject, so they share a history series too. Only the disk
        // number tells them apart, and the trend, which may compare one drive with the other, joins
        // neither: the card rests on the failing drive's own reading.
        IReadOnlyList<Verdict> verdicts = [V("smart.reallocated", 0, Grade.Good, Model, 0), V("smart.reallocated", 5, Grade.Bad, Model, 1)];
        IReadOnlyList<TrendFinding> trends = [Trend(TrendKind.CounterGrowth, "smart.reallocated", Model, Grade.Bad, 3, 5)];

        var section = Derive([TestAdviceItem.CardPerFailingDrive()], verdicts, trends);

        var card = Assert.Single(section.Recommendations);
        Assert.Equal(1, card.DiskNumber);
        Assert.Equal("T:disk1", card.Id);
        Assert.Equal(AdviceEvidence.From(verdicts[1]), Assert.Single(card.Evidence));
        Assert.Equal(1, section.OtherFindings); // the trend is left to its own row
        Assert.Empty(section.ItemErrors);
    }

    public static TheoryData<string, double, double, double, TrendKind, string> SharedSeries => new()
    {
        // HistoryStore keeps one series per id and Subject, and TrendAnalyzer compares a reading with
        // the last sample stored: disk 0's 2 against disk 1's 0 is a count that never grew.
        { "a count that only differs", 2, 0, 0, TrendKind.CounterGrowth, "smart.reallocated" },
        { "a count both drives hold", 5, 5, 3, TrendKind.CounterGrowth, "smart.reallocated" },
        // A projection starts from the series' first sample, which may be the other drive's.
        { "a projection", 75, 20, 20, TrendKind.Projection, "storage.wear_pct" },
    };

    [Theory]
    [MemberData(nameof(SharedSeries))]
    public void A_trend_on_a_series_two_drives_write_to_joins_neither_and_makes_no_group_of_its_own(
        string what, double disk0, double disk1, double from, TrendKind kind, string metricId)
    {
        var facts = Facts(
            [V(metricId, disk0, Grade.Normal, Model, 0), V(metricId, disk1, Grade.Good, Model, 1),
             V("smart.reallocated", 0, Grade.Good, "MODEL-B — 1000 GB", 2)],
            [Trend(kind, metricId, Model, Grade.Bad, from, disk0)]);

        Assert.True(facts.DiskGroups.All(g => g.Trends.Count == 0), what);
        Assert.Equal(["disk0", "disk1", "disk2"], facts.DiskGroups.Select(g => g.Key));
        Assert.Equal([true, true, false], facts.DiskGroups.Select(g => g.SharesHistory));
        Assert.Single(facts.TrendsOf(metricId, Model)); // still in the report, for its own row
    }

    [Fact]
    public void A_trend_of_something_that_is_not_a_drive_joins_no_drive()
    {
        var facts = Facts(
            [V("battery.capacity_pct", 55, Grade.Bad, "BAT0", category: HealthCategory.Battery)],
            [Trend(TrendKind.Projection, "battery.capacity_pct", "BAT0", Grade.Bad, 70, 55, HealthCategory.Battery)]);

        Assert.Empty(facts.DiskGroups);
        Assert.Single(facts.TrendsOf("battery.capacity_pct", "BAT0"));
    }

    // --- The facts: the system volume ---------------------------------------------------------------

    [Theory]
    [InlineData("C:", true)]
    [InlineData("c:", true)]
    [InlineData("C: (Windows)", true)]
    [InlineData("CD:", false)]
    [InlineData("D:", false)]
    [InlineData("C:\\", false)]
    public void The_system_volume_is_the_free_space_row_of_the_drive_Windows_runs_from(string subject, bool isSystem)
    {
        var facts = Facts([V("storage.volume.free_pct", 8, Grade.Bad, subject)]);

        Assert.Equal(isSystem, facts.SystemVolume is not null);
    }

    [Fact]
    public void An_information_only_free_space_row_is_never_the_system_volume_and_no_system_drive_means_none()
    {
        Assert.Null(Facts([V("storage.volume.free_pct", 5, Grade.Unknown, "C:", infoOnly: true)]).SystemVolume);
        Assert.Null(Facts([V("storage.volume.free_pct", 5, Grade.Bad, "C:")], systemDrive: null).SystemVolume);
    }

    [Theory]
    [InlineData("C:\\", "C:")]
    [InlineData("c", "C:")]
    [InlineData("D:", "D:")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("\\\\server\\share", null)]
    [InlineData("CD", null)]
    public void The_system_drive_is_named_as_a_drive_letter(string? raw, string? expected) =>
        Assert.Equal(expected, AdviceContext.NormalizeDrive(raw));

    [Fact]
    public void The_facts_answer_by_id_subject_disk_and_module()
    {
        var facts = AdviceFacts.Build(
            [V("smart.reallocated", 5, Grade.Bad, Model, 0), V("os.uptime_days", 9.5, Grade.Good)], [],
            [new ModuleRunSummary("disk.smart", "Drive SMART health", ModuleStatus.PartiallyCompleted, TimeSpan.Zero, null, [])],
            new AdviceContext("C:"));

        Assert.Single(facts.All("smart.reallocated", Model));
        Assert.Empty(facts.All("smart.reallocated", "OTHER"));
        Assert.Single(facts.OnDisk(0));
        Assert.Equal(9.5, facts.First("os.uptime_days")!.Metric.Value.Number);
        Assert.Null(facts.First("os.pending_reboot"));
        Assert.Equal(ModuleStatus.PartiallyCompleted, facts.StatusOf("disk.smart"));
        Assert.Null(facts.StatusOf("disk.reliability")); // not part of this scan
    }

    // --- Fingerprints -------------------------------------------------------------------------------

    private static string FingerprintOf(IReadOnlyList<Verdict> verdicts, IReadOnlyList<TrendFinding>? trends = null) =>
        Assert.Single(Derive([TestAdviceItem.CardPerFailingDrive()], verdicts, trends).Recommendations).Fingerprint;

    [Fact]
    public void A_fingerprint_is_the_same_on_a_repeated_scan_and_moves_when_the_problem_gets_worse()
    {
        Verdict Reallocated(double count, Grade grade) => V("smart.reallocated", count, grade, Model, 0);
        var first = FingerprintOf([Reallocated(3, Grade.Normal)]);

        Assert.Equal(first, FingerprintOf([Reallocated(3, Grade.Normal)]));
        Assert.Equal(first, FingerprintOf([V("smart.temperature_c", 39, Grade.Good, Model, 0), Reallocated(3, Grade.Normal)]));
        Assert.NotEqual(first, FingerprintOf([Reallocated(5, Grade.Bad)]));                         // Normal turned Bad
        Assert.NotEqual(first, FingerprintOf([Reallocated(4, Grade.Normal)]));                       // a counter that grew
        Assert.NotEqual(first, FingerprintOf([Reallocated(3, Grade.Normal), V("smart.pending_sectors", 1, Grade.Bad, Model, 0)])); // new evidence
        Assert.NotEqual(first, FingerprintOf([Reallocated(3, Grade.Normal)],
            [Trend(TrendKind.CounterGrowth, "smart.reallocated", Model, Grade.Bad, 2, 3)]));        // a new growth trend
    }

    [Fact]
    public void A_fingerprint_moves_with_the_urgency_and_ignores_the_order_evidence_was_added_in()
    {
        var a = V("smart.reallocated", 5, Grade.Bad, Model, 0);
        var b = V("smart.pending_sectors", 1, Grade.Bad, Model, 0);
        string Print(AdviceUrgency urgency, params Verdict[] evidence)
        {
            var card = new AdviceCard("T", "T", "drive-health", urgency, 10) { DiskNumber = 0 }.Title("advice.section.title");
            foreach (var v in evidence) card.Because(v);
            return card.Build().Fingerprint;
        }

        Assert.Equal(Print(AdviceUrgency.ActNow, a, b), Print(AdviceUrgency.ActNow, b, a));
        Assert.NotEqual(Print(AdviceUrgency.CheckAgain, a, b), Print(AdviceUrgency.ActNow, a, b));
        // Without countInFingerprint, a value that moves inside the same grade does not bring a card back.
        Assert.Equal(Print(AdviceUrgency.ActNow, a), Print(AdviceUrgency.ActNow, V("smart.reallocated", 9, Grade.Bad, Model, 0)));
    }

    // --- Invariant 5: no Detail, no trend Message ----------------------------------------------------

    [Fact]
    public void A_card_never_carries_a_reading_s_detail_or_a_trend_s_message()
    {
        var verdict = V("smart.reallocated", 5, Grade.Bad, Model, 0, detail: "Found under C:\\Users\\Example\\Secret plans");
        var trend = new TrendFinding(TrendKind.CounterGrowth, "smart.reallocated", Model, "Reallocated", Grade.Bad,
            "Getting worse: 3,0 at the last scan", 3, 5, DateTimeOffset.Now.AddDays(-7), []);
        var report = new ScanReport
        {
            GeneratedAt = DateTimeOffset.Now, MachineName = "TESTPC", OsDescription = "Windows", AppVersion = "0", RanElevated = false,
            Modules = [], Verdicts = [], Receipt = [], CategoryGrades = new Dictionary<HealthCategory, Grade>(), OverallGrade = Grade.Bad,
            Advice = Derive([TestAdviceItem.CardPerFailingDrive()], [verdict], [trend]),
        };

        var json = JsonReportExporter.ToJson(report);

        Assert.Contains("\"smart.reallocated\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret plans", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Getting worse", json, StringComparison.Ordinal);
    }

    // --- The numbers advice adds ----------------------------------------------------------------------

    // The space in use is in Windows' GB (1024^3 bytes) and the sizes are the box's (10^9 bytes): a
    // "256 GB" SSD holds 238.4 of Windows' GB, so 1.5 times 165 GB in use (247.5) does not fit in it.
    [Theory]
    [InlineData(0, 128)]
    [InlineData(79.4, 128)]      // 127.88 box GB fits in 128
    [InlineData(79.5, 256)]      // 128.04 does not
    [InlineData(158.9, 256)]
    [InlineData(159.0, 512)]     // 256.09
    [InlineData(165, 512)]       // was 256: 1.44 times, not 1.5
    [InlineData(620.8, 1000)]
    [InlineData(620.9, 2000)]
    [InlineData(2483.5, 4000)]
    public void A_replacement_drive_is_the_smallest_common_size_of_one_and_a_half_times_the_space_in_use(double usedGb, int sizeGb) =>
        Assert.Equal(sizeGb, AdviceThresholds.ReplacementSizeGb(usedGb));

    [Fact]
    public void Space_beyond_the_largest_common_size_gives_no_size_rather_than_a_wrong_one() =>
        Assert.Null(AdviceThresholds.ReplacementSizeGb(2483.6));

    [Theory]
    [InlineData(104.4)]
    [InlineData(165)]
    [InlineData(341)]
    [InlineData(2000)]
    public void The_size_picked_holds_one_and_a_half_times_the_space_in_use_counted_in_bytes(double usedGb)
    {
        var size = AdviceThresholds.ReplacementSizeGb(usedGb);

        Assert.NotNull(size);
        Assert.True(size.Value * 1e9 >= usedGb * 1073741824.0 * AdviceThresholds.ReplacementFactor, $"{size} GB for {usedGb} GB in use");
    }

    [Theory]
    [InlineData(104.4, 100, 20)] // 17.8 minutes
    [InlineData(104.4, 50, 40)]  // 35.6 minutes
    [InlineData(29.296875, 100, 5)] // exactly 5 minutes stays 5
    [InlineData(29.4, 100, 10)]
    [InlineData(0, 100, 0)]
    public void A_copy_time_is_rounded_up_to_five_minutes(double usedGb, double mbPerSecond, double minutes) =>
        Assert.Equal(minutes, AdviceThresholds.CopyMinutes(usedGb, mbPerSecond));

    [Fact]
    public void The_restart_threshold_alone_can_never_make_a_card()
    {
        // S1's own number sits below the uptime row's Good band (os.json: Good up to 30 days), so
        // "up more than 7 days" can only narrow a trigger that a graded reading makes.
        var uptimeRule = RulesLoader.LoadEmbedded("windows").Single(r => r.MetricId == "os.uptime_days");
        var good = uptimeRule.Bands.First(b => string.Equals(b.Grade, "Good", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(good.Lte);
        Assert.True(good.Lte > AdviceThresholds.RestartAfterDays);
    }
}
