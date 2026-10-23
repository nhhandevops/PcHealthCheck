using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Browsers;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// What is installed in your browsers, and what each one can reach.
///
/// Browser extensions are the widest-open door on a modern home PC: one with access to
/// every site can read your email, your bank pages and everything you type, and it needs no
/// administrator rights and no malware to do it. They are also the thing people install once
/// and never think about again.
///
/// Reported without verdicts on purpose. A writing assistant reading every page you type on
/// is that assistant working; a download manager talking to a program outside the browser is
/// that manager working. Colouring those red would train the reader to skip this section —
/// which is exactly when the one extension they do not recognise slips past. The job here is
/// a clear inventory, worst reach first, in words that need no explaining.
/// </summary>
internal sealed class BrowserExtensionsModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "browser.extensions",
        Name = "Browser extensions",
        Description = "Lists the extensions installed in Chrome, Edge, Brave, Vivaldi and Opera, and what each one " +
                      "is able to reach — including which can read every page you visit.",
        Reads = ["Each extension's manifest.json (the same information its store page shows)",
                 "Whether the browser has each extension switched on, and whether it came from the store"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(5),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var all = new List<BrowserExtension>();
        var browsersSeen = new List<string>();
        var failures = new ModuleFailures(notes);

        // Every note ChromiumExtensionReader adds is a folder it could not list or a manifest it
        // could not parse — it stays silent when a read works — so the notes it appends here are
        // the count of extensions this audit never saw.
        var notesBeforeReading = notes.Count;

        foreach (var (browser, userDataPath) in ChromiumExtensionReader.KnownBrowsers())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(userDataPath)) continue;
            browsersSeen.Add(browser);
            all.AddRange(ChromiumExtensionReader.Read(browser, userDataPath, notes));
        }

        var unreadable = notes.Count - notesBeforeReading;
        if (unreadable > 0)
        {
            // The inventory is the entire product of this module, and an extension whose folder
            // would not open is one whose reach nobody has seen. Reporting that as a finished
            // audit is the reassuring lie: the reader would take a short list for a complete one
            // and conclude nothing on this PC can read their banking pages.
            failures.Record("Browser extension audit",
                $"{ChromiumExtensionReader.Count(unreadable)} extension folder(s) or manifest(s) could not be read, " +
                "so this scan saw fewer extensions than are installed");
        }

        if (browsersSeen.Count == 0)
        {
            notes.Add("No Chrome, Edge, Brave, Vivaldi or Opera profile was found for this user account. " +
                      "Firefox stores its add-ons differently and is not covered yet.");
            return Task.FromResult(failures.Resolve(ModuleStatus.Skipped));
        }

        notes.Add($"Checked {string.Join(", ", browsersSeen)}. Firefox add-ons are not covered yet, and only this " +
                  "Windows user account was examined.");

        var active = all.Where(e => e.Enabled).ToList();
        var everySite = active.Where(e => e.Reach() == ExtensionReach.EverySite).ToList();
        var outsideStore = active.Where(e => !e.FromWebStore).ToList();

        var dormant = all.Where(e => !e.Enabled).ToList();

        metrics.Add(new Metric("browser.extensions.count", "Extensions installed",
            all.Count, HealthCategory.Security, InfoOnly: true,
            Detail: all.Count == 0
                // "None are installed" is a statement about the PC; after a folder that would not
                // open, the only honest statement is about what the scan managed to see.
                ? unreadable == 0
                    ? "No extensions are installed."
                    : "Nothing could be read from this PC's extension folders, so whether any are installed is not known."
                : $"{ChromiumExtensionReader.Count(active.Count)} switched on, " +
                  $"{ChromiumExtensionReader.Count(dormant.Count)} turned off, across " +
                  $"{ChromiumExtensionReader.Count(browsersSeen.Count)} browser(s)." +
                  // Named here rather than given a row each: something switched off can do
                  // nothing, and twenty rows of them would bury the ones that can.
                  (dormant.Count == 0
                      ? ""
                      : " Turned off (they can do nothing while they stay that way, but they are still installed): " +
                        string.Join("; ", dormant.Select(e => $"{e.Name} ({e.Browser})")))));

        metrics.Add(new Metric("browser.extensions.every_site", "Extensions that can read every site",
            everySite.Count, HealthCategory.Security, InfoOnly: true,
            Detail: everySite.Count == 0
                ? "No enabled extension can read the pages you visit."
                : "Each of these can read and change everything you see and type on every website, including " +
                  "banking and email. That is normal for what they do — the point is that you are trusting each " +
                  "one completely, so remove any you no longer use: " +
                  string.Join("; ", everySite.Select(e => $"{e.Name} ({e.Browser})"))));

        if (outsideStore.Count > 0)
        {
            metrics.Add(new Metric("browser.extensions.outside_store", "Extensions a program installed for you",
                outsideStore.Count, HealthCategory.Security, InfoOnly: true,
                Detail: "These were added by a program on this PC rather than by you from the browser's store. " +
                        "Download managers, antivirus tools and password managers all do this legitimately — but " +
                        "so does adware, so the question worth asking is whether you installed the program behind " +
                        "each one on purpose: " +
                        string.Join("; ", outsideStore.Select(e => $"{e.Name} ({e.Browser})"))));
        }

        // Two extensions can carry the same display name — a store listing published to both
        // the Chrome and Edge catalogues, for instance. Without the id the report shows two
        // identical lines and reads like a bug in the app rather than what it is.
        foreach (var extension in active.OrderBy(ExtensionRisk.SortKey).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var profile = extension.Profile.Equals("Default", StringComparison.OrdinalIgnoreCase)
                ? extension.Browser
                : $"{extension.Browser} / {extension.Profile}";

            metrics.Add(new Metric("browser.extension", extension.Name,
                ExtensionRisk.DescribeReach(extension.Reach()), HealthCategory.Security,
                Subject: $"{profile} — {extension.Name} [{ShortId(extension.Id)}]", InfoOnly: true,
                Detail: ExtensionRisk.Describe(extension)));
        }

        // Two extensions doing the same job — two ad blockers especially — fight over every
        // request, which shows up as pages that load slowly or break for no obvious reason.
        foreach (var duplicate in active
                     .GroupBy(e => e.Browser + "\0" + e.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var first = duplicate.First();
            notes.Add($"{first.Browser} has {ChromiumExtensionReader.Count(duplicate.Count())} separate extensions " +
                      $"all called \"{first.Name}\" switched on at once " +
                      $"({string.Join(", ", duplicate.Select(e => ShortId(e.Id)))}). Two copies of the same tool " +
                      "compete over every page and are a common cause of pages loading slowly or breaking — " +
                      "keeping one is usually better.");
        }

        return Task.FromResult(failures.Resolve());
    }

    /// <summary>Enough of the extension id to tell two same-named entries apart and to look
    /// one up in the store, without a 32-character wall of letters in every line.</summary>
    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;
}
