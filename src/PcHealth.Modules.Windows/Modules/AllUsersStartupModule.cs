using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Startup;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// What starts up for the OTHER accounts on this PC.
///
/// The ordinary startup check can only see the account it runs as. On a shared PC that
/// leaves a hole big enough to walk through: another account's Run keys and Startup folder
/// launch when that person signs in, and are completely invisible to a scan run as someone
/// else. Malware that has taken one account rather than the machine lives exactly there.
///
/// Needs administrator rights, because reading another person's profile is precisely what
/// Windows stops an ordinary user doing.
///
/// PRIVACY: accounts are numbered ("Account 2"), never named, and each item is listed by its
/// name and where it starts from, never by the command it runs. Until 2026-10-27 this check
/// wrote other people's account names and full Run commands into the report (PRIV-002).
/// </summary>
internal sealed class AllUsersStartupModule : WindowsModuleBase
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "startup.allusers",
        Name = "Startup for other accounts",
        Description = "Checks what starts up for the other Windows accounts on this PC, not just yours. " +
                      "On a shared computer, anything set to run for someone else's account is invisible " +
                      "to a normal scan.",
        Reads = ["The list of user profiles and their folders (registry)",
                 "Each account's Run/RunOnce keys, where Windows has them loaded — the report gets entry names only, never the commands",
                 "Each account's Startup folder — file names only, never file contents"],
        NeedsAdmin = true,
        EstimatedDuration = TimeSpan.FromSeconds(5),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        // "Other" means other than the account the scan was started from, whose items the ordinary
        // startup check covers. This check runs in the elevated helper, as whoever typed the
        // administrator password: on a company laptop often a technician, so the employee's own
        // account was listed as someone else's and the technician's Run keys were read by neither
        // check (PRIV-002, review of 2026-10-28). The window tells the helper its account. An
        // unelevated window runs as the person signed in, so that account is the reader's; an app
        // started with "Run as administrator" may run as whoever typed the password, so then its
        // account is named only as the one that started the scan.
        var readersOwn = context.StartedBySid is not null || !context.IsElevated;
        var profiles = UserProfiles.Enumerate(notes, context.StartedBySid);
        var others = profiles.Where(p => !p.IsCurrentUser).ToList();

        if (others.Count == 0)
        {
            metrics.Add(new Metric("startup.other_accounts", "Other accounts on this PC", 0,
                HealthCategory.Security, InfoOnly: true,
                Detail: OtherAccountReport.OnlyAccount(readersOwn)));
            return Task.FromResult(ModuleStatus.Completed);
        }

        var results = new List<AccountStartupResult>();
        var unchecked_ = new List<string>();

        foreach (var profile in others)
        {
            ct.ThrowIfCancellationRequested();
            var items = new List<string>();
            var complete = true;

            complete &= ReadRunKeys(profile, items, unchecked_);
            complete &= ReadStartupFolder(profile, items, unchecked_);

            results.Add(new AccountStartupResult(profile.Label, items, complete));
        }

        var totalItems = results.Sum(r => r.Items.Count);
        var partiallyExamined = results.Count(r => !r.FullyExamined);

        metrics.Add(new Metric("startup.other_accounts", "Other accounts on this PC", others.Count,
            HealthCategory.Security, InfoOnly: true,
            Detail: OtherAccountReport.DescribeAccounts(results, profiles.FirstOrDefault(p => p.IsCurrentUser)?.Label, readersOwn)));

        metrics.Add(new Metric("startup.other_accounts_items", "Programs starting for other accounts", totalItems,
            HealthCategory.Security, InfoOnly: true,
            Detail: OtherAccountReport.DescribeTotal(totalItems, partiallyExamined, others.Count, readersOwn)));

        // Saying which accounts could not be fully read is the whole point: an account this
        // check silently skipped would otherwise be reported as having nothing to start.
        foreach (var reason in unchecked_) notes.Add(reason);

        return Task.FromResult(unchecked_.Count == 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted);
    }

    /// <summary>
    /// Reads another account's Run keys, which are only reachable while Windows has that
    /// account's registry hive loaded — that is, while they are signed in or switched away.
    /// A hive that is not loaded is a gap, and is reported as one rather than passed over.
    /// </summary>
    private static bool ReadRunKeys(UserProfile profile, List<string> items, List<string> unchecked_)
    {
        try
        {
            using var hive = Registry.Users.OpenSubKey(profile.Sid);
            if (hive is null)
            {
                unchecked_.Add($"{profile.Label} is not signed in, so its Run entries could not be read — " +
                               "Windows only keeps an account's registry loaded while it is in use. Its Startup " +
                               "folder was still checked. To cover the rest, sign in to that account and scan again.");
                return false;
            }

            foreach (var (path, source) in new[] { (RunKey, "Run"), (RunOnceKey, "RunOnce") })
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    // The command is read only to skip an empty entry. It is not written into the
                    // report: it holds the other account's own folder, and so its name.
                    if (string.IsNullOrWhiteSpace(key.GetValue(name)?.ToString())) continue;
                    items.Add($"{name} [{source}]");
                }
            }
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            unchecked_.Add($"{profile.Label}'s Run entries could not be read: {e.GetType().Name}.");
            return false;
        }
    }

    /// <summary>
    /// Reads the account's Startup folder — file names only, never contents.
    ///
    /// The access check comes first and is not optional. When a profile folder is unreadable,
    /// .NET reports a missing directory rather than an access error, so asking "does the
    /// Startup folder exist?" answers "no" for an account whose contents were never visible —
    /// and this check would report an unexamined account as having nothing to start.
    /// </summary>
    private static bool ReadStartupFolder(UserProfile profile, List<string> items, List<string> unchecked_)
    {
        var startup = Path.Combine(profile.ProfilePath,
            "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

        if (!UserProfiles.CanRead(profile.ProfilePath, out var failure))
        {
            unchecked_.Add($"{profile.Label}'s profile folder could not be opened ({failure}), so its " +
                           "Startup folder was NOT examined. This is not the same as it being empty.");
            return false;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(startup))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add($"{name} [Startup folder]");
            }
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // The profile itself opened, so a missing Startup folder genuinely means none.
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            unchecked_.Add($"{profile.Label}'s Startup folder could not be listed: {e.GetType().Name}.");
            return false;
        }
    }
}
