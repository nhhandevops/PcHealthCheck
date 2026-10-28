using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Security;

/// <summary>Which on-demand scan the user asked Defender to run.</summary>
public enum DefenderScanKind
{
    /// <summary>Defender's own quick scan: memory, startup locations, the usual hiding places.</summary>
    Quick,

    /// <summary>One folder the user picked, scanned with remediation disabled so nothing is changed.</summary>
    Folder,
}

/// <summary>The result of an on-demand scan, including the exact command that produced it.</summary>
public sealed record DefenderScanOutcome(
    DefenderScanKind Kind,
    int ExitCode,
    bool TimedOut,
    TimeSpan Elapsed,
    IReadOnlyList<string> Detections,
    string Message,
    string CommandLine);

/// <summary>
/// Turns MpCmdRun's exit code into something a person can act on.
///
/// Pure on purpose. The interesting branches — "malware found", "remediation failed" —
/// never run on a healthy PC, which is precisely how the last silent failure in this area
/// survived to release. Everything here is decided from the exit code and structured WMI
/// records, never from MpCmdRun's console text, which is localized.
/// </summary>
public static class DefenderScanAssessment
{
    /// <summary>
    /// MpCmdRun's documented meaning: malware was found and NOT remediated, the user must
    /// act, or the scan itself errored. Notably it does not distinguish those cases, so the
    /// wording must not claim more than that.
    /// </summary>
    public const int ThreatsFoundOrIncomplete = 2;

    /// <summary>
    /// Picks the newest Defender platform folder (they are named like "4.18.26070.9-0").
    /// Sorting these as strings is wrong — "4.18.9.0" would beat "4.18.10.0" — and running
    /// a stale platform build is how odd, unreproducible scan failures start.
    /// </summary>
    public static string? PickNewestPlatform(IEnumerable<string> directoryNames)
    {
        string? best = null;
        var bestVersion = new Version(0, 0);
        var bestRevision = -1;

        foreach (var name in directoryNames)
        {
            var dash = name.LastIndexOf('-');
            var versionPart = dash > 0 ? name[..dash] : name;
            if (!Version.TryParse(versionPart, out var version)) continue;

            var revision = 0;
            if (dash > 0)
            {
                _ = int.TryParse(name[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision);
            }

            if (version < bestVersion || (version == bestVersion && revision <= bestRevision)) continue;
            bestVersion = version;
            bestRevision = revision;
            best = name;
        }

        return best;
    }

    /// <summary>
    /// How long it took, in the unit a person would use. Rounding everything up to
    /// "1 minute(s)" made a scan that took a twentieth of a second claim it had worked for
    /// a minute — a small lie, but this app's whole value is that its numbers are true.
    /// </summary>
    public static string DescribeElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1)) return "under a second";
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            var seconds = (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
            return $"{seconds.ToString(CultureInfo.InvariantCulture)} second{(seconds == 1 ? "" : "s")}";
        }

        var minutes = (int)Math.Round(elapsed.TotalMinutes, MidpointRounding.AwayFromZero);
        return $"{minutes.ToString(CultureInfo.InvariantCulture)} minute{(minutes == 1 ? "" : "s")}";
    }

    /// <summary>
    /// The sentence shown to the user. <paramref name="subject"/> is the scanned folder for
    /// a folder scan and is ignored for a quick scan.
    /// </summary>
    public static string Describe(
        DefenderScanKind kind,
        string? subject,
        int exitCode,
        bool timedOut,
        IReadOnlyList<string> detections,
        TimeSpan elapsed)
    {
        var duration = DescribeElapsed(elapsed);

        if (timedOut)
        {
            return $"The scan was still running after {duration}, so this app stopped waiting. " +
                   "Defender itself carries on in the background — open Windows Security to see how it ends.";
        }

        if (exitCode < 0)
        {
            return "Defender's scan tool could not be started, so no scan ran. " +
                   "If another antivirus is in charge of this PC, use that product to scan instead.";
        }

        var found = detections.Count == 0 ? "" : " " + string.Join("; ", detections.Take(10)) +
                                                 (detections.Count > 10 ? " …" : "");

        return (kind, exitCode) switch
        {
            (DefenderScanKind.Quick, 0) when detections.Count == 0 =>
                $"Quick scan finished in {duration} and found nothing. That covers memory, startup " +
                "locations and the usual hiding places — it is not a check of every file on the PC.",

            (DefenderScanKind.Quick, 0) =>
                $"Quick scan finished in {duration}. Defender found and dealt with " +
                $"{detections.Count} item(s):{found} Nothing further is needed, but something did reach this PC.",

            (DefenderScanKind.Quick, ThreatsFoundOrIncomplete) =>
                $"Quick scan finished in {duration}, and Defender could NOT finish dealing with what it " +
                $"found — or the scan errored. Open Windows Security → Protection history and act on it.{found}",

            (DefenderScanKind.Folder, 0) =>
                $"Scanned {subject} in {duration}: nothing found. Files were only read — " +
                "this scan was run with remediation switched off, so nothing was moved, changed or deleted.",

            (DefenderScanKind.Folder, ThreatsFoundOrIncomplete) =>
                $"Defender flagged something in {subject}. Nothing was changed or removed, because this scan ran " +
                "with remediation switched off — the files are still exactly where they were. Open Windows " +
                "Security and run a scan of that folder there if you want Defender to act on it.",

            _ => $"The scan ended with result code {exitCode.ToString(CultureInfo.InvariantCulture)}, which this app " +
                 "does not recognise. Treat the result as unknown rather than clean, and check Windows Security.",
        };
    }
}

/// <summary>
/// How long ago Defender last scanned - including the two answers that are not a number.
/// </summary>
/// <remarks>
/// Defender reports "this has never happened" as <c>uint.MaxValue</c>, not as null. Passing that
/// straight through as a <c>double</c> put 1.7976931348623157e+308 into the machine-readable
/// report and 309 digits into the HTML table's Value column, under a red "Action needed" chip:
/// a figure no reader could interpret, and one that any tool reading the JSON would take at face
/// value. "Never scanned" is not a quantity, so it is carried as text instead. The verdict does
/// not move: <c>rules/malware.json</c> matches the text by name, and the catch-all band behind
/// that would catch it anyway, so a PC that has never been scanned is still graded Bad.
///
/// A figure Defender did not return at all is a third answer and must borrow neither of the
/// others. It is emitted <see cref="Metric.InfoOnly"/> - shown, never graded - and the module
/// records a failure so the scan says out loud that it could not establish this. Grading it Bad
/// would put a red row on a PC that may well have been scanned this morning; grading it Good is
/// the defect this project keeps shipping.
///
/// "Never run" on a young install (<see cref="InstallAge"/>) is the fourth: Defender has not had
/// its first scheduled scan yet. It is shown, ungraded, with a Note that says why. That is
/// something deliberately not judged, so it is not a failure either.
/// </remarks>
public static class ScanRecency
{
    /// <summary>Defender reports "never happened" as uint.MaxValue rather than null.</summary>
    public const uint NeverSentinel = uint.MaxValue;

    /// <summary>The id the rules file, the reports and the audit kit all key on.</summary>
    public const string QuickScanMetricId = "malware.quick_scan_age_days";

    /// <summary>The label every answer shares.</summary>
    public const string QuickScanLabel = "Days since last quick scan";

    /// <summary>
    /// The value when no quick scan has ever run. Graded Bad by rule.malware.quick-scan-age,
    /// which matches this exact text; keep the two in step.
    /// </summary>
    public const string NeverText = "never run";

    /// <summary>The value when Defender returned no figure at all. Never graded.</summary>
    public const string NotReportedText = "not reported";

    /// <summary>The note that degrades the scan when the figure could not be read.</summary>
    public const string NotReportedNote =
        "Defender did not report when it last ran a quick scan, so scan recency was NOT established.";

    /// <summary>Why a scan that never ran is not judged on a young install.</summary>
    private const string FirstScanTakesDays = "On a new install, Defender's first scheduled quick scan can take several days.";

    /// <summary>The Note that goes with a scan that never ran on a young install. Not a failure.</summary>
    public static string NotJudgedNote(int youngInstallDays) =>
        InstallAge.NotJudged(QuickScanLabel, youngInstallDays, FirstScanTakesDays);

    /// <summary>
    /// The quick-scan-recency metric. <paramref name="ageDays"/> is Defender's QuickScanAge:
    /// null when the property could not be read, <see cref="NeverSentinel"/> when no scan has
    /// ever run, and otherwise a count of days.
    /// </summary>
    /// <param name="youngInstallDays">Days since install, only when Windows is young (<see cref="InstallAge.Young"/>).
    /// It changes nothing but a scan that never ran.</param>
    public static Metric QuickScan(uint? ageDays, bool defenderInCharge, int? youngInstallDays = null) => ageDays switch
    {
        null => new Metric(QuickScanMetricId, QuickScanLabel, NotReportedText,
            HealthCategory.Security, InfoOnly: true,
            Detail: "Defender did not say when it last ran a quick scan, so this scan could not establish " +
                    "how recently it looked. That is left ungraded rather than counted as either clean or " +
                    "overdue - open Windows Security > Virus & threat protection to see the real date."),

        NeverSentinel when youngInstallDays is { } days => new Metric(QuickScanMetricId, QuickScanLabel, NeverText,
            HealthCategory.Security, InfoOnly: true,
            Detail: $"No quick scan has been recorded yet. Not judged: {InstallAge.Installed(days)}. {FirstScanTakesDays}"),

        NeverSentinel => new Metric(QuickScanMetricId, QuickScanLabel, NeverText,
            HealthCategory.Security, InfoOnly: !defenderInCharge,
            Detail: "No quick scan has ever been recorded on this PC."),

        { } age => new Metric(QuickScanMetricId, QuickScanLabel, (double)age,
            HealthCategory.Security, Unit: "days", InfoOnly: !defenderInCharge,
            Detail: "Quick scans check the places malware actually starts from. Combined with real-time " +
                    "protection they are what Microsoft recommends for everyday use."),
    };
}
