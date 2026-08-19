using System.Security.Cryptography;
using System.Text;
using PcHealth.Core.Metrics;
using PcHealth.Core.Processes;

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
    public TimeSpan EstimatedDuration { get; init; } = TimeSpan.FromSeconds(5);

    private string? _capabilityHash;

    /// <summary>SHA-256 over the capability descriptor (not the display strings).</summary>
    public string CapabilityHash => _capabilityHash ??= ComputeCapabilityHash();

    private string ComputeCapabilityHash()
    {
        var descriptor = string.Join('|',
            Id,
            NeedsAdmin,
            NeedsInternet,
            Writes?.PathDescription ?? "-",
            Writes?.MaxBytes.ToString() ?? "-",
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
