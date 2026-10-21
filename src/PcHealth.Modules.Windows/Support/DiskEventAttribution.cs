using System.Globalization;
using System.Text.RegularExpressions;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Says which drive a disk-error event came from. A bare count told a laptop's owner "2 disk
/// errors" when both were on a USB backup drive and none on the disk Windows runs from
/// (HW-001, 2026-10-21) - the one thing the reader needed to know.
///
/// The disk driver names the device in the event's first insertion string, for example
/// <c>\Device\Harddisk2\DR2</c>. That string is not localized, so this works on any Windows
/// language. Controller and file-system events name no disk and are counted separately.
/// </summary>
public static partial class DiskEventAttribution
{
    [GeneratedRegex(@"\\Device\\Harddisk(\d+)\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HarddiskPattern();

    public static int? DiskNumber(string? deviceText)
    {
        var match = HarddiskPattern().Match(deviceText ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>One sentence for the metric's Detail, or empty when there were no events.</summary>
    public static string Describe(IEnumerable<string?> deviceTexts, int? systemDisk)
    {
        var onSystem = 0;
        var unattributed = 0;
        var others = new SortedDictionary<int, int>();
        foreach (var text in deviceTexts)
        {
            if (DiskNumber(text) is not { } n) unattributed++;
            else if (n == systemDisk) onSystem++;
            else others[n] = others.GetValueOrDefault(n) + 1;
        }
        if (onSystem + unattributed + others.Count == 0) return "";

        var parts = new List<string>();
        var otherList = string.Join(", ", others.Select(kv => string.Create(CultureInfo.InvariantCulture, $"disk {kv.Key}: {kv.Value}")));
        if (systemDisk is { } system)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"on the disk Windows runs from (disk {system}): {onSystem}"));
            if (others.Count > 0) parts.Add($"on other drives: {otherList}");
        }
        else if (others.Count > 0)
        {
            parts.Add($"by disk number: {otherList}");
        }
        if (unattributed > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"from controllers or the file system, no disk named: {unattributed}"));

        var sentence = "By drive: " + string.Join("; ", parts) + ".";
        // Disk numbers are handed out in the order drives appear, so a USB drive's number can
        // belong today to a different drive than the one that logged the error.
        if (others.Count > 0)
            sentence += " USB drives take the next free number when plugged in, so these numbers may name drives no longer attached.";
        return sentence;
    }
}
