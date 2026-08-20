using System.Security.Cryptography;
using Microsoft.Win32;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Startup;

/// <summary>One program set to run when Windows starts, and what is known about its file.</summary>
public sealed record StartupItem(
    string Name,
    string Source,
    string Command,
    string? ResolvedPath,
    AuthenticodeVerifier.SignatureState Signature,
    string? Sha256,
    bool Disabled);

/// <summary>
/// Enumerates what starts with Windows: Run/RunOnce in both hives (including the 32-bit
/// view), the two Startup folders, and each item's disabled state from StartupApproved.
///
/// Each resolved binary gets an offline Authenticode check and a SHA-256 hash. Reads
/// program files only — never documents — and the hash is computed in this process; the
/// file's contents never go anywhere.
///
/// Shared by the startup check and the opt-in reputation lookup, so both look at exactly
/// the same list rather than two implementations that can drift apart.
/// </summary>
public static class StartupInventory
{
    public static IReadOnlyList<StartupItem> Collect(List<string> notes, CancellationToken ct = default)
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

        return items;
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
