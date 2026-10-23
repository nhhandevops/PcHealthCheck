namespace PcHealth.Modules.Linux.Support;

/// <summary>
/// Reads the kernel's files under a root that is "/" on a real machine and a fixture folder in
/// tests, so the modules themselves run unchanged against both. Every read is the only kind of
/// access these modules make: no writes, no programs, no network.
/// </summary>
internal sealed class SystemFiles(string root)
{
    public string Root { get; } = root;

    public string PathOf(string path) => Path.Combine(Root, path.TrimStart('/'));

    /// <summary>
    /// The text of one file, or null with the reason. Some sysfs attributes refuse to be read
    /// (an empty battery slot answers ENODEV, buggy firmware EIO); .NET surfaces those as
    /// <see cref="IOException"/>, and they mean "not available", never a crash.
    /// </summary>
    public string? TryRead(string path, out string? error)
    {
        try
        {
            error = null;
            return File.ReadAllText(PathOf(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {e.Message}";
            return null;
        }
    }

    public string? TryRead(string path) => TryRead(path, out _);

    /// <summary>
    /// The names of the directories inside <paramref name="path"/>, sorted, or null with the
    /// reason. Entries under /sys/class are symbolic links to directories, so each entry is
    /// tested with <see cref="Directory.Exists"/>, which follows links, instead of trusting the
    /// enumeration's own idea of what a directory is.
    /// </summary>
    public List<string>? TryListDirectories(string path, out string? error)
    {
        try
        {
            error = null;
            var full = PathOf(path);
            var names = Directory.EnumerateFileSystemEntries(full)
                .Where(Directory.Exists)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToList();
            names.Sort(StringComparer.Ordinal);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {e.Message}";
            return null;
        }
    }

    /// <summary>The names of the files in <paramref name="path"/> matching <paramref name="pattern"/>, or none.</summary>
    public List<string> ListFiles(string path, string pattern)
    {
        try
        {
            var names = Directory.EnumerateFiles(PathOf(path), pattern)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToList();
            names.Sort(StringComparer.Ordinal);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The last part of a symbolic link's target ("nvme0" for hwmon's "device" link), or null
    /// when <paramref name="path"/> is not a link. Reads the link itself; never follows it.
    /// </summary>
    public string? LinkTargetName(string path)
    {
        try
        {
            var target = new FileInfo(PathOf(path)).LinkTarget;
            return target is null ? null : Path.GetFileName(target.TrimEnd('/'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The name of the directory a link's target sits in ("sdb" for /sys/class/block/sdb1,
    /// whose target ends in ".../block/sdb/sdb1"), or null when it is not a link.
    /// </summary>
    public string? LinkTargetParentName(string path)
    {
        try
        {
            var target = new FileInfo(PathOf(path)).LinkTarget;
            var parent = target is null ? null : Path.GetDirectoryName(target.TrimEnd('/'));
            return parent is null ? null : Path.GetFileName(parent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
