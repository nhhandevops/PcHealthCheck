using System.Globalization;
using System.Text;

namespace PcHealth.Modules.Linux.Support;

/// <summary>
/// Parsers for the small text files under /proc and /etc. Pure, so they are tested on fixture
/// text (rule 8): a development machine never produces the unusual cases. None of these files
/// is translated, so no parser depends on the system language.
/// </summary>
internal static class ProcFiles
{
    /// <summary>
    /// /proc/meminfo as name → number: "MemTotal:       16244392 kB" becomes ("MemTotal",
    /// 16244392). Values are kB except the HugePages_* counts, which have no unit. Lines that do
    /// not parse are skipped rather than guessed at.
    /// </summary>
    public static Dictionary<string, long> ParseMeminfo(string text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var number = line[(colon + 1)..].Trim();
            var space = number.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0) number = number[..space];
            if (long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                values[line[..colon].Trim()] = value;
        }
        return values;
    }

    /// <summary>The first number in /proc/uptime, seconds since boot.</summary>
    public static double? ParseUptimeSeconds(string text)
    {
        var first = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? seconds
            : null;
    }

    /// <summary>One counter from /proc/vmstat ("oom_kill 3"), or null when this kernel has no such line.</summary>
    public static long? VmStatCounter(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[0], name, StringComparison.Ordinal) &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return null;
    }

    /// <summary>A file holding one small integer, such as /proc/sys/vm/overcommit_memory.</summary>
    public static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// /etc/os-release as key → value. The format is shell-like: values may be bare, in double
    /// quotes (where backslash escapes \" \\ \$ \` apply) or in single quotes (taken literally).
    /// Comments and lines without "=" are skipped.
    /// </summary>
    public static Dictionary<string, string> ParseOsRelease(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            values[line[..eq]] = Unquote(line[(eq + 1)..]);
        }
        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var inner = value[1..^1];
        var result = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] is '"' or '\\' or '$' or '`')
                i++;
            result.Append(inner[i]);
        }
        return result.ToString();
    }

    /// <summary>"Ubuntu 26.04 LTS (kernel 7.0.0-14-generic, X64)" from os-release and the kernel version.</summary>
    public static string DescribeOs(IReadOnlyDictionary<string, string>? osRelease, string? kernel, string architecture)
    {
        string? name = null;
        if (osRelease is not null)
        {
            name = osRelease.GetValueOrDefault("PRETTY_NAME");
            if (string.IsNullOrWhiteSpace(name))
                name = string.Join(' ', new[] { osRelease.GetValueOrDefault("NAME"), osRelease.GetValueOrDefault("VERSION") }
                    .Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        if (string.IsNullOrWhiteSpace(name)) name = "Linux";
        return string.IsNullOrWhiteSpace(kernel)
            ? $"{name} ({architecture})"
            : $"{name} (kernel {kernel.Trim()}, {architecture})";
    }

    /// <summary>
    /// Placeholders firmware writes where a value belongs. Reporting "To Be Filled By O.E.M."
    /// as the computer's model would be worse than leaving it out.
    /// </summary>
    private static readonly HashSet<string> FirmwarePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "None", "Not Applicable", "Not Specified", "Default string", "System Product Name",
        "System Version", "System manufacturer", "To Be Filled By O.E.M.", "To be filled by O.E.M.",
        "OEM", "O.E.M.", "x.x", "0", "1.0", "0.1", "Type1ProductConfigId", "INVALID",
    };

    /// <summary>
    /// The computer, from DMI: "LENOVO 20J6CTO1WW (ThinkPad T470p)". Lenovo puts the machine type
    /// in product_name and the familiar name in product_version; most makers leave the version
    /// empty or a placeholder. Null when the firmware published nothing usable.
    /// </summary>
    public static string? DescribeModel(string? vendor, string? product, string? version)
    {
        static string? Clean(string? v) => v is null || FirmwarePlaceholders.Contains(v.Trim()) ? null : v.Trim();
        var maker = Clean(vendor);
        var name = Clean(product);
        var extra = Clean(version);
        // HP and others repeat the maker in the product name ("HP EliteBook 840 G8").
        if (maker is not null && name?.StartsWith(maker, StringComparison.OrdinalIgnoreCase) == true) maker = null;
        var parts = new[] { maker, name }.OfType<string>().ToList();
        if (parts.Count == 0) return extra;
        var model = string.Join(' ', parts);
        return extra is null || model.Contains(extra, StringComparison.OrdinalIgnoreCase) ? model : $"{model} ({extra})";
    }

    /// <summary>The processor's name from /proc/cpuinfo ("model name" on x86), with runs of spaces collapsed.</summary>
    public static string? CpuModelName(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !string.Equals(line[..colon].Trim(), "model name", StringComparison.Ordinal)) continue;
            var name = string.Join(' ', line[(colon + 1)..].Split(' ', '\t').Where(p => p.Length > 0));
            return name.Length > 0 ? name : null;
        }
        return null;
    }
}
