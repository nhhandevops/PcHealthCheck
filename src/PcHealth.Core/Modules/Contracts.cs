using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PcHealth.Core.Metrics;
using PcHealth.Core.Processes;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Modules;

/// <summary>
/// One health check. Everything the app audits is a module behind this interface,
/// registered per-OS via <see cref="IModuleProvider"/>.
/// </summary>
public interface IHealthModule
{
    ModuleManifest Manifest { get; }

    Task<ModuleResult> RunAsync(ModuleContext context, IProgress<ModuleProgress>? progress, CancellationToken ct);
}

/// <summary>Registers the modules available on the current OS.</summary>
public interface IModuleProvider
{
    IReadOnlyList<IHealthModule> CreateModules();
}

/// <summary>
/// Exactly what a module writes, shown verbatim on its consent row.
/// A module whose <see cref="ModuleManifest.Writes"/> is null is strictly read-only.
/// </summary>
public sealed record WriteSpec(string PathDescription, long MaxBytes, string Lifetime);

/// <summary>
/// Capability metadata for one module. The consent screen renders one row per manifest;
/// <see cref="CapabilityHash"/> changes whenever the declared capabilities change, which
/// forces a re-consent (v2 of a module that gains a write can never ride v1's consent).
/// </summary>
public sealed record ModuleManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Human-readable list of what the module reads, for the consent row.</summary>
    public required string[] Reads { get; init; }

    public bool NeedsAdmin { get; init; }
    public WriteSpec? Writes { get; init; }
    public bool NeedsInternet { get; init; }

    /// <summary>
    /// True when the module needs a kernel-level driver to read its data. This is the one
    /// thing the app can do that genuinely changes the system, so it gets its own badge,
    /// its own line in the capability hash, and its own confirmation from the elevated
    /// helper — it must never hide behind a generic "needs admin" flag.
    /// </summary>
    public bool LoadsDriver { get; init; }

    public TimeSpan EstimatedDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True when the module must have the machine to itself, because it measures speed or
    /// temperature and other checks running beside it would become part of what it reports.
    /// The orchestrator runs these one at a time, after everything else. It describes how the
    /// module is scheduled, not what it is allowed to do, so it is not part of the capability hash.
    /// </summary>
    public bool RunsAlone { get; init; }

    /// <summary>
    /// True when the module draws its conclusion from what the other checks found
    /// (<see cref="ModuleContext.EarlierFindings"/>), the elevated helper's checks included, so it
    /// must start after they have all finished. The orchestrator runs it with the
    /// <see cref="RunsAlone"/> modules, one at a time, after everything else. Like that flag it is
    /// scheduling only, not part of the capability hash.
    /// </summary>
    public bool RunsAfterOthers { get; init; }

    private string? _capabilityHash;

    /// <summary>SHA-256 over the capability descriptor (not the display strings).</summary>
    public string CapabilityHash => _capabilityHash ??= ComputeCapabilityHash();

    private string ComputeCapabilityHash()
    {
        // Invariant formatting throughout: the hash decides whether stored consent still
        // applies, so it must be identical on every machine and in every display language.
        var descriptor = string.Join('|',
            Id,
            NeedsAdmin ? "admin" : "no-admin",
            NeedsInternet ? "internet" : "no-internet",
            LoadsDriver ? "driver" : "no-driver",
            Writes?.PathDescription ?? "-",
            Writes?.MaxBytes.ToString(CultureInfo.InvariantCulture) ?? "-",
            Writes?.Lifetime ?? "-");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>Services handed to a running module. All external process launches go
/// through <see cref="Processes"/> so the post-scan receipt is complete.</summary>
public sealed class ModuleContext
{
    public required IProcessRunner Processes { get; init; }
    public required ReceiptLog Receipt { get; init; }

    /// <summary>App-owned temp directory (the only place modules may write, e.g. the battery report XML).</summary>
    public required string AppTempDir { get; init; }

    public bool IsElevated { get; init; }
    public DateTimeOffset ScanStartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// The Windows account (its SID) the scan was started from, set only in the elevated helper,
    /// which the unelevated window tells. The helper runs as whoever gave administrator rights, and
    /// when a technician types an administrator's password on an employee's laptop that is the
    /// technician, while the window and its checks run as the employee. Null in the process that
    /// started the scan, whose own account it is.
    /// </summary>
    public string? StartedBySid { get; init; }

    /// <summary>
    /// What every other check found, graded, the elevated helper's checks included. The
    /// orchestrator sets it once, after those checks have finished and before the checks marked
    /// <see cref="ModuleManifest.RunsAlone"/> or <see cref="ModuleManifest.RunsAfterOthers"/>
    /// start, so it is empty for a check that runs beside others. It exists so a check that
    /// exercises hardware can ask the app's own findings first: the disk benchmark once wrote to a
    /// drive the same scan called failing (REMEDIES-PLAN H2). The file-backup check also uses it,
    /// for the shadow copies only the elevated helper can count (REMEDIES-PLAN H1).
    /// </summary>
    public IReadOnlyList<Verdict> EarlierFindings { get; set; } = [];
}

public sealed record ModuleProgress(string Message, double? Fraction = null);

public enum ModuleStatus
{
    Completed,
    PartiallyCompleted,
    Failed,
    Skipped,
}

public sealed record ModuleResult
{
    public required string ModuleId { get; init; }
    public required ModuleStatus Status { get; init; }
    public IReadOnlyList<Metric> Metrics { get; init; } = [];

    /// <summary>Human-readable notes surfaced in the report (e.g. "RAC data collection is disabled").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }
}
