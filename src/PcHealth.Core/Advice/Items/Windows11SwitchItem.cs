namespace PcHealth.Core.Advice.Items;

/// <summary>
/// O1: Windows 11 is blocked only by a firmware setting someone can switch on (REMEDIES-PLAN O1,
/// STEP-B-SPEC 5). It is written for IT. It speaks only when every code in Windows' own verdict
/// is a firmware one, the kit's rule (<c>audit-common.ps1</c>): a processor block cannot be fixed by
/// any setting (decision 9), and an unknown code is not guessed at, so either keeps it silent. The
/// row's own tips explain those.
///
/// The verdict's date is in its Detail and is never parsed, so the card always says to check again.
/// Windows checks about once a day, so a scan run just after IT switched a setting on can read the
/// setting as on while the verdict still names it. Its line then says so, from the platform check's
/// own reading, rather than telling IT to switch on what this report shows is on.
/// </summary>
public sealed class Windows11SwitchItem : IAdviceItem
{
    public const string ItemId = "O1";

    private const string Windows10Ready = "os.win11_ready", Windows11Supported = "os.win11_hardware_supported",
        Blockers = "os.win11_blockers";

    /// <summary>The codes a firmware setting can clear, with the sentence for each (Win11Readiness's reason codes).</summary>
    private static readonly Dictionary<string, string> FirmwareCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tpm"] = "advice.o1.blocker.tpm",
        ["TpmVersion"] = "advice.o1.blocker.tpmversion",
        ["UefiSecureBoot"] = "advice.o1.blocker.uefisecureboot",
        ["SecureBoot"] = "advice.o1.blocker.secureboot",
        ["Uefi"] = "advice.o1.blocker.uefi",
    };

    /// <summary>Switching these on stops a Windows installed in legacy BIOS mode from starting.</summary>
    private static readonly HashSet<string> BootModeCodes = new(StringComparer.OrdinalIgnoreCase) { "Uefi", "UefiSecureBoot", "SecureBoot" };

    /// <summary>
    /// The codes the platform check can show are cleared since Windows last checked. "Tpm" is "no TPM
    /// 2.0 security chip is turned on" and "TpmVersion" "older than 2.0" (Win11Readiness): a TPM read
    /// as ready whose version is 2.x clears both. Secure Boot read as on clears its two codes. Legacy
    /// BIOS mode has no reading of its own, so "Uefi" keeps its line.
    /// </summary>
    private static readonly HashSet<string> TpmCodes = new(StringComparer.OrdinalIgnoreCase) { "Tpm", "TpmVersion" };

    private static readonly HashSet<string> SecureBootCodes = new(StringComparer.OrdinalIgnoreCase) { "UefiSecureBoot", "SecureBoot" };

    private const string TpmReady = "security.tpm_ready", TpmVersion = "security.tpm_version", SecureBoot = "security.secure_boot";

    public string Id => ItemId;

    public string Area => "windows";

    public string Os => "windows";

    public int Rank => 50;

    public IReadOnlyList<string> Needs { get; } = ["sys.overview"];

    public IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier)
    {
        var verdict = facts.Problems(Windows10Ready).Concat(facts.Problems(Windows11Supported)).FirstOrDefault();
        if (verdict is null) return [];

        // The blocker row is information only: its codes narrow the trigger, the graded verdict makes it.
        var codes = (facts.First(Blockers)?.Metric.Value.Text ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(c => !string.Equals(c, "none", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0 || !codes.All(FirmwareCodes.ContainsKey)) return [];

        var tpmOn = facts.Flag(TpmReady) == true &&
                    facts.First(TpmVersion)?.Metric.Value.Text?.Trim().StartsWith("2.", StringComparison.Ordinal) == true;
        var secureBootOn = facts.Flag(SecureBoot) == true;
        string KeyFor(string code) =>
            tpmOn && TpmCodes.Contains(code) ? "advice.o1.now-on.tpm"
            : secureBootOn && SecureBootCodes.Contains(code) ? "advice.o1.now-on.secureboot"
            : FirmwareCodes[code];

        var card = new AdviceCard(ItemId, ItemId, Area, AdviceUrgency.Advice, Rank).Title("advice.o1.title").Because(verdict);
        foreach (var key in codes.Select(KeyFor).Distinct(StringComparer.Ordinal)) card.Line(AdviceLineRole.Finding, key);
        if (verdict.Metric.Id == Windows11Supported) card.Line(AdviceLineRole.Finding, "advice.o1.windows11");
        card.Line(AdviceLineRole.Finding, "advice.o1.ready-after");
        card.Line(AdviceLineRole.ForIt, "advice.o1.for-it.bitlocker");
        if (codes.Exists(BootModeCodes.Contains)) card.Line(AdviceLineRole.ForIt, "advice.o1.for-it.mbr2gpt");
        card.Line(AdviceLineRole.Do, "advice.o1.do.check-again");
        return [card.Build()];
    }
}
