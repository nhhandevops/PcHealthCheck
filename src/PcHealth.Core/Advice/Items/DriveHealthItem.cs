using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Advice.Items;

/// <summary>
/// H1: a drive inside this PC is failing or worn (REMEDIES-PLAN H1, STEP-B-SPEC 5). One card per
/// drive, never about a drive plugged in from outside: the auditor's USB stick has read errors that
/// are still graded Bad, and "back up now" about it would be a false alarm on the laptop.
///
/// Only damaged-storage readings make the card (<see cref="DriveDamage"/>, the same set the disk
/// benchmark refuses to write to), and the wear readings for the "plan" tier. Cable (CRC) errors,
/// command timeouts, temperatures and the event-log disk errors never do: they say nothing about
/// whether the storage itself is damaged.
/// </summary>
public sealed class DriveHealthItem : IAdviceItem
{
    public const string ItemId = "H1";
    public const string SmartModule = "disk.smart", ReliabilityModule = "disk.reliability";

    private const int ActNowRank = 10, CheckAgainRank = 12, PlanRank = 20;

    private const string Reallocated = "smart.reallocated", Pending = "smart.pending_sectors", Offline = "smart.offline_uncorrectable";

    /// <summary>
    /// Each reading the card can name, in the order its lines are printed, with its sentence. The
    /// key is <c>advice.h1.evidence.</c> and the reading's id, underscores written as hyphens. Each
    /// key is written out whole, so the catalog tests find every one in this file.
    /// </summary>
    private static readonly (string MetricId, string Key)[] Evidence =
    [
        ("smart.predict_failure", "advice.h1.evidence.smart.predict-failure"),
        ("smart.attributes_at_threshold", "advice.h1.evidence.smart.attributes-at-threshold"),
        (Reallocated, "advice.h1.evidence.smart.reallocated"),
        (Pending, "advice.h1.evidence.smart.pending-sectors"),
        (Offline, "advice.h1.evidence.smart.offline-uncorrectable"),
        ("smart.reported_uncorrectable", "advice.h1.evidence.smart.reported-uncorrectable"),
        ("storage.read_errors_uncorrected", "advice.h1.evidence.storage.read-errors-uncorrected"),
        ("storage.write_errors_uncorrected", "advice.h1.evidence.storage.write-errors-uncorrected"),
        ("storage.wear_pct", "advice.h1.evidence.storage.wear-pct"),
        ("smart.ssd_percent_consumed", "advice.h1.evidence.smart.ssd-percent-consumed"),
    ];

    /// <summary>Endurance used, graded Normal from 70%: the "plan to replace" tier.</summary>
    private static readonly HashSet<string> WearIds = new(StringComparer.Ordinal) { "storage.wear_pct", "smart.ssd_percent_consumed" };

    public string Id => ItemId;

    public string Area => "drive-health";

    public string Os => "windows";

    public int Rank => ActNowRank;

    public IReadOnlyList<string> Needs { get; } = [SmartModule, ReliabilityModule];

    /// <summary>The id of the card about one drive: "H1:disk0", "H1:unknown-1". Never a model name.</summary>
    public static string CardId(DiskGroup drive) => ItemId + ":" + drive.Key;

    /// <summary>
    /// The role of the drive a card says is failing, Act now or Check again, or null for any other
    /// card. SPACE drops its card and S1 adds S4's label when this is the drive Windows runs from or
    /// may be (REMEDIES-PLAN principle 3). A worn drive (Plan) is not failing: freeing space on it is
    /// harmless, and a restart still helps.
    /// </summary>
    public static DiskRole? FailingDriveRole(Recommendation card, AdviceFacts facts) =>
        string.Equals(card.Item, ItemId, StringComparison.Ordinal) && card.Urgency is AdviceUrgency.ActNow or AdviceUrgency.CheckAgain
            ? facts.DiskGroups.FirstOrDefault(g => string.Equals(CardId(g), card.Id, StringComparison.Ordinal))?.Role
            : null;

    public IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier) =>
        [.. facts.DiskGroups.Where(d => d.Role != DiskRole.External).Select(d => CardFor(d, facts)).OfType<Recommendation>()];

    private static Recommendation? CardFor(DiskGroup drive, AdviceFacts facts)
    {
        var damage = drive.Verdicts.Where(v => DriveDamage.MetricIds.Contains(v.Metric.Id) && AdviceEvidence.IsProblem(v)).ToList();
        var wear = drive.Verdicts.Where(v => WearIds.Contains(v.Metric.Id) && AdviceEvidence.IsProblem(v)).ToList();
        var growth = drive.Trends
            .Where(t => t.Kind == TrendKind.CounterGrowth && DriveDamage.MetricIds.Contains(t.MetricId) && AdviceEvidence.IsProblem(t))
            .ToList();

        // The app's own grades decide (REMEDIES-PLAN H1, urgency): a Bad damage reading, any unstable or
        // unrecoverable area, or a counter that grew is act now; a small count with no history is
        // check again; endurance mostly used is plan.
        AdviceUrgency urgency;
        if (growth.Count > 0 || damage.Any(v => v.Grade == Grade.Bad || v.Metric.Id is Pending or Offline)) urgency = AdviceUrgency.ActNow;
        else if (damage.Count > 0) urgency = AdviceUrgency.CheckAgain;
        else if (wear.Count > 0) urgency = AdviceUrgency.Plan;
        else return null;

        var failing = urgency != AdviceUrgency.Plan;
        var card = new AdviceCard(CardId(drive), ItemId, "drive-health", urgency,
            urgency switch { AdviceUrgency.ActNow => ActNowRank, AdviceUrgency.CheckAgain => CheckAgainRank, _ => PlanRank })
        {
            Subject = drive.Subject,
            DiskNumber = drive.DiskNumber,
        };
        card.Title(urgency switch
        {
            AdviceUrgency.ActNow => drive.Role switch
            {
                DiskRole.System => "advice.h1.title.act-now.system",
                DiskRole.Internal => "advice.h1.title.act-now.internal",
                _ => "advice.h1.title.act-now.unknown",
            },
            AdviceUrgency.CheckAgain => "advice.h1.title.check-again",
            _ => "advice.h1.title.plan",
        });

        var systemDrive = facts.Context.SystemDrive;
        switch (drive.Role)
        {
            case DiskRole.System when systemDrive is not null:
                card.Line(AdviceLineRole.Finding, "advice.h1.role.system", ("drive", AdviceArg.Words(systemDrive)));
                break;
            case DiskRole.System:
                card.Line(AdviceLineRole.Finding, "advice.h1.role.system.unmeasured");
                break;
            case DiskRole.Internal when drive.Letters is not null:
                card.Line(AdviceLineRole.Finding, "advice.h1.role.internal", ("letters", AdviceArg.Words(drive.Letters)));
                break;
            case DiskRole.Unknown:
                card.Line(AdviceLineRole.Finding, "advice.h1.role.unknown");
                break;
        }

        AddEvidence(card, drive, damage.Concat(wear).ToList(), growth);

        if (urgency == AdviceUrgency.ActNow) card.Line(AdviceLineRole.Why, "advice.h1.why.act-now");
        else if (urgency == AdviceUrgency.CheckAgain) card.Line(AdviceLineRole.Why, "advice.h1.why.check-again");

        card.Line(AdviceLineRole.Do, "advice.h1.do.copy");
        var usedGb = drive.UsedGb is { } used && used > 0 ? used : (double?)null;
        if (usedGb is { } gb && drive.Letters is { } letters)
        {
            // Both ends round up to the same 5 minutes on a lightly used drive (under about 14.6 GB):
            // "about 5 to 5 minutes" is not a range.
            var minLow = AdviceThresholds.CopyMinutes(gb, AdviceThresholds.FastCopyMbPerSecond);
            var minHigh = AdviceThresholds.CopyMinutes(gb, AdviceThresholds.SlowCopyMbPerSecond);
            // Only the drive Windows runs from holds Windows and programs, and only there are your own
            // files usually much less than the space in use.
            if (minLow < minHigh)
            {
                card.Line(AdviceLineRole.Do, drive.Role == DiskRole.System ? "advice.h1.do.estimate" : "advice.h1.do.estimate.data",
                    ("usedGb", AdviceArg.Gb(gb)), ("letters", AdviceArg.Words(letters)),
                    ("minLow", AdviceArg.Minutes(minLow)), ("minHigh", AdviceArg.Minutes(minHigh)));
            }
            else
            {
                card.Line(AdviceLineRole.Do, "advice.h1.do.estimate.short",
                    ("usedGb", AdviceArg.Gb(gb)), ("letters", AdviceArg.Words(letters)), ("min", AdviceArg.Minutes(minHigh)));
            }
        }

        foreach (var key in ProtectionKeys(facts)) card.Line(AdviceLineRole.Finding, key);

        // The drive Windows runs from is also low on space: the space card is dropped in favour of
        // this line, because cleaning up first works a failing drive and copying off comes first.
        if (failing && drive.Role is DiskRole.System or DiskRole.Unknown && facts.SystemVolume is { } volume && AdviceEvidence.IsProblem(volume))
        {
            card.Because(volume);
            if (drive.Role == DiskRole.System)
            {
                card.Line(AdviceLineRole.Finding, "advice.h1.also-low.system",
                    ("drive", AdviceArg.Words(systemDrive!)), ("freePct", PercentOf(volume)));
            }
            else
            {
                card.Line(AdviceLineRole.Finding, "advice.h1.also-low.unknown", ("drive", AdviceArg.Words(systemDrive!)));
            }
        }

        if (failing)
        {
            card.Line(AdviceLineRole.Finding, "advice.h1.lost");
            card.Line(AdviceLineRole.DoNot, "advice.h1.do-not.stress");
        }
        // Another drive of this model shares its history, so no later scan can say this count grew:
        // the card does not promise that one will.
        if (urgency == AdviceUrgency.CheckAgain)
            card.Line(AdviceLineRole.Do, drive.SharesHistory ? "advice.h1.check-again.rescan.shared" : "advice.h1.check-again.rescan");

        // A drive only checked again is not replaced yet, unless it is also worn.
        if (urgency != AdviceUrgency.CheckAgain || wear.Count > 0)
        {
            if (usedGb is { } inUse && AdviceThresholds.ReplacementSizeGb(inUse) is { } size)
                card.Line(AdviceLineRole.ForIt, "advice.h1.for-it.replace", ("sizeGb", AdviceArg.Gb(size)));
            else
                card.Line(AdviceLineRole.ForIt, "advice.h1.for-it.replace.unmeasured");
            card.Line(AdviceLineRole.ForIt, "advice.h1.for-it.retire");
        }

        return card.Build();
    }

    /// <summary>
    /// One line per reading, each followed by what the history says about it. A counter's value goes
    /// into the card's fingerprint, so a card someone chose not to see again comes back when it grows,
    /// even with history off. A growth trend on a counter this drive has no Normal or Bad reading of
    /// is named by the trend's own current value.
    /// </summary>
    private static void AddEvidence(AdviceCard card, DiskGroup drive, List<Verdict> readings, List<TrendFinding> growth)
    {
        foreach (var (metricId, key) in Evidence)
        {
            var named = false;
            var wear = WearIds.Contains(metricId);
            foreach (var reading in readings.Where(v => string.Equals(v.Metric.Id, metricId, StringComparison.Ordinal)))
            {
                card.Because(reading, countInFingerprint: !wear);
                card.TryLine(AdviceLineRole.Finding, key, ValueArgs(reading.Metric.Value.Number, wear));
                named = true;
                if (!wear) continue;

                foreach (var projection in drive.Trends.Where(t => t.Kind == TrendKind.Projection &&
                                                                    string.Equals(t.MetricId, metricId, StringComparison.Ordinal)))
                {
                    if (AdviceEvidence.IsProblem(projection)) card.Because(projection);
                    card.TryLine(AdviceLineRole.Finding, "advice.h1.projection",
                        ("from", AdviceArg.Percent(projection.PreviousValue)), ("to", AdviceArg.Percent(projection.CurrentValue)),
                        ("since", AdviceArg.Date(projection.PreviousSeen)));
                }
            }

            foreach (var trend in growth.Where(t => string.Equals(t.MetricId, metricId, StringComparison.Ordinal)))
            {
                card.Because(trend);
                if (!named) card.TryLine(AdviceLineRole.Finding, key, ValueArgs(trend.CurrentValue, wear: false));
                card.TryLine(AdviceLineRole.Finding, "advice.h1.growth",
                    ("from", AdviceArg.Count(trend.PreviousValue)), ("to", AdviceArg.Count(trend.CurrentValue)),
                    ("since", AdviceArg.Date(trend.PreviousSeen)));
            }
        }
    }

    /// <summary>A counter is "count", endurance is "pct"; a sentence with no value to fill is left out, never printed with 0.</summary>
    private static (string Name, AdviceArg Value)[] ValueArgs(double? number, bool wear) =>
        number is not { } n ? []
        : wear ? [("pct", AdviceArg.Percent(n))]
        : [("count", AdviceArg.Count(n))];

    private static AdviceArg PercentOf(Verdict verdict) =>
        verdict.Metric.Value.Number is { } n ? AdviceArg.Percent(n) : new AdviceArg(null, null, AdviceUnit.Percent);

    /// <summary>
    /// What already protects this account's files, for the account this scan ran as (REMEDIES-PLAN
    /// principle 12). OneDrive and File History speak for themselves when on; OneDrive's line says
    /// nothing else is backed up only when File History was read as off. "Nothing" is said only when
    /// the scan concluded it, <c>backup.any_protection</c> graded Bad; without that conclusion, as on a
    /// scan that could not count shadow copies or read File History, the line says it was not
    /// established. The scan counts shadow copies without knowing which drive holds them, so their
    /// line holds for any drive.
    /// </summary>
    private static IEnumerable<string> ProtectionKeys(AdviceFacts facts)
    {
        var oneDrive = facts.Flag("backup.onedrive_folders") == true;
        var fileHistory = facts.Flag("backup.file_history");
        if (oneDrive) yield return fileHistory == false ? "advice.h1.protection.onedrive" : "advice.h1.protection.onedrive.folders";
        if (fileHistory == true) yield return "advice.h1.protection.file-history";
        if (oneDrive || fileHistory == true) yield break;

        if (fileHistory is null) yield return "advice.h1.protection.unknown";
        else if (facts.Problems("backup.any_protection").Any()) yield return "advice.h1.protection.none";
        else if (facts.Flag("backup.any_protection") == true) yield return "advice.h1.protection.shadow-only";
        else yield return "advice.h1.protection.unknown";
    }
}
