using System.Globalization;
using System.Text.RegularExpressions;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;

namespace PcHealth.Modules.Windows.Support;

/// <summary>One update waiting in Windows Update's local cache, reduced to what decides how it is counted.</summary>
/// <param name="IsDriver">IUpdate.Type 2, a driver; 1 is software.</param>
/// <param name="DriverClass">A driver's device class, "Firmware" for a BIOS/UEFI update; null for software. Not translated.</param>
/// <param name="BrowseOnly">Optional: Windows lists it but does not install it on its own.</param>
/// <param name="Classifications">The update's classification category IDs. GUIDs, never the translated names.</param>
public sealed record PendingUpdate(bool IsDriver, string? DriverClass, bool BrowseOnly, IReadOnlyList<string> Classifications);

/// <summary>Updates waiting, by kind. Only <see cref="Software"/> is graded.</summary>
public sealed record PendingCounts(int Software, int Drivers, int Firmware, int Definitions, int Optional);

/// <summary>One installed update in Windows Update's local cache: its KB numbers and its LastDeploymentChangeTime (UTC).</summary>
public sealed record CachedUpdate(IReadOnlyList<string> Kbs, DateTime DatedUtc);

/// <summary>
/// How current Windows' own updates are. Three readings used to say it and none of them did: the
/// newest install date of any update, which a Defender definition refreshes several times a day;
/// the hotfix install dates, which after a reimage are all the reimage day; and the count of
/// everything waiting, which on the dev T470p was 13 driver offers and no security update at all
/// (OS-001). Updates are now told apart by Windows Update's own type and classification IDs, and
/// the newest Windows update is the cumulative update the running build came from, dated by its
/// release, not by when it was installed.
/// </summary>
public static partial class WindowsUpdates
{
    /// <summary>
    /// The "Definition Updates" classification (Defender's definitions, which it installs itself
    /// several times a day): https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ff357803(v=vs.85).
    /// </summary>
    public const string DefinitionUpdates = "e0789628-ce08-4437-be74-2495b842f43b";

    /// <summary>
    /// A zero from a search older than this is not an all-clear. Windows Update searches about
    /// once a day on a PC that is on; a week covers a long weekend, not a missed Patch Tuesday.
    /// </summary>
    public const int FreshSearchDays = 7;

    public static PendingCounts Count(IEnumerable<PendingUpdate> pending)
    {
        int software = 0, drivers = 0, firmware = 0, definitions = 0, optional = 0;
        foreach (var update in pending)
        {
            if (update.IsDriver)
            {
                if (string.Equals(update.DriverClass, "Firmware", StringComparison.OrdinalIgnoreCase)) firmware++;
                else drivers++;
            }
            else if (update.Classifications.Any(c => string.Equals(c, DefinitionUpdates, StringComparison.OrdinalIgnoreCase))) definitions++;
            else if (update.BrowseOnly) optional++;
            else software++;
        }
        return new PendingCounts(software, drivers, firmware, definitions, optional);
    }

    /// <summary>Whether Windows Update searched recently enough for its zero to mean something. A date after the clock is not fresh: one of the two is wrong.</summary>
    public static bool SearchIsFresh(DateTime? lastSearchUtc, DateTime clockUtc) =>
        lastSearchUtc is { } last && last <= clockUtc.AddDays(1) && clockUtc - last <= TimeSpan.FromDays(FreshSearchDays);

    /// <summary>
    /// Adds the pending-update rows. The security and quality count is graded when Windows Update
    /// searched recently, or when it is above zero, which is a lower bound however old the search.
    /// A zero from an old search, or from no search, is a Note: Windows Update simply has not
    /// looked, so nothing broke. Driver and firmware offers are information only.
    /// </summary>
    /// <param name="searchDateRead">False when the last-search date could not be read; that failure is recorded by the caller.</param>
    public static void AddPendingMetrics(List<Metric> metrics, ModuleFailures failures, PendingCounts counts,
        DateTime? lastSearchUtc, bool searchDateRead, DateTime clockUtc)
    {
        var fresh = SearchIsFresh(lastSearchUtc, clockUtc);
        var asOf = lastSearchUtc is { } searched
            ? string.Create(CultureInfo.InvariantCulture, $"as of Windows Update's last search on {searched:yyyy-MM-dd}")
            : "Windows Update has no record of a search";

        if (counts.Software > 0 || fresh)
        {
            var notCounted = new List<string>();
            if (counts.Drivers > 0) notCounted.Add(Plural(counts.Drivers, "driver offer"));
            if (counts.Firmware > 0) notCounted.Add(Plural(counts.Firmware, "firmware offer"));
            if (counts.Definitions > 0) notCounted.Add(Plural(counts.Definitions, "Defender definition update"));
            if (counts.Optional > 0) notCounted.Add(Plural(counts.Optional, "optional update"));
            var detail = fresh
                ? $"Security and quality updates waiting, from Windows Update's local cache {asOf} (this scan does not go online)."
                : string.Create(CultureInfo.InvariantCulture,
                    $"At least {counts.Software}: counted from Windows Update's local cache {asOf}, so more may be waiting by now.");
            if (notCounted.Count > 0) detail += $" Not counted here: {string.Join(", ", notCounted)}.";
            metrics.Add(new Metric("updates.pending_count", "Windows updates waiting to install", counts.Software,
                HealthCategory.SoftwareOs, Detail: detail));
        }
        else if (searchDateRead)
        {
            failures.Note(lastSearchUtc switch
            {
                null => "Windows Update has never searched for updates on this PC, so whether updates are waiting was NOT established.",
                { } last when last > clockUtc.AddDays(1) => string.Create(CultureInfo.InvariantCulture,
                    $"Windows Update's last search is dated {last:yyyy-MM-dd}, after this PC's clock, so its count of updates waiting was NOT graded."),
                { } last => string.Create(CultureInfo.InvariantCulture,
                    $"Windows Update has not searched since {last:yyyy-MM-dd}, so its cached count of 0 updates waiting was NOT graded."),
            });
        }

        if (counts.Drivers > 0 || fresh)
        {
            metrics.Add(new Metric("updates.pending_drivers", "Driver updates offered", counts.Drivers, HealthCategory.SoftwareOs,
                InfoOnly: true,
                Detail: $"Driver offers in Windows Update's local cache, {asOf}. Shown for information, not graded: they are not security " +
                        "updates. Install one when it fixes a problem, or use the maker's own update tool."));
        }
        if (counts.Firmware > 0 || fresh)
        {
            metrics.Add(new Metric("updates.pending_firmware", "Firmware (BIOS/UEFI) updates offered", counts.Firmware, HealthCategory.SoftwareOs,
                InfoOnly: true,
                Detail: $"Firmware offers in Windows Update's local cache, {asOf}. Shown for information, not graded. " +
                        "Install firmware with the laptop on mains power."));
        }
    }

    /// <summary>
    /// The update number a cumulative update's servicing package carries, from its name:
    /// "Package_for_RollupFix~31bf3856ad364e35~amd64~~19041.6466.1.0" is 6466. The first number is
    /// the base build (19041 for Windows 10 22H2), so only the second is compared with UBR.
    /// </summary>
    public static int? RollupUbr(string packageName)
    {
        var match = RollupName().Match(packageName);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ubr) ? ubr : null;
    }

    /// <summary>
    /// The KB number a servicing package manifest (.mum) names in its package element,
    /// &lt;package identifier="KB5071959" ...&gt;. It is near the top, so the start of the file is enough.
    /// </summary>
    public static string? ManifestKb(string manifestStart) =>
        ManifestPackage().Match(manifestStart) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant() : null;

    /// <summary>
    /// Each KB's date from the cached installed updates, the earliest where several entries carry
    /// one. LastDeploymentChangeTime is "the last published date of the update ... on the server
    /// that deploys the update" (https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdate-get_lastdeploymentchangetime):
    /// a revision moves it later, so the latest of several could only make an update look newer.
    /// </summary>
    public static Dictionary<string, DateTime> ReleaseDates(IEnumerable<CachedUpdate> installed)
    {
        var byKb = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var update in installed)
        {
            foreach (var kb in update.Kbs)
            {
                if (!byKb.TryGetValue(kb, out var known) || update.DatedUtc < known) byKb[kb] = update.DatedUtc;
            }
        }
        return byKb;
    }

    /// <summary>
    /// Adds the newest Windows update and how long ago it was released. Nothing here is a failure:
    /// a build with no cumulative update package, or a KB Windows Update's cache does not know,
    /// leaves the age NOT graded and says so. The reads that can break are recorded by the caller.
    /// </summary>
    /// <param name="build">The running build with its update number, "19045.6466".</param>
    /// <param name="kb">The cumulative update the running build came from, or null when no package was found.</param>
    /// <param name="releasedUtc">Its release date from Windows Update's cache, or null when the cache does not list it.</param>
    /// <param name="datedByUpdateServer">
    /// True when Windows Update uses a company update server (WSUS, or Configuration Manager through
    /// it). The cache's date is then that server's: the day the update was approved there, which can
    /// be weeks after Microsoft released it, so the age is NOT graded rather than graded too young.
    /// </param>
    public static void AddAgeMetrics(List<Metric> metrics, ModuleFailures failures, string build, string? kb, DateTime? releasedUtc,
        bool datedByUpdateServer, DateTime clockUtc)
    {
        metrics.Add(new Metric("updates.newest_windows_update", "Newest Windows update installed",
            kb is null ? $"build {build}" : $"{kb} (build {build})", HealthCategory.SoftwareOs, InfoOnly: true,
            Detail: "The cumulative update the running build of Windows came from. Defender definitions, drivers and other updates are not it."));

        if (kb is null)
        {
            failures.Note($"No installed cumulative update package matches build {build}, so how old this PC's Windows updates are was NOT checked.");
            return;
        }
        if (releasedUtc is not { } releasedAt)
        {
            failures.Note($"Windows Update's local cache does not list {kb}, so its release date, and how old this PC's Windows updates are, " +
                          "was NOT checked.");
            return;
        }

        var today = WindowsSupport.Today(clockUtc);
        var released = DateOnly.FromDateTime(releasedAt);
        if (datedByUpdateServer)
        {
            failures.Note(string.Create(CultureInfo.InvariantCulture,
                $"This PC gets its updates from a company update server (WSUS or Configuration Manager), which dates {kb} {released:yyyy-MM-dd}: " +
                $"the day the server approved it, not the day Microsoft released it, so how old this PC's Windows updates are was NOT graded."));
            return;
        }
        if (released > today)
        {
            failures.Note(string.Create(CultureInfo.InvariantCulture,
                $"{kb} is dated {released:yyyy-MM-dd} in Windows Update's cache, after this PC's clock, so how old this PC's Windows updates are was NOT graded."));
            return;
        }
        metrics.Add(new Metric("os.patch.age_days", "Days since the newest Windows update was released", today.DayNumber - released.DayNumber,
            HealthCategory.SoftwareOs, Unit: "days",
            Detail: string.Create(CultureInfo.InvariantCulture,
                $"{kb} (build {build}), the newest Windows cumulative update installed, was released on {released:yyyy-MM-dd}. " +
                $"Counted from its release, not from when it was installed; Defender definitions and other updates do not count.")));
    }

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    [GeneratedRegex(@"^Package_for_RollupFix~[^~]*~[^~]*~[^~]*~\d+\.(\d+)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RollupName();

    [GeneratedRegex("""<package\s[^>]*?identifier="(KB\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ManifestPackage();
}
