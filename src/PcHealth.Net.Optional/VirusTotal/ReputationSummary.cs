using System.Globalization;

namespace PcHealth.Net.Optional.VirusTotal;

/// <summary>
/// The one-paragraph answer to "so what?" after a batch of lookups.
///
/// Ordered by what the user has to do: anything malicious first, then what to look at, and
/// only then the reassurance. Files that could not be checked are counted out loud — the
/// same rule the rest of the app follows, because a lookup that failed is not a pass.
/// </summary>
public static class ReputationSummary
{
    public static string Describe(IReadOnlyList<FileReputation> results)
    {
        if (results.Count == 0) return "Nothing was looked up.";

        var malicious = results.Where(r => r.Verdict == ReputationVerdict.Malicious).ToList();
        var suspicious = results.Where(r => r.Verdict == ReputationVerdict.Suspicious).ToList();
        var unknown = results.Where(r => r.Verdict == ReputationVerdict.NotKnown).ToList();
        var failed = results.Where(r => r.Verdict == ReputationVerdict.LookupFailed).ToList();
        var clean = results.Count(r => r.Verdict == ReputationVerdict.Clean);

        var parts = new List<string>();

        if (malicious.Count > 0)
        {
            parts.Add($"ACT NOW — {Names(malicious)} " +
                      (malicious.Count == 1 ? "is" : "are") +
                      " flagged as malicious by multiple antivirus engines. Do not start " +
                      (malicious.Count == 1 ? "it" : "them") + " again; have Windows Security scan the file.");
        }

        if (suspicious.Count > 0)
        {
            parts.Add($"WORTH A LOOK — {Names(suspicious)} " +
                      (suspicious.Count == 1 ? "was" : "were") +
                      " flagged by a few engines. That is often a false positive, so the question to ask is " +
                      "whether you installed it deliberately.");
        }

        if (unknown.Count > 0)
        {
            parts.Add($"{Count(unknown.Count)} file(s) are unknown to VirusTotal: {Names(unknown)}. " +
                      "Unknown is not clean — it means rare, new, or built for this machine.");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{Count(failed.Count)} lookup(s) did not complete, so those files were not checked at all.");
        }

        if (clean > 0)
        {
            parts.Add($"{Count(clean)} file(s) came back clean. Reassuring, but not proof — brand-new malware is " +
                      "unknown to every scanner on its first day.");
        }

        return string.Join(" ", parts);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Names(List<FileReputation> results)
    {
        var names = results.Take(5).Select(r => r.DisplayName).ToList();
        var text = string.Join(", ", names);
        return results.Count > names.Count
            ? $"{text} and {Count(results.Count - names.Count)} more"
            : text;
    }
}
