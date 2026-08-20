namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>
/// Finds a command-line tool the user may have installed, without assuming how.
///
/// winget installs portable packages under <c>%LOCALAPPDATA%\Microsoft\WinGet\Packages\</c>
/// and usually — but not always — drops a shim in the <c>Links</c> folder that is on PATH.
/// A user may equally have unzipped the tool themselves. Searching all of those, in the
/// cheapest order, is the difference between "benchmark unavailable" and it just working.
/// </summary>
public static class ToolLocator
{
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

    private static string? SearchWingetPackages(string packagesRoot, string prefix, string exeName)
    {
        try
        {
            if (!Directory.Exists(packagesRoot)) return null;
            foreach (var package in Directory.EnumerateDirectories(packagesRoot, prefix + "*"))
            {
                // Portable packages unpack into a version- or architecture-named subfolder,
                // so the exe is rarely at the top level; the tree is small enough to walk.
                var match = Directory.EnumerateFiles(package, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null) return match;
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
