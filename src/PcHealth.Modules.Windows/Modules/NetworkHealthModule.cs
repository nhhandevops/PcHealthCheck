using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Network;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Network configuration health, including the settings malware likes to change:
/// the hosts file (used to block antivirus and update servers), proxy redirection,
/// and which DNS servers resolve every name this PC looks up. Also catches the classic
/// bad-cable symptom of a gigabit port negotiating 100 Mbit.
/// </summary>
internal sealed class NetworkHealthModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "network.health",
        Name = "Network settings & integrity",
        Description = "Link speed of wired connections, plus the settings malware targets: hosts-file redirections, proxy hijacking, and configured DNS servers.",
        Reads = ["Network adapter link speed (WMI)", "Windows hosts file", "Proxy settings (registry)", "DNS configuration (WMI)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(4),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        AddLinkSpeeds(metrics, failures);
        AddHostsFileFindings(metrics, failures);
        AddProxyFindings(metrics, failures);
        AddDnsInformation(metrics, failures);

        // Four independent checks, any one of which can break while the others succeed. The old
        // "any metric at all means Completed" test let a failed adapter or DNS query hide behind
        // the hosts-file metrics that did come back — and in this module a silent gap reads as
        // "no sign of tampering here", which is the one thing it must never say without looking.
        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    private static void AddLinkSpeeds(List<Metric> metrics, ModuleFailures failures)
    {
        var adapters = Wmi.TryQuery(@"root\StandardCimv2",
            "SELECT Name, InterfaceDescription, Speed, MediaConnectState, HardwareInterface, Virtual, ConnectorPresent " +
            "FROM MSFT_NetAdapter", failures, "Network adapter query");

        foreach (var adapter in adapters)
        {
            // Only real, physical, connected adapters: virtual switches from VMware/Hyper-V
            // legitimately report 100 Mbit and would otherwise look like a fault.
            if (Wmi.Get<uint>(adapter, "MediaConnectState") is not 1) continue;
            if (Wmi.Get<bool>(adapter, "Virtual") == true) continue;
            if (Wmi.Get<bool>(adapter, "HardwareInterface") != true) continue;

            var description = Wmi.GetString(adapter, "InterfaceDescription") ?? "";
            if (NetworkAnalysis.IsVirtualAdapter(description)) continue;

            var name = Wmi.GetString(adapter, "Name") ?? description;
            if (Wmi.Get<ulong>(adapter, "Speed") is not { } bitsPerSecond || bitsPerSecond == 0) continue;
            var mbps = bitsPerSecond / 1_000_000.0;

            // Wireless speed varies with distance and interference; only wired links have a
            // "should be gigabit" expectation worth flagging.
            var wired = !NetworkAnalysis.IsWireless(description, name);
            metrics.Add(new Metric(wired ? "network.wired_link_mbps" : "network.wireless_link_mbps",
                wired ? "Wired link speed" : "Wi-Fi link rate", Math.Round(mbps),
                HealthCategory.SoftwareOs, Unit: "Mbps", Subject: name, InfoOnly: !wired,
                Detail: wired
                    ? "Almost all modern wired networking is gigabit (1000 Mbps) or faster. A wired link stuck at 100 Mbps is usually a damaged cable or a bad port."
                    : "Wi-Fi rates change with distance and interference; this is a snapshot, not a fault."));
        }
    }

    private static void AddHostsFileFindings(List<Metric> metrics, ModuleFailures failures)
    {
        var hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        string[] lines;
        try
        {
            if (!File.Exists(hostsPath))
            {
                // A file that is not there is an answer rather than a failed read — nothing can be
                // redirected by a hosts file that does not exist — so this stays a plain note and
                // does not fire the "some checks could not finish" banner.
                failures.Note("No hosts file found (unusual, but harmless).");
                return;
            }
            lines = File.ReadAllLines(hostsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Both metrics below assert an absence — "nothing is redirecting Windows Update or
            // antivirus" — which is exactly the claim an unread file cannot support. Suppressing
            // them is right, but staying silent about the one file malware edits most often is
            // not: the scan has to come back marked incomplete.
            failures.Record("Could not read the hosts file", e.Message);
            return;
        }

        var entries = NetworkAnalysis.ParseHostsEntries(lines);
        var blockedSecurity = entries.Where(e => NetworkAnalysis.IsSecurityRelatedHost(e.Hostname)).ToList();

        metrics.Add(new Metric("network.hosts_blocks_security", "Security sites blocked in hosts file",
            blockedSecurity.Count == 0, HealthCategory.Security,
            Detail: blockedSecurity.Count == 0
                ? "Nothing in the hosts file is redirecting Windows Update or antivirus services."
                : "The hosts file redirects: " + string.Join(", ", blockedSecurity.Select(e => e.Hostname).Take(8)) +
                  ". Malware does exactly this to stop Windows updating and antivirus from working."));

        metrics.Add(new Metric("network.hosts_custom_entries", "Custom hosts-file entries", entries.Count,
            HealthCategory.Security,
            Detail: entries.Count == 0
                ? "The hosts file has no custom entries."
                : "Entries: " + string.Join("; ", entries.Take(8).Select(e => $"{e.Hostname} → {e.Address}")) +
                  (entries.Count > 8 ? " …" : "") +
                  ". These override normal name lookups. Ad-blocking lists and developer test entries are common and harmless — but make sure you recognise each one."));
    }

    private static void AddProxyFindings(List<Metric> metrics, ModuleFailures failures)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null)
        {
            // A key that will not open reads back identically to a PC with no proxy set: every
            // value comes out null, and the metric below would then state "web traffic goes out
            // normally" about settings nobody managed to look at. Proxy hijacking is one of the
            // things this module is here to catch, so it says it did not check instead.
            failures.Record("Proxy settings",
                "the Internet Settings registry key could not be opened, so proxy redirection was NOT checked.");
            return;
        }

        var proxyEnabled = (key.GetValue("ProxyEnable") as int?) == 1;
        var proxyServer = key.GetValue("ProxyServer") as string;
        var autoConfig = key.GetValue("AutoConfigURL") as string;

        var redirected = proxyEnabled || !string.IsNullOrWhiteSpace(autoConfig);
        metrics.Add(new Metric("network.proxy_clean", "Web traffic not redirected", !redirected,
            HealthCategory.Security,
            Detail: redirected
                ? $"A proxy is configured{(proxyServer is null ? "" : $" ({proxyServer})")}" +
                  $"{(string.IsNullOrWhiteSpace(autoConfig) ? "" : $", auto-config script: {autoConfig}")}. " +
                  "This is normal on a work network or with some VPNs, but it also means every site you visit can be watched or altered — confirm you set it up."
                : "No proxy or auto-config script is set, so web traffic goes out normally."));
    }

    private static void AddDnsInformation(List<Metric> metrics, ModuleFailures failures)
    {
        var configured = Wmi.TryQuery(@"root\StandardCimv2",
            "SELECT InterfaceAlias, ServerAddresses FROM MSFT_DNSClientServerAddress", failures, "DNS query");

        var servers = new List<string>();
        foreach (var row in configured)
        {
            if (row.GetValueOrDefault("ServerAddresses") is not string[] addresses) continue;
            servers.AddRange(addresses.Where(NetworkAnalysis.IsRoutableDnsAddress));
        }

        var distinct = servers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) return;

        // Reported for review rather than graded: plenty of people deliberately use
        // Cloudflare or Google DNS, and guessing at "suspicious" here would cry wolf.
        metrics.Add(new Metric("network.dns_servers", "DNS servers in use", string.Join(", ", distinct),
            HealthCategory.Security, InfoOnly: true,
            Detail: "Whoever runs these servers sees and can redirect every website name this PC looks up. " +
                    NetworkAnalysis.DescribeKnownProviders(distinct)));
    }
}
