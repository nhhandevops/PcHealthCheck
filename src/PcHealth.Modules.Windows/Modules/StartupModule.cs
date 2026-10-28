using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Startup;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Native startup-item audit: Run/RunOnce keys (both hives, incl. WOW6432Node), the policy
/// Run keys, Startup folders, and Winlogon shell values. Each resolved binary gets an OFFLINE
/// Authenticode check, falling back to Windows' signature catalogs for Windows' own files,
/// and a SHA-256 hash (which the opt-in reputation lookup can use). Reads program binaries
/// only — never user documents.
///
/// The enumeration itself lives in <see cref="StartupInventory"/> so the reputation
/// lookup examines exactly the same list this check grades, and the wording lives in
/// <see cref="StartupReport"/> so it can be tested in both directions.
/// </summary>
internal sealed class StartupModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "startup",
        Name = "Startup programs",
        Description = "What starts with Windows, who signed each item and whether that signature verifies (checked offline), and Winlogon integrity.",
        Reads = ["Run/RunOnce and policy Run registry keys", "Startup folders", "Task Manager's startup on/off switches",
                 "Winlogon shell values", "Startup program files (signature, signer name + hash only)",
                 "Windows' signature catalogs, for Windows' own files"],
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
                // Windows falls back to explorer.exe/userinit.exe — which is why StartupReport.Winlogon
                // still counts the IsNullOrEmpty cases as standard. That is the case an unopenable
                // key above must never be confused with.
                metrics.Add(StartupReport.Winlogon(winlogon.GetValue("Shell")?.ToString(), winlogon.GetValue("Userinit")?.ToString()));
            }
        }

        StartupReport.Describe(items, metrics, failures);

        return Task.FromResult(failures.Resolve());
    }
}
