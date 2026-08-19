using System.Security.Cryptography;
using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Native startup-item audit: Run/RunOnce keys (both hives, incl. WOW6432Node),
/// Startup folders, and Winlogon shell values. Each resolved binary gets an OFFLINE
/// Authenticode check and a SHA-256 hash (which later feeds the opt-in VirusTotal module).
/// Reads program binaries only — never user documents.
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

    private sealed record StartupItem(string Name, string Source, string Command, string? ResolvedPath,
        AuthenticodeVerifier.SignatureState Signature, string? Sha256, bool Disabled);

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var items = new List<StartupItem>();
        var disabledMap = ReadStartupApprovedStates();

        foreach (var (hive, path, source) in RunKeyLocations())
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            foreach (var valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                var command = key.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(command)) continue;
                var disabled = disabledMap.TryGetValue(valueName, out var d) && d;
                items.Add(Inspect(valueName, source, command, disabled));
                ct.ThrowIfCancellationRequested();
            }
        }

        foreach (var (folder, source) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder (user)"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)"),
                 })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals("desktop", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file, notes) : file;
                items.Add(Inspect(name, source, target ?? file, disabled: false));
                ct.ThrowIfCancellationRequested();
            }
        }

        // Winlogon Shell/Userinit — classic persistence spot; anything beyond the defaults is suspicious.
        using (var winlogon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
        {
            var shell = winlogon?.GetValue("Shell")?.ToString()?.Trim();
            var userinit = winlogon?.GetValue("Userinit")?.ToString()?.Trim().TrimEnd(',');
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

        return Task.FromResult(ModuleStatus.Completed);
    }

    private static IEnumerable<(RegistryKey Hive, string Path, string Source)> RunKeyLocations()
    {
        const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string runOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        const string runWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        yield return (Registry.LocalMachine, run, "HKLM Run");
        yield return (Registry.LocalMachine, runOnce, "HKLM RunOnce");
        yield return (Registry.LocalMachine, runWow, "HKLM Run (32-bit)");
        yield return (Registry.CurrentUser, run, "HKCU Run");
        yield return (Registry.CurrentUser, runOnce, "HKCU RunOnce");
    }

    /// <summary>StartupApproved: byte[0] odd = disabled via Task Manager.</summary>
    private static Dictionary<string, bool> ReadStartupApprovedStates()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var sub in new[] { "Run", "Run32", "StartupFolder" })
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{sub}");
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is byte[] { Length: > 0 } bytes)
                    {
                        map[name] = (bytes[0] & 0x01) == 0x01;
                    }
                }
            }
        }
        return map;
    }

    private static StartupItem Inspect(string name, string source, string command, bool disabled)
    {
        var resolved = TryResolveExecutable(command);
        var signature = AuthenticodeVerifier.SignatureState.Unknown;
        string? hash = null;
        if (resolved is not null)
        {
            signature = AuthenticodeVerifier.Verify(resolved);
            try
            {
                using var stream = File.OpenRead(resolved);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new StartupItem(name, source, command, resolved, signature, hash, disabled);
    }

    private static string? TryResolveExecutable(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            candidate = end > 1 ? expanded[1..end] : expanded.Trim('"');
        }
        else
        {
            var exeIdx = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exeIdx > 0 ? expanded[..(exeIdx + 4)] : expanded.Split(' ')[0];
        }
        candidate = candidate.Trim();
        if (candidate.Length == 0) return null;
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        // Bare name — try PATH and System32 the way CreateProcess would.
        if (!candidate.Contains('\\'))
        {
            var withExe = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? candidate : candidate + ".exe";
            var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), withExe);
            if (File.Exists(system32)) return system32;
        }
        return null;
    }

    private static string? ResolveShortcut(string lnkPath, List<string> notes)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            object? shortcutObject = null;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                shortcutObject = shortcut;
                var target = (string?)shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                // Both COM objects must be released, or the scan leaks one RCW per shortcut.
                if (shortcutObject is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcutObject);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception e)
        {
            notes.Add($"Could not resolve shortcut {Path.GetFileName(lnkPath)}: {e.GetType().Name}");
            return null;
        }
    }
}
