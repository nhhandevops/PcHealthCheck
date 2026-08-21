namespace PcHealth.Modules.Windows.Browsers;

/// <summary>How much of your browsing an extension can see.</summary>
public enum ExtensionReach
{
    /// <summary>Every page on every site, including your bank and your email.</summary>
    EverySite,

    /// <summary>Only the sites it names in its manifest.</summary>
    NamedSites,

    /// <summary>Nothing outside its own windows.</summary>
    ItsOwnPagesOnly,
}

/// <summary>One installed browser extension, as its manifest and the browser's own records describe it.</summary>
public sealed record BrowserExtension(
    string Browser,
    string Profile,
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> HostPermissions,
    bool Enabled,
    bool FromWebStore);

/// <summary>
/// Explains, in words a person can act on, what an extension is able to do.
///
/// Nothing here produces a verdict, and that is deliberate. Grammarly reading every page
/// you type on IS Grammarly working; a download manager talking to a program outside the
/// browser IS the download manager working. An app that coloured those red would be teaching
/// its user to ignore it. What the user cannot easily get anywhere else is a plain list of
/// what each extension can reach — so the one they do not recognise stands out.
/// </summary>
public static class ExtensionRisk
{
    private static readonly string[] EverySitePatterns =
        ["<all_urls>", "*://*/*", "http://*/*", "https://*/*", "file:///*", "*://*/"];

    /// <summary>
    /// Permissions worth spelling out, and what each one actually means for the person
    /// using the browser. Anything not listed is ordinary and left unmentioned rather than
    /// padding the report with noise.
    /// </summary>
    private static readonly (string Permission, string Meaning)[] NotablePowers =
    [
        ("cookies", "read the cookies that keep you signed in to websites"),
        ("nativeMessaging", "talk to a program installed on your PC, outside the browser"),
        ("proxy", "change where your browsing traffic is sent"),
        ("management", "turn your other extensions off, including security ones"),
        ("debugger", "take complete control of any page"),
        ("webRequest", "watch and alter the requests pages make"),
        ("declarativeNetRequest", "block or redirect requests pages make"),
        ("declarativeNetRequestWithHostAccess", "block or redirect requests pages make"),
        ("history", "read your browsing history"),
        ("downloads", "download files onto your PC"),
        ("tabs", "see the address of every tab you have open"),
        ("clipboardRead", "read whatever you have copied"),
        ("privacy", "change your browser's privacy settings"),
        ("browsingData", "erase your browsing data"),
        ("identity", "see the account you are signed into the browser with"),
        ("geolocation", "read your location"),
    ];

    /// <summary>
    /// In Manifest V2 the site patterns sit in the same list as everything else, so both
    /// lists are searched — a V2 extension with full access would otherwise look harmless.
    /// </summary>
    public static ExtensionReach ReachOf(IEnumerable<string> hostPermissions, IEnumerable<string> permissions)
    {
        var all = hostPermissions.Concat(permissions).ToList();

        if (all.Any(p => EverySitePatterns.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            return ExtensionReach.EverySite;
        }

        return all.Any(LooksLikeSitePattern) ? ExtensionReach.NamedSites : ExtensionReach.ItsOwnPagesOnly;
    }

    private static bool LooksLikeSitePattern(string permission) =>
        permission.Contains("://", StringComparison.Ordinal);

    public static string DescribeReach(ExtensionReach reach) => reach switch
    {
        ExtensionReach.EverySite => "can read and change every page you visit, including banking and email",
        ExtensionReach.NamedSites => "can read and change the specific sites listed in its manifest",
        _ => "cannot read the pages you visit",
    };

    /// <summary>The notable things this extension can do, in the order listed above.</summary>
    public static IReadOnlyList<string> Powers(IEnumerable<string> permissions)
    {
        var held = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var meanings = new List<string>();
        foreach (var (permission, meaning) in NotablePowers)
        {
            // Two request permissions mean the same thing to a reader; say it once.
            if (held.Contains(permission) && !meanings.Contains(meaning, StringComparer.Ordinal))
            {
                meanings.Add(meaning);
            }
        }
        return meanings;
    }

    /// <summary>Most reach first, so whatever deserves a second look is at the top.</summary>
    public static int SortKey(BrowserExtension extension) => extension.Reach() switch
    {
        ExtensionReach.EverySite => 0,
        ExtensionReach.NamedSites => 1,
        _ => 2,
    };

    public static ExtensionReach Reach(this BrowserExtension extension) =>
        ReachOf(extension.HostPermissions, extension.Permissions);

    /// <summary>One line per extension, as it appears in the report.</summary>
    public static string Describe(BrowserExtension extension)
    {
        var parts = new List<string> { DescribeReach(extension.Reach()) };
        parts.AddRange(Powers(extension.Permissions));

        var origin = extension.FromWebStore
            ? ""
            : " — installed by a program on this PC rather than by you from the store, which download managers " +
              "and antivirus tools do legitimately; the question is whether you installed that program on purpose";

        var state = extension.Enabled ? "" : " [turned off]";

        return $"{extension.Name}{state} ({extension.Browser}): {string.Join("; ", parts)}.{origin}";
    }
}
