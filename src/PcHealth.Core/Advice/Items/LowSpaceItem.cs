using PcHealth.Core.Metrics;

namespace PcHealth.Core.Advice.Items;

/// <summary>
/// SPACE: the drive Windows runs from is low on space, with the D4-D9 advice (REMEDIES-PLAN D4-D9,
/// STEP-B-SPEC 5). One card, for the system drive only: a full USB drive is shown for information
/// and never graded, and a data drive's free space is its owner's business.
///
/// The lines go safest first. None names a size: the hibernation file, page file, reserved storage,
/// old Windows copy, Downloads and caches are Step C's readings, and until they exist no line can
/// print a number it does not have. The app offers nothing for deletion (decision 3).
///
/// When H1 finds the drive Windows runs from failing, this card is dropped: cleaning up works a
/// failing drive, and copying the files off comes first (principle 3). H1's card says the drive is
/// also low on space instead.
/// </summary>
public sealed class LowSpaceItem : IAdviceItem
{
    public const string ItemId = "SPACE";

    public string Id => ItemId;

    public string Area => "space";

    public string Os => "windows";

    public int Rank => 45;

    public IReadOnlyList<string> Needs { get; } = ["volumes"];

    public IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier)
    {
        if (facts.SystemVolume is not { } volume || !AdviceEvidence.IsProblem(volume)) return [];
        if (earlier.Any(c => DriveHealthItem.FailingDriveRole(c, facts) is DiskRole.System or DiskRole.Unknown)) return [];

        // Only the drive letter is printed, never the volume's Subject: "C: (label)" can carry a personal label.
        var drive = AdviceArg.Words(facts.Context.SystemDrive!);
        var freePct = volume.Metric.Value.Number is { } n ? AdviceArg.Percent(n) : new AdviceArg(null, null, AdviceUnit.Percent);
        var bad = volume.Grade == Grade.Bad;

        var card = new AdviceCard(ItemId + ":" + facts.Context.SystemDrive, ItemId, Area, AdviceUrgency.Advice, Rank)
            .Title(bad ? "advice.space.title.bad" : "advice.space.title.normal", ("drive", drive), ("freePct", freePct))
            .Because(volume)
            .Line(AdviceLineRole.Do, "advice.space.d7", ("drive", drive))
            .Line(AdviceLineRole.Do, "advice.space.storage-sense");

        // Crash dumps and error reports are the evidence a repair needs, and an image Windows can
        // repair should be repaired, not cleaned around (D7).
        if (facts.Problems("events.bugcheck_90d").Any() || facts.Problems("events.gpu_tdr_90d").Any())
            card.Line(AdviceLineRole.Do, "advice.space.d7.dumps");
        if (facts.Problems("integrity.image_health").Any(v => v.Grade == Grade.Normal))
            card.Line(AdviceLineRole.Do, "advice.space.d7.repair-first");

        card.Line(AdviceLineRole.DoNot, "advice.space.d7.restore-points")
            .Line(AdviceLineRole.DoNot, "advice.space.d5");
        if (bad) card.Line(AdviceLineRole.DoNot, "advice.space.d4");
        card.Line(AdviceLineRole.Finding, "advice.space.d6")
            .Line(AdviceLineRole.Do, "advice.space.d8");
        if (facts.Flag("backup.onedrive_folders") == true) card.Line(AdviceLineRole.Do, "advice.space.d8.onedrive");
        card.Line(AdviceLineRole.Finding, "advice.space.d9")
            .Line(AdviceLineRole.Finding, "advice.space.graded");
        return [card.Build()];
    }
}
