using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace PcHealth.Modules.Windows.Startup;

/// <summary>One Windows account that has a profile folder on this PC.</summary>
public sealed record UserProfile(string Sid, string ProfilePath)
{
    /// <summary>True when this is the account the scan was started from, whose items the
    /// unelevated startup check already covers. In the elevated helper that need not be the
    /// helper's own account (<see cref="UserProfiles.Enumerate"/>).</summary>
    public bool IsCurrentUser { get; init; }

    /// <summary>
    /// The account's number on this PC, from 1, in the order Windows issued the accounts (by
    /// the SID's last part), the current account included, so "account 2" names the same
    /// account whoever runs the scan.
    /// </summary>
    public int Number { get; init; }

    /// <summary>
    /// How the report names this account. Never the account's name: under principle 10 of
    /// REMEDIES-PLAN a report carries no user names. The report says which number is the
    /// reader's own, and the rest follow the order Windows created the accounts. The name is not
    /// even looked up, which for a domain account would also have meant asking a domain controller.
    /// </summary>
    public string Label => "Account " + Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Finds the other people who have accounts on this PC.
///
/// This matters because the ordinary startup check can only see the account it runs as. A
/// second account — a family member's, an old one nobody signs into any more — has its own
/// Run keys and its own Startup folder, and something living there starts when that account
/// signs in while remaining completely invisible to a scan run as someone else.
/// </summary>
public static class UserProfiles
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>
    /// Below this, the SID belongs to Windows rather than a person. Real accounts start at
    /// 500 (the built-in Administrator); everything under it is a machine-local well-known
    /// group or account that never has startup items.
    /// </summary>
    private const int FirstRealAccountRid = 500;

    /// <summary>
    /// Whether a SID belongs to a person rather than to Windows.
    ///
    /// Only <c>S-1-5-21-…</c> SIDs describe accounts issued by this machine or its domain.
    /// The service identities that also have profile folders — SYSTEM, LOCAL SERVICE,
    /// NETWORK SERVICE, and the <c>S-1-5-80-…</c> per-service SIDs — would otherwise appear
    /// as half a dozen accounts nobody recognises, which is how a real finding gets lost.
    /// </summary>
    public static bool IsRealUserAccount(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)) return false;

        var lastDash = sid.LastIndexOf('-');
        if (lastDash < 0 || lastDash == sid.Length - 1) return false;

        return int.TryParse(sid[(lastDash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rid)
               && rid >= FirstRealAccountRid;
    }

    /// <param name="startedBySid">The account the scan was started from, whose items the unelevated
    /// startup check covers (<see cref="PcHealth.Core.Modules.ModuleContext.StartedBySid"/>). Null for
    /// this process's own account, which is that account unless this is the elevated helper.</param>
    public static IReadOnlyList<UserProfile> Enumerate(List<string> notes, string? startedBySid = null)
    {
        var profiles = new List<UserProfile>();
        var currentSid = startedBySid ?? CurrentUserSid();

        using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        if (list is null)
        {
            notes.Add("The list of user profiles could not be read from the registry, so only this account was checked.");
            return profiles;
        }

        foreach (var sid in list.GetSubKeyNames())
        {
            if (!IsRealUserAccount(sid)) continue;

            using var entry = list.OpenSubKey(sid);
            var path = entry?.GetValue("ProfileImagePath")?.ToString();
            if (string.IsNullOrWhiteSpace(path)) continue;

            profiles.Add(new UserProfile(sid, Environment.ExpandEnvironmentVariables(path))
            {
                IsCurrentUser = string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase),
            });
        }

        return Numbered(profiles);
    }

    /// <summary>Numbers the accounts from 1 in the order Windows issued them: by the SID's last
    /// part (the relative ID), then by the whole SID for accounts from different domains.</summary>
    public static IReadOnlyList<UserProfile> Numbered(IEnumerable<UserProfile> profiles) =>
        profiles
            .OrderBy(p => RelativeId(p.Sid))
            .ThenBy(p => p.Sid, StringComparer.OrdinalIgnoreCase)
            .Select((p, index) => p with { Number = index + 1 })
            .ToList();

    private static long RelativeId(string sid) =>
        long.TryParse(sid[(sid.LastIndexOf('-') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rid)
            ? rid
            : long.MaxValue;

    /// <summary>
    /// Whether this profile folder can actually be read, asked by opening it rather than by
    /// testing whether a path inside it exists.
    ///
    /// The distinction is not pedantic. When a profile folder is denied, .NET reports paths
    /// beneath it as *missing* rather than as forbidden — so "does this account have a
    /// Startup folder?" answers "no" for an account whose contents were never visible, and a
    /// check built on that would report an unexamined account as having nothing that starts.
    /// Verified against a real second account on the development PC.
    /// </summary>
    public static bool CanRead(string profilePath, out string? failure)
    {
        failure = null;
        try
        {
            _ = Directory.EnumerateFileSystemEntries(profilePath).Take(1).ToList();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            failure = e.GetType().Name;
            return false;
        }
    }

    /// <summary>This process's own account, as a SID, or null when Windows will not say.</summary>
    public static string? CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
