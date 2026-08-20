using System.Globalization;

namespace PcHealth.Net.Optional.VirusTotal;

/// <summary>How much the file's own publisher signature already tells us.</summary>
public enum SignatureTrust
{
    /// <summary>Signed, but the signature does not verify — tampering, or a broken download.</summary>
    BrokenSignature,

    /// <summary>No publisher signature at all.</summary>
    NoSignature,

    /// <summary>The file could not be checked (path not resolved, unreadable).</summary>
    Unknown,

    /// <summary>Signed, and the signature verifies.</summary>
    Verified,
}

/// <summary>One file that could be looked up: what it is, and what is already known about it.</summary>
public sealed record ReputationCandidate(string Name, string Path, string Sha256, SignatureTrust Trust);

/// <summary>
/// Chooses which files are worth spending lookups on, and writes the sentence the consent
/// dialog shows.
///
/// This is the privacy-critical decision in the whole feature: each lookup tells VirusTotal
/// that this machine holds that exact file. So the list is deliberately short, ordered by
/// how much the answer would actually add, and shown in full before anything is sent.
///
/// The free tier allows four lookups a minute, which is the real reason for a cap — but a
/// cap that hides what it dropped would let the user believe everything was checked, so the
/// count of what was left out is part of the output, not a footnote.
/// </summary>
public static class ReputationSelection
{
    /// <summary>Twenty files at four a minute is about five minutes — long enough already.</summary>
    public const int MaxPerRun = 20;

    public static (IReadOnlyList<ReputationCandidate> Selected, int Skipped) Choose(
        IEnumerable<ReputationCandidate> candidates, int max = MaxPerRun)
    {
        // The same binary can appear under several startup entries; one lookup answers for
        // all of them, and sending the hash twice would leak nothing new but waste the quota.
        var unique = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Sha256))
            .GroupBy(c => c.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var ordered = unique
            .OrderBy(c => c.Trust switch
            {
                SignatureTrust.BrokenSignature => 0,
                SignatureTrust.NoSignature => 1,
                SignatureTrust.Unknown => 2,
                _ => 3,
            })
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selected = ordered.Take(max).ToList();
        return (selected, ordered.Count - selected.Count);
    }

    /// <summary>The exact list of hashes that would leave this PC, for the consent dialog.</summary>
    public static string DescribeForConsent(IReadOnlyList<ReputationCandidate> selected, int skipped)
    {
        if (selected.Count == 0)
        {
            return "There is nothing to look up: no startup item resolved to a file this app could hash.";
        }

        var lines = selected.Select(c => $"{Shorten(c.Sha256)}  {c.Name}  ({DescribeTrust(c.Trust)})");

        var tail = skipped == 0
            ? ""
            : $"\n\n{skipped.ToString(CultureInfo.InvariantCulture)} further startup item(s) will NOT be looked up: " +
              "VirusTotal's free tier allows four lookups a minute, so the list is capped at " +
              $"{MaxPerRun.ToString(CultureInfo.InvariantCulture)}. Those files were not checked — which is not the " +
              "same as them being clean.";

        return $"{selected.Count.ToString(CultureInfo.InvariantCulture)} file fingerprint(s) will be sent, " +
               "least-trusted first:\n\n" + string.Join("\n", lines) + tail;
    }

    /// <summary>
    /// Enough of the fingerprint to recognise, without a wall of hex. Guarded on length
    /// because a consent dialog must never be the thing that crashes.
    /// </summary>
    private static string Shorten(string sha256) =>
        sha256.Length > 16 ? sha256[..16].ToLowerInvariant() + "…" : sha256.ToLowerInvariant();

    private static string DescribeTrust(SignatureTrust trust) => trust switch
    {
        SignatureTrust.BrokenSignature => "signature does not verify",
        SignatureTrust.NoSignature => "no publisher signature",
        SignatureTrust.Unknown => "signature unknown",
        _ => "signed",
    };
}
