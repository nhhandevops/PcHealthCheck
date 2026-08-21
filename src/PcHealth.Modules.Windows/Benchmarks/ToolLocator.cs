using System.Runtime.InteropServices;

namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>
/// Finds a command-line tool the user may have installed, without assuming how.
///
/// winget installs portable packages under <c>%LOCALAPPDATA%\Microsoft\WinGet\Packages\</c>
/// and *sometimes* drops a shim in the <c>Links</c> folder that is on PATH — DiskSpd 2.2
/// reports adding one and does not, so the package folder is the path that actually
/// matters. A user may equally have unzipped the tool themselves. Searching all of those,
/// in the cheapest order, is the difference between "benchmark unavailable" and it just
/// working.
/// </summary>
public static class ToolLocator
{
    /// <summary>
    /// Directory names that mean "this build is for a particular processor". Portable
    /// packages ship one folder per architecture side by side, so a plain recursive search
    /// picks whichever the file system happens to return first.
    /// </summary>
    private static readonly Dictionary<Architecture, string[]> ArchitectureFolders = new()
    {
        [Architecture.X64] = ["amd64", "x64", "x86_64", "win-x64"],
        [Architecture.Arm64] = ["arm64", "aarch64", "win-arm64"],
        [Architecture.X86] = ["x86", "i386", "win-x86"],
    };

    public static string? Find(string exeName, string wingetPackagePrefix, params string[] extraDirectories)
    {
        foreach (var directory in extraDirectories)
        {
            var candidate = SafeCombine(directory, exeName);
            if (candidate is not null && File.Exists(candidate)) return candidate;
        }

        var wingetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");

        var link = SafeCombine(Path.Combine(wingetRoot, "Links"), exeName);
        if (link is not null && File.Exists(link)) return link;

        var found = SearchWingetPackages(Path.Combine(wingetRoot, "Packages"), wingetPackagePrefix, exeName);
        if (found is not null) return found;

        return FindOnPath(exeName);
    }

    /// <summary>
    /// Chooses the build that will actually run here.
    ///
    /// Returns null rather than guessing when every candidate is for another processor: a
    /// tool that fails with an opaque Windows error is worse than a check that says plainly
    /// it could not find the tool.
    /// </summary>
    public static string? PickBuildFor(IEnumerable<string> candidatePaths, Architecture architecture)
    {
        var candidates = candidatePaths.ToList();
        if (candidates.Count == 0) return null;

        var wanted = ArchitectureFolders.GetValueOrDefault(architecture, []);
        var match = candidates.FirstOrDefault(path => Segments(path).Any(
            segment => wanted.Contains(segment, StringComparer.OrdinalIgnoreCase)));
        if (match is not null) return match;

        // No architecture folders at all means the package ships a single build for
        // everyone, which is the common case for small tools.
        var allKnown = ArchitectureFolders.Values.SelectMany(names => names).ToArray();
        return candidates.FirstOrDefault(path => !Segments(path).Any(
            segment => allKnown.Contains(segment, StringComparer.OrdinalIgnoreCase)));
    }

    private static string[] Segments(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static string? SearchWingetPackages(string packagesRoot, string prefix, string exeName)
    {
        try
        {
            if (!Directory.Exists(packagesRoot)) return null;
            foreach (var package in Directory.EnumerateDirectories(packagesRoot, prefix + "*"))
            {
                // Portable packages unpack into a version- or architecture-named subfolder,
                // so the exe is rarely at the top level; the tree is small enough to walk.
                var matches = Directory.EnumerateFiles(package, exeName, SearchOption.AllDirectories).ToList();
                if (matches.Count == 0) continue;
                if (PickBuildFor(matches, RuntimeInformation.ProcessArchitecture) is { } chosen) return chosen;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A tool we cannot find is a feature that stays unavailable, never a crash.
        }
        return null;
    }

    private static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = SafeCombine(directory.Trim('"'), exeName);
            if (candidate is not null && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>PATH entries are user-editable and routinely contain junk that would make
    /// Path.Combine throw.</summary>
    private static string? SafeCombine(string directory, string fileName)
    {
        try { return Path.Combine(directory, fileName); }
        catch (ArgumentException) { return null; }
    }
}
