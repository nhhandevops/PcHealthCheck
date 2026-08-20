namespace PcHealth.Modules.Windows.Network;

public sealed record HostsEntry(string Address, string Hostname);

/// <summary>
/// Parsing and classification for the network checks, kept free of WMI so it can be
/// tested directly — these are the judgements that decide whether a user is told
/// something is wrong, so they need to be exercised properly.
/// </summary>
public static class NetworkAnalysis
{
    /// <summary>
    /// Domains whose redirection is a red flag rather than a preference: blocking these
    /// is how malware stops Windows updating and antivirus from working or updating.
    /// </summary>
    private static readonly string[] SecurityRelatedMarkers =
    [
        "windowsupdate", "update.microsoft", "defender", "msftncsi", "msftconnecttest",
        "sophos", "mcafee", "norton", "symantec", "kaspersky", "avast", "avg.com",
        "bitdefender", "malwarebytes", "eset", "trendmicro", "avira", "virustotal",
        "clamav", "sucuri", "f-secure", "drweb",
    ];

    private static readonly Dictionary<string, string> KnownDnsProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1.1.1.1"] = "Cloudflare",
        ["1.0.0.1"] = "Cloudflare",
        ["8.8.8.8"] = "Google",
        ["8.8.4.4"] = "Google",
        ["9.9.9.9"] = "Quad9",
        ["149.112.112.112"] = "Quad9",
        ["208.67.222.222"] = "OpenDNS",
        ["208.67.220.220"] = "OpenDNS",
    };

    /// <summary>
    /// Real entries from a hosts file: comments and blank lines dropped, and each address
    /// may map to several names on one line.
    /// </summary>
    public static IReadOnlyList<HostsEntry> ParseHostsEntries(IEnumerable<string> lines)
    {
        var entries = new List<HostsEntry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // Strip trailing comments: "127.0.0.1 example.com # note"
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            foreach (var hostname in parts.Skip(1))
            {
                entries.Add(new HostsEntry(parts[0], hostname));
            }
        }
        return entries;
    }

    public static bool IsSecurityRelatedHost(string hostname) =>
        SecurityRelatedMarkers.Any(marker => hostname.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static bool IsWireless(string description, string name) =>
        description.Contains("wi-fi", StringComparison.OrdinalIgnoreCase)
        || description.Contains("wireless", StringComparison.OrdinalIgnoreCase)
        || description.Contains("802.11", StringComparison.OrdinalIgnoreCase)
        || name.Contains("wi-fi", StringComparison.OrdinalIgnoreCase)
        || name.Contains("wireless", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Virtual switches from VMware, Hyper-V, VirtualBox and VPN clients report low link
    /// speeds by design; treating them as faulty cabling would be a permanent false alarm.
    /// </summary>
    public static bool IsVirtualAdapter(string description)
    {
        string[] markers =
        [
            "virtual", "vmware", "hyper-v", "virtualbox", "vethernet", "loopback",
            "tap-", "tun", "wireguard", "openvpn", "zerotier", "tailscale", "bluetooth",
        ];
        return markers.Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Skips the site-local IPv6 placeholders Windows lists when no DNS is actually
    /// configured (fec0:0:0:ffff::1-3), which are not real servers.
    /// </summary>
    public static bool IsRoutableDnsAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        return !address.StartsWith("fec0:", StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeKnownProviders(IEnumerable<string> addresses)
    {
        var recognised = addresses
            .Where(KnownDnsProviders.ContainsKey)
            .Select(a => KnownDnsProviders[a])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recognised.Count > 0)
            return $"Recognised public provider(s): {string.Join(", ", recognised)}.";

        return "These are not well-known public providers — usually that means your router or " +
               "internet provider, which is normal. Investigate only if you did not expect it.";
    }
}
