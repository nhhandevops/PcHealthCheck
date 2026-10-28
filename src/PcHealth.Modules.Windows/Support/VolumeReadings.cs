using System.Globalization;
using PcHealth.Core.Metrics;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Rows about one lettered volume, and about the disk it is on. A volume on a drive outside the PC (see
/// <see cref="DriveMap.ExternalBusOf"/>) is shown for information and not graded: on the pilot,
/// the only "disk nearly full" on three laptops was the auditor's USB drive, and every USB volume
/// was graded "not encrypted" against the laptop (REMEDIES-PLAN D1 and Step A row 11). An
/// information-only row carries no tips, so its advice goes into the detail.
/// </summary>
public static class VolumeReadings
{
    public static string NotCounted(ushort externalBus) =>
        $"On {DiskBus.Describe(externalBus)}: shown for information, it does not count toward this PC's health.";

    public static Metric FreeSpace(string subject, ulong size, ulong free, ushort? externalBus)
    {
        var freePct = Math.Round(free * 100.0 / size, 1);
        var space = string.Create(CultureInfo.InvariantCulture,
            $"{Math.Round(free / 1073741824.0, 1)} GB free of {Math.Round(size / 1073741824.0, 0)} GB.");
        return externalBus is { } bus
            ? new Metric("storage.volume.free_pct", "Free space", freePct, HealthCategory.Storage,
                Unit: "%", Subject: subject, InfoOnly: true,
                Detail: $"{NotCounted(bus)} {space} To make room on it, empty its Recycle Bin or move old backups off it.")
            : new Metric("storage.volume.free_pct", "Free space", freePct, HealthCategory.Storage,
                Unit: "%", Subject: subject,
                Detail: $"{space} SSDs slow down and wear faster when nearly full.");
    }

    /// <summary>
    /// Where one attached disk is (<see cref="DiskRoles"/>). Information only: it grades nothing, and
    /// says only what the drive map read. Windows reports "unknown" for some buses, and such a disk
    /// counts as inside the PC, so the detail says it was counted that way rather than that it is.
    /// </summary>
    public static Metric DiskRole(AttachedDisk disk, string subject, string role, IReadOnlyList<string> letters)
    {
        var where = (role, disk.BusType) switch
        {
            (DiskRoles.SystemRole, { } bus) when DiskBus.IsExternal(bus) =>
                $"The disk Windows runs from. It is {DiskBus.Describe(bus)}, and counts as this PC's own.",
            (DiskRoles.SystemRole, _) => "The disk Windows runs from.",
            (DiskRoles.ExternalRole, { } bus) => $"Plugged in from outside this PC: {DiskBus.Describe(bus)}.",
            (_, null or 0) => "Counted as inside this PC: Windows did not say how it is connected.",
            _ => "Inside this PC.",
        };
        var holds = letters.Count > 0 ? $" It holds {string.Join(", ", letters)}." : " It has no drive letter.";
        return new Metric("storage.disk.role", "Disk role", role, HealthCategory.Storage,
            Subject: subject, InfoOnly: true, DiskNumber: disk.Number, Detail: where + holds);
    }

    /// <summary>
    /// The space in use over one disk's lettered drives, in GB as <see cref="FreeSpace"/> counts it
    /// (1 GB = 1024³ bytes), rounded to 0.1. Information only. Only the disk Windows runs from is
    /// said to hold Windows and programs: the report prints the detail, and the auditor's USB drive
    /// holds neither.
    /// </summary>
    public static Metric DiskUsed(int diskNumber, string subject, ulong usedBytes, bool holdsWindows) =>
        new("storage.disk.used_gb", "Space in use", Math.Round(usedBytes / 1073741824.0, 1), HealthCategory.Storage,
            Unit: "GB", Subject: subject, InfoOnly: true, DiskNumber: diskNumber,
            Detail: holdsWindows
                ? $"Added up over this disk's drives ({subject}), Windows and programs included."
                : $"Added up over this disk's drives ({subject}).");

    /// <summary>
    /// One volume's encryption, from Win32_EncryptableVolume. ProtectionStatus: 0 off, 1 on, 2
    /// unknown (a locked volume reads 2). ConversionStatus: 0 fully decrypted, 1 fully encrypted,
    /// 2 encryption in progress, 3 decryption in progress, 4 encryption paused, 5 decryption paused.
    ///
    /// Protection "off" covers three different drives: one never encrypted, one whose encryption
    /// has not finished, and one fully encrypted whose key is kept in the clear on the drive. The
    /// last is what Windows 11 device encryption leaves on a PC with only local accounts, and what a
    /// suspended BitLocker looks like; it read "not encrypted" until the conversion status was used
    /// (GOLDEN-IMAGE-PLAN I1). None of the three protects the data, so all three keep the grade an
    /// unencrypted drive always had (encryption is not required: REMEDIES-PLAN decision 12), and
    /// the Detail says which it is. A state Windows could not tell is shown, never graded and never
    /// "no": a locked BitLocker drive is the usual cause, and it is encrypted.
    /// </summary>
    public static Metric Encryption(string driveLetter, uint? protectionStatus, uint? conversionStatus, ushort? externalBus)
    {
        var (encrypted, state) = (protectionStatus, conversionStatus) switch
        {
            (not (0 or 1), _) => ((bool?)null,
                "Whether this drive is protected could not be determined (a locked BitLocker drive reads this way), so it was not graded."),
            (_, 2) => (false, "Encryption of this drive has started and is not finished, so it is not protected: anyone who takes the drive out can still read it."),
            (_, 4) => (false, "Encryption of this drive was started and is paused, so it is not protected: anyone who takes the drive out can still read it."),
            (_, 3 or 5) => (false, "BitLocker is decrypting this drive, or its decryption is paused, so it is not protected: anyone who takes the drive out can read it."),
            (1, _) => (true, "This drive is encrypted and protected. If the PC is lost or stolen, the data stays unreadable."),
            (_, 1) => (false, "This drive is encrypted, but its key is in the clear (device encryption waiting, or BitLocker suspended), " +
                              "so anyone who takes the drive out can still read every file on it. Resuming BitLocker, or finishing " +
                              "device encryption with a saved recovery key, protects it."),
            (_, 0) => (false, "This drive is not encrypted — anyone who takes the drive out can read every file on it."),
            _ => (false, "This drive is not protected: it is not encrypted, not fully encrypted, or its key is in the clear, and " +
                         "Windows did not say which. Anyone who takes the drive out can read it."),
        };
        return new Metric("security.drive_encrypted", "Drive encryption",
            encrypted is { } flag ? flag : "unknown",
            HealthCategory.Security, Subject: driveLetter, InfoOnly: externalBus is not null || encrypted is null,
            Detail: externalBus is { } bus ? $"{NotCounted(bus)} {state}" : state);
    }
}
