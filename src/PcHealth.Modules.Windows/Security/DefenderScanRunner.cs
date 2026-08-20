using System.Diagnostics;
using PcHealth.Core.Processes;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Security;

/// <summary>
/// Runs an on-demand Microsoft Defender scan through <c>MpCmdRun.exe</c>.
///
/// Deliberately separate from the health modules: a scan takes minutes rather than seconds,
/// and unlike everything else in this app it asks another program to act on the machine.
/// It therefore never runs as part of a scan — only from an explicit button, behind its own
/// consent dialog that shows this exact command line.
///
/// No elevation is needed: MpCmdRun scans as the signed-in user, so there is no UAC prompt
/// and no elevated helper involved.
/// </summary>
public sealed class DefenderScanRunner(IProcessRunner processes)
{
    public const string ActionId = "malware.defender.scan";

    private const string DefenderNamespace = @"root\Microsoft\Windows\Defender";

    /// <summary>
    /// Locates the Defender command-line tool. Windows keeps the real binary under a
    /// versioned platform folder and updates it out of band, so the newest one wins; the
    /// copy in Program Files is the stub kept for compatibility and is used only as a
    /// fallback.
    /// </summary>
    public static string? FindMpCmdRun()
    {
        var platformRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows Defender", "Platform");
        try
        {
            if (Directory.Exists(platformRoot))
            {
                var names = Directory.GetDirectories(platformRoot).Select(Path.GetFileName).OfType<string>();
                if (DefenderScanAssessment.PickNewestPlatform(names) is { } newest)
                {
                    var candidate = Path.Combine(platformRoot, newest, "MpCmdRun.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall through to the stub below rather than failing the action outright.
        }

        var stub = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Windows Defender", "MpCmdRun.exe");
        return File.Exists(stub) ? stub : null;
    }

    /// <summary>The command line the consent dialog must show before anything runs.</summary>
    public static string DescribeCommand(DefenderScanKind kind, string? folder) =>
        $"MpCmdRun.exe {Arguments(kind, folder)}";

    private static string Arguments(DefenderScanKind kind, string? folder) => kind switch
    {
        DefenderScanKind.Quick => "-Scan -ScanType 1",
        // -DisableRemediation is what keeps this read-only: Defender reports what it finds
        // and changes nothing. It also makes it scan inside archives.
        _ => $"-Scan -ScanType 3 -File \"{folder}\" -DisableRemediation",
    };

    public async Task<DefenderScanOutcome> RunAsync(
        DefenderScanKind kind,
        string? folder,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var exe = FindMpCmdRun();
        var arguments = Arguments(kind, folder);
        var commandLine = DescribeCommand(kind, folder);

        if (exe is null)
        {
            return new DefenderScanOutcome(kind, -1, TimedOut: false, TimeSpan.Zero, [],
                DefenderScanAssessment.Describe(kind, folder, -1, timedOut: false, [], TimeSpan.Zero), commandLine);
        }

        // Anything Defender records from here on belongs to this scan. Taken before the
        // process starts so a detection in the first instant is not missed.
        var startedAt = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();

        ProcessResult result;
        try
        {
            result = await processes.RunAsync(ActionId, exe, arguments, timeout, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killing MpCmdRun only stops this app waiting — the scan lives in Defender's
            // own service, so it must be asked to stop as well.
            await TryCancelRunningScanAsync(exe).ConfigureAwait(false);
            throw;
        }
        stopwatch.Stop();

        var detections = kind == DefenderScanKind.Quick ? ReadDetectionsSince(startedAt) : [];

        return new DefenderScanOutcome(
            kind, result.ExitCode, result.TimedOut, stopwatch.Elapsed, detections,
            DefenderScanAssessment.Describe(kind, folder, result.ExitCode, result.TimedOut, detections, stopwatch.Elapsed),
            commandLine);
    }

    private async Task TryCancelRunningScanAsync(string exe)
    {
        try
        {
            await processes.RunAsync(ActionId, exe, "-Scan -Cancel", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
        {
            // Best effort: the user cancelled, and a scan they can still stop from Windows
            // Security is not worth surfacing an error for.
        }
    }

    /// <summary>
    /// Reads what Defender logged during the scan. Structured records rather than MpCmdRun's
    /// console text, which is localized — the same reason every other check in this app
    /// keys on typed values.
    /// </summary>
    private static IReadOnlyList<string> ReadDetectionsSince(DateTime startedAt)
    {
        var detectionRows = Wmi.TryQuery(DefenderNamespace,
            "SELECT ThreatID, InitialDetectionTime, ThreatStatusID FROM MSFT_MpThreatDetection", out var detectionError);
        var threatRows = Wmi.TryQuery(DefenderNamespace,
            "SELECT ThreatID, ThreatName FROM MSFT_MpThreat", out var threatError);
        if (detectionError is not null || threatError is not null) return [];

        var detections = detectionRows.Select(row => new DetectionRecord(
            Wmi.GetString(row, "ThreatID") ?? "",
            Wmi.Get<uint>(row, "ThreatStatusID") ?? 0,
            Wmi.GetCimDate(row, "InitialDetectionTime")));
        var threats = threatRows.Select(row => new ThreatRecord(
            Wmi.GetString(row, "ThreatID") ?? "",
            Wmi.GetString(row, "ThreatName") ?? "unnamed threat",
            IsActive: false));

        return ThreatAssessment.DetectionsSince(threats, detections, startedAt);
    }
}
