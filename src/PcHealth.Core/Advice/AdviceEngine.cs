using PcHealth.Core.Advice.Items;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using PcHealth.Core.Text;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Advice;

/// <summary>
/// One piece of advice: a small class that reads the report's facts and makes zero or more cards
/// (STEP-B-SPEC 3.2). Items are C#, because a card joins disks, volumes, modules and trends; the
/// sentences are data, in the catalog.
/// </summary>
public interface IAdviceItem
{
    /// <summary>The plan's id: "H1", "O4", "O1", "SPACE", "O3", "S1".</summary>
    string Id { get; }

    /// <summary>drive-health, security, windows, space, heat or startup.</summary>
    string Area { get; }

    /// <summary>The OS the advice is written for, as <see cref="Rule.Os"/> names it ("windows").</summary>
    string Os { get; }

    /// <summary>Where its cards sort. An item with several urgencies gives each card its own rank.</summary>
    int Rank { get; }

    /// <summary>The modules whose readings decide it, for the "not measured" lines.</summary>
    IReadOnlyList<string> Needs { get; }

    /// <summary>
    /// The cards this item makes. <paramref name="earlier"/> holds the cards of the items that ran
    /// before it, in run order, so S1 can see whether H1 found the system drive failing.
    /// </summary>
    IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier);
}

/// <summary>
/// Works out the "What you can do" section from a graded report, once, for every surface: the JSON,
/// the HTML report, the window and the headless console all show what this returns. Pure: it reads
/// the report's records and touches nothing else.
///
/// It can never change a grade, and it can never fail a scan. Each item runs on its own; an item
/// that throws, or makes a card that breaks a rule every card keeps, becomes a line in
/// <see cref="AdviceSection.ItemErrors"/>, and the other items' cards stand. The rules: a card rests
/// on at least one reading or trend in this report graded Normal or Bad (an information-only row
/// never counts), every sentence was printable, and it was made with <see cref="AdviceCard"/>.
/// </summary>
public sealed class AdviceEngine
{
    /// <summary>
    /// The OSes the advice is written for. Elsewhere there is no section at all rather than an empty
    /// one: "nothing to suggest" on a Linux report would claim its readings were looked at.
    /// </summary>
    public static readonly IReadOnlyList<string> AdvisedOs = ["windows"];

    /// <summary>
    /// The built-in items, in the order they run. The order matters: SPACE and S1 read H1's cards,
    /// SPACE to step aside for a failing system drive and S1 for its "will not fix a slow start"
    /// line, so H1 runs first and S1 last (STEP-B-SPEC 3.2).
    /// </summary>
    private static IReadOnlyList<IAdviceItem> BuiltIn =>
    [
        new DriveHealthItem(),
        new SecurityOffItem(),
        new Windows11SwitchItem(),
        new LowSpaceItem(),
        new HotAndSlowItem(),
        new RestartItem(),
    ];

    /// <summary>Null for an OS with no advice: <see cref="Derive"/> then returns null.</summary>
    private readonly IReadOnlyList<IAdviceItem>? _items;

    /// <summary>An engine running exactly these items, in this order.</summary>
    public AdviceEngine(IEnumerable<IAdviceItem> items) => _items = [.. items];

    private AdviceEngine() { }

    /// <summary>An engine that works out no section at all, as on an OS with no advice.</summary>
    public static AdviceEngine None { get; } = new();

    /// <summary>The built-in items for <paramref name="os"/> (a <see cref="RulesLoader.KnownOs"/> name), or <see cref="None"/>.</summary>
    public static AdviceEngine ForOs(string os) =>
        AdvisedOs.Contains(os, StringComparer.OrdinalIgnoreCase)
            ? new AdviceEngine(BuiltIn.Where(i => string.Equals(i.Os, os, StringComparison.OrdinalIgnoreCase)))
            : None;

    /// <summary>The section for these graded readings, or null when this engine has no advice for the OS.</summary>
    public AdviceSection? Derive(
        IReadOnlyList<Verdict> verdicts,
        IReadOnlyList<TrendFinding> trends,
        IReadOnlyList<ModuleRunSummary> modules,
        AdviceContext context)
    {
        if (_items is null) return null;
        try
        {
            return Work(_items, verdicts, trends, modules, context);
        }
        catch (Exception e)
        {
            // Only a broken catalog gets here: every item's own failure is caught in Work. The line is
            // written out in English, because the catalog it would come from is what broke.
            IReadOnlyDictionary<string, AdviceArg> args = new Dictionary<string, AdviceArg>(StringComparer.Ordinal)
            {
                ["area"] = AdviceArg.Words("any area"),
                ["error"] = AdviceArg.Words(e.GetType().Name),
            };
            return new AdviceSection([], [], [], CountOtherFindings(verdicts, trends, []),
                [new AdviceText(AdviceLineRole.Finding, ItemErrorKey, args,
                    "Advice about any area could not be worked out (" + e.GetType().Name + "). The readings below are complete.")]);
        }
    }

    private const string ItemErrorKey = "advice.section.item-error";

    private static AdviceSection Work(
        IReadOnlyList<IAdviceItem> items,
        IReadOnlyList<Verdict> verdicts,
        IReadOnlyList<TrendFinding> trends,
        IReadOnlyList<ModuleRunSummary> modules,
        AdviceContext context)
    {
        var cards = new List<Recommendation>();
        var errors = new List<AdviceText>();
        AdviceFacts facts;
        try
        {
            facts = AdviceFacts.Build(verdicts, trends, modules, context);
        }
        catch (Exception e)
        {
            errors.AddRange(items.Select(i => i.Area).Distinct(StringComparer.Ordinal).Select(area => ItemError(area, e.GetType().Name)));
            return new AdviceSection([], [], [], CountOtherFindings(verdicts, trends, cards), errors);
        }

        foreach (var item in items)
        {
            try
            {
                var made = item.Evaluate(facts, [.. cards]);
                var accepted = new List<Recommendation>();
                var refused = false;
                foreach (var card in made)
                {
                    if (IsSound(card, item, facts, [.. cards, .. accepted])) accepted.Add(card);
                    else refused = true;
                }
                cards.AddRange(accepted);
                if (refused) errors.Add(ItemError(item.Area, nameof(AdviceInvariantException)));
            }
            catch (Exception e)
            {
                errors.Add(ItemError(item.Area, e.GetType().Name));
            }
        }

        var notMeasured = new List<NotMeasured>();
        var notes = new List<AdviceText>();
        void Gaps(string itemId, Action<IAdviceItem> work)
        {
            if (items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal)) is not { } item) return;
            try
            {
                work(item);
            }
            catch (Exception e)
            {
                errors.Add(ItemError(item.Area, e.GetType().Name));
            }
        }
        Gaps(DriveHealthItem.ItemId, item => DriveHealthGaps(item, facts, cards, notMeasured, notes));
        Gaps(LowSpaceItem.ItemId, item => SpaceGaps(item, facts, notMeasured));
        Gaps(HotAndSlowItem.ItemId, item => HeatGaps(item, facts, notMeasured, notes));

        return new AdviceSection(
            [.. Ordered(cards)],
            NotMeasured: notMeasured,
            Notes: notes,
            OtherFindings: CountOtherFindings(verdicts, trends, cards),
            ItemErrors: errors);
    }

    // --- What the section says about the checks it could not use (STEP-B-SPEC 3.5) ----------------
    //
    // Three areas only, where silence would mislead about data or hardware; the report's banner
    // already lists every other check that did not finish. A check that tried and broke and left the
    // readings missing is a "not measured" line: without it an empty section would read as "no
    // failing drive". A check deliberately not run, absent or Skipped, or a drive that has no damage
    // counters to give, is at most a grey note. Neither changes a module's status or the banner, and
    // a module's status alone never decides: the volume check going Partial over TRIM while C: was
    // read is not "free space not measured", and a drive check that Completed may still have read no
    // damage counter from a drive inside the PC.

    private static bool Broke(AdviceFacts facts, string moduleId) =>
        facts.StatusOf(moduleId) is ModuleStatus.Failed or ModuleStatus.PartiallyCompleted;

    /// <summary>A graded reading of this id: not information only, and given a grade by its rule.</summary>
    private static bool Graded(Verdict verdict) => !verdict.Metric.InfoOnly && verdict.Grade != Grade.Unknown;

    /// <summary>
    /// A drive check broke: a failing drive may be missing from this section, unless every drive
    /// inside the PC already has a "back up now" card. A card on one drive says nothing about another
    /// whose counters the broken check would have read, and a worn or checked-again drive may be
    /// worse than its card says.
    ///
    /// Neither drive check took part at all, as on a standard user's scan: a note, which blames
    /// administrator rights only when the scan did not have them. The checks ran and a drive inside
    /// the PC has no damage reading, the benchmark's own test (<c>FailingDrives.HealthWasRead</c>), as
    /// an NVMe drive whose record is empty or carries wear alone: a note, since nothing broke but a
    /// failing drive would not show. An NVMe laptop whose reliability check read its error counters
    /// gets neither, with the SMART check Skipped by design.
    /// </summary>
    private static void DriveHealthGaps(
        IAdviceItem item, AdviceFacts facts, IReadOnlyList<Recommendation> cards, List<NotMeasured> notMeasured, List<AdviceText> notes)
    {
        var inside = facts.DiskGroups.Where(g => g.Role != DiskRole.External).ToList();
        var actNow = cards.Where(c => string.Equals(c.Item, item.Id, StringComparison.Ordinal) && c.Urgency == AdviceUrgency.ActNow)
            .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (inside.Count > 0 && inside.TrueForAll(g => actNow.Contains(DriveHealthItem.CardId(g)))) return;

        static bool DamageRead(DiskGroup drive) => drive.Verdicts.Any(v => DriveDamage.MetricIds.Contains(v.Metric.Id));
        var broken = item.Needs.Where(id => Broke(facts, id)).ToList();
        if (broken.Count > 0)
            notMeasured.Add(NotMeasuredLine(item.Area, facts, broken, "advice.not-measured.drive-health"));
        else if (item.Needs.All(id => facts.StatusOf(id) is null or ModuleStatus.Skipped))
            notes.Add(SectionLine(facts.Context.RanElevated ? "advice.note.drive-health-not-selected" : "advice.note.drive-health-not-included"));
        else if (inside.Exists(g => g.DiskNumber is not null && !DamageRead(g)) || !inside.Exists(DamageRead))
            notes.Add(SectionLine("advice.note.drive-health-not-reported"));
    }

    /// <summary>The volume check broke and the drive Windows runs from has no graded free space.</summary>
    private static void SpaceGaps(IAdviceItem item, AdviceFacts facts, List<NotMeasured> notMeasured)
    {
        if (facts.SystemVolume is { } volume && Graded(volume)) return;
        var broken = item.Needs.Where(id => Broke(facts, id)).ToList();
        if (broken.Count == 0) return;
        notMeasured.Add(facts.Context.SystemDrive is { } drive
            ? NotMeasuredLine(item.Area, facts, broken, "advice.not-measured.space", ("drive", AdviceArg.Words(drive)))
            : NotMeasuredLine(item.Area, facts, broken, "advice.not-measured.space.unmeasured"));
    }

    /// <summary>
    /// The sensors check broke and no processor temperature was graded; or the processor is hot, the
    /// speed test broke and no speed was graded. A hot processor whose speed test was not part of the
    /// scan, as in every kit run, gets the grey note instead.
    /// </summary>
    private static void HeatGaps(IAdviceItem item, AdviceFacts facts, List<NotMeasured> notMeasured, List<AdviceText> notes)
    {
        var temperatures = HotAndSlowItem.TemperatureIds.SelectMany(id => facts.All(id)).Where(Graded).ToList();
        var hot = temperatures.Exists(AdviceEvidence.IsProblem);
        var clockGraded = facts.All(HotAndSlowItem.ClockId).Any(Graded);

        if (temperatures.Count == 0 && Broke(facts, HotAndSlowItem.SensorsModule))
            notMeasured.Add(NotMeasuredLine(item.Area, facts, [HotAndSlowItem.SensorsModule], "advice.not-measured.heat"));
        else if (hot && !clockGraded && Broke(facts, HotAndSlowItem.BenchmarkModule))
            notMeasured.Add(NotMeasuredLine(item.Area, facts, [HotAndSlowItem.BenchmarkModule], "advice.not-measured.heat-clock"));

        if (hot && !clockGraded && facts.StatusOf(HotAndSlowItem.BenchmarkModule) is null or ModuleStatus.Skipped)
            notes.Add(SectionLine("advice.note.clock-not-included"));
    }

    /// <summary>"Drive health was not fully checked: Drive SMART health did not finish, …". Module names stay English until Step E.</summary>
    private static NotMeasured NotMeasuredLine(
        string area, AdviceFacts facts, IReadOnlyList<string> moduleIds, string key, params (string Name, AdviceArg Value)[] args)
    {
        var names = moduleIds.Select(id => facts.Module(id)?.ModuleName ?? id).ToList();
        return new NotMeasured(area, moduleIds, SectionLine(key, [.. args, ("modules", AdviceArg.Words(AdviceCatalog.English.List(names)))]));
    }

    private static AdviceText SectionLine(string key, params (string Name, AdviceArg Value)[] args) =>
        AdviceCatalog.English.Text(AdviceLineRole.Finding, key, args)
        ?? throw new InvalidDataException($"The catalog's {key} names an argument the engine does not give.");

    /// <summary>
    /// Display order: hardware and data first, by rank (STEP-B-SPEC 3.4), then Bad before Normal,
    /// then by drive and subject. The card id breaks any remaining tie, so the order never depends
    /// on the order the readings came in.
    /// </summary>
    public static IEnumerable<Recommendation> Ordered(IEnumerable<Recommendation> cards) => cards
        .OrderBy(c => c.Rank)
        .ThenBy(c => c.Severity switch { Grade.Bad => 0, Grade.Normal => 1, _ => 2 })
        .ThenBy(c => c.DiskNumber ?? int.MaxValue)
        .ThenBy(c => c.Subject ?? "", StringComparer.Ordinal)
        .ThenBy(c => c.Id, StringComparer.Ordinal);

    /// <summary>
    /// Readings and trends graded Normal or Bad that no card rests on. The section counts them, so an
    /// empty section is never read as "nothing is wrong"; their tips stay in their own rows.
    /// </summary>
    private static int CountOtherFindings(
        IReadOnlyList<Verdict> verdicts, IReadOnlyList<TrendFinding> trends, IReadOnlyList<Recommendation> cards)
    {
        var used = cards.SelectMany(c => c.Evidence).ToHashSet();
        return verdicts.Count(v => AdviceEvidence.IsProblem(v) && !used.Contains(AdviceEvidence.From(v)))
             + trends.Count(t => AdviceEvidence.IsProblem(t) && !used.Contains(AdviceEvidence.From(t)));
    }

    /// <summary>
    /// The checks <see cref="AdviceCard.Build"/> makes, made again on what the item handed back, since
    /// a <see cref="Recommendation"/> can also be made with its constructor. Its evidence must be in
    /// this report as the card says, and one piece of it graded Normal or Bad there.
    /// </summary>
    private static bool IsSound(Recommendation card, IAdviceItem item, AdviceFacts facts, IReadOnlyList<Recommendation> accepted) =>
        card is { Fingerprint.Length: > 0, Title.Text.Length: > 0, Lines: not null, Evidence.Count: > 0 } &&
        string.Equals(card.Item, item.Id, StringComparison.Ordinal) &&
        string.Equals(card.Area, item.Area, StringComparison.Ordinal) &&
        !accepted.Any(c => string.Equals(c.Id, card.Id, StringComparison.Ordinal)) &&
        card.Lines.All(l => l is { Text.Length: > 0 }) &&
        card.Evidence.All(facts.Holds) && card.Evidence.Any(facts.IsProblemEvidence);

    /// <summary>"Advice about drive health could not be worked out (NullReferenceException)." The error's type only: its message could quote a reading.</summary>
    private static AdviceText ItemError(string area, string errorType)
    {
        var areaKey = "advice.area." + area;
        var areaName = AdviceCatalog.English.Contains(areaKey) ? AdviceCatalog.English.Render(areaKey, new Dictionary<string, AdviceArg>()) : null;
        return AdviceCatalog.English.Text(AdviceLineRole.Finding, ItemErrorKey,
                   ("area", AdviceArg.Words(areaName ?? area)), ("error", AdviceArg.Words(errorType)))
               ?? throw new InvalidDataException($"The catalog's {ItemErrorKey} names an argument the engine does not give.");
    }
}
