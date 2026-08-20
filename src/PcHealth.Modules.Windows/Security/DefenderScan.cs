using System.Globalization;

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
