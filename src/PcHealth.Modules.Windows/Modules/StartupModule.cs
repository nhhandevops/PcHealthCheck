using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Startup;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Native startup-item audit: Run/RunOnce keys (both hives, incl. WOW6432Node),
/// Startup folders, and Winlogon shell values. Each resolved binary gets an OFFLINE
/// Authenticode check and a SHA-256 hash (which the opt-in reputation lookup can use).
/// Reads program binaries only — never user documents.
///
/// The enumeration itself lives in <see cref="StartupInventory"/> so the reputation
/// lookup examines exactly the same list this check grades.
/// </summary>
internal sealed class StartupModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "startup",
        Name = "Startup programs",
        Description = "What starts with Windows, whether each item's publisher signature verifies (checked offline), and Winlogon integrity.",
        Reads = ["Run/RunOnce registry keys", "Startup folders", "Winlogon shell values", "Startup program files (signature + hash only)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(10),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);
        var items = StartupInventory.Collect(notes, ct);

        // Winlogon Shell/Userinit — classic persistence spot; anything beyond the defaults is suspicious.
        using (var winlogon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
        {
            if (winlogon is null)
            {
                // Both values come back null when the key cannot be opened, and IsNullOrEmpty(null)
                // is true for both, so the old code graded an unreadable key as "Shell and Userinit
                // have their default values": the security check for a known malware persistence
                // point asserting clean from a read that returned nothing. No metric is emitted
                // here, because unchecked is not the same as checked and standard.
                failures.Record("Winlogon values",
                    "the registry key could not be opened, so the Shell/Userinit persistence point was NOT checked");
            }
            else
            {
                // A value that is absent or empty under a key we DID open is a genuine reading —
                // Windows falls back to explorer.exe/userinit.exe — which is why the IsNullOrEmpty
                // cases below still count as standard. That is the case an unopenable key above
                // must never be confused with.
                var shell = winlogon.GetValue("Shell")?.ToString()?.Trim();
                var userinit = winlogon.GetValue("Userinit")?.ToString()?.Trim().TrimEnd(',');
                var shellOk = string.IsNullOrEmpty(shell) || shell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
                var userinitOk = string.IsNullOrEmpty(userinit)
                    || userinit.EndsWith(@"\userinit.exe", StringComparison.OrdinalIgnoreCase)
                    || userinit.Equals("userinit.exe", StringComparison.OrdinalIgnoreCase)
                    || userinit.EndsWith(@"\userinit.exe""", StringComparison.OrdinalIgnoreCase);
                metrics.Add(new Metric("startup.winlogon_default", "Winlogon shell is standard", shellOk && userinitOk,
                    HealthCategory.Security,
                    Detail: shellOk && userinitOk
                        ? "Shell and Userinit have their default values."
                        : $"Non-default Winlogon values — Shell: '{shell}', Userinit: '{userinit}'. This is a known malware persistence point."));
            }
        }

        var active = items.Where(i => !i.Disabled).ToList();
        var unsigned = active.Where(i => i.Signature == AuthenticodeVerifier.SignatureState.NotSigned && i.ResolvedPath is not null).ToList();
        var invalid = active.Where(i => i.Signature == AuthenticodeVerifier.SignatureState.Invalid).ToList();
        var unresolved = active.Count(i => i.ResolvedPath is null);
        if (unresolved > 0) notes.Add($"{unresolved} startup command(s) could not be resolved to a file; their signatures were not checked.");

        metrics.Add(new Metric("startup.count", "Programs starting with Windows", active.Count, HealthCategory.Security,
            InfoOnly: true,
            Detail: string.Join("; ", active.Take(15).Select(i => i.Name)) + (active.Count > 15 ? " …" : "")));

        metrics.Add(new Metric("startup.unsigned_count", "Startup items without a publisher signature", unsigned.Count,
            HealthCategory.Security,
            Detail: unsigned.Count == 0
                ? "Every resolved startup item has a valid publisher signature."
                : "Unsigned: " + string.Join("; ", unsigned.Take(12).Select(i => $"{i.Name} ({i.ResolvedPath})")) +
                  (unsigned.Count > 12 ? " …" : "") + " Unsigned is not proof of malware — but check that you recognize each one."));

        metrics.Add(new Metric("startup.invalid_sig_count", "Startup items with a BROKEN signature", invalid.Count,
            HealthCategory.Security,
            Detail: invalid.Count == 0
                ? "No tampered/invalid signatures."
                : "Invalid signature (possible tampering): " + string.Join("; ", invalid.Select(i => $"{i.Name} ({i.ResolvedPath})"))));

        return Task.FromResult(failures.Resolve());
    }
}
