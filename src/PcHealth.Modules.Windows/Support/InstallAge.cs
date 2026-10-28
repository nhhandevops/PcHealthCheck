using System.Globalization;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// How long ago Windows was installed, for the readings that are empty on a new install by
/// nature rather than by fault: Defender's first scheduled quick scan (on the dev T470p it came 4
/// days after install) and Reliability Monitor's history. Grading those on a fresh install put a
/// Bad, or a "could not finish", on exactly the PC that should read cleanest. On a young install
/// each one is a Note instead, "not judged: Windows was installed 3 days ago", and on an older
/// install it keeps its grade (REMEDIES-PLAN Step A row 10).
///
/// A restored disk image keeps the image's install date, so on the company's reimaged laptops
/// this rarely fires: it covers a real clean install, not a restore.
/// </summary>
public static class InstallAge
{
    /// <summary>Under this many whole days since install, those readings are not judged.</summary>
    public const int YoungDays = 7;

    /// <summary>
    /// Whole days since <paramref name="installedUtc"/>, or null when that cannot be told: no date,
    /// or a date later than the clock, so one of the two is wrong. Compared in UTC, and "now" is
    /// never earlier than <see cref="WindowsSupport.CheckedOn"/> (the app cannot run before it was
    /// built), so a clock set back cannot make an old install look new. A clock set forward only
    /// makes it look older, and an older install keeps the strict grades.
    /// </summary>
    public static int? DaysSince(DateTime? installedUtc, DateTime clockUtc)
    {
        if (installedUtc is not { } installed || installed > clockUtc) return null;
        var floor = WindowsSupport.CheckedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var now = clockUtc > floor ? clockUtc : floor;
        return (int)(now - installed).TotalDays;
    }

    /// <summary>The days since install when Windows is younger than <see cref="YoungDays"/>; otherwise null, and readings are judged as usual.</summary>
    public static int? Young(DateTime? installedUtc, DateTime clockUtc) =>
        DaysSince(installedUtc, clockUtc) is { } days && days < YoungDays ? days : null;

    /// <summary>"Windows was installed 3 days ago".</summary>
    public static string Installed(int days) => days switch
    {
        0 => "Windows was installed less than a day ago",
        1 => "Windows was installed 1 day ago",
        _ => string.Create(CultureInfo.InvariantCulture, $"Windows was installed {days} days ago"),
    };

    /// <summary>The Note for a reading not judged on a young install.</summary>
    public static string NotJudged(string what, int days, string why) => $"{what}: not judged: {Installed(days)}. {why}";

    /// <summary>
    /// Reads the install date now and returns the days since install when Windows is young, or null.
    /// Called only when a reading is empty, so a healthy scan never pays for it. A failed read is a
    /// note, not a failure: the reading it would have excused is then graded as on any other PC,
    /// so nothing reads healthier than it is.
    /// </summary>
    internal static int? ReadYoung(string what, List<string> notes) =>
        ReadDaysSince(what, notes) is { } days && days < YoungDays ? days : null;

    /// <summary>
    /// Reads the install date now and returns the whole days since install (<see cref="DaysSince"/>),
    /// or null, with the same note as <see cref="ReadYoung"/> when it could not be read. For a
    /// history that cannot be older than the install, such as Defender's detections (M1).
    /// </summary>
    internal static int? ReadDaysSince(string what, List<string> notes)
    {
        var os = Wmi.TryQuery(@"root\cimv2", "SELECT InstallDate FROM Win32_OperatingSystem", out var error).FirstOrDefault();
        if ((os is null ? null : Wmi.GetCimDate(os, "InstallDate")) is not { } installed)
        {
            notes.Add($"Windows' install date could not be read ({error ?? "no date returned"}), so whether Windows is new " +
                      $"was not checked: {what} is judged as on any other PC.");
            return null;
        }
        return DaysSince(installed.ToUniversalTime(), DateTime.UtcNow);
    }
}
