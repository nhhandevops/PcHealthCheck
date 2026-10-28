using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows.Benchmarks;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// The one check every heavy disk task asks first: does this scan's own evidence say this drive is
/// failing? (REMEDIES-PLAN H2, project rule 4.) The benchmark trusted only Windows' own health
/// flag, which said "Healthy" on the pilot's SSD while SMART counted 5 retired sectors; and it once
/// created the very disk errors the app then graded Bad.
///
/// Drives are matched by Windows' disk number, never by model name, which two identical drives
/// share. Of the drive's own readings, only damage to its storage counts. Cable (CRC) errors,
/// command timeouts and temperature do not: they say nothing about whether writing to the drive
/// harms it. The event log is different: its disk errors are what this scan's "Disk I/O errors"
/// row grades, so each one keeps the benchmark off every drive it may have come from.
/// </summary>
public static class FailingDrives
{
    /// <summary>
    /// Readings of damaged storage, shared with the advice that tells the owner to back the drive up
    /// (<see cref="DriveDamage"/>), so the two never disagree about which drive is failing.
    /// </summary>
    private static readonly IReadOnlySet<string> DamageReadings = DriveDamage.MetricIds;

    /// <summary>
    /// Why each drive should be left alone, by disk number: a damage reading graded Normal or Bad,
    /// or disk errors in the event log that may have come from it. The log names a number, and USB
    /// drives take the next free one, so an error from a drive unplugged since can hold back a
    /// drive that took its number. Skipping a benchmark is the cheaper of the two mistakes.
    ///
    /// An error that names no number is placed by its volume when it names one that is on a drive
    /// now (Ntfs names "C:"). Any other, a controller reset naming \Device\RaidPort0 for one, could
    /// be from any drive inside the PC. The event-log check counts it against this PC and grades it,
    /// "back up now" included, so it keeps the benchmark off every drive that is not external
    /// (review of 2026-10-28). That is a deliberate skip, and says why: not a failure.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Find(
        IEnumerable<Verdict> findings, IEnumerable<string?> diskErrorDevices,
        IReadOnlyCollection<VolumeInfo> volumes, IReadOnlyCollection<PhysicalDiskInfo> disks)
    {
        var reasons = new SortedDictionary<int, List<string>>();
        void Add(int disk, string reason)
        {
            if (!reasons.TryGetValue(disk, out var list)) reasons[disk] = list = [];
            list.Add(reason);
        }
        static string Errors(int count) =>
            string.Create(CultureInfo.InvariantCulture, $"the event log holds {count} disk error{(count == 1 ? "" : "s")} from the last 90 days");

        foreach (var verdict in findings)
        {
            if (verdict.Metric.DiskNumber is not { } disk || !DamageReadings.Contains(verdict.Metric.Id)) continue;
            if (verdict.Grade is not (Grade.Normal or Grade.Bad)) continue;
            var reading = verdict.Metric.Value.Flag is null ? $"{verdict.Metric.Label}: {verdict.Metric.Value}" : verdict.Metric.Label;
            Add(disk, $"this scan's own drive check graded \"{reading}\" {verdict.Grade}");
        }

        var diskByLetter = volumes.GroupBy(v => v.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DiskNumber, StringComparer.OrdinalIgnoreCase);
        var numbered = new List<int>();
        var onVolumes = new List<(int Disk, string Letter)>();
        var nowhere = 0;
        foreach (var device in diskErrorDevices)
        {
            if (DiskEventAttribution.DiskNumber(device) is { } number) numbered.Add(number);
            else if (VolumeLetter(device) is { } letter && diskByLetter.TryGetValue(letter, out var disk)) onVolumes.Add((disk, letter));
            else nowhere++;
        }

        foreach (var group in numbered.GroupBy(n => n))
        {
            Add(group.Key, $"{Errors(group.Count())} that name this drive's number");
        }
        foreach (var group in onVolumes.GroupBy(e => e.Disk))
        {
            var letters = group.Select(e => e.Letter).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            Add(group.Key, $"{Errors(group.Count())} on this drive's volume{(letters.Count == 1 ? "" : "s")} {string.Join(", ", letters)}");
        }
        if (nowhere > 0)
        {
            foreach (var disk in disks.Where(d => d.Class != DriveClass.External).Select(d => d.DiskNumber).Distinct())
            {
                Add(disk, $"{Errors(nowhere)} that name no drive (controller resets or file-system errors), which this scan " +
                          "counts against this PC, so they may be this drive's");
            }
        }

        return reasons.ToDictionary(kv => kv.Key, kv => string.Join("; ", kv.Value));
    }

    /// <summary>"C:" for an event that names the volume C: ("C:" or "C:\"), otherwise null.</summary>
    private static string? VolumeLetter(string? device)
    {
        var text = device?.Trim().TrimEnd('\\');
        return text is { Length: 2 } && char.IsAsciiLetter(text[0]) && text[1] == ':'
            ? char.ToUpperInvariant(text[0]) + ":"
            : null;
    }

    /// <summary>Whether this scan read the drive's own health at all: any damage reading for its number.</summary>
    public static bool HealthWasRead(IEnumerable<Verdict> findings, int diskNumber) =>
        findings.Any(v => v.Metric.DiskNumber == diskNumber && DamageReadings.Contains(v.Metric.Id));
}
