using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// The disk errors Windows logged in the last 90 days, read by the event-log check and again by
/// the disk benchmark, which must not write to a drive the log says is erroring (REMEDIES-PLAN
/// H2). One query, so the two can never disagree about what counts as a disk error. Keyed on
/// provider and event id, never on translated message text.
/// </summary>
internal static class DiskErrorLog
{
    private const long NinetyDaysMs = 90L * 24 * 60 * 60 * 1000;

    /// <summary>A pathological log is capped: 5000 already means "very bad".</summary>
    private const int Cap = 5000;

    // iaStorA/iaStorAC/iaStorAVC are Intel RST, the storage driver on many laptops; without
    // them its controller resets (129) were never counted.
    private static readonly string XPath = string.Create(CultureInfo.InvariantCulture,
        $"*[System[(Provider[@Name='disk'] or Provider[@Name='stordisk'] or Provider[@Name='storahci'] or Provider[@Name='stornvme'] or " +
        $"Provider[@Name='iaStorA'] or Provider[@Name='iaStorAC'] or Provider[@Name='iaStorAVC'] or Provider[@Name='Ntfs']) and " +
        $"(EventID=7 or EventID=51 or EventID=55 or EventID=129 or EventID=153 or EventID=157) and " +
        $"TimeCreated[timediff(@SystemTime) <= {NinetyDaysMs}]]]");

    /// <summary>
    /// The device each error names (the event's first insertion string, such as
    /// <c>\Device\Harddisk2\DR2</c>, or null), newest first: one entry per error. Null, with the
    /// reason, when the System log could not be read: unknown, never zero.
    /// </summary>
    public static List<string?>? Read(out string? error)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, XPath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var devices = new List<string?>();
            while (devices.Count < Cap && reader.ReadEvent() is { } record)
            {
                using (record) devices.Add(FirstInsertionString(record));
            }
            error = null;
            return devices;
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
        {
            error = e.Message;
            return null;
        }
    }

    private static string? FirstInsertionString(EventRecord record)
    {
        try { return record.Properties.Count > 0 ? record.Properties[0].Value as string : null; }
        catch (EventLogException) { return null; }
    }
}
