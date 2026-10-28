using System.Text;
using System.Text.RegularExpressions;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Writes a file path for the report with its folder as a token: <c>%LOCALAPPDATA%\Programs\x.exe</c>,
/// never <c>C:\Users\&lt;name&gt;\AppData\Local\Programs\x.exe</c>. The report travels when it is
/// shared, and a profile folder carries the Windows user name (principle 10 of REMEDIES-PLAN).
///
/// A path in another account's profile has no token of its own, so its folder is written as
/// "&lt;another account&gt;" rather than left in.
/// </summary>
public static partial class ReportPaths
{
    public const string AnotherAccount = "<another account>";

    private static readonly Lazy<(IReadOnlyList<(string Folder, string Token)> Folders, string? ProfilesRoot)> ThisPc = new(() =>
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new List<(string, string)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (profile, "%USERPROFILE%"),
            (Environment.GetEnvironmentVariable("PUBLIC") ?? "", "%PUBLIC%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "%ProgramFiles(x86)%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "%ProgramFiles%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%ProgramData%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "%SystemRoot%"),
        };
        return (folders, profile.Length > 0 ? Path.GetDirectoryName(profile) : null);
    });

    public static string Tokenize(string path) => Tokenize(path, ThisPc.Value.Folders, ThisPc.Value.ProfilesRoot);

    /// <param name="folders">Known folders and their tokens. The longest match wins, so
    /// %LOCALAPPDATA% is preferred to %USERPROFILE% for a path under both.</param>
    /// <param name="profilesRoot">The folder that holds every account's profile, usually C:\Users.</param>
    public static string Tokenize(string path, IReadOnlyList<(string Folder, string Token)> folders, string? profilesRoot)
    {
        foreach (var (folder, token) in folders.Where(f => f.Folder.Length > 0).OrderByDescending(f => f.Folder.Length))
        {
            var rest = Under(path, folder);
            if (rest is not null) return token + rest;
        }

        if (!string.IsNullOrEmpty(profilesRoot))
        {
            var rest = Under(path, profilesRoot);
            if (rest is { Length: > 1 })
            {
                var next = rest.IndexOf('\\', 1);
                return AnotherAccount + (next < 0 ? "" : rest[next..]);
            }
        }
        return path;
    }

    public static string TokenizeWithin(string text) => TokenizeWithin(text, ThisPc.Value.Folders, ThisPc.Value.ProfilesRoot);

    /// <summary>
    /// <see cref="Tokenize(string)"/> for every path inside a command line or a comma-separated
    /// list, such as Winlogon's Userinit, which a prefix match would only half cover. The Winlogon
    /// row quoted those values with the user's folder in them (PRIV-002, review of 2026-10-28).
    /// A path starts at each drive letter ("C:\") and runs to the next one.
    /// </summary>
    public static string TokenizeWithin(string text, IReadOnlyList<(string Folder, string Token)> folders, string? profilesRoot)
    {
        var starts = DriveRoot().Matches(text).Select(m => m.Index).ToList();
        if (starts.Count == 0) return text;

        var written = new StringBuilder(text[..starts[0]]);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            written.Append(Tokenize(text[starts[i]..end], folders, profilesRoot));
        }
        return written.ToString();
    }

    /// <summary>A drive letter that begins a path, not one inside a word.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:\\")]
    private static partial Regex DriveRoot();

    /// <summary>The part of <paramref name="path"/> after <paramref name="folder"/>, starting with
    /// its backslash, or null when the path is not inside it. "C:\Program Files (x86)\x" is not
    /// inside "C:\Program Files".</summary>
    private static string? Under(string path, string folder)
    {
        var trimmed = folder.TrimEnd('\\');
        if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)) return null;
        if (path.Length == trimmed.Length) return "";
        return path[trimmed.Length] == '\\' ? path[trimmed.Length..] : null;
    }
}
