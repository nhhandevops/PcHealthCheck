namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Recognises a storage reliability-counter record that carries no measurement at all. That is
/// what Windows returns for a drive behind a USB bridge that does not pass health data through:
/// on 2026-10-21 a Realtek RTL9210B enclosure came back as Wear 0, Temperature 0 and every
/// other field empty, and the app graded it "0% wear, Good" - a drive nobody had read, reported
/// as healthy (HW-001, HANDOFF rule 7).
///
/// A genuinely new drive also reports 0% wear, but it reports its temperature or power-on hours
/// or its error totals alongside. Only a record with none of them is treated as unread.
/// </summary>
public static class ReliabilityCounterCheck
{
    public static bool IsPlaceholder(
        ushort? wear, ushort? temperature, ulong? powerOnHours, ulong? readErrorsTotal, ulong? writeErrorsTotal) =>
        (wear ?? 0) == 0 &&
        (temperature ?? 0) == 0 &&
        (powerOnHours ?? 0) == 0 &&
        readErrorsTotal is null &&
        writeErrorsTotal is null;

    /// <summary>MSFT_PhysicalDisk.BusType value for USB.</summary>
    public const ushort BusTypeUsb = 7;

    /// <summary>
    /// Whether a wear figure can be graded. Over USB, exactly 0% is not evidence: on the same
    /// laptop a Seagate 2.5" hard disk in a USB caddy came back as "SSD, spindle speed 0, wear 0"
    /// and was graded "SSD endurance used 0%, Good". A USB drive reporting real wear (above 0) is
    /// still graded, and internal drives are unaffected.
    /// </summary>
    public static bool WearIsTrustworthy(ushort wear, bool onUsb) => wear > 0 || !onUsb;
}
