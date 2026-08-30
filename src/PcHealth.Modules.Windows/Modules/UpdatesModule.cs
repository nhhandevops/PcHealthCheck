using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Patch recency and Windows Update health. Uses the WUA COM API strictly OFFLINE
/// (Online = false → cached results only; an online search is a separate opt-in, M4).
/// </summary>
internal sealed class UpdatesModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "updates",
        Name = "Windows Update health",
        Description = "Days since the last successful update, pending updates (from the local cache — no internet), and repeated update failures.",
        Reads = ["Installed hotfixes (WMI)", "Windows Update history and cache (local COM API, offline)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(6),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);
        DateTime? lastSuccess = null;

        // Installed hotfixes (locale-tolerant date parsing; WUA history below is the primary signal).
        foreach (var qfe in Wmi.TryQuery(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering",
            failures, "Installed hotfix list"))
        {
            if (TryParseQfeDate(Wmi.GetString(qfe, "InstalledOn")) is { } date && (lastSuccess is null || date > lastSuccess))
            {
                lastSuccess = date;
            }
        }

        var pendingCount = -1;
        var repeatedFailure = false;
        string? repeatedTitle = null;
        // Non-null the moment anything stops the update history being walked end to end. The
        // repeated-failure verdict at the bottom is only a fact while this stays null.
        string? historyError = null;
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null)
            {
                historyError = "the Windows Update COM API is not registered on this PC";
            }
            else
            {
                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = false; // cached results only — never goes to the internet

                try
                {
                    dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
                    pendingCount = (int)result.Updates.Count;
                }
                catch (Exception e)
                {
                    // pendingCount stays -1 so updates.pending_count is omitted rather than
                    // reported as zero — but omitting it silently would leave a report that never
                    // says nobody managed to look for updates waiting to install.
                    failures.Record("Cached pending-update search unavailable", e.GetType().Name);
                }

                int historyTotal = (int)searcher.GetTotalHistoryCount();
                if (historyTotal > 0)
                {
                    dynamic history = searcher.QueryHistory(0, Math.Min(50, historyTotal));
                    var failuresByTitle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < (int)history.Count; i++)
                    {
                        dynamic entry = history.Item(i);
                        int resultCode = (int)entry.ResultCode; // 2 = succeeded, 3 = succeeded w/ errors, 4 = failed
                        DateTime date = (DateTime)entry.Date;
                        if (resultCode is 2 or 3 && (lastSuccess is null || date > lastSuccess)) lastSuccess = date;
                        if (resultCode == 4)
                        {
                            string title = (string)(entry.Title ?? "");
                            if (title.Length > 0)
                            {
                                failuresByTitle[title] = failuresByTitle.GetValueOrDefault(title) + 1;
                                if (failuresByTitle[title] >= 2 && !repeatedFailure)
                                {
                                    repeatedFailure = true;
                                    repeatedTitle = title;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            historyError = $"{e.GetType().Name}: {e.Message}";
        }

        if (lastSuccess is { } success)
        {
            var age = Math.Round((DateTime.Now - success).TotalDays, 0);
            metrics.Add(new Metric("os.patch.age_days", "Days since last successful update", age,
                HealthCategory.SoftwareOs, Unit: "days", Detail: $"Last successful update: {success:yyyy-MM-dd}."));
        }
        else
        {
            // Not a failure on its own: a PC that has genuinely never installed an update has no
            // date to find. Whichever read actually broke — the hotfix list or the update
            // history — recorded itself as a failure, so the status already reflects that.
            failures.Note("Could not determine the last successful update date.");
        }

        if (pendingCount >= 0)
        {
            metrics.Add(new Metric("updates.pending_count", "Updates waiting to install", pendingCount,
                HealthCategory.SoftwareOs,
                Detail: "Counted from the local Windows Update cache — this scan did not contact the internet, so the number can lag behind reality."));
        }

        // A repeated failure that was already seen survives a history walk that threw afterwards:
        // two entries carrying the same title and result code 4 were read end to end, and nothing
        // further down the list can un-see them. The malformed entry that throws is most likely on
        // exactly the damaged update stack this metric exists to catch, so discarding the finding
        // would drop the only Bad verdict this module can raise, and its DISM/sfc repair advice
        // with it, in favour of "could not finish".
        if (historyError is null || repeatedFailure)
        {
            metrics.Add(new Metric("updates.repeated_failures", "An update keeps failing", repeatedFailure,
                HealthCategory.SoftwareOs,
                Detail: repeatedFailure
                    ? $"'{repeatedTitle}' failed at least twice — the Windows Update stack may need repair."
                    : "No update failed more than once in recent history."));
        }

        if (historyError is not null)
        {
            // The history read broke either way, so the scan still says a check could not finish.
            // What changes is the claim left standing: with no repeated failure seen, the metric
            // above is suppressed because "No update failed more than once" would be a green
            // verdict on a history this scan never opened; with one seen, that finding is fact and
            // only the part of the history nobody got to read is named as missing.
            failures.Record("Windows Update history", repeatedFailure
                ? $"{historyError} — the repeated failure reported above was seen before the read stopped, " +
                  "but the rest of the update history was not examined."
                : $"{historyError} — so whether an update keeps failing was NOT checked. " +
                  "That is not the same as no update failing.");
        }

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    private static DateTime? TryParseQfeDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] formats = ["M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy"];
        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact;
        if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var current)) return current;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant)) return invariant;
        return null;
    }
}
