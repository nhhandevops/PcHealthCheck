using PcHealth.Core.Metrics;
using PcHealth.Modules.Windows.Benchmarks;

namespace PcHealth.Modules.Windows.Storage;

/// <summary>
/// A drive's SMART temperature, graded by what kind of drive it is. SSDs run warmer than hard
/// disks and are made to: the pilot's SATA SSD read 40 °C, which is graded Normal on the hard-disk
/// bands, beside the sentence "Mechanical drives last longest below about 40 °C" (HW-001,
/// REMEDIES-PLAN H7). An SSD's reading has its own id, so its own rule (smart.json,
/// rule.smart.ssd-temperature) grades it. A typical SATA SSD is rated to work at 0-70 °C, for
/// example the Samsung 870 EVO
/// (https://download.semiconductor.samsung.com/resources/data-sheet/Samsung_SSD_870_EVO_Data_Sheet_Rev1.1_230509_10129500053000.pdf).
///
/// The kind comes from Windows' media and bus type, never from the drive's attribute list. A drive
/// Windows did not describe, or one behind a USB adapter (which misreport what is inside them),
/// keeps the hard-disk bands: the stricter grade is the one that cannot hide a hot hard disk.
/// </summary>
public static class SmartTemperature
{
    public static Metric Reading(string subject, int? diskNumber, double celsius, DriveClass driveClass) => driveClass switch
    {
        DriveClass.SataSsd or DriveClass.NvmeSsd => new Metric("smart.ssd_temperature_c", "Drive temperature", celsius,
            HealthCategory.Storage, Unit: "°C", Subject: subject, DiskNumber: diskNumber,
            Detail: "SSDs run warmer than hard disks and are made to: most are rated to work up to 70 °C."),
        DriveClass.Hdd => new Metric("smart.temperature_c", "Drive temperature", celsius,
            HealthCategory.Storage, Unit: "°C", Subject: subject, DiskNumber: diskNumber,
            Detail: "Mechanical drives last longest below about 40 °C."),
        _ => new Metric("smart.temperature_c", "Drive temperature", celsius,
            HealthCategory.Storage, Unit: "°C", Subject: subject, DiskNumber: diskNumber,
            Detail: "Windows did not say whether this is a hard disk or an SSD, so it is graded as a hard disk, " +
                    "which lasts longest below about 40 °C. SSDs run warmer and are made to."),
    };
}
