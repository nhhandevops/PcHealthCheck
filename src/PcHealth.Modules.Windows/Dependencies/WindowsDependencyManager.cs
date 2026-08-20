using System.ComponentModel;
using System.Diagnostics;
using PcHealth.Core.Dependencies;
using PcHealth.Modules.Windows.Sensors;

namespace PcHealth.Modules.Windows.Dependencies;

/// <summary>
/// One optional tool the app can install (with consent) to read more of the machine.
/// The display command is shown to the user verbatim before anything runs.
/// </summary>
public sealed record DependencyDefinition(
    string Id,
    string Name,
    string Purpose,
    string InstallCommandDisplay,
    string InstallFileName,
    string InstallArguments,
    string UninstallCommandDisplay,
    string UninstallFileName,
    string UninstallArguments,
    string ReinstallHint,
    string[] KnownConsumerProcesses,
    Func<bool> IsInstalled);

public static class DependencyCatalog
{
    /// <summary>
    /// The signed, open-source low-level driver LibreHardwareMonitor uses for CPU and
    /// motherboard sensors. Also used by FanControl and OpenRGB — which is exactly why
    /// removal must check for them first.
    /// </summary>
    public static DependencyDefinition PawnIo { get; } = new(
        Id: "pawnio",
        Name: "PawnIO sensor driver",
        Purpose: "Lets the Temperatures & fans check read CPU temperature, motherboard sensors and case-fan speeds. " +
                 "Without it, only graphics card and memory readings are available.",
        InstallCommandDisplay: "winget install namazso.PawnIO",
        InstallFileName: "winget",
        InstallArguments: "install namazso.PawnIO --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
        UninstallCommandDisplay: "winget uninstall namazso.PawnIO",
        UninstallFileName: "winget",
        UninstallArguments: "uninstall namazso.PawnIO --silent --disable-interactivity",
        ReinstallHint: "winget install namazso.PawnIO  (or download from pawnio.eu)",
        KnownConsumerProcesses: ["FanControl", "OpenRGB", "LibreHardwareMonitor"],
        IsInstalled: Ring0Driver.IsPawnIoInstalled);

    public static IReadOnlyList<DependencyDefinition> All { get; } = [PawnIo];
}

/// <summary>
/// Installs and removes optional dependencies — always with the user's explicit consent,
/// always showing the exact command first, and never removing anything that was on the
/// PC before this app installed it (the ledger is the proof of who installed what).
/// </summary>
public sealed class WindowsDependencyManager(DependencyLedger ledger)
{
    private const int UacDeclined = 1223;

    public DependencyLedger Ledger => ledger;

    /// <summary>Catalog entries not currently installed — candidates to offer.</summary>
    public IReadOnlyList<DependencyDefinition> GetMissing() =>
        [.. DependencyCatalog.All.Where(d => !SafeIsInstalled(d))];

    /// <summary>
    /// What the exit cleanup may offer to remove: installed by THIS app (per the ledger)
    /// and still present. Anything the user has since removed themselves is forgotten
    /// silently; anything not in the ledger was never ours to touch.
    /// </summary>
    public IReadOnlyList<(DependencyDefinition Definition, InstalledDependency Record)> GetRemovable()
    {
        var state = ledger.LoadInstalledByApp();
        var removable = new List<(DependencyDefinition, InstalledDependency)>();
        foreach (var definition in DependencyCatalog.All)
        {
            if (!state.TryGetValue(definition.Id, out var record)) continue;
            if (SafeIsInstalled(definition)) removable.Add((definition, record));
            else ledger.ForgetWithoutHistory(definition.Id);
        }
        return removable;
    }

    /// <summary>Known consumers of this dependency that are running right now.</summary>
    public IReadOnlyList<string> GetRunningConsumers(DependencyDefinition definition)
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process) { names.Add(process.ProcessName); }
        }
        return ConsumerCheck.FindRunning(definition.KnownConsumerProcesses, names);
    }

    public async Task<(bool Success, string Message)> InstallAsync(DependencyDefinition definition, CancellationToken ct = default)
    {
        var result = await RunElevatedAsync(definition.InstallFileName, definition.InstallArguments,
            TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (result.Declined)
            return (false, "Administrator permission was declined — nothing was installed.");
        if (result.NotFound)
            return (false, $"The {definition.InstallFileName} tool is not available on this PC. " +
                           $"You can install {definition.Name} manually instead: {definition.ReinstallHint}");
        if (!result.Success || !SafeIsInstalled(definition))
            return (false, $"Installation did not complete (exit code {result.ExitCode}). Nothing was recorded.");

        ledger.RecordInstalled(new InstalledDependency(
            definition.Id, definition.Name, DateTimeOffset.Now,
            definition.InstallCommandDisplay, definition.ReinstallHint));
        return (true, $"{definition.Name} installed. Run the scan again to include the new readings.");
    }

    public async Task<(bool Success, string Message)> UninstallAsync(
        DependencyDefinition definition, InstalledDependency record, CancellationToken ct = default)
    {
        var result = await RunElevatedAsync(definition.UninstallFileName, definition.UninstallArguments,
            TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (result.Declined)
            return (false, "Administrator permission was declined — nothing was removed.");
        if (!result.Success || SafeIsInstalled(definition))
            return (false, $"Removal did not complete (exit code {result.ExitCode}); {definition.Name} is still installed.");

        ledger.RecordRemoved(record);
        return (true, $"{definition.Name} removed. The reinstall command is noted in {ledger.HistoryPath} for 15 days.");
    }

    private sealed record ElevatedResult(bool Success, bool Declined, bool NotFound, int ExitCode);

    private static async Task<ElevatedResult> RunElevatedAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,   // required for the UAC (runas) verb
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == UacDeclined)
        {
            return new ElevatedResult(false, Declined: true, NotFound: false, -1);
        }
        catch (Win32Exception)
        {
            return new ElevatedResult(false, Declined: false, NotFound: true, -1);
        }

        if (process is null) return new ElevatedResult(false, false, true, -1);
        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
                return new ElevatedResult(false, false, false, -1);
            }
            return new ElevatedResult(process.ExitCode == 0, false, false, process.ExitCode);
        }
    }

    private static bool SafeIsInstalled(DependencyDefinition definition)
    {
        try { return definition.IsInstalled(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
