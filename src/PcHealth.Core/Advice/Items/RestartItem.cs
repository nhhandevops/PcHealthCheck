namespace PcHealth.Core.Advice.Items;

/// <summary>
/// S1: restart Windows once, with Restart rather than Shut down (REMEDIES-PLAN S1, STEP-B-SPEC 5).
/// Uptime comes from LastBootUpTime, which Fast Startup's Shut down does not reset. More than
/// <see cref="AdviceThresholds.RestartAfterDays"/> days only narrows the trigger: the uptime row is
/// graded Good up to 30 days, so a graded reading must make the card, a pending restart or memory
/// promised to programs graded Normal or Bad.
///
/// It runs last, after H1. When H1 finds the drive Windows runs from failing, its first line is S4's
/// label, "this will not fix a slow start", and its rank already keeps it below every hardware card.
/// The low-memory warnings are not a trigger: a 30-day count says nothing about the time since the
/// last restart.
/// </summary>
public sealed class RestartItem : IAdviceItem
{
    public const string ItemId = "S1";

    public string Id => ItemId;

    public string Area => "startup";

    public string Os => "windows";

    public int Rank => 60;

    public IReadOnlyList<string> Needs { get; } = ["sys.overview"];

    public IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier)
    {
        var uptime = facts.First("os.uptime_days");
        if (uptime?.Metric.Value.Number is not { } days || !(days > AdviceThresholds.RestartAfterDays)) return [];
        var pending = facts.Problems("os.pending_reboot").FirstOrDefault();
        var commit = facts.Problems("memory.commit_pct").FirstOrDefault(v => v.Metric.Value.Number is not null);
        if (pending is null && commit is null) return [];

        var card = new AdviceCard(ItemId, ItemId, Area, AdviceUrgency.Advice, Rank).Title("advice.s1.title");
        if (AdviceEvidence.IsProblem(uptime)) card.Because(uptime);

        var failing = earlier.Select(c => DriveHealthItem.FailingDriveRole(c, facts)).ToList();
        if (failing.Contains(DiskRole.System)) card.Line(AdviceLineRole.Finding, "advice.s4.failing-drive");
        else if (failing.Contains(DiskRole.Unknown)) card.Line(AdviceLineRole.Finding, "advice.s4.failing-drive.unknown");

        card.Line(AdviceLineRole.Finding, "advice.s1.uptime", ("days", AdviceArg.Days(days)));
        if (pending is not null) card.Because(pending).Line(AdviceLineRole.Finding, "advice.s1.pending");
        if (commit is not null)
        {
            card.Because(commit).Line(AdviceLineRole.Finding, "advice.s1.commit",
                ("commitPct", AdviceArg.Percent(commit.Metric.Value.Number!.Value)));
        }
        return
        [
            card.Line(AdviceLineRole.Why, "advice.s1.why")
                .Line(AdviceLineRole.Do, "advice.s1.do")
                .Build(),
        ];
    }
}
