using PcHealth.Modules.Windows.Browsers;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The extension audit's whole value is that a person can read it and spot the one they do
/// not recognise. These cover the two ways that fails: describing an extension's reach
/// wrongly, and burying it in wording nobody acts on.
/// </summary>
public class BrowserExtensionTests
{
    private static BrowserExtension Extension(
        string name,
        string[]? permissions = null,
        string[]? hosts = null,
        bool enabled = true,
        bool fromStore = true) =>
        new("Chrome", "Default", "abcdefghijklmnop", name, "1.0",
            permissions ?? [], hosts ?? [], enabled, fromStore);

    [Theory]
    [InlineData("<all_urls>")]
    [InlineData("http://*/*")]
    [InlineData("https://*/*")]
    [InlineData("*://*/*")]
    public void Wildcard_host_access_means_every_site(string pattern)
    {
        Assert.Equal(ExtensionReach.EverySite, ExtensionRisk.ReachOf([pattern], []));
    }

    [Fact]
    public void Manifest_v2_hides_its_site_access_among_the_ordinary_permissions()
    {
        // In MV2 there is no host_permissions list at all — the patterns sit in the same
        // array as "storage" and "tabs". Reading only the V3 field would report a V2
        // extension with total access as having none.
        var reach = ExtensionRisk.ReachOf([], ["identity", "webview", "<all_urls>"]);

        Assert.Equal(ExtensionReach.EverySite, reach);
    }

    [Fact]
    public void An_extension_limited_to_named_sites_is_reported_as_such()
    {
        var reach = ExtensionRisk.ReachOf(["https://docs.google.com/*", "https://drive.google.com/*"], ["storage"]);

        Assert.Equal(ExtensionReach.NamedSites, reach);
    }

    [Fact]
    public void An_extension_with_no_site_access_is_not_made_to_sound_dangerous()
    {
        var reach = ExtensionRisk.ReachOf([], ["storage", "alarms", "sidePanel"]);

        Assert.Equal(ExtensionReach.ItsOwnPagesOnly, reach);
        Assert.Contains("cannot read the pages you visit",
            ExtensionRisk.DescribeReach(reach), StringComparison.Ordinal);
    }

    [Fact]
    public void The_powers_worth_knowing_about_are_spelled_out_in_plain_words()
    {
        var powers = ExtensionRisk.Powers(["storage", "cookies", "nativeMessaging", "proxy", "management"]);

        Assert.Contains(powers, p => p.Contains("keep you signed in", StringComparison.Ordinal));
        Assert.Contains(powers, p => p.Contains("outside the browser", StringComparison.Ordinal));
        Assert.Contains(powers, p => p.Contains("where your browsing traffic is sent", StringComparison.Ordinal));
        Assert.Contains(powers, p => p.Contains("turn your other extensions off", StringComparison.Ordinal));
        // "storage" is ordinary and would only pad the report.
        Assert.Equal(4, powers.Count);
    }

    [Fact]
    public void Two_permissions_that_mean_the_same_thing_are_said_once()
    {
        var powers = ExtensionRisk.Powers(["declarativeNetRequest", "declarativeNetRequestWithHostAccess"]);

        Assert.Single(powers);
    }

    [Fact]
    public void Extensions_a_program_installed_are_flagged_without_being_accused()
    {
        // A download manager adding its own extension is normal. Calling that malware would
        // teach the reader to skip this section, which is when the real one gets through.
        var text = ExtensionRisk.Describe(Extension("IDM Integration Module",
            ["cookies", "nativeMessaging"], ["<all_urls>"], fromStore: false));

        Assert.Contains("installed by a program on this PC", text, StringComparison.Ordinal);
        Assert.Contains("do legitimately", text, StringComparison.Ordinal);
        Assert.Contains("whether you installed that program on purpose", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_extension_gets_no_origin_remark_at_all()
    {
        var text = ExtensionRisk.Describe(Extension("Grammarly", ["cookies"], ["https://*/*"]));

        Assert.DoesNotContain("installed by a program", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_extension_says_so_rather_than_looking_active()
    {
        var text = ExtensionRisk.Describe(Extension("Cookie-Editor", ["cookies"], enabled: false));

        Assert.Contains("[turned off]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a throwaway Chrome-shaped profile so the reader can be driven end to end.
    /// </summary>
    private static string MakeProfile(string manifest, string securePreferences)
    {
        var root = Path.Combine(Path.GetTempPath(), "pchealth-ext-" + Guid.NewGuid().ToString("N")[..8]);
        var version = Path.Combine(root, "Default", "Extensions", "abcdefghijklmnopabcdefghijklmnop", "2.1_0");
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(root, "Default", "Secure Preferences"), securePreferences);
        return root;
    }

    /// <summary>
    /// The crash that real Chrome caused on the first run: disable_reasons is a LIST in
    /// current Chrome, and JsonElement.TryGetInt32 throws on an array rather than returning
    /// false — taking the whole check down. Older builds wrote a single number, so both
    /// shapes have to work.
    /// </summary>
    [Theory]
    [InlineData("[]", true)]            // current Chrome, enabled
    [InlineData("[1, 2]", false)]       // current Chrome, disabled
    [InlineData("0", true)]             // older Chrome, enabled
    [InlineData("4", false)]            // older Chrome, disabled
    [InlineData("\"unexpected\"", true)]  // anything unrecognised must not read as disabled
    public void Both_shapes_of_chromes_disabled_record_are_understood(string disableReasons, bool expectedEnabled)
    {
        var root = MakeProfile(
            """{ "name": "Test Extension", "version": "2.1", "permissions": ["cookies"] }""",
            $$"""
            { "extensions": { "settings": { "abcdefghijklmnopabcdefghijklmnop": {
                "disable_reasons": {{disableReasons}}, "from_webstore": true } } } }
            """);
        try
        {
            var notes = new List<string>();
            var found = ChromiumExtensionReader.Read("Chrome", root, notes);

            var extension = Assert.Single(found);
            Assert.Equal("Test Extension", extension.Name);
            Assert.Equal(expectedEnabled, extension.Enabled);
            Assert.True(extension.FromWebStore);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_placeholder_name_is_resolved_from_the_extensions_own_locale_files()
    {
        // Most extensions declare "__MSG_extName__" and keep the real text in _locales.
        // Left unresolved, the report is a list of placeholders and useless for its one job.
        var root = MakeProfile(
            """{ "name": "__MSG_extName__", "version": "2.1", "default_locale": "en_US" }""",
            """{ "extensions": { "settings": {} } }""");
        var locale = Path.Combine(root, "Default", "Extensions", "abcdefghijklmnopabcdefghijklmnop", "2.1_0",
            "_locales", "en_US");
        Directory.CreateDirectory(locale);
        File.WriteAllText(Path.Combine(locale, "messages.json"),
            """{ "extName": { "message": "AdBlock - block ads across the web" } }""");
        try
        {
            var extension = Assert.Single(ChromiumExtensionReader.Read("Chrome", root, []));
            Assert.Equal("AdBlock - block ads across the web", extension.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_unreadable_manifest_costs_one_extension_not_the_whole_check()
    {
        var root = MakeProfile("{ this is not json", """{ "extensions": { "settings": {} } }""");
        try
        {
            var notes = new List<string>();
            Assert.Empty(ChromiumExtensionReader.Read("Chrome", root, notes));
            Assert.Single(notes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_widest_reach_sorts_to_the_top()
    {
        var extensions = new[]
        {
            Extension("Quiet one"),
            Extension("Reads everything", hosts: ["<all_urls>"]),
            Extension("Reads one site", hosts: ["https://example.com/*"]),
        };

        var ordered = extensions.OrderBy(ExtensionRisk.SortKey).Select(e => e.Name).ToList();

        Assert.Equal(["Reads everything", "Reads one site", "Quiet one"], ordered);
    }
}
