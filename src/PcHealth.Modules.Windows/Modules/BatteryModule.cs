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
        Reads = ["Battery presence (WMI)", "powercfg battery report"],
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
            var index = 0;
            foreach (var battery in doc.Descendants(ns + "Battery"))
            {
                index++;
                var design = ParseCapacity(battery.Element(ns + "DesignCapacity")?.Value);
                var fullCharge = ParseCapacity(battery.Element(ns + "FullChargeCapacity")?.Value);
                var cycles = ParseCapacity(battery.Element(ns + "CycleCount")?.Value);
                var subject = doc.Descendants(ns + "Battery").Count() > 1 ? $"Battery {index}" : "Battery";

                if (design is > 0 && fullCharge is > 0)
                {
                    var pct = Math.Round(fullCharge.Value * 100.0 / design.Value, 1);
                    metrics.Add(new Metric("battery.capacity_pct", "Battery capacity vs design", pct,
                        HealthCategory.Battery, Unit: "%", Subject: subject,
                        Detail: $"Full-charge {fullCharge:N0} mWh of {design:N0} mWh design. If the battery is swollen, stop using the device immediately."));
                }
                if (cycles is > 0)
                {
                    metrics.Add(new Metric("battery.cycles", "Charge cycles", (double)cycles.Value,
                        HealthCategory.Battery, Subject: subject, InfoOnly: true,
                        Detail: "Most consumer batteries are rated for 300–1000 cycles."));
                }
            }
            if (metrics.Count == 0)
            {
                notes.Add("Battery report contained no capacity data (some batteries do not expose it).");
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
