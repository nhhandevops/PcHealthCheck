using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace PcHealth.Modules.Windows.Startup;

/// <summary>One Windows account that has a profile folder on this PC.</summary>
public sealed record UserProfile(string Sid, string ProfilePath, string AccountName)
{
    /// <summary>True when this is the account running the app, whose items the unelevated
    /// startup check already covers.</summary>
    public bool IsCurrentUser { get; init; }
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

    public static IReadOnlyList<UserProfile> Enumerate(List<string> notes)
    {
        var profiles = new List<UserProfile>();
        var currentSid = CurrentUserSid();

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

            profiles.Add(new UserProfile(sid, Environment.ExpandEnvironmentVariables(path), NameOf(sid, path))
            {
                IsCurrentUser = string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase),
            });
        }

        return profiles;
    }

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

    /// <summary>
    /// The account's readable name. Translating the SID is the accurate way and it is tried
    /// first, but it fails for an account that has been deleted while its profile folder
    /// remains — which is exactly the sort of leftover worth reporting rather than dropping.
    /// </summary>
    private static string NameOf(string sid, string profilePath)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception e) when (e is IdentityNotMappedException or ArgumentException or SystemException)
        {
            return Path.GetFileName(profilePath.TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    private static string? CurrentUserSid()
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
