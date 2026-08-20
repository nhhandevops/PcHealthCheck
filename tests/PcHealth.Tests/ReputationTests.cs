using PcHealth.Net.Optional.VirusTotal;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Covers the reputation feature from recorded responses — no network, no API key.
///
/// Two properties matter more than the parsing: a file VirusTotal has never seen must never
/// read as clean, and the consent dialog must list every fingerprint that would leave the
/// machine, including saying what it left out.
/// </summary>
public class ReputationTests
{
    private const string Sha = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f";

    private static string StatsBody(int malicious, int suspicious, int harmless, int undetected, string? name = null)
    {
        var meaningful = name is null ? "" : $"\"meaningful_name\": \"{name}\",";
        return $$"""
            { "data": { "id": "{{Sha}}", "type": "file", "attributes": {
                {{meaningful}}
                "last_analysis_stats": {
                    "malicious": {{malicious}}, "suspicious": {{suspicious}},
                    "harmless": {{harmless}}, "undetected": {{undetected}},
                    "timeout": 0, "failure": 0, "type-unsupported": 0 } } } }
            """;
    }

    [Fact]
    public void A_file_no_engine_objects_to_is_clean_but_not_guaranteed()
    {
        var result = VirusTotalResponseReader.Read(Sha, "steam.exe", 200, StatsBody(0, 0, 8, 62));

        Assert.Equal(ReputationVerdict.Clean, result.Verdict);
        Assert.Equal(70, result.TotalEngines);
        Assert.Contains("not proof", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_detections_are_called_malicious_with_something_to_do()
    {
        var result = VirusTotalResponseReader.Read(Sha, "svch0st.exe", 200, StatsBody(58, 2, 0, 10));

        Assert.Equal(ReputationVerdict.Malicious, result.Verdict);
        Assert.Equal(58, result.MaliciousEngines);
        Assert.Contains("Windows Security", result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    [InlineData(0, 3)]
    public void A_handful_of_detections_is_flagged_without_calling_it_malware(int malicious, int suspicious)
    {
        // Small utilities and game mods routinely pick up a couple of detections. Calling
        // that "malware" would train the user to ignore the app the day it is right.
        var result = VirusTotalResponseReader.Read(Sha, "fan-control.exe", 200, StatsBody(malicious, suspicious, 5, 60));

        Assert.Equal(ReputationVerdict.Suspicious, result.Verdict);
        Assert.Contains("false positive", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_file_is_never_reported_as_clean()
    {
        var result = VirusTotalResponseReader.Read(Sha, "mytool.exe", 404, null);

        Assert.Equal(ReputationVerdict.NotKnown, result.Verdict);
        Assert.Contains("NOT the same as clean", result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, "rejected the API key")]
    [InlineData(429, "free-tier limit")]
    [InlineData(503, "nothing is known")]
    public void A_failed_lookup_says_nothing_is_known(int status, string expected)
    {
        var result = VirusTotalResponseReader.Read(Sha, "mytool.exe", status, null);

        Assert.Equal(ReputationVerdict.LookupFailed, result.Verdict);
        Assert.Contains(expected, result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>gateway timeout</html>")]
    [InlineData("{ \"data\": { \"attributes\": {} } }")]
    public void A_response_that_cannot_be_read_degrades_to_unknown_rather_than_throwing(string body)
    {
        var result = VirusTotalResponseReader.Read(Sha, "mytool.exe", 200, body);

        Assert.Equal(ReputationVerdict.LookupFailed, result.Verdict);
    }

    [Fact]
    public void The_name_virustotal_knows_the_file_by_is_surfaced_when_it_differs()
    {
        var result = VirusTotalResponseReader.Read(Sha, "updater", 200, StatsBody(0, 0, 8, 62, "AcmeUpdate.exe"));

        Assert.Contains("AcmeUpdate.exe", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Least_trusted_files_are_looked_up_first()
    {
        var (selected, _) = ReputationSelection.Choose(
        [
            Candidate("signed-app", "aaa", SignatureTrust.Verified),
            Candidate("no-signature", "bbb", SignatureTrust.NoSignature),
            Candidate("tampered", "ccc", SignatureTrust.BrokenSignature),
            Candidate("unresolved", "ddd", SignatureTrust.Unknown),
        ]);

        Assert.Equal(["tampered", "no-signature", "unresolved", "signed-app"], selected.Select(c => c.Name));
    }

    [Fact]
    public void The_same_binary_under_two_startup_entries_costs_one_lookup()
    {
        var (selected, skipped) = ReputationSelection.Choose(
        [
            Candidate("Launcher", "aaa", SignatureTrust.Verified),
            Candidate("Launcher (again)", "AAA", SignatureTrust.Verified),
        ]);

        Assert.Single(selected);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Files_without_a_hash_are_left_out_entirely()
    {
        var (selected, _) = ReputationSelection.Choose(
            [Candidate("unreadable", "", SignatureTrust.Unknown), Candidate("fine", "aaa", SignatureTrust.Verified)]);

        Assert.Equal("fine", Assert.Single(selected).Name);
    }

    [Fact]
    public void The_cap_is_stated_rather_than_silently_applied()
    {
        var many = Enumerable.Range(0, 25)
            .Select(i => Candidate($"app{i}", $"hash{i}", SignatureTrust.NoSignature))
            .ToList();

        var (selected, skipped) = ReputationSelection.Choose(many);
        var consent = ReputationSelection.DescribeForConsent(selected, skipped);

        Assert.Equal(ReputationSelection.MaxPerRun, selected.Count);
        Assert.Equal(5, skipped);
        Assert.Contains("5 further startup item(s) will NOT be looked up", consent, StringComparison.Ordinal);
        Assert.Contains("not the same as them being clean", consent, StringComparison.Ordinal);
    }

    [Fact]
    public void Consent_shows_the_fingerprints_that_would_actually_be_sent()
    {
        var (selected, skipped) = ReputationSelection.Choose([Candidate("mytool.exe", Sha, SignatureTrust.NoSignature)]);
        var consent = ReputationSelection.DescribeForConsent(selected, skipped);

        Assert.Contains(Sha[..16], consent, StringComparison.Ordinal);
        Assert.Contains("mytool.exe", consent, StringComparison.Ordinal);
        Assert.Contains("no publisher signature", consent, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT be looked up", consent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_leads_with_what_needs_doing()
    {
        var summary = ReputationSummary.Describe(
        [
            Clean("steam.exe"),
            new FileReputation("c", "dropper.exe", ReputationVerdict.Malicious, 58, 2, 70, ""),
            new FileReputation("d", "odd-tool.exe", ReputationVerdict.Suspicious, 2, 0, 70, ""),
        ]);

        Assert.StartsWith("ACT NOW", summary, StringComparison.Ordinal);
        Assert.True(summary.IndexOf("dropper.exe", StringComparison.Ordinal)
                    < summary.IndexOf("odd-tool.exe", StringComparison.Ordinal));
        Assert.Contains("1 file(s) came back clean", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Lookups_that_failed_are_counted_out_loud()
    {
        var summary = ReputationSummary.Describe(
            [Clean("a.exe"), new FileReputation("b", "b.exe", ReputationVerdict.LookupFailed, 0, 0, 0, "")]);

        Assert.Contains("1 lookup(s) did not complete", summary, StringComparison.Ordinal);
        Assert.Contains("not checked at all", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_clean_still_refuses_to_promise_safety()
    {
        var summary = ReputationSummary.Describe([Clean("a.exe"), Clean("b.exe")]);

        Assert.Contains("not proof", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ACT NOW", summary, StringComparison.Ordinal);
    }

    private static ReputationCandidate Candidate(string name, string hash, SignatureTrust trust) =>
        new(name, $@"C:\Program Files\{name}", hash, trust);

    private static FileReputation Clean(string name) =>
        new("hash-" + name, name, ReputationVerdict.Clean, 0, 0, 70, "");
}
