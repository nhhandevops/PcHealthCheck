using System.Globalization;
using System.Text.Json;

namespace PcHealth.Modules.Windows.Browsers;

/// <summary>
/// Reads the extensions installed in Chromium-family browsers, from the browser's own files.
///
/// Read-only, and nothing outside the extension folders is opened — the browser's history,
/// cookies, passwords and bookmarks all live in the same profile directory and none of them
/// is touched. What is read is each extension's <c>manifest.json</c> (public, and identical
/// to what the Web Store page shows) plus the two facts about it the browser keeps in its
/// own preferences: whether it is turned on, and whether it came from the store.
/// </summary>
public static class ChromiumExtensionReader
{
    /// <summary>Browsers that share Chrome's profile layout, and where each keeps it.</summary>
    public static IReadOnlyList<(string Name, string UserDataPath)> KnownBrowsers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data")),
            ("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
        ];
    }

    public static IReadOnlyList<BrowserExtension> Read(string browser, string userDataPath, List<string> notes)
    {
        var found = new List<BrowserExtension>();
        if (!Directory.Exists(userDataPath)) return found;

        foreach (var profile in ProfileDirectories(userDataPath))
        {
            var extensionsRoot = Path.Combine(profile, "Extensions");
            if (!Directory.Exists(extensionsRoot)) continue;

            var records = ReadProfileRecords(profile);
            foreach (var extensionDirectory in SafeDirectories(extensionsRoot, notes))
            {
                var id = Path.GetFileName(extensionDirectory);
                // Extensions keep one folder per version; the newest is the one in use.
                var versionDirectory = SafeDirectories(extensionDirectory, notes)
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (versionDirectory is null) continue;

                if (ReadManifest(browser, Path.GetFileName(profile), id, versionDirectory,
                        records.GetValueOrDefault(id), notes) is { } extension)
                {
                    found.Add(extension);
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> ProfileDirectories(string userDataPath)
    {
        // "Default", "Profile 1", "Profile 2"… plus Opera, which has no profile level at all.
        if (Directory.Exists(Path.Combine(userDataPath, "Extensions"))) yield return userDataPath;

        foreach (var directory in Directory.EnumerateDirectories(userDataPath))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
    }

    private static BrowserExtension? ReadManifest(
        string browser, string profile, string id, string versionDirectory,
        (bool Enabled, bool FromWebStore)? record, List<string> notes)
    {
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;

            var name = Text(root, "name") ?? id;
            var version = Text(root, "version") ?? Path.GetFileName(versionDirectory);

            // Most extensions declare their name as "__MSG_extName__" and keep the real
            // text in a locale file. Without resolving it the report would be a list of
            // placeholders — useless for the one job it has, letting the user recognise
            // what is installed.
            if (name.StartsWith("__MSG_", StringComparison.Ordinal))
            {
                name = ResolveLocalizedName(versionDirectory, name, Text(root, "default_locale")) ?? id;
            }

            return new BrowserExtension(
                browser, profile, id, name, version,
                Strings(root, "permissions"),
                Strings(root, "host_permissions"),
                record?.Enabled ?? true,
                record?.FromWebStore ?? false);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            notes.Add($"An extension in {browser} ({id}) could not be read: {e.GetType().Name}.");
            return null;
        }
    }

    private static string? ResolveLocalizedName(string versionDirectory, string placeholder, string? defaultLocale)
    {
        var key = placeholder.Trim('_')[4..];   // "__MSG_extName__" -> "extName"
        var localesRoot = Path.Combine(versionDirectory, "_locales");
        if (!Directory.Exists(localesRoot)) return null;

        // The extension's own default locale first; failing that, any English variant, then
        // whatever is there — a name in the wrong language still beats "__MSG_extName__".
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(defaultLocale)) candidates.Add(Path.Combine(localesRoot, defaultLocale));
        candidates.Add(Path.Combine(localesRoot, "en_US"));
        candidates.Add(Path.Combine(localesRoot, "en"));
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(localesRoot));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var directory in candidates)
        {
            var messages = Path.Combine(directory, "messages.json");
            if (!File.Exists(messages)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(messages));
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    // Message keys are case-insensitive in Chrome's own lookup.
                    if (!entry.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Value.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString();
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Try the next locale rather than giving up on the name entirely.
            }
        }
        return null;
    }

    /// <summary>
    /// The two things the browser knows that the manifest does not: whether the extension
    /// is currently switched on, and whether it arrived from the Web Store or was put there
    /// by a program on the PC.
    /// </summary>
    private static Dictionary<string, (bool Enabled, bool FromWebStore)> ReadProfileRecords(string profile)
    {
        var records = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(profile, "Secure Preferences");
        if (!File.Exists(path)) return records;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("extensions", out var extensions)
                || !extensions.TryGetProperty("settings", out var settings))
            {
                return records;
            }

            foreach (var entry in settings.EnumerateObject())
            {
                var disabled = entry.Value.TryGetProperty("disable_reasons", out var reasons)
                               && IsDisabled(reasons);
                var fromStore = entry.Value.TryGetProperty("from_webstore", out var store)
                                && store.ValueKind == JsonValueKind.True;
                records[entry.Name] = (!disabled, fromStore);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Without this file every extension is simply reported as enabled and of
            // unknown origin, which is a smaller loss than skipping the browser entirely.
        }
        return records;
    }

    /// <summary>
    /// Chrome records why an extension is off rather than that it is on. The shape of that
    /// record has changed: current Chrome writes a LIST of reasons (empty when enabled),
    /// while older builds wrote a single bitmask. Both are read, and anything unrecognised
    /// counts as enabled — reporting a working extension as "turned off" would be a lie in
    /// the reassuring direction, which is the worse way to be wrong here.
    ///
    /// Reading this as a number was a real crash: JsonElement.TryGetInt32 throws on an
    /// array rather than returning false, which took the whole check down on the first run
    /// against a real browser.
    /// </summary>
    private static bool IsDisabled(JsonElement reasons) => reasons.ValueKind switch
    {
        JsonValueKind.Array => reasons.GetArrayLength() > 0,
        JsonValueKind.Number => reasons.TryGetInt32(out var mask) && mask != 0,
        _ => false,
    };

    private static List<string> SafeDirectories(string path, List<string> notes)
    {
        try { return [.. Directory.EnumerateDirectories(path)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not list {path}: {e.GetType().Name}.");
            return [];
        }
    }

    private static string? Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> Strings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            // Manifests may hold objects here (declarative rules); only the plain strings
            // are permissions.
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text) items.Add(text);
        }
        return items;
    }

    /// <summary>Formats a count for the report without tripping the locale analyzers.</summary>
    internal static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
