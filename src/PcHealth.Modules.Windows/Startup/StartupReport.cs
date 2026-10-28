using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;
using State = PcHealth.Modules.Windows.Support.AuthenticodeVerifier.SignatureState;

namespace PcHealth.Modules.Windows.Startup;

/// <summary>
/// Turns the startup list into the report's rows and notes.
///
/// Pure and separate for the same reason as <see cref="OtherAccountReport"/>: the sentence beside
/// a count must agree with what was actually looked at. The old wording said "Every resolved
/// startup item has a valid publisher signature" whenever nothing was unsigned, so it was written
/// beside a BROKEN signature too, and beside files whose signature could not be verified at all
/// (START-002). A healthy PC shows neither, so this needs tests rather than a careful reading.
///
/// The rules:
/// - only a signature that verified counts as signed;
/// - "could not verify" and "certificate expired" are each a Note naming the item, and are counted
///   neither as signed nor as unsigned;
/// - the verifier itself failing degrades the module, because that is a check that broke, not a
///   reading about a file;
/// - when no item could be checked at all, the counts are left out rather than shown as zero.
///
/// Paths go into the report as %VAR% tokens, never with the user's folder (<see cref="ReportPaths"/>).
/// </summary>
public static class StartupReport
{
    public static void Describe(IReadOnlyList<StartupItem> items, List<Metric> metrics, ModuleFailures failures)
    {
        var active = items.Where(i => !i.Disabled).ToList();
        var resolved = active.Where(i => i.ResolvedPath is not null).ToList();
        var unresolved = active.Count - resolved.Count;
        var unsigned = resolved.Where(i => i.Signature == State.NotSigned).ToList();
        var invalid = resolved.Where(i => i.Signature == State.Invalid).ToList();
        var expired = resolved.Where(i => i.Signature == State.Expired).ToList();
        var unverified = resolved.Where(i => i.Signature == State.Unknown).ToList();
        var answered = resolved.Count - unverified.Count;

        if (unresolved > 0) failures.Note($"{N(unresolved)} startup command(s) could not be resolved to a file; their signatures were not checked.");

        foreach (var item in expired)
        {
            failures.Note($"{Located(item)}: signed by {SignerOf(item)}, but the certificate has expired and no timestamp shows the " +
                          "file was signed while it was valid, so Windows no longer accepts the signature. Nothing says the file " +
                          "was changed. It is counted neither as signed nor as unsigned.");
        }

        foreach (var item in unverified)
        {
            failures.Note($"{Located(item)}: its signature could not be verified ({ReasonOf(item)}), so it is counted neither " +
                          "as signed nor as unsigned.");
        }

        var verifierFailures = unverified
            .Select(i => i.Verification?.Failure).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
        if (verifierFailures.Count > 0)
        {
            failures.Record("Startup signature check", string.Join("; ", verifierFailures) +
                                                       ", so some startup items' signatures were NOT checked");
        }

        metrics.Add(new Metric("startup.count", "Programs starting with Windows", active.Count, HealthCategory.Security,
            InfoOnly: true,
            Detail: string.Join("; ", active.Take(15).Select(NameAndSigner)) + (active.Count > 15 ? " …" : "")));

        if (active.Count > 0 && answered == 0)
        {
            failures.Note($"No startup item's signature could be checked ({N(active.Count)} item(s)), so the signature counts " +
                          "are left out rather than shown as zero.");
            return;
        }

        var notCounted = new List<string>();
        if (invalid.Count > 0) notCounted.Add($"{N(invalid.Count)} with a BROKEN signature (see that row)");
        if (expired.Count > 0) notCounted.Add($"{N(expired.Count)} whose certificate has expired");
        if (unverified.Count > 0) notCounted.Add($"{N(unverified.Count)} that could not be verified");
        var notCountedText = notCounted.Count == 0
            ? ""
            : " Not counted here: " + string.Join(", ", notCounted) +
              (expired.Count + unverified.Count > 0 ? "; the notes name each one." : ".");
        var unresolvedText = unresolved == 0
            ? ""
            : $" {N(unresolved)} startup command(s) could not be resolved to a file and were not checked.";

        metrics.Add(new Metric("startup.unsigned_count", "Startup items without a publisher signature", unsigned.Count,
            HealthCategory.Security,
            Detail: unsigned.Count > 0
                ? "Unsigned: " + string.Join("; ", unsigned.Take(12).Select(Located)) + (unsigned.Count > 12 ? " …" : "") +
                  " Unsigned is not proof of malware — but check that you recognize each one." + notCountedText + unresolvedText
                : active.Count == 0
                    ? "No startup items were found, so there was nothing to verify."
                    : notCounted.Count == 0
                        ? "Every resolved startup item has a valid publisher signature." + unresolvedText
                        : "No startup item is unsigned, which is not the same as every one having a valid signature." +
                          notCountedText + unresolvedText));

        metrics.Add(new Metric("startup.invalid_sig_count", "Startup items with a BROKEN signature", invalid.Count,
            HealthCategory.Security,
            Detail: invalid.Count == 0
                ? "No tampered/invalid signatures." + (unverified.Count == 0
                    ? ""
                    : $" {N(unverified.Count)} item(s) could not be verified and are not counted here; the notes name each one.")
                : "Invalid signature (possible tampering): " + string.Join("; ", invalid.Select(Located))));
    }

    /// <summary>
    /// The Winlogon row: Shell and Userinit, a classic persistence point, where anything beyond the
    /// defaults is suspicious. Values as read from a key that opened; absent or empty means Windows
    /// falls back to explorer.exe and userinit.exe, which is standard.
    ///
    /// A non-default value is quoted with every path in it as a token. It was quoted as found, so
    /// a program planted under the user's folder put the user name into the report most likely to
    /// be sent to IT (PRIV-002, review of 2026-10-28).
    /// </summary>
    public static Metric Winlogon(string? shellValue, string? userinitValue)
    {
        var shell = shellValue?.Trim();
        var userinit = userinitValue?.Trim().TrimEnd(',');
        var shellOk = string.IsNullOrEmpty(shell) || shell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
        var userinitOk = string.IsNullOrEmpty(userinit)
            || userinit.EndsWith(@"\userinit.exe", StringComparison.OrdinalIgnoreCase)
            || userinit.Equals("userinit.exe", StringComparison.OrdinalIgnoreCase)
            || userinit.EndsWith(@"\userinit.exe""", StringComparison.OrdinalIgnoreCase);
        return new Metric("startup.winlogon_default", "Winlogon shell is standard", shellOk && userinitOk,
            HealthCategory.Security,
            Detail: shellOk && userinitOk
                ? "Shell and Userinit have their default values."
                : $"Non-default Winlogon values — Shell: '{ReportPaths.TokenizeWithin(shell ?? "")}', " +
                  $"Userinit: '{ReportPaths.TokenizeWithin(userinit ?? "")}'. This is a known malware persistence point.");
    }

    /// <summary>The name, with the publisher when the signature verified. A broken or expired
    /// signature's subject is a claim, not a publisher, so it is not shown here.</summary>
    private static string NameAndSigner(StartupItem item) =>
        item.Signature == State.SignedAndValid && item.Signer is { } signer
            ? $"{item.Name} ({signer.Display})"
            : item.Name;

    private static string Located(StartupItem item) =>
        item.ResolvedPath is null ? item.Name : $"{item.Name} ({ReportPaths.Tokenize(item.ResolvedPath)})";

    private static string SignerOf(StartupItem item) => item.Signer?.Display is { Length: > 0 } name ? name : "an unnamed publisher";

    private static string ReasonOf(StartupItem item) =>
        item.Verification?.Failure
        ?? "Windows answered 0x" + (item.Verification?.Status ?? 0).ToString("X8", CultureInfo.InvariantCulture);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
