using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using PcHealth.Core.Metrics;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// How far back the records behind a "last N days" count really go (REMEDIES-PLAN M1, Step A row
/// 6). A count is only as long as the history it was read from. After a reinstall, a cleared log
/// or a small log that wrapped, "Blue screens (90 days): 0" came from a few days of records and
/// still read as 90 clean days. On the dev T470p, installed on 2026-10-19, the System event log
/// went back 9 days on 2026-10-28.
///
/// A count whose records cover its whole window is left exactly as it was. One whose records
/// cover less says so in its Detail: "In the last 90 days (records cover only 9 days: ...)".
/// - A count above its rule's Good band keeps its grade: what was found was found.
/// - A count inside the Good band (zero, for nearly every count) from under
///   <see cref="JudgedFromDays"/> days of records is shown but not graded, with a Note. A few days
///   of nothing is not an all-clear, and nothing broke, so it is not a failure.
/// - When the clock looks wrong, the span is unknown, and a zero is not graded either.
///
/// The span is given in days, never as a date: on a new install the oldest record is the install
/// date, which the report never carries (see <see cref="InstallAge"/>).
/// </summary>
public sealed class RecordSpan
{
    /// <summary>Under this many whole days of records, a count of zero is not judged.</summary>
    public const int JudgedFromDays = 7;

    private readonly string? _limit;
    private readonly string? _unknownBecause;
    private readonly List<string> _notJudged = [];
    private bool _aboveZeroNotJudged;

    private RecordSpan(string source, int? days, string? limit, string? unknownBecause)
    {
        Source = source;
        Days = days;
        _limit = limit;
        _unknownBecause = unknownBecause;
    }

    /// <summary>What the records are, for the text: "the System event log".</summary>
    public string Source { get; }

    /// <summary>Whole days the records cover, or null when that could not be told.</summary>
    public int? Days { get; }

    /// <summary>
    /// Records whose oldest entry is <paramref name="oldestUtc"/>; null means there are none at
    /// all. Unknown when the clock is set earlier than this app was built, or earlier than the
    /// oldest record: one of the two is wrong, and a span measured against it means nothing.
    /// </summary>
    public static RecordSpan FromOldest(string source, DateTime? oldestUtc, DateTime clockUtc)
    {
        if (clockUtc < WindowsSupport.CheckedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            return Unknown(source, "the clock is set earlier than this app was built");
        if (oldestUtc is not { } oldest) return new RecordSpan(source, 0, $"{source} is empty", null);
        if (oldest > clockUtc)
            return Unknown(source, "its oldest record is dated later than the clock, so one of the two is wrong");
        return new RecordSpan(source, (int)(clockUtc - oldest).TotalDays, $"{source} goes back no further", null);
    }

    /// <summary>Records kept for <paramref name="days"/> days, for the reason given ("Windows was installed 3 days ago").</summary>
    public static RecordSpan Kept(string source, int days, string why) => new(source, Math.Max(days, 0), why, null);

    /// <summary>Records that go back further than any window, so every count keeps its text and grade.</summary>
    public static RecordSpan Whole(string source) => new(source, int.MaxValue, null, null);

    /// <summary>Records whose span could not be told, for the reason given.</summary>
    public static RecordSpan Unknown(string source, string why) => new(source, null, null, why);

    /// <summary>"less than a day", "1 day", "9 days".</summary>
    public static string DaysText(int days) => days switch
    {
        <= 0 => "less than a day",
        1 => "1 day",
        _ => string.Create(CultureInfo.InvariantCulture, $"{days} days"),
    };

    /// <summary>
    /// The sentence for a count over the last <paramref name="windowDays"/> days, or empty when the
    /// records cover the whole window.
    /// </summary>
    public string Describe(int windowDays) => Days switch
    {
        { } days when days >= windowDays => "",
        { } days => string.Create(CultureInfo.InvariantCulture,
            $"In the last {windowDays} days (records cover only {DaysText(days)}: {_limit})."),
        null => string.Create(CultureInfo.InvariantCulture,
            $"In the last {windowDays} days (how many days the records cover is unknown: {_unknownBecause})."),
    };

    /// <summary>
    /// The count over the last <paramref name="windowDays"/> days, with its real span. Unchanged
    /// when the records cover the window. Otherwise the span is added to its Detail, and a count
    /// its rule would grade Good, from too short or an unknown span, is made information only and
    /// remembered for <see cref="Note"/>. A count that was information only already stays so, and
    /// is not listed.
    /// </summary>
    /// <param name="goodUpTo">
    /// The top of the count's Good band in its rule: 0 for nearly every count. "Application crashes
    /// (7 days)" is Good up to 2, so on a log of one day, 2 crashes (about 14 a week) read as a clean
    /// week while the zero beside it was not judged (review of 2026-10-28).
    /// </param>
    public Metric Apply(Metric count, int windowDays, double goodUpTo = 0)
    {
        var span = Describe(windowDays);
        if (span.Length == 0) return count;

        var number = count.Value.Number;
        var judged = count.InfoOnly || number is null || number > goodUpTo || Days >= JudgedFromDays;
        if (!judged)
        {
            _notJudged.Add(count.Label);
            if (number is not 0d) _aboveZeroNotJudged = true;
            var what = number is 0d ? "zero" : "a count this low";
            span += Days is { } days
                ? $" Not judged: only {DaysText(days)} of history, so {what} is not an all-clear."
                : $" Not judged: with the span unknown, {what} is not an all-clear.";
        }
        return count with
        {
            Detail = string.IsNullOrEmpty(count.Detail) ? span : count.Detail + " " + span,
            InfoOnly = count.InfoOnly || !judged,
        };
    }

    /// <summary>
    /// The Note naming every count <see cref="Apply"/> left ungraded, or null when it left none. A
    /// Note, not a failure: nothing broke. It never contains "; ", which the audit kit splits notes on.
    /// </summary>
    public string? Note()
    {
        if (_notJudged.Count == 0) return null;
        var counts = string.Join(", ", _notJudged);
        var what = _aboveZeroNotJudged ? "a count this low" : "a zero";
        return Days is { } days
            ? $"Not judged: only {DaysText(days)} of history in {Source}, so {what} there is not an all-clear: {counts}. " +
              "Scan again after two or three weeks of normal use."
            : $"Not judged: how many days {Source} covers is unknown ({_unknownBecause}), so {what} there is not an all-clear: {counts}.";
    }

    /// <summary>
    /// The span of one Windows event log, from its oldest record. When the log cannot be read the
    /// span is unknown and <paramref name="error"/> says why, for the caller to record as a failure.
    /// </summary>
    internal static RecordSpan ReadEventLog(string log, DateTime clockUtc, out string? error)
    {
        var source = $"the {log} event log";
        try
        {
            // Read forward, so the first record is the oldest one the log still holds.
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName));
            using var oldest = reader.ReadEvent();
            error = null;
            if (oldest is null) return FromOldest(source, null, clockUtc);
            return oldest.TimeCreated is { } created
                ? FromOldest(source, created.ToUniversalTime(), clockUtc)
                : Unknown(source, "its oldest record carries no date");
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
        {
            error = e.Message;
            return Unknown(source, "it could not be read");
        }
    }
}
