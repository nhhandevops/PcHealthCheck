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
        DateTime? lastSuccess = null;

        // Installed hotfixes (locale-tolerant date parsing; WUA history below is the primary signal).
        foreach (var qfe in Wmi.TryQuery(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering", out var qfeError))
        {
            if (TryParseQfeDate(Wmi.GetString(qfe, "InstalledOn")) is { } date && (lastSuccess is null || date > lastSuccess))
            {
                lastSuccess = date;
            }
        }

        var pendingCount = -1;
        var repeatedFailure = false;
        string? repeatedTitle = null;
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is not null)
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
                    notes.Add($"Cached pending-update search unavailable: {e.GetType().Name}.");
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
            notes.Add($"Windows Update COM API unavailable: {e.GetType().Name}: {e.Message}");
        }

        if (lastSuccess is { } success)
        {
            var age = Math.Round((DateTime.Now - success).TotalDays, 0);
            metrics.Add(new Metric("os.patch.age_days", "Days since last successful update", age,
                HealthCategory.SoftwareOs, Unit: "days", Detail: $"Last successful update: {success:yyyy-MM-dd}."));
        }
        else
        {
            notes.Add("Could not determine the last successful update date.");
        }

        if (pendingCount >= 0)
        {
            metrics.Add(new Metric("updates.pending_count", "Updates waiting to install", pendingCount,
                HealthCategory.SoftwareOs,
                Detail: "Counted from the local Windows Update cache — this scan did not contact the internet, so the number can lag behind reality."));
        }

        metrics.Add(new Metric("updates.repeated_failures", "An update keeps failing", repeatedFailure,
            HealthCategory.SoftwareOs,
            Detail: repeatedFailure
                ? $"'{repeatedTitle}' failed at least twice — the Windows Update stack may need repair."
                : "No update failed more than once in recent history."));

        return Task.FromResult(metrics.Count > 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted);
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
