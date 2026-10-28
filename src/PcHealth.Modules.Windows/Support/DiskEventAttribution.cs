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

    /// <summary>
    /// Which of the errors count against this PC (REMEDIES-PLAN H3), by the audit kit's own rule
    /// (<c>Get-DiskErrorsAgainstLaptop</c>, KIT-005), so the app and the kit agree. The disk
    /// Windows runs from always counts, whatever its connection, and so does a disk attached now
    /// on an internal or unknown bus, and an error that names no disk: nothing shows those came
    /// from elsewhere. A disk on a USB, FireWire or card-reader bus is not this PC's, and neither
    /// is a disk number nothing is attached at now. With no list of attached disks, every error
    /// counts, as before.
    /// </summary>
    public static DiskErrorShare Share(IReadOnlyCollection<string?> deviceTexts, IReadOnlyCollection<AttachedDisk>? attached)
    {
        if (attached is null) return new DiskErrorShare(deviceTexts.Count, deviceTexts.Count, [], []);

        var byNumber = attached.GroupBy(d => d.Number).ToDictionary(g => g.Key, g => g.First());
        var own = 0;
        var outside = new SortedDictionary<int, int>();
        foreach (var text in deviceTexts)
        {
            if (DiskNumber(text) is { } n && (!byNumber.TryGetValue(n, out var disk) || (!disk.IsBoot && DiskBus.IsExternal(disk.BusType))))
                outside[n] = outside.GetValueOrDefault(n) + 1;
            else
                own++;
        }

        string Entry(KeyValuePair<int, int> kv) => string.Create(CultureInfo.InvariantCulture, $"disk {kv.Key}: {kv.Value}");
        return new DiskErrorShare(deviceTexts.Count, own,
            [.. outside.Where(kv => byNumber.ContainsKey(kv.Key)).Select(Entry)],
            [.. outside.Where(kv => !byNumber.ContainsKey(kv.Key)).Select(Entry)]);
    }

    /// <summary>
    /// The sentences for the metric's Detail when some errors were not counted, or empty when every
    /// error counted. The audit kit reads "Counted against this PC: N of T." for the total, so that
    /// wording is fixed English, like <see cref="Describe"/>'s.
    /// </summary>
    public static string DescribeShare(DiskErrorShare share)
    {
        if (share.NotCounted == 0) return "";
        var where = new List<string>();
        if (share.OnExternalDrives.Count > 0)
            where.Add($"on drives plugged in over USB or another external connection ({string.Join(", ", share.OnExternalDrives)})");
        if (share.OnDetachedDrives.Count > 0)
            where.Add($"on drives not attached now ({string.Join(", ", share.OnDetachedDrives)})");
        return string.Create(CultureInfo.InvariantCulture,
            $"Counted against this PC: {share.AgainstThisPc} of {share.Total}. Not counted, {share.NotCounted} {string.Join(" and ", where)}. ") +
            "On a USB drive these usually mean it was unplugged without being ejected, a loose cable or a weak port; " +
            "if such a drive holds the only copy of anything, copy that off.";
    }
}

/// <summary>
/// How many disk errors count against this PC, out of all those logged, and the ones that did not:
/// "disk N: count" for disks attached now on an external bus, and for disk numbers nothing is
/// attached at now.
/// </summary>
public sealed record DiskErrorShare(
    int Total, int AgainstThisPc, IReadOnlyList<string> OnExternalDrives, IReadOnlyList<string> OnDetachedDrives)
{
    public int NotCounted => Total - AgainstThisPc;
}
