using System.Globalization;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// One verdict Windows' own compatibility appraiser stored for an upgrade target, from
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators\&lt;target&gt;.
/// </summary>
public sealed record UpgradeAssessment(
    string Target, int DestinationBuild, string UpgradeExperience, string? RedReason, string? GatedBlockReason, DateTimeOffset? AssessedAt);

/// <summary>What the report says about moving this PC to Windows 11.</summary>
/// <param name="Ready">True or false from Windows' verdict; null when the verdict is one this app does not interpret.</param>
/// <param name="BlockerCodes">Windows' own reason codes, verbatim ("CpuFms").</param>
/// <param name="Blockers">The same reasons in words.</param>
public sealed record Win11Verdict(
    bool? Ready, UpgradeAssessment Assessment, IReadOnlyList<string> BlockerCodes, IReadOnlyList<string> Blockers)
{
    /// <summary>A processor block is the one reason no setting can fix.</summary>
    public bool ProcessorBlocked => BlockerCodes.Any(c => c.StartsWith("Cpu", StringComparison.OrdinalIgnoreCase) ||
                                                          Win11Readiness.IsInstructionSetCode(c));

    /// <summary>
    /// Whether at least one reason is a Windows 11 hardware requirement this app knows. Only then
    /// does a Red verdict on Windows 11 itself mean "hardware Microsoft does not support".
    /// </summary>
    public bool RequirementBlocked => BlockerCodes.Any(Win11Readiness.IsRequirementCode);
}

/// <summary>
/// Windows 11 readiness, read from the verdict Windows itself keeps rather than from a processor
/// list this app would have to maintain. Windows 10 runs the Microsoft Compatibility Appraiser on
/// a schedule and stores one verdict per upgrade target: "Green" (no blocker) or "Red" with
/// reason codes. It is what Windows Update uses to decide whether to offer Windows 11, and it is
/// readable without administrator rights. Verified on 2026-10-23 on a ThinkPad T470p
/// (i7-7820HQ): Windows 11 targets Red, RedReason "CpuFms", Windows 10 target Green.
/// </summary>
public static class Win11Readiness
{
    public const int FirstWindows11Build = 22000;

    /// <summary>
    /// The reason codes in words. Only CpuFms has been seen on a real machine (2026-10-23); the
    /// others follow the requirement they are named after. An unknown code is shown verbatim
    /// rather than guessed at.
    /// </summary>
    private static readonly Dictionary<string, string> ReasonText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CpuFms"] = "the processor is not on Microsoft's supported list for Windows 11",
        ["CpuCores"] = "the processor has fewer than 2 cores",
        ["CpuFreq"] = "the processor is slower than 1 GHz",
        ["Tpm"] = "no TPM 2.0 security chip is turned on",
        ["TpmVersion"] = "the TPM security chip is older than version 2.0",
        ["UefiSecureBoot"] = "the firmware is not set up for UEFI with Secure Boot",
        ["SecureBoot"] = "Secure Boot is not available",
        ["Uefi"] = "the firmware is not UEFI",
        ["SystemDriveSize"] = "the system drive is smaller than 64 GB",
        ["Memory"] = "the PC has less than 4 GB of memory",
        ["Ram"] = "the PC has less than 4 GB of memory",
        ["PopCnt"] = "the processor lacks an instruction Windows 11 24H2 and later need (POPCNT)",
        ["Sse42"] = "the processor lacks instructions Windows 11 24H2 and later need (SSE4.2)",
        ["Sse4.2"] = "the processor lacks instructions Windows 11 24H2 and later need (SSE4.2)",
        ["DX12"] = "the graphics does not support DirectX 12",
        ["Wddm"] = "the graphics driver is older than WDDM 2.0",
    };

    /// <summary>Every code in the table names a Windows 11 hardware or system requirement.</summary>
    internal static bool IsRequirementCode(string code) => ReasonText.ContainsKey(code);

    internal static bool IsInstructionSetCode(string code) =>
        code.Equals("PopCnt", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Sse", StringComparison.OrdinalIgnoreCase);

    private static readonly char[] CodeSeparators = [' ', ',', ';', '|'];

    /// <summary>
    /// A registry value as text. RedReason is a REG_MULTI_SZ, which .NET returns as a string[]:
    /// Convert.ToString turned it into "System.String[]" on the first live run (2026-10-23), and
    /// the report then gave TPM advice to a PC blocked by its processor. PowerShell had hidden this
    /// by printing the array's items.
    /// </summary>
    public static string? RegistryText(object? value) => value switch
    {
        null => null,
        string text => text,
        string[] parts => string.Join(' ', parts),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// One target's verdict from its registry values (names compared without case). Null when the
    /// key holds no verdict, as the "UNV" key does.
    /// </summary>
    public static UpgradeAssessment? FromValues(string target, IReadOnlyDictionary<string, string?> values)
    {
        string? Get(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        if (Get("UpgEx") is not { } verdict ||
            !int.TryParse(Get("DestBuildNum"), NumberStyles.None, CultureInfo.InvariantCulture, out var build))
            return null;

        DateTimeOffset? assessed = null;
        if (long.TryParse(Get("TimestampEpochString"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch > 0)
            assessed = DateTimeOffset.FromUnixTimeSeconds(epoch);
        else if (long.TryParse(Get("Timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out var fileTime) && fileTime > 0)
            assessed = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));

        static string? NoneIsNull(string? v) => v is null || v.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : v;
        return new UpgradeAssessment(target, build, verdict, NoneIsNull(Get("RedReason")), NoneIsNull(Get("GatedBlockReason")), assessed);
    }

    /// <summary>
    /// Whether a verdict was assessed before this Windows was installed, so it may describe another
    /// PC: a restored disk image brings the verdict of the machine it was made on, and the appraiser
    /// may not have run on this one yet. Such a verdict is not graded (review round, 2026-10-28).
    ///
    /// Setup writes the install date on the PC itself, late in its first starts: on the dev T470p,
    /// clean-installed from Microsoft's own generalized image, it is 2026-10-19 00:27, after that
    /// install's first boots on 2026-10-18 and 2026-10-19. A Sysprep-generalized image goes through
    /// the same specialize and OOBE passes, so it very likely gets a new date too; no Microsoft page
    /// says so. An image that was not generalized keeps its old date, and then this cannot tell.
    /// False when either date is missing, or the install date is after the clock (one of them is wrong).
    /// </summary>
    public static bool PredatesInstall(UpgradeAssessment assessment, DateTime? installedUtc, DateTime clockUtc) =>
        assessment.AssessedAt is { } assessed && installedUtc is { } installed && installed <= clockUtc &&
        assessed.UtcDateTime < installed;

    /// <summary>
    /// The verdict for the newest Windows 11 release Windows has assessed, or null when it has
    /// assessed none (the appraiser never ran, or company policy turns it off). Windows 10 targets
    /// are ignored: being able to install the next Windows 10 update says nothing about 11.
    /// </summary>
    public static Win11Verdict? Assess(IEnumerable<UpgradeAssessment> assessments)
    {
        var newest = assessments
            .Where(a => a.DestinationBuild >= FirstWindows11Build)
            .OrderByDescending(a => a.DestinationBuild)
            .ThenBy(a => a.Target, StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest is null) return null;

        var codes = newest.RedReason?.Split(CodeSeparators, StringSplitOptions.RemoveEmptyEntries) ?? [];
        bool? ready = newest.UpgradeExperience.ToUpperInvariant() switch
        {
            "GREEN" => true,
            "RED" => false,
            _ => null,
        };
        var words = codes.Select(c => ReasonText.TryGetValue(c, out var text) ? text : $"'{c}' (Windows' own code, not explained by this app)").ToList();
        if (ready == false && words.Count == 0) words.Add("Windows reports a block without naming it");
        return new Win11Verdict(ready, newest, codes, words);
    }
}
