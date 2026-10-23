using System.Xml.Linq;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Battery wear: full-charge vs design capacity from powercfg /batteryreport.
/// The one write this module performs is the report XML inside the app's own temp
/// folder — declared on the consent row, deleted after parsing.
/// </summary>
internal sealed class BatteryModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "battery",
        Name = "Battery health",
        Description = "Battery wear (full-charge vs design capacity) and cycle count. Skipped automatically on desktops.",
        Reads = ["Battery presence and charge state (WMI)", "powercfg battery report", "Battery manufacture date (SMBIOS, via WMI)"],
        NeedsAdmin = false,
        Writes = new WriteSpec("battery-report.xml inside the app's own temp folder", 512 * 1024, "deleted right after parsing"),
        EstimatedDuration = TimeSpan.FromSeconds(6),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);
        var batteries = Wmi.TryQuery(@"root\cimv2", "SELECT Name FROM Win32_Battery", failures, "Battery query");

        // A failed query hands back exactly the same empty list a desktop does, and the old code
        // read that emptiness as proof of a desktop: a laptop whose WMI was broken was told, in
        // writing, that it had no battery. When the query did not run we do not know what this
        // machine is, so the module says so rather than claiming a skip it never earned.
        if (failures.Any)
        {
            failures.Note("Whether this PC has a battery at all could not be established, so battery wear was NOT checked.");
            return failures.Resolve(gotAnything: false);
        }

        if (batteries.Count == 0)
        {
            // The query succeeded and came back empty, which is a real answer: there is no
            // battery here. This deliberate skip must NOT degrade the status, or the "some
            // checks could not finish" banner would fire on every healthy desktop scan.
            failures.Note("No battery detected — desktop system, module skipped.");
            return ModuleStatus.Skipped;
        }

        Directory.CreateDirectory(context.AppTempDir);
        var reportPath = Path.Combine(context.AppTempDir, "battery-report.xml");
        try
        {
            var run = await context.Processes.RunAsync(Manifest.Id, "powercfg",
                $"/batteryreport /output \"{reportPath}\" /XML", TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);

            if (!run.Succeeded || !File.Exists(reportPath))
            {
                notes.Add($"powercfg /batteryreport failed (exit {run.ExitCode}): {Truncate(run.StdErr + run.StdOut)}");
                metrics.Add(new Metric("battery.present", "Battery detected", true, HealthCategory.Battery, InfoOnly: true));
                return ModuleStatus.PartiallyCompleted;
            }

            context.Receipt.Add(new FileWriteReceipt(DateTimeOffset.Now, Manifest.Id, reportPath,
                new FileInfo(reportPath).Length, "battery report for wear calculation", Deleted: false));

            var doc = XDocument.Load(reportPath);
            XNamespace ns = "http://schemas.microsoft.com/battery/2012";

            var usage = BatteryUsage.Summarize(doc.Descendants(ns + "History").Elements()
                .Select(e => ((string?)e.Attribute("ActiveAcTime"), (string?)e.Attribute("ActiveDcTime"))));
            if (usage is not null)
            {
                metrics.Add(new Metric("battery.time_on_battery_pct", "Time on battery (recent use)", usage.OnBatteryPct,
                    HealthCategory.Battery, Unit: "%", InfoOnly: true,
                    Detail: $"Of {usage.HoursInUse:0.#} hours in use covered by Windows' battery report, this share ran on battery. " +
                            "It covers only as far back as Windows has records, which after a reinstall is a few days."));
            }
            // A laptop that is almost never unplugged loses mobility to a worn battery, not data.
            var deskBound = usage is { OnBatteryPct: < 5, HoursInUse: >= 8 };

            // The manufacture date, only when it can be tied to the one battery in the report:
            // with two packs there is no reliable way to say which date belongs to which.
            var today = DateOnly.FromDateTime(DateTime.Now);
            DateOnly? made = null;
            if (doc.Descendants(ns + "Battery").Count() == 1)
            {
                var smbios = Wmi.TryQuery(@"root\wmi", "SELECT SMBiosData FROM MSSmBios_RawSMBiosTables", out _)
                    .FirstOrDefault()?.GetValueOrDefault("SMBiosData") as byte[];
                if (BatteryAge.ManufactureDates(smbios) is [var only] && only <= today && only.Year >= 1995)
                {
                    made = only;
                    metrics.Add(new Metric("battery.age_years", "Battery age",
                        Math.Round((today.DayNumber - only.DayNumber) / 365.25, 1), HealthCategory.Battery,
                        Unit: "years", InfoOnly: true,
                        Detail: $"Made {only:yyyy-MM-dd}, according to the battery's own record (SMBIOS)."));
                }
            }

            var index = 0;
            foreach (var battery in doc.Descendants(ns + "Battery"))
            {
                index++;
                var design = ParseCapacity(battery.Element(ns + "DesignCapacity")?.Value);
                var fullCharge = ParseCapacity(battery.Element(ns + "FullChargeCapacity")?.Value);
                var cycles = ParseCapacity(battery.Element(ns + "CycleCount")?.Value);
                var subject = doc.Descendants(ns + "Battery").Count() > 1 ? $"Battery {index}" : "Battery";

                var identity = string.Join(" ", new[] { battery.Element(ns + "Manufacturer")?.Value, battery.Element(ns + "Id")?.Value }
                    .Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)));
                if (identity.Length > 0)
                {
                    var chemistry = battery.Element(ns + "Chemistry")?.Value?.Trim();
                    metrics.Add(new Metric("battery.identity", "Battery reports itself as",
                        string.IsNullOrEmpty(chemistry) ? identity : $"{identity} ({chemistry})",
                        HealthCategory.Battery, Subject: subject, InfoOnly: true,
                        Detail: "The name and part number the battery's own chip reports. Compare them with the label on the " +
                                "battery and your laptop's official battery list: a part number from a different model " +
                                "usually means a third-party pack."));
                }

                if (design is > 0 && fullCharge is > 0)
                {
                    var pct = Math.Round(fullCharge.Value * 100.0 / design.Value, 1);
                    // 60% is where the rule calls it replacement territory; past that, 40% is where
                    // an unplugged laptop stops being practical.
                    var target = pct > 60 ? 60 : 40;
                    var timeline = made is { } m && BatteryAge.LossPerYear(pct, m, today) is { } perYear
                        ? $" It has lost about {perYear:0.#}% of its design capacity per year since it was made ({m:yyyy-MM-dd})" +
                          (BatteryAge.ReachesPercent(pct, m, today, target) is { } when
                              ? $"; if that continues it reaches {target}% around {when:yyyy-MM}."
                              : ".")
                        : "";
                    metrics.Add(new Metric("battery.capacity_pct", "Battery capacity vs design", pct,
                        HealthCategory.Battery, Unit: "%", Subject: subject,
                        Detail: $"Full-charge {fullCharge:N0} mWh of {design:N0} mWh design. If the battery is swollen, stop using the device immediately." +
                                timeline +
                                (deskBound && pct < 60
                                    ? $" This PC ran on battery {usage!.OnBatteryPct:0.#}% of the time recently, so the worn battery limits how " +
                                      "long it can work unplugged rather than putting anything at risk: replace it when you need to work away " +
                                      "from a socket, or at once if it swells."
                                    : "")));
                }
                if (fullCharge is > 0 && doc.Descendants(ns + "Battery").Count() == 1 &&
                    Wmi.TryQuery(@"root\wmi", "SELECT PowerOnline, Charging, Discharging, RemainingCapacity FROM BatteryStatus", out _)
                        .FirstOrDefault() is { } status &&
                    Wmi.Get<uint>(status, "RemainingCapacity") is { } remaining)
                {
                    var onMains = Wmi.Get<bool>(status, "PowerOnline") ?? false;
                    var charging = Wmi.Get<bool>(status, "Charging") ?? false;
                    metrics.Add(new Metric("battery.charge_state", "Charge right now",
                        BatteryChargeState.Describe(Math.Min(100, remaining * 100.0 / fullCharge.Value), onMains, charging,
                            Wmi.Get<bool>(status, "Discharging") ?? false),
                        HealthCategory.Battery, Subject: subject, InfoOnly: true,
                        Detail: "Not charging on mains power is usually deliberate: most laptops stop at 95-100%, and many hold " +
                                "a lower limit to slow wear (often 80%; Lenovo's conservation mode holds 55-60%). It only " +
                                "points at the battery or charger when no such limit is set."));
                }

                if (cycles is > 0)
                {
                    metrics.Add(new Metric("battery.cycles", "Charge cycles", (double)cycles.Value,
                        HealthCategory.Battery, Subject: subject, InfoOnly: true,
                        Detail: "Most consumer batteries are rated for 300–1000 cycles."));
                }
            }
            // Capacity is what this module is for. Counting metrics let the cycle count, identity or
            // usage rows stand in for it, so a report without capacity read back as Completed.
            if (!metrics.Any(m => m.Id == "battery.capacity_pct"))
            {
                notes.Add("Battery report contained no capacity data (some batteries do not expose it), so battery wear was NOT checked.");
                return ModuleStatus.PartiallyCompleted;
            }
            return ModuleStatus.Completed;
        }
        finally
        {
            if (File.Exists(reportPath))
            {
                try
                {
                    File.Delete(reportPath);
                    context.Receipt.MarkFileDeleted(reportPath);
                }
                catch (IOException) { notes.Add("Could not delete the temporary battery report."); }
            }
        }
    }

    private static double? ParseCapacity(string? raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;
}
