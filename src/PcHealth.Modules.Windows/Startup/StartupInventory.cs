using System.Collections.Concurrent;
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
    bool Disabled)
{
    /// <summary>The verifier's whole answer: the signer, whether a catalog vouched for the file,
    /// and why a file could not be verified. Null when the file was not examined.</summary>
    public AuthenticodeVerifier.Result? Verification { get; init; }

    public AuthenticodeVerifier.Signer? Signer => Verification?.Signer;
}

/// <summary>
/// Enumerates what starts with Windows: Run/RunOnce in both hives (including the 32-bit
/// view), the policy Run keys, the two Startup folders, and each item's disabled state from
/// StartupApproved.
///
/// Each resolved binary gets an offline Authenticode check and a SHA-256 hash. Reads
/// program files only — never documents — and the hash is computed in this process; the
/// file's contents never go anywhere.
///
/// Shared by the startup check and the opt-in reputation lookup, so both look at exactly
/// the same list rather than two implementations that can drift apart.
///
/// Speed (2026-10-23): this was the slowest unelevated check, 8-17 s on a ThinkPad T470p,
/// because large programs such as a 99 MB Google Drive client cost 3 s each to verify and hash,
/// one after another. Files are now examined up to <see cref="ParallelFiles"/> at a time, each
/// distinct file once, and disabled items are listed without being examined: nothing reads
/// their signature or hash, since the startup check and the reputation lookup both skip them.
/// </summary>
public static class StartupInventory
{
    public const int ParallelFiles = 4;

    private sealed record Entry(string Name, string Source, string Command, bool Disabled);

    public static IReadOnlyList<StartupItem> Collect(List<string> notes, CancellationToken ct = default)
    {
        // Gathering the list is fast (registry and folder reads) and stays sequential; shortcut
        // resolution goes through COM. Examining the files is the slow part and runs in parallel.
        var entries = new List<Entry>();
        var approved = StartupApproved.Read();

        foreach (var (hive, path, source, approvedAt) in RunKeyLocations())
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            var hiveName = hive == Registry.LocalMachine ? "HKLM" : "HKCU";
            foreach (var valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                var command = key.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(command)) continue;
                var disabled = approvedAt is not null && approved.IsDisabled(hiveName, approvedAt, valueName);
                entries.Add(new Entry(valueName, source, command, disabled));
                ct.ThrowIfCancellationRequested();
            }
        }

        // A Startup folder item's switch is filed under the same hive's StartupFolder subkey, by
        // its file name WITH the extension ("VpnClient.lnk"); the report names it without one.
        foreach (var (folder, source, hiveName) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder (user)", "HKCU"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)", "HKLM"),
                 })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals("desktop", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file, notes) : file;
                var disabled = approved.IsDisabled(hiveName, StartupApproved.StartupFolder, Path.GetFileName(file));
                entries.Add(new Entry(name, source, target ?? file, disabled));
                ct.ThrowIfCancellationRequested();
            }
        }

        var examined = new ConcurrentDictionary<string, Lazy<(AuthenticodeVerifier.Result, string?)>>(StringComparer.OrdinalIgnoreCase);
        return entries
            .AsParallel().AsOrdered()
            .WithDegreeOfParallelism(ParallelFiles)
            .WithCancellation(ct)
            .Select(e => Inspect(e, examined))
            .ToList();
    }

    /// <summary>
    /// Where Run entries live, and the StartupApproved subkey that holds Task Manager's switch for
    /// each. RunOnce and policy entries have none: Task Manager does not list them, so they are
    /// never read as disabled.
    /// </summary>
    private static IEnumerable<(RegistryKey Hive, string Path, string Source, string? ApprovedAt)> RunKeyLocations()
    {
        const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string runOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        const string runWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string runOnceWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
        const string policyRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
        yield return (Registry.LocalMachine, run, "HKLM Run", StartupApproved.Run);
        yield return (Registry.LocalMachine, runOnce, "HKLM RunOnce", null);
        yield return (Registry.LocalMachine, runWow, "HKLM Run (32-bit)", StartupApproved.Run32);
        yield return (Registry.LocalMachine, runOnceWow, "HKLM RunOnce (32-bit)", null);
        yield return (Registry.LocalMachine, policyRun, "HKLM Run (policy)", null);
        yield return (Registry.CurrentUser, run, "HKCU Run", StartupApproved.Run);
        yield return (Registry.CurrentUser, runOnce, "HKCU RunOnce", null);
        yield return (Registry.CurrentUser, policyRun, "HKCU Run (policy)", null);
    }

    private static StartupItem Inspect(
        Entry entry, ConcurrentDictionary<string, Lazy<(AuthenticodeVerifier.Result, string?)>> examined)
    {
        var resolved = TryResolveExecutable(entry.Command);
        if (resolved is null || entry.Disabled)
            return new StartupItem(entry.Name, entry.Source, entry.Command, resolved, AuthenticodeVerifier.SignatureState.Unknown, null, entry.Disabled);

        // Lazy so two entries pointing at one file (two "Uninstall" entries both running cmd.exe)
        // examine it once even when they are handled at the same moment.
        var (verification, hash) = examined.GetOrAdd(resolved, path => new(() => Examine(path))).Value;
        return new StartupItem(entry.Name, entry.Source, entry.Command, resolved, verification.State, hash, entry.Disabled)
        {
            Verification = verification,
        };
    }

    private static (AuthenticodeVerifier.Result, string?) Examine(string path)
    {
        var verification = AuthenticodeVerifier.Verify(path);
        string? hash = null;
        try
        {
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (verification, hash);
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
