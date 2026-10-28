using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Reporting;
using PcHealth.Core.Text;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Advice;

/// <summary>How soon a card asks the reader to do something (STEP-B-SPEC 3.4).</summary>
public enum AdviceUrgency
{
    ActNow,
    CheckAgain,
    Plan,
    Advice,
}

/// <summary>What one line of a card is. ForIt lines are shown labelled "For IT", so an employee knows to pass them on.</summary>
public enum AdviceLineRole
{
    Title,
    Finding,
    Why,
    Do,
    DoNot,
    ForIt,
}

/// <summary>How a number in a sentence is written. The formatting is done in code, never in the catalog (<see cref="AdviceFormat"/>).</summary>
public enum AdviceUnit
{
    None,
    Count,
    Percent,
    Gb,
    Minutes,
    Days,
    Celsius,
    Date,
}

/// <summary>
/// One value a sentence is filled in with: exactly one of <see cref="Number"/> and <see cref="Text"/>
/// is set, the shape of <see cref="MetricValue"/>, so a PowerShell reader writes
/// <c>.Args.count.Number</c> the way the kit reads <c>.Metric.Value.Number</c>.
/// </summary>
public readonly record struct AdviceArg(double? Number, string? Text, AdviceUnit Unit)
{
    public static AdviceArg Count(double count) => new(count, null, AdviceUnit.Count);

    public static AdviceArg Percent(double percent) => new(percent, null, AdviceUnit.Percent);

    public static AdviceArg Gb(double gb) => new(gb, null, AdviceUnit.Gb);

    public static AdviceArg Minutes(double minutes) => new(minutes, null, AdviceUnit.Minutes);

    public static AdviceArg Days(double days) => new(days, null, AdviceUnit.Days);

    public static AdviceArg Celsius(double celsius) => new(celsius, null, AdviceUnit.Celsius);

    /// <summary>A day, as "yyyy-MM-dd" in this PC's time zone, the way the System overview writes dates.</summary>
    public static AdviceArg Date(DateTimeOffset when) =>
        new(null, when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), AdviceUnit.Date);

    /// <summary>Words put in as they are: a drive letter, a profile name, a module's name.</summary>
    public static AdviceArg Words(string text) => new(null, text, AdviceUnit.None);
}

/// <summary>
/// One sentence: the catalog key and typed arguments it was made from, and its English rendering.
/// Key and the argument names are the stable contract; <see cref="Text"/> is not (KIT-005).
/// </summary>
public sealed record AdviceText(AdviceLineRole Role, string Key, IReadOnlyDictionary<string, AdviceArg> Args, string Text);

/// <summary>
/// Which reading a card rests on. A reference only: it never carries the reading's Detail, which can
/// hold a folder path, nor a trend's Message, which is formatted in the current culture (STEP-B-SPEC
/// invariant 5).
/// </summary>
public sealed record AdviceEvidence(string MetricId, string? Subject, int? DiskNumber, Grade Grade, TrendKind? Trend)
{
    public static AdviceEvidence From(Verdict verdict) =>
        new(verdict.Metric.Id, verdict.Metric.Subject, verdict.Metric.DiskNumber, verdict.Grade, null);

    public static AdviceEvidence From(TrendFinding trend) =>
        new(trend.MetricId, trend.Subject, null, trend.Grade, trend.Kind);

    /// <summary>
    /// A graded problem: a reading graded Normal or Bad. An information-only row is never one, whatever
    /// grade a hand-built verdict claims; the verdict engine grades every one of them Unknown.
    /// </summary>
    public static bool IsProblem(Verdict verdict) =>
        !verdict.Metric.InfoOnly && verdict.Grade is Grade.Normal or Grade.Bad;

    /// <summary>
    /// A trend graded Normal or Bad. A projection can be Good, when the limit is two years or more
    /// away, so a trend is a problem only by its own grade.
    /// </summary>
    public static bool IsProblem(TrendFinding trend) => trend.Grade is Grade.Normal or Grade.Bad;
}

/// <summary>
/// One "What you can do" card. Make it with <see cref="AdviceCard"/>, which checks it: the engine
/// refuses a card that was not.
/// </summary>
/// <param name="Id">Stable within a report: "H1:disk0", "H1:unknown-1", "O4", "SPACE:C:". Never a model name.</param>
/// <param name="Item">The plan's id for the advice: "H1", "O4", "O1", "SPACE", "O3", "S1".</param>
/// <param name="Area">drive-health, security, windows, space, heat or startup.</param>
/// <param name="Severity">The worst grade in the evidence.</param>
public sealed record Recommendation(
    string Id,
    string Item,
    string Area,
    AdviceUrgency Urgency,
    Grade Severity,
    int Rank,
    string? Subject,
    int? DiskNumber,
    AdviceText Title,
    IReadOnlyList<AdviceText> Lines,
    IReadOnlyList<AdviceEvidence> Evidence)
{
    /// <summary>
    /// SHA-256 of what the card says is wrong, so a card someone chose not to see again comes back
    /// when it gets worse. Not written to the report.
    /// </summary>
    [JsonIgnore]
    public string Fingerprint { get; init; } = "";
}

/// <summary>A check that would have decided some advice tried and did not finish, so the advice may be missing.</summary>
public sealed record NotMeasured(string Area, IReadOnlyList<string> ModuleIds, AdviceText Text);

/// <summary>The report's "What you can do" section, worked out once for every surface.</summary>
/// <param name="Recommendations">Every card, in display order. Never capped: a cap is the surface's.</param>
/// <param name="Notes">Things deliberately not checked here, shown grey. They never degrade a scan.</param>
/// <param name="OtherFindings">Readings graded Normal or Bad that no card used. Their tips stay in their rows.</param>
/// <param name="ItemErrors">An item that broke. Empty on every test fixture.</param>
public sealed record AdviceSection(
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<NotMeasured> NotMeasured,
    IReadOnlyList<AdviceText> Notes,
    int OtherFindings,
    IReadOnlyList<AdviceText> ItemErrors);

/// <summary>A card broke one of the rules every card must keep (STEP-B-SPEC 1 and 3.7). Its message names keys, never readings.</summary>
public sealed class AdviceInvariantException(string message) : Exception(message);

/// <summary>
/// Builds one card and checks it. A line whose sentence lacks an argument is never printed with a
/// gap or a "0": <see cref="Line"/> marks the card broken, and an item that has no value for a
/// line uses <see cref="TryLine"/> and an <c>.unmeasured</c> variant instead. <see cref="Build"/>
/// refuses a card with a broken line, no title, or no reading graded Normal or Bad behind it.
/// </summary>
public sealed class AdviceCard(string id, string item, string area, AdviceUrgency urgency, int rank)
{
    private readonly List<AdviceText> _lines = [];
    private readonly List<(AdviceEvidence Evidence, bool IsProblem, long? Count)> _evidence = [];
    private readonly List<string> _broken = [];
    private AdviceText? _title;

    public string? Subject { get; init; }

    public int? DiskNumber { get; init; }

    public AdviceCard Title(string key, params (string Name, AdviceArg Value)[] args)
    {
        _title = AdviceCatalog.English.Text(AdviceLineRole.Title, key, args);
        if (_title is null) _broken.Add(key);
        return this;
    }

    /// <summary>Adds a line that must be printable with these arguments.</summary>
    public AdviceCard Line(AdviceLineRole role, string key, params (string Name, AdviceArg Value)[] args)
    {
        if (AdviceCatalog.English.Text(role, key, args) is { } line) _lines.Add(line);
        else _broken.Add(key);
        return this;
    }

    /// <summary>Adds the line when every argument it names is here, and says whether it did.</summary>
    public bool TryLine(AdviceLineRole role, string key, params (string Name, AdviceArg Value)[] args)
    {
        if (AdviceCatalog.English.Text(role, key, args) is not { } line) return false;
        _lines.Add(line);
        return true;
    }

    /// <summary>
    /// A reading this card rests on. <paramref name="countInFingerprint"/> puts its whole-number value
    /// in the fingerprint as well, for a counter that only ever grows (H1's damage counters): with
    /// history off, 5 retired sectors becoming 9 keeps the grade Bad and makes no trend.
    /// </summary>
    public AdviceCard Because(Verdict verdict, bool countInFingerprint = false)
    {
        long? count = countInFingerprint && verdict.Metric.Value.Number is { } n && double.IsFinite(n)
            ? (long)Math.Round(n, MidpointRounding.AwayFromZero)
            : null;
        _evidence.Add((AdviceEvidence.From(verdict), AdviceEvidence.IsProblem(verdict), count));
        return this;
    }

    public AdviceCard Because(TrendFinding trend)
    {
        _evidence.Add((AdviceEvidence.From(trend), AdviceEvidence.IsProblem(trend), null));
        return this;
    }

    public Recommendation Build()
    {
        if (_broken.Count > 0)
            throw new AdviceInvariantException($"Card {item}: a sentence is missing an argument ({string.Join(", ", _broken)}).");
        if (_title is null)
            throw new AdviceInvariantException($"Card {item} has no title.");
        if (!_evidence.Any(e => e.IsProblem))
            throw new AdviceInvariantException($"Card {item} rests on no reading graded Normal or Bad.");

        var evidence = _evidence.Select(e => e.Evidence).ToList();
        return new Recommendation(id, item, area, urgency, ScanReport.Worst(evidence.Select(e => e.Grade)), rank,
            Subject, DiskNumber, _title, [.. _lines], evidence)
        {
            Fingerprint = FingerprintOf(item, urgency, DiskNumber, Subject, _evidence.Select(e => (e.Evidence, e.Count))),
        };
    }

    /// <summary>
    /// What the card says is wrong: its item, urgency and drive, and each reading's id, subject,
    /// grade and trend kind, sorted so the order the item added them in does not matter. A card
    /// whose problem gets worse in any of those ways gets a new fingerprint.
    /// </summary>
    internal static string FingerprintOf(
        string item, AdviceUrgency urgency, int? diskNumber, string? subject,
        IEnumerable<(AdviceEvidence Evidence, long? Count)> evidence)
    {
        const char field = '\u001f', record = '\u001e';
        var readings = evidence
            .Select(e => string.Join(field,
                e.Evidence.MetricId,
                e.Evidence.Subject ?? "",
                e.Evidence.Grade.ToString(),
                e.Evidence.Trend?.ToString() ?? "",
                e.Count?.ToString(CultureInfo.InvariantCulture) ?? ""))
            .Order(StringComparer.Ordinal);
        var descriptor = string.Join(record,
            [item, urgency.ToString(), diskNumber?.ToString(CultureInfo.InvariantCulture) ?? "", subject ?? "", .. readings]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }
}
