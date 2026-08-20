using System.Globalization;
using System.Text.Json;

namespace PcHealth.Net.Optional.VirusTotal;

public enum ReputationVerdict
{
    /// <summary>No engine objected. Still not a guarantee — new malware is unknown to everyone.</summary>
    Clean,

    /// <summary>A small number of engines objected. Often a false positive; worth a look, not a panic.</summary>
    Suspicious,

    /// <summary>Enough engines agree that this should be treated as malicious.</summary>
    Malicious,

    /// <summary>VirusTotal has never seen this file. NOT the same as clean.</summary>
    NotKnown,

    /// <summary>The lookup itself did not work, so nothing at all is known.</summary>
    LookupFailed,
}

/// <summary>What the reputation service says about one file, by hash.</summary>
public sealed record FileReputation(
    string Sha256,
    string DisplayName,
    ReputationVerdict Verdict,
    int MaliciousEngines,
    int SuspiciousEngines,
    int TotalEngines,
    string Summary);

/// <summary>
/// Turns a VirusTotal HTTP response into a verdict.
///
/// Pure and separate from the client so every branch — malicious, rate-limited, rejected
/// key, unknown file — is testable from a recorded response with no network and no API key.
///
/// The wording matters as much as the parsing. Two rules it must never break: a file
/// VirusTotal has never seen is NOT clean, and one or two detections out of seventy are
/// more often a false positive than malware.
/// </summary>
public static class VirusTotalResponseReader
{
    /// <summary>Below this many detections, say "check it" rather than "it is malware".</summary>
    private const int MaliciousThreshold = 5;

    public static FileReputation Read(string sha256, string displayName, int httpStatus, string? body)
    {
        switch (httpStatus)
        {
            case 200:
                return ReadStats(sha256, displayName, body);

            case 404:
                return Failed(sha256, displayName, ReputationVerdict.NotKnown,
                    "VirusTotal has never seen this file. That is NOT the same as clean — it usually means the " +
                    "file is rare, freshly built, or made just for this machine. For something that starts with " +
                    "Windows, rare is worth understanding.");

            case 401:
                return Failed(sha256, displayName, ReputationVerdict.LookupFailed,
                    "VirusTotal rejected the API key, so nothing was looked up. Check the key on your " +
                    "VirusTotal account page and try again.");

            case 429:
                return Failed(sha256, displayName, ReputationVerdict.LookupFailed,
                    "VirusTotal's free-tier limit was reached (4 lookups a minute, 500 a day), so this file was " +
                    "not checked. Nothing is known about it either way — try again later.");

            default:
                return Failed(sha256, displayName, ReputationVerdict.LookupFailed,
                    $"The lookup failed with HTTP status {httpStatus.ToString(CultureInfo.InvariantCulture)}, so " +
                    "nothing is known about this file. Treat that as unknown, not as clean.");
        }
    }

    private static FileReputation ReadStats(string sha256, string displayName, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Failed(sha256, displayName, ReputationVerdict.LookupFailed,
                "VirusTotal returned an empty response, so nothing is known about this file.");
        }

        int malicious, suspicious, total;
        string? knownName = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("attributes", out var attributes)
                || !attributes.TryGetProperty("last_analysis_stats", out var stats))
            {
                return Failed(sha256, displayName, ReputationVerdict.LookupFailed,
                    "VirusTotal's response did not contain analysis results, so nothing is known about this file.");
            }

            malicious = Count(stats, "malicious");
            suspicious = Count(stats, "suspicious");
            total = malicious + suspicious + Count(stats, "harmless") + Count(stats, "undetected")
                    + Count(stats, "timeout") + Count(stats, "failure") + Count(stats, "type-unsupported");

            if (attributes.TryGetProperty("meaningful_name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                knownName = name.GetString();
            }
        }
        catch (JsonException)
        {
            return Failed(sha256, displayName, ReputationVerdict.LookupFailed,
                "VirusTotal's response could not be read, so nothing is known about this file.");
        }

        var scanned = total == 0 ? "" : $" of {total.ToString(CultureInfo.InvariantCulture)}";
        var engineCount = total == 0 ? "the engines" : $"{total.ToString(CultureInfo.InvariantCulture)} engines";
        var alsoKnownAs = string.IsNullOrWhiteSpace(knownName) || knownName.Equals(displayName, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" VirusTotal knows this file as \"{knownName}\".";

        if (malicious >= MaliciousThreshold)
        {
            return new FileReputation(sha256, displayName, ReputationVerdict.Malicious, malicious, suspicious, total,
                $"{malicious.ToString(CultureInfo.InvariantCulture)}{scanned} antivirus engines call this malicious. " +
                "Do not dismiss this: disconnect from the internet, do not enter passwords, and have Windows " +
                "Security scan the file before doing anything else." + alsoKnownAs);
        }

        if (malicious > 0 || suspicious > 0)
        {
            var flagged = malicious + suspicious;
            return new FileReputation(sha256, displayName, ReputationVerdict.Suspicious, malicious, suspicious, total,
                $"{flagged.ToString(CultureInfo.InvariantCulture)}{scanned} engines flagged this file. A handful of " +
                "detections out of seventy is more often a false positive than real malware — especially for small " +
                "utilities and games. Worth checking that you installed it deliberately." + alsoKnownAs);
        }

        return new FileReputation(sha256, displayName, ReputationVerdict.Clean, 0, 0, total,
            $"None of {engineCount} objects to this file. That is reassuring, but it is not proof — brand-new " +
            "malware is unknown to every scanner on its first day." + alsoKnownAs);
    }

    private static int Count(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;

    private static FileReputation Failed(string sha256, string displayName, ReputationVerdict verdict, string summary) =>
        new(sha256, displayName, verdict, 0, 0, 0, summary);
}
