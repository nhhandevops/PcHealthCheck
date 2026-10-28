using System.Globalization;
using Microsoft.Win32;
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
        Description = "How long ago the newest Windows update was released, security and quality updates waiting (from the local cache — no internet), driver and firmware offers, and repeated update failures.",
        Reads = ["The running build's update number (registry)", "Which cumulative update that build came from (Windows' servicing records)", "Windows Update cache: updates waiting by type, and installed updates' release dates (local COM API, offline)", "Whether Windows Update is set to use a company update server (registry policy; the server's name is not read)", "When Windows Update last searched (local COM API)", "Windows Update history (local COM API)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(8),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);
        var clock = DateTime.UtcNow;

        PendingCounts? pending = null;
        // KB number → the date Windows Update's cache gives for it. Null when the list could not be read.
        Dictionary<string, DateTime>? releasedByKb = null;
        var repeatedFailure = false;
        string? repeatedTitle = null;
        // Non-null the moment anything stops the update history being walked end to end. The
        // repeated-failure verdict at the bottom is only a fact while this stays null.
        string? historyError = null;
        // False when the API itself never came up, so nothing below it was read either.
        var searcherReady = false;
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
                searcherReady = true;

                try
                {
                    dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
                    pending = WindowsUpdates.Count(ReadPending(result.Updates));
                }
                catch (Exception e)
                {
                    // No updates.pending_count rather than a zero — but omitting it silently would
                    // leave a report that never says nobody managed to look for updates waiting.
                    failures.Record("Cached pending-update search unavailable", e.GetType().Name);
                }

                try
                {
                    dynamic installed = searcher.Search("IsInstalled=1 and Type='Software'");
                    releasedByKb = ReadReleaseDates(installed.Updates);
                }
                catch (Exception e)
                {
                    failures.Record("Installed updates in the Windows Update cache",
                        $"{e.GetType().Name} — the newest Windows update's release date, and so how old it is, was NOT checked.");
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

        if (pending is not null)
        {
            var (lastSearch, searchDateRead) = ReadLastSearch(failures);
            WindowsUpdates.AddPendingMetrics(metrics, failures, pending, lastSearch, searchDateRead, clock);
        }

        AddNewestUpdate(metrics, failures, releasedByKb, clock);

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
                : searcherReady
                    ? $"{historyError} — so whether an update keeps failing was NOT checked. " +
                      "That is not the same as no update failing."
                    : $"{historyError} — so whether an update keeps failing, how many updates are waiting, and how old " +
                      "the newest Windows update is were NOT checked. That is not the same as no update failing.");
        }

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    /// <summary>Each waiting update's type, driver class, optional flag and classification IDs; never its (translated) title.</summary>
    private static List<PendingUpdate> ReadPending(dynamic updates)
    {
        var list = new List<PendingUpdate>();
        for (var i = 0; i < (int)updates.Count; i++)
        {
            dynamic update = updates.Item(i);
            var isDriver = (int)update.Type == 2;
            string? driverClass = null;
            if (isDriver)
            {
                try { driverClass = (string?)update.DriverClass; }
                catch (Exception e) when (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException) { }
            }
            list.Add(new PendingUpdate(isDriver, driverClass, (bool)update.BrowseOnly, ClassificationIds(update)));
        }
        return list;
    }

    private static List<string> ClassificationIds(dynamic update)
    {
        var ids = new List<string>();
        dynamic categories = update.Categories;
        for (var i = 0; i < (int)categories.Count; i++)
        {
            dynamic category = categories.Item(i);
            if ((string)category.Type == "UpdateClassification") ids.Add((string)category.CategoryID);
        }
        return ids;
    }

    /// <summary>
    /// Installed updates' KB numbers and the date Windows Update gives each (LastDeploymentChangeTime,
    /// UTC): the date the update was published on the server this PC gets updates from. From
    /// Microsoft's own Windows Update that is the day it was released, or last revised; from a
    /// company update server it is the day the server approved it (<see cref="ReadUsesUpdateServer"/>).
    /// Never an install date.
    /// </summary>
    private static Dictionary<string, DateTime> ReadReleaseDates(dynamic updates)
    {
        var installed = new List<CachedUpdate>();
        for (var i = 0; i < (int)updates.Count; i++)
        {
            dynamic update = updates.Item(i);
            DateTime date = (DateTime)update.LastDeploymentChangeTime;
            dynamic kbs = update.KBArticleIDs;
            var names = new List<string>();
            for (var k = 0; k < (int)kbs.Count; k++) names.Add("KB" + (string)kbs.Item(k));
            installed.Add(new CachedUpdate(names, date));
        }
        return WindowsUpdates.ReleaseDates(installed);
    }

    private const string UpdatePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    /// <summary>
    /// Whether Windows Update is set to use a company update server: the UseWUServer policy, which
    /// at 1 makes Windows Update "use a server that is running Software Update Services instead of
    /// Windows Update" (https://learn.microsoft.com/en-us/windows/deployment/update/waas-wu-settings).
    /// Configuration Manager sets the same policy. Only the switch is read, never the server's name.
    /// Null when it could not be read; that is recorded.
    /// </summary>
    private static bool? ReadUsesUpdateServer(ModuleFailures failures)
    {
        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(UpdatePolicyKey);
            return policy?.GetValue("UseWUServer") is 1;
        }
        catch (Exception e) when (WindowsSupport.IsReadFailure(e))
        {
            failures.Record("Windows Update policy",
                $"{e.Message} — whether this PC's update dates come from a company update server, and so how old its Windows updates are, was NOT checked.");
            return null;
        }
    }

    /// <summary>
    /// When Windows Update last searched successfully (UTC), or null when it never has. A read
    /// that breaks is recorded, and the second value is then false.
    /// </summary>
    private static (DateTime? LastSearch, bool Read) ReadLastSearch(ModuleFailures failures)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
            if (type is null)
            {
                failures.Record("When Windows Update last searched", "the Windows Update COM API is not registered on this PC");
                return (null, false);
            }
            dynamic autoUpdate = Activator.CreateInstance(type)!;
            object? date = autoUpdate.Results.LastSearchSuccessDate;
            return (date as DateTime?, true);
        }
        catch (Exception e)
        {
            failures.Record("When Windows Update last searched",
                $"{e.GetType().Name} — so a cached count of 0 updates waiting was NOT graded.");
            return (null, false);
        }
    }

    private const string ServicingPackagesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";
    private const int PackageInstalled = 112;

    /// <summary>
    /// The newest Windows update: the cumulative update package (RollupFix) whose update number
    /// is the running build's UBR, its KB number from the package's own manifest, and that KB's
    /// release date from Windows Update's cache. All readable without admin.
    /// </summary>
    private static void AddNewestUpdate(List<Metric> metrics, ModuleFailures failures, Dictionary<string, DateTime>? releasedByKb, DateTime clock)
    {
        string build;
        string? kb = null;
        try
        {
            using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
                ?? throw new InvalidDataException("the registry has no Windows version key");
            if (version.GetValue("CurrentBuild") is not string { Length: > 0 } currentBuild || version.GetValue("UBR") is not int ubr)
                throw new InvalidDataException("the registry holds no CurrentBuild or UBR (the running build's update number)");
            build = string.Create(CultureInfo.InvariantCulture, $"{currentBuild.Trim()}.{ubr}");

            using var packages = Registry.LocalMachine.OpenSubKey(ServicingPackagesKey)
                ?? throw new InvalidDataException("the registry has no servicing package list");
            foreach (var name in packages.GetSubKeyNames().Where(n => WindowsUpdates.RollupUbr(n) == ubr).OrderByDescending(n => n, StringComparer.Ordinal))
            {
                using var package = packages.OpenSubKey(name);
                if (package?.GetValue("CurrentState") is not PackageInstalled || package.GetValue("InstallName") is not string { Length: > 0 } manifest)
                    continue;
                kb = WindowsUpdates.ManifestKb(ReadStart(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "servicing", "Packages", Path.GetFileName(manifest))));
                if (kb is not null) break;
            }
        }
        catch (Exception e) when (WindowsSupport.IsReadFailure(e))
        {
            failures.Record("Newest Windows update", $"{e.Message} — which Windows update this PC last installed, and how old it is, was NOT checked.");
            return;
        }

        void AddNewestOnly() =>
            metrics.Add(new Metric("updates.newest_windows_update", "Newest Windows update installed", $"{kb} (build {build})",
                HealthCategory.SoftwareOs, InfoOnly: true,
                Detail: "The cumulative update the running build of Windows came from. Defender definitions, drivers and other updates are not it."));

        if (kb is not null && releasedByKb is null)
        {
            // The installed-update list failed and was recorded; the KB is still worth naming.
            AddNewestOnly();
            return;
        }
        DateTime? released = kb is not null && releasedByKb!.TryGetValue(kb, out var date) ? date : null;
        var fromServer = false;
        if (released is not null)
        {
            if (ReadUsesUpdateServer(failures) is not { } usesServer)
            {
                // The policy read failed and was recorded: whose date this is was not established.
                AddNewestOnly();
                return;
            }
            fromServer = usesServer;
        }
        WindowsUpdates.AddAgeMetrics(metrics, failures, build, kb, released, fromServer, clock);
    }

    /// <summary>The first 4 KB of a manifest: its package element is near the top, and the whole file can be half a megabyte.</summary>
    private static string ReadStart(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[4096];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }
}
