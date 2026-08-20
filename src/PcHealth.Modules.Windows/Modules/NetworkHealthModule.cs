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
        AddLinkSpeeds(metrics, notes);
        AddHostsFileFindings(metrics, notes);
        AddProxyFindings(metrics);
        AddDnsInformation(metrics, notes);
        return Task.FromResult(metrics.Count > 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted);
    }

    private static void AddLinkSpeeds(List<Metric> metrics, List<string> notes)
    {
        var adapters = Wmi.TryQuery(@"root\StandardCimv2",
            "SELECT Name, InterfaceDescription, Speed, MediaConnectState, HardwareInterface, Virtual, ConnectorPresent " +
            "FROM MSFT_NetAdapter", out var error);
        if (error is not null)
        {
            notes.Add($"Network adapter query: {error}");
            return;
        }

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

    private static void AddHostsFileFindings(List<Metric> metrics, List<string> notes)
    {
        var hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        string[] lines;
        try
        {
            if (!File.Exists(hostsPath))
            {
                notes.Add("No hosts file found (unusual, but harmless).");
                return;
            }
            lines = File.ReadAllLines(hostsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not read the hosts file: {e.Message}");
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

    private static void AddProxyFindings(List<Metric> metrics)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var proxyEnabled = (key?.GetValue("ProxyEnable") as int?) == 1;
        var proxyServer = key?.GetValue("ProxyServer") as string;
        var autoConfig = key?.GetValue("AutoConfigURL") as string;

        var redirected = proxyEnabled || !string.IsNullOrWhiteSpace(autoConfig);
        metrics.Add(new Metric("network.proxy_clean", "Web traffic not redirected", !redirected,
            HealthCategory.Security,
            Detail: redirected
                ? $"A proxy is configured{(proxyServer is null ? "" : $" ({proxyServer})")}" +
                  $"{(string.IsNullOrWhiteSpace(autoConfig) ? "" : $", auto-config script: {autoConfig}")}. " +
                  "This is normal on a work network or with some VPNs, but it also means every site you visit can be watched or altered — confirm you set it up."
                : "No proxy or auto-config script is set, so web traffic goes out normally."));
    }

    private static void AddDnsInformation(List<Metric> metrics, List<string> notes)
    {
        var configured = Wmi.TryQuery(@"root\StandardCimv2",
            "SELECT InterfaceAlias, ServerAddresses FROM MSFT_DNSClientServerAddress", out var error);
        if (error is not null)
        {
            notes.Add($"DNS query: {error}");
            return;
        }

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
