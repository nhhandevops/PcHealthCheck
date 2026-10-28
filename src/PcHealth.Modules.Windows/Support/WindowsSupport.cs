using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;

namespace PcHealth.Modules.Windows.Support;

/// <summary>Which of Microsoft's lifecycle tables a Windows edition follows.</summary>
public enum EditionFamily
{
    /// <summary>An edition this app has no dates for (Windows Server, Windows 11 SE, anything new).</summary>
    Unknown,
    /// <summary>Home, Pro, Pro Education, Pro for Workstations.</summary>
    HomePro,
    /// <summary>Enterprise, Education, Enterprise multi-session, IoT Enterprise (general availability channel).</summary>
    EnterpriseEducation,
    /// <summary>Enterprise LTSB/LTSC.</summary>
    Ltsc,
    /// <summary>IoT Enterprise LTSB/LTSC, which from 2021 has its own, longer dates.</summary>
    IotLtsc,
    /// <summary>Windows 10 Team, the Surface Hub edition: it ended with Windows 10 and has no Extended Security Updates.</summary>
    Team,
}

/// <summary>One row of Microsoft's lifecycle data: the last day a release gets security updates.</summary>
public sealed record SupportEnd(int Build, EditionFamily Family, string Release, DateOnly LastDay, string Source);

/// <summary>A year of Microsoft's paid Extended Security Updates for Windows 10, by its published activation ID.</summary>
public sealed record EsuYear(int Year, Guid ActivationId, DateOnly LastDay);

/// <summary>One ESU add-on row of SoftwareLicensingProduct (LicenseIsAddon = true).</summary>
/// <param name="Status">LicenseStatus: 1 is licensed, 0 unlicensed, the rest grace or notification states.</param>
/// <param name="KeyInstalled">Whether a key is installed for it (a partial product key exists); the key itself is never kept.</param>
public sealed record EsuLicence(Guid Id, uint Status, bool KeyInstalled);

/// <summary>What os.supported says. <see cref="Supported"/> is null when this app cannot tell, and <see cref="Detail"/> then says why.</summary>
/// <param name="EndingSoon">Set when the support that applies ends within about six months: the os.support_ending detail.</param>
public sealed record SupportVerdict(bool? Supported, string Detail, string? EndingSoon = null);

/// <summary>
/// Whether this Windows still gets security updates. Before 2026-10-27 the app hard-coded one
/// date, the end of consumer ESU, and called every Windows 10 PC supported until then without
/// checking that it was enrolled, or allowed to be: consumer ESU "can't be used in commercial
/// scenarios" (OS-001). Now the dates are one table, each row with its source, and Windows 10
/// 22H2 counts as supported only on evidence: an active ESU licence for the year that covers
/// today, or, when no ESU licence is active at all and only until consumer ESU ends, a build that
/// only enrolled PCs are offered. A build never outweighs a licence: a Year 1 licence explains
/// every build Year 1 brought (review round, 2026-10-28).
///
/// Every date here was checked on <see cref="CheckedOn"/>. Re-check them before each release:
/// Microsoft has moved the consumer ESU end once already (HW-001). Lifecycle tables print the end
/// as "6:59:59 AM" on the NEXT day; the dates below are the last day itself, the Patch Tuesday.
/// </summary>
public static class WindowsSupport
{
    /// <summary>When the dates below were checked against Microsoft's pages.</summary>
    public static readonly DateOnly CheckedOn = new(2026, 10, 27);

    /// <summary>About six months: from here on, the end of support is a plan to make now.</summary>
    public const int EndingSoonDays = 183;

    public const int Windows10Build = 19045;

    /// <summary>
    /// The last Windows 10 22H2 build a PC without ESU is offered: KB5071959, 2025-11-11, an
    /// out-of-band release "offered to consumer devices that are not enrolled in the Extended
    /// Security Updates", carrying the October 2025 security content
    /// (https://support.microsoft.com/help/5071959). The next build, 19045.6575 (KB5068781, the
    /// same day), is the first in the "Latest update for ESU" line of Microsoft's release table
    /// (https://learn.microsoft.com/en-us/windows/release-health/release-information), whose
    /// header says the October 2025 update "was the final update available" for 22H2.
    /// So a 19045 build above this one means ESU updates arrived.
    /// </summary>
    public const int Windows10LastBuildWithoutEsu = 6466;

    /// <summary>
    /// Consumer ESU ends 2027-10-12 and is for personal use only: "The consumer ESU program can't
    /// be used in commercial scenarios" (https://www.microsoft.com/en-us/windows/extended-security-updates).
    /// Never a company laptop's end date.
    /// </summary>
    public static readonly DateOnly ConsumerEsuLastDay = new(2027, 10, 12);

    /// <summary>
    /// Commercial ESU, bought per year, for "Enterprise, Education, and Pro in Commercial use"
    /// (dates: https://learn.microsoft.com/en-us/lifecycle/faq/extended-security-updates). The
    /// activation IDs "are the same across all eligible Windows ESU editions and all devices
    /// enrolled" (https://learn.microsoft.com/en-us/windows/whats-new/enable-extended-security-updates).
    /// A PC carries more add-on rows than these (Years 4-6, IoT, a second, KMS row for each year);
    /// only these three are Microsoft's published commercial ones, so only they count. Matched by
    /// ID, never by the Name text.
    /// </summary>
    public static readonly IReadOnlyList<EsuYear> EsuYears =
    [
        new(1, new Guid("f520e45e-7413-4a34-a497-d2765967d094"), new DateOnly(2026, 10, 13)),
        new(2, new Guid("1043add5-23b1-4afb-9a0f-64343c8f3f8d"), new DateOnly(2027, 10, 12)),
        new(3, new Guid("83d49986-add3-41d7-ba33-87c7bfb5c0fb"), new DateOnly(2028, 10, 10)),
    ];

    private const string Win10HomePro = "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro";
    private const string Win10Enterprise = "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-enterprise-and-education";
    private const string Win10Team = "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-team-surface-hub";
    private const string Win11HomePro = "https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro";
    private const string Win11Enterprise = "https://learn.microsoft.com/en-us/lifecycle/products/windows-11-enterprise-and-education";
    // Builds, the 26H2 dates (whose lifecycle stamps break the next-day pattern) and LTSC 2024.
    private const string Win11Releases = "https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information";

    /// <summary>
    /// The support table. Builds are the major build Windows reports; LTSC shares its build with a
    /// general release, so the edition decides. The LTSB 2015, IoT LTSB/LTSC and Windows 10 Team rows
    /// were added on 2026-10-28: without them an ended Windows was "NOT graded" instead of Bad.
    /// </summary>
    public static readonly IReadOnlyList<SupportEnd> Table =
    [
        new(19045, EditionFamily.HomePro, "Windows 10 version 22H2", new(2025, 10, 14), Win10HomePro),
        new(19045, EditionFamily.EnterpriseEducation, "Windows 10 version 22H2", new(2025, 10, 14), Win10Enterprise),
        new(10240, EditionFamily.Ltsc, "Windows 10 Enterprise LTSB 2015", new(2025, 10, 14), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-2015-ltsb"),
        new(10240, EditionFamily.IotLtsc, "Windows 10 IoT Enterprise LTSB 2015", new(2025, 10, 14), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-iot-enterprise-ltsb-2015"),
        new(14393, EditionFamily.Ltsc, "Windows 10 Enterprise LTSB 2016", new(2026, 10, 13), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-2016-ltsb"),
        new(14393, EditionFamily.IotLtsc, "Windows 10 IoT Enterprise LTSB 2016", new(2026, 10, 13), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-iot-enterprise-ltsb-2016"),
        new(17763, EditionFamily.Ltsc, "Windows 10 Enterprise LTSC 2019", new(2029, 1, 9), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-enterprise-ltsc-2019"),
        new(17763, EditionFamily.IotLtsc, "Windows 10 IoT Enterprise LTSC 2019", new(2029, 1, 9), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-iot-enterprise-ltsc-2019"),
        new(19044, EditionFamily.Ltsc, "Windows 10 Enterprise LTSC 2021", new(2027, 1, 12), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-enterprise-ltsc-2021"),
        new(19044, EditionFamily.IotLtsc, "Windows 10 IoT Enterprise LTSC 2021", new(2032, 1, 13), "https://learn.microsoft.com/en-us/lifecycle/products/windows-10-iot-enterprise-ltsc-2021"),
        // Windows 10 Team "doesn't have an extended support option beyond this date"
        // (https://learn.microsoft.com/en-us/surface-hub/surface-hub-windows10-eos-migration). Its
        // 2020 Update is build 19042 and its 2022 Update 19045 (Surface Hub update history).
        new(19042, EditionFamily.Team, "Windows 10 Team (Surface Hub)", new(2025, 10, 14), Win10Team),
        new(19045, EditionFamily.Team, "Windows 10 Team (Surface Hub)", new(2025, 10, 14), Win10Team),

        new(22000, EditionFamily.HomePro, "Windows 11 version 21H2 (Home and Pro)", new(2023, 10, 10), Win11HomePro),
        new(22621, EditionFamily.HomePro, "Windows 11 version 22H2 (Home and Pro)", new(2024, 10, 8), Win11HomePro),
        new(22631, EditionFamily.HomePro, "Windows 11 version 23H2 (Home and Pro)", new(2025, 11, 11), Win11HomePro),
        new(26100, EditionFamily.HomePro, "Windows 11 version 24H2 (Home and Pro)", new(2026, 10, 13), Win11HomePro),
        new(26200, EditionFamily.HomePro, "Windows 11 version 25H2 (Home and Pro)", new(2027, 10, 12), Win11HomePro),
        new(28000, EditionFamily.HomePro, "Windows 11 version 26H1 (Home and Pro)", new(2028, 3, 14), Win11HomePro),
        new(26300, EditionFamily.HomePro, "Windows 11 version 26H2 (Home and Pro)", new(2028, 10, 10), Win11Releases),

        new(22000, EditionFamily.EnterpriseEducation, "Windows 11 version 21H2 (Enterprise and Education)", new(2024, 10, 8), Win11Enterprise),
        new(22621, EditionFamily.EnterpriseEducation, "Windows 11 version 22H2 (Enterprise and Education)", new(2025, 10, 14), Win11Enterprise),
        new(22631, EditionFamily.EnterpriseEducation, "Windows 11 version 23H2 (Enterprise and Education)", new(2026, 11, 10), Win11Enterprise),
        new(26100, EditionFamily.EnterpriseEducation, "Windows 11 version 24H2 (Enterprise and Education)", new(2027, 10, 12), Win11Enterprise),
        new(26200, EditionFamily.EnterpriseEducation, "Windows 11 version 25H2 (Enterprise and Education)", new(2028, 10, 10), Win11Enterprise),
        new(28000, EditionFamily.EnterpriseEducation, "Windows 11 version 26H1 (Enterprise and Education)", new(2029, 3, 13), Win11Enterprise),
        new(26300, EditionFamily.EnterpriseEducation, "Windows 11 version 26H2 (Enterprise and Education)", new(2029, 10, 9), Win11Releases),

        new(26100, EditionFamily.Ltsc, "Windows 11 Enterprise LTSC 2024", new(2029, 10, 9), Win11Releases),
        new(26100, EditionFamily.IotLtsc, "Windows 11 IoT Enterprise LTSC 2024", new(2034, 10, 10), Win11Releases),
    ];

    /// <summary>
    /// The EditionID value in HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion, which unlike the
    /// WMI caption is never translated. IoT Enterprise shares the Enterprise dates: Microsoft's
    /// release table has one column for "Enterprise, Education, IoT Enterprise, and Enterprise
    /// multi-session" (ServerRdsh).
    /// </summary>
    private static readonly Dictionary<string, EditionFamily> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core"] = EditionFamily.HomePro, ["CoreN"] = EditionFamily.HomePro,
        ["CoreSingleLanguage"] = EditionFamily.HomePro, ["CoreCountrySpecific"] = EditionFamily.HomePro,
        ["Professional"] = EditionFamily.HomePro, ["ProfessionalN"] = EditionFamily.HomePro,
        ["ProfessionalEducation"] = EditionFamily.HomePro, ["ProfessionalEducationN"] = EditionFamily.HomePro,
        ["ProfessionalWorkstation"] = EditionFamily.HomePro, ["ProfessionalWorkstationN"] = EditionFamily.HomePro,
        ["Enterprise"] = EditionFamily.EnterpriseEducation, ["EnterpriseN"] = EditionFamily.EnterpriseEducation,
        ["Education"] = EditionFamily.EnterpriseEducation, ["EducationN"] = EditionFamily.EnterpriseEducation,
        ["ServerRdsh"] = EditionFamily.EnterpriseEducation, ["IoTEnterprise"] = EditionFamily.EnterpriseEducation,
        ["EnterpriseS"] = EditionFamily.Ltsc, ["EnterpriseSN"] = EditionFamily.Ltsc,
        ["IoTEnterpriseS"] = EditionFamily.IotLtsc, ["IoTEnterpriseSK"] = EditionFamily.IotLtsc,
        ["PPIPro"] = EditionFamily.Team,
    };

    public static EditionFamily FamilyOf(string editionId) =>
        Families.TryGetValue(editionId, out var family) ? family : EditionFamily.Unknown;

    /// <summary>Home (EditionID "Core..."): no BitLocker, and outside Microsoft's company ESU.</summary>
    public static bool IsHome(string editionId) => editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Today, for support dates: the clock, but never earlier than <see cref="CheckedOn"/>. The
    /// app cannot run before the day its table was checked, so a date already past then needs no
    /// clock, and a clock set back a year cannot make an ended Windows look supported.
    /// </summary>
    public static DateOnly Today(DateTime clockUtc)
    {
        var clock = DateOnly.FromDateTime(clockUtc);
        return clock > CheckedOn ? clock : CheckedOn;
    }

    /// <summary>Whether <see cref="Assess"/> needs this PC's ESU licence rows: Windows 10 22H2 past its end, not Home (Home has none to read).</summary>
    public static bool NeedsEsuLicences(int build, string editionId, DateTime clockUtc) =>
        IsWindows10GeneralRelease(build, editionId) && !IsHome(editionId) &&
        Today(clockUtc) > Find(build, FamilyOf(editionId))!.LastDay;

    private static bool IsWindows10GeneralRelease(int build, string editionId) =>
        build == Windows10Build && FamilyOf(editionId) is EditionFamily.HomePro or EditionFamily.EnterpriseEducation;

    private static SupportEnd? Find(int build, EditionFamily family) =>
        Table.FirstOrDefault(e => e.Build == build && e.Family == family);

    /// <summary>
    /// The verdict for this Windows. <paramref name="esu"/> is ignored unless
    /// <see cref="NeedsEsuLicences"/> says it is needed; pass what was read, or nothing.
    /// </summary>
    public static SupportVerdict Assess(int build, int ubr, string editionId, IReadOnlyList<EsuLicence> esu, DateTime clockUtc)
    {
        var today = Today(clockUtc);
        var family = FamilyOf(editionId);

        if (build is >= 10240 and < Windows10Build && family is EditionFamily.HomePro or EditionFamily.EnterpriseEducation)
        {
            return new SupportVerdict(false, string.Create(Inv,
                $"Windows 10 build {build} is a version older than 22H2: it is out of support, and Extended Security Updates need 22H2."));
        }

        if (Find(build, family) is not { } end)
        {
            return new SupportVerdict(null, string.Create(Inv,
                $"This Windows (build {build}, edition {editionId}) is not in this app's support table (checked {CheckedOn:yyyy-MM-dd}), " +
                $"so whether it still gets security updates was NOT graded."));
        }

        if (today <= end.LastDay)
        {
            var left = end.LastDay.DayNumber - today.DayNumber;
            return new SupportVerdict(true, string.Create(Inv, $"{end.Release} gets security updates until {end.LastDay:yyyy-MM-dd}."),
                left <= EndingSoonDays ? string.Create(Inv, $"Support for {end.Release} ends on {end.LastDay:yyyy-MM-dd}.") : null);
        }

        return IsWindows10GeneralRelease(build, editionId)
            ? AssessWindows10Esu(end, ubr, editionId, esu, today)
            : new SupportVerdict(false, string.Create(Inv, $"{end.Release} left support on {end.LastDay:yyyy-MM-dd}: it gets no more security updates."));
    }

    private static SupportVerdict AssessWindows10Esu(SupportEnd end, int ubr, string editionId, IReadOnlyList<EsuLicence> esu, DateOnly today)
    {
        var home = IsHome(editionId);
        const string homeLimits = " Windows 10 Home: Microsoft's company ESU covers Pro, Enterprise and Education only, and BitLocker drive encryption needs Pro.";
        var covering = EsuYears.FirstOrDefault(y => today <= y.LastDay);
        if (covering is null)
        {
            var last = EsuYears[^1];
            return new SupportVerdict(false, string.Create(Inv,
                $"Windows 10 gets no more security updates: Microsoft's Extended Security Updates ended on {last.LastDay:yyyy-MM-dd}, the last day of Year {last.Year}."));
        }

        bool Active(EsuYear year) => esu.Any(l => l.Id == year.ActivationId && l.Status == 1);

        // A company ESU licence, once any year's is active, decides on its own. The build then
        // proves only that the years it covered sent updates: a PC licensed for Year 1 alone runs
        // Year 1's last build after Year 1 ends, and no build tells which year covers today.
        if (!home && EsuYears.Any(Active))
        {
            return Active(covering)
                ? CoveredByLicence(end, covering, Active, today)
                : new SupportVerdict(false, string.Create(Inv,
                    $"No Windows security updates: this PC has no active Extended Security Updates licence for Year {covering.Year}, " +
                    $"the year that covers today (to {covering.LastDay:yyyy-MM-dd}).") + LicenceHint(esu, covering, Active));
        }

        // With no licence active, the build shows ESU updates reached this PC, from a programme that
        // leaves no licence row this app can match: consumer ESU, or a company ESU that needs no key
        // on the PC, such as through Windows 365 or Azure Virtual Desktop
        // (https://learn.microsoft.com/en-us/windows/whats-new/enable-extended-security-updates-virtual).
        // It cannot tell which, so it counts the build only while consumer ESU runs: after that a
        // build proves only that updates once came. How recently they came is the update-age row's job.
        var consumerStillRuns = today <= ConsumerEsuLastDay;
        var arrived = string.Create(Inv,
            $"{end.Release} left general support on {end.LastDay:yyyy-MM-dd}, and Extended Security Updates have reached this PC: " +
            $"build 19045.{ubr} is newer than 19045.{Windows10LastBuildWithoutEsu}, the last build Microsoft offers to a PC without them.");
        const string keyless = "a company ESU that needs no key on the PC, such as through Windows 365 or Azure Virtual Desktop";
        if (ubr > Windows10LastBuildWithoutEsu && consumerStillRuns)
        {
            var left = ConsumerEsuLastDay.DayNumber - today.DayNumber;
            if (home)
            {
                return new SupportVerdict(true, arrived + string.Create(Inv,
                        $" On Home that can only be consumer ESU, which is for personal use only and ends {ConsumerEsuLastDay:yyyy-MM-dd}.") + homeLimits,
                    left <= EndingSoonDays
                        ? string.Create(Inv, $"Consumer Extended Security Updates end on {ConsumerEsuLastDay:yyyy-MM-dd}, and Windows 10 Home cannot get the company ESU.")
                        : null);
            }
            // Microsoft's consumer ESU is for "Home, Professional, Pro Education, or Workstations"
            // (https://www.microsoft.com/en-us/windows/extended-security-updates): never Enterprise or Education.
            return new SupportVerdict(true, arrived + (FamilyOf(editionId) == EditionFamily.HomePro
                    ? string.Create(Inv, $" No company ESU licence is active here, so this is consumer ESU (personal use only, to {ConsumerEsuLastDay:yyyy-MM-dd}) or {keyless}.")
                    : $" No company ESU licence is active here, and consumer ESU is not offered for this edition, so this is {keyless}."),
                left <= EndingSoonDays
                    ? string.Create(Inv,
                        $"This PC's Extended Security Updates show only in its build, and consumer ESU ends on {ConsumerEsuLastDay:yyyy-MM-dd}: " +
                        $"after that this app no longer counts the build as proof that updates still arrive. If this is a company PC, activate its ESU licence for Year {covering.Year}, which this app can check.")
                    : null);
        }

        string detail;
        // Enterprise and Education never get consumer ESU, so on them the build can only mean a keyless
        // company ESU, which runs to Year 3's end. This app cannot see it, so it judges nothing rather
        // than call a PC that may well be covered unsupported (rule 3).
        if (ubr > Windows10LastBuildWithoutEsu && !home && FamilyOf(editionId) != EditionFamily.HomePro && today <= EsuYears[^1].LastDay)
        {
            return new SupportVerdict(null, arrived + string.Create(Inv,
                $" No company ESU licence is active here, and consumer ESU is not offered for this edition, so this would be {keyless}, " +
                $"which this app cannot see: whether security updates still arrive was NOT established. Check the ESU in Windows 365 or Azure."));
        }
        if (ubr > Windows10LastBuildWithoutEsu && !home)
        {
            detail = arrived + string.Create(Inv,
                $" But no company ESU licence is active here, and consumer ESU ended on {ConsumerEsuLastDay:yyyy-MM-dd}, so nothing on this PC shows " +
                $"that security updates still arrive. If it gets {keyless}, check it there: this app cannot see one.");
        }
        else
        {
            detail = home && !consumerStillRuns
                ? string.Create(Inv, $"No Windows security updates: consumer Extended Security Updates ended on {ConsumerEsuLastDay:yyyy-MM-dd}.")
                : string.Create(Inv, $"No Windows security updates since October 2025: no Extended Security Updates licence is active, and this build (19045.{ubr}) has nothing newer.");
        }
        return new SupportVerdict(false, home ? detail + homeLimits : detail + LicenceHint(esu, covering, Active));
    }

    /// <summary>
    /// The verdict when the covering year's licence is active. Years activated in a row extend the
    /// cover: with Years 2 and 3 both active, Year 2's end is not a deadline.
    /// </summary>
    private static SupportVerdict CoveredByLicence(SupportEnd end, EsuYear covering, Func<EsuYear, bool> active, DateOnly today)
    {
        var last = covering;
        while (EsuYears.FirstOrDefault(y => y.Year == last.Year + 1) is { } next && active(next)) last = next;

        var left = last.LastDay.DayNumber - today.DayNumber;
        string? ending = left > EndingSoonDays ? null
            : last.Year < EsuYears[^1].Year
                ? string.Create(Inv, $"This PC's Extended Security Updates Year {last.Year} ends on {last.LastDay:yyyy-MM-dd}: activate Year {last.Year + 1} before then, or move the PC off Windows 10.")
                : string.Create(Inv, $"Extended Security Updates for Windows 10 end for good on {last.LastDay:yyyy-MM-dd}: move this PC off Windows 10 before then.");
        var licences = last == covering
            ? string.Create(Inv, $"the Year {covering.Year} licence is active")
            : string.Create(Inv, $"the licences for Year {covering.Year} through Year {last.Year} are active");
        return new SupportVerdict(true, string.Create(Inv,
            $"{end.Release} left general support on {end.LastDay:yyyy-MM-dd}; this PC is covered by Microsoft's Extended Security Updates: " +
            $"{licences}, to {last.LastDay:yyyy-MM-dd}."), ending);
    }

    /// <summary>What to do about a covering year that is not active: a key that only needs activating, or an earlier year that has ended.</summary>
    private static string LicenceHint(IReadOnlyList<EsuLicence> esu, EsuYear covering, Func<EsuYear, bool> active)
    {
        if (esu.FirstOrDefault(l => l.Id == covering.ActivationId && l.KeyInstalled && l.Status != 1) is { } inactive)
        {
            return string.Create(Inv,
                $" An ESU Year {covering.Year} key is installed but not activated (licence status {inactive.Status}): " +
                $"activate it with slmgr /ato {covering.ActivationId}.");
        }
        return EsuYears.LastOrDefault(y => y.Year < covering.Year && active(y)) is { } earlier
            ? string.Create(Inv,
                $" The Year {earlier.Year} licence is active, but Year {earlier.Year} ended on {earlier.LastDay:yyyy-MM-dd}; " +
                $"Year {covering.Year} is needed now.")
            : "";
    }

    /// <summary>
    /// Adds os.supported and os.support_ending from the reads. Each read throws when it cannot
    /// finish; that is recorded as a failure and os.supported is left out, because a support
    /// verdict built on a licence or registry nobody read is a guess (rule 1). A Windows this
    /// table does not know is a Note, not a failure: nothing broke.
    /// </summary>
    /// <param name="readEditionAndUbr">The EditionID and UBR values from the registry.</param>
    /// <param name="readEsuLicences">The ESU add-on rows; called only when they are needed.</param>
    public static void AddMetrics(List<Metric> metrics, ModuleFailures failures, int build,
        Func<(string EditionId, int Ubr)> readEditionAndUbr, Func<IReadOnlyList<EsuLicence>> readEsuLicences, DateTime clockUtc)
    {
        string editionId;
        int ubr;
        try
        {
            (editionId, ubr) = readEditionAndUbr();
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            failures.Record("Windows edition and build", $"{e.Message} — whether this Windows still gets security updates was NOT checked.");
            return;
        }

        if (IsHome(editionId))
        {
            metrics.Add(new Metric("os.home_edition", "Windows edition", "Home", HealthCategory.SoftwareOs, InfoOnly: true,
                Detail: "Home has no BitLocker drive encryption (it needs Pro, Enterprise or Education), and on Windows 10 it is outside " +
                        "Microsoft's company Extended Security Updates, which cover Pro, Enterprise and Education only. " +
                        "On a company laptop, either one needs a Pro licence first."));
        }

        IReadOnlyList<EsuLicence> esu = [];
        if (NeedsEsuLicences(build, editionId, clockUtc))
        {
            try
            {
                esu = readEsuLicences();
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                failures.Record("Extended Security Updates licence",
                    $"{e.Message} — whether this Windows 10 PC still gets security updates was NOT checked.");
                return;
            }
        }

        var verdict = Assess(build, ubr, editionId, esu, clockUtc);
        if (verdict.Supported is not { } supported)
        {
            failures.Note(verdict.Detail);
            return;
        }
        metrics.Add(new Metric("os.supported", "OS support status", supported, HealthCategory.SoftwareOs, Detail: verdict.Detail));
        if (verdict.EndingSoon is { } ending)
        {
            metrics.Add(new Metric("os.support_ending", "Windows support ending soon", true, HealthCategory.SoftwareOs, Detail: ending));
        }
    }

    /// <summary>The ways a registry or WMI read fails on a real PC.</summary>
    public static bool IsReadFailure(Exception e) =>
        e is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidDataException
            or System.Management.ManagementException or System.Runtime.InteropServices.COMException;

    /// <summary>
    /// The Windows licence among SoftwareLicensingProduct rows: the one with a key installed that
    /// is not an add-on. Once an ESU key is installed, its add-on row has a key too, and taking
    /// the first row with a key reported the ESU year as "the Windows licence", MAK channel
    /// included (OS-001).
    /// </summary>
    public static Dictionary<string, object?>? WindowsLicence(IEnumerable<Dictionary<string, object?>> products) =>
        products.FirstOrDefault(p =>
            !(p.TryGetValue("LicenseIsAddon", out var addon) && addon is true) &&
            p.TryGetValue("PartialProductKey", out var key) && key is string { Length: > 0 });

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
}
