using PcHealth.Modules.Windows.Network;
using Xunit;

namespace PcHealth.Tests;

public class NetworkAnalysisTests
{
    [Fact]
    public void Hosts_parser_ignores_comments_and_blank_lines()
    {
        string[] lines =
        [
            "# Copyright (c) 1993-2009 Microsoft Corp.",
            "",
            "#\t127.0.0.1       localhost",
            "   ",
            "203.162.76.221 sdk.adx.vn",
        ];

        var entry = Assert.Single(NetworkAnalysis.ParseHostsEntries(lines));
        Assert.Equal("203.162.76.221", entry.Address);
        Assert.Equal("sdk.adx.vn", entry.Hostname);
    }

    [Fact]
    public void Hosts_parser_expands_multiple_names_on_one_line()
    {
        var entries = NetworkAnalysis.ParseHostsEntries(["0.0.0.0 ads.example.com tracker.example.com"]);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("0.0.0.0", e.Address));
        Assert.Contains(entries, e => e.Hostname == "tracker.example.com");
    }

    [Fact]
    public void Hosts_parser_strips_trailing_comments()
    {
        var entry = Assert.Single(NetworkAnalysis.ParseHostsEntries(["127.0.0.1 test.local # my dev box"]));
        Assert.Equal("test.local", entry.Hostname);
    }

    [Theory]
    [InlineData("www.update.microsoft.com")]
    [InlineData("definitionupdates.microsoft.com.windowsupdate.com")]
    [InlineData("go.microsoft.com.nsatc.net.defender.microsoft.com")]
    [InlineData("downloads.malwarebytes.com")]
    [InlineData("update.eset.com")]
    public void Security_domains_are_recognised(string hostname) =>
        Assert.True(NetworkAnalysis.IsSecurityRelatedHost(hostname));

    [Theory]
    [InlineData("sdk.adx.vn")]
    [InlineData("ads.example.com")]
    [InlineData("my-dev-box.local")]
    public void Ordinary_domains_are_not_treated_as_security_blocks(string hostname) =>
        Assert.False(NetworkAnalysis.IsSecurityRelatedHost(hostname));

    [Theory]
    [InlineData("VMware Virtual Ethernet Adapter for VMnet1")]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("Software Loopback Interface 1")]
    [InlineData("Bluetooth Device (Personal Area Network)")]
    public void Virtual_adapters_are_excluded_from_link_speed_checks(string description) =>
        Assert.True(NetworkAnalysis.IsVirtualAdapter(description));

    [Theory]
    [InlineData("Realtek Gaming 2.5GbE Family Controller")]
    [InlineData("Intel(R) Ethernet Connection I219-V")]
    public void Physical_adapters_are_not_excluded(string description) =>
        Assert.False(NetworkAnalysis.IsVirtualAdapter(description));

    [Theory]
    [InlineData("Intel(R) Wi-Fi 6 AX201 160MHz", "Wi-Fi")]
    [InlineData("Realtek 802.11ac Wireless Adapter", "WLAN")]
    public void Wireless_adapters_are_detected(string description, string name) =>
        Assert.True(NetworkAnalysis.IsWireless(description, name));

    [Fact]
    public void Wired_adapters_are_not_mistaken_for_wireless() =>
        Assert.False(NetworkAnalysis.IsWireless("Realtek Gaming 2.5GbE Family Controller", "Ethernet 2"));

    [Fact]
    public void Windows_site_local_dns_placeholders_are_ignored()
    {
        // Windows lists fec0:0:0:ffff::1-3 when no DNS is really configured; reporting
        // them as "servers that can redirect your traffic" would alarm people for nothing.
        Assert.False(NetworkAnalysis.IsRoutableDnsAddress("fec0:0:0:ffff::1"));
        Assert.False(NetworkAnalysis.IsRoutableDnsAddress(""));
        Assert.True(NetworkAnalysis.IsRoutableDnsAddress("192.168.1.1"));
        Assert.True(NetworkAnalysis.IsRoutableDnsAddress("1.1.1.1"));
    }

    [Fact]
    public void Known_dns_providers_are_named_and_unknown_ones_are_not_accused()
    {
        Assert.Contains("Cloudflare", NetworkAnalysis.DescribeKnownProviders(["1.1.1.1"]), StringComparison.Ordinal);
        Assert.Contains("Google", NetworkAnalysis.DescribeKnownProviders(["8.8.8.8", "8.8.4.4"]), StringComparison.Ordinal);

        var unknown = NetworkAnalysis.DescribeKnownProviders(["192.168.1.1"]);
        Assert.Contains("router", unknown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suspicious", unknown, StringComparison.OrdinalIgnoreCase);
    }
}
