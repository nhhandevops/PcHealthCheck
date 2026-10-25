using System.Globalization;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Storage;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Drive SMART health. Reads the ATA SMART attribute table Windows exposes through
/// WMI (root\wmi), which covers SATA/ATA drives; NVMe drives report their health
/// through the storage reliability counters module instead, and USB-bridged drives
/// often expose neither — all three cases are reported honestly rather than as zeros.
///
/// Strictly read-only: no self-tests, no configuration changes. (Bundling smartctl for
/// deeper per-vendor and NVMe attributes is a separate, later step — see README.)
/// </summary>
internal sealed class DiskSmartModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "disk.smart",
        Name = "Drive SMART health",
        Description = "The drive's own failure indicators: reallocated and pending sectors, uncorrectable errors, cable (CRC) errors, temperature and power-on hours.",
        Reads = ["SMART attribute tables, and the failure limits the drive's maker set (WMI root\\wmi)", "Disk model/serial inventory (WMI)"],
        NeedsAdmin = true,
        EstimatedDuration = TimeSpan.FromSeconds(6),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        // Inventory first: PNPDeviceID is what links a SMART instance to a real drive.
        var drives = Wmi.TryQuery(@"root\cimv2",
            "SELECT Model, PNPDeviceID, InterfaceType, Index, Size FROM Win32_DiskDrive",
            failures, "Disk inventory");

        var byPnpId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in drives)
        {
            var pnp = Wmi.GetString(drive, "PNPDeviceID");
            if (string.IsNullOrEmpty(pnp)) continue;
            var model = Wmi.GetString(drive, "Model")?.Trim() ?? $"Disk {Wmi.GetString(drive, "Index")}";
            var sizeGb = Wmi.Get<ulong>(drive, "Size") is { } size ? $" — {Math.Round(size / 1e9)} GB" : "";
            byPnpId[pnp] = $"{model}{sizeGb}";
        }

        var status = Wmi.TryQuery(@"root\wmi",
            "SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus", out var statusError);
        var data = Wmi.TryQuery(@"root\wmi",
            "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", out var dataError);
        var thresholdRows = Wmi.TryQuery(@"root\wmi",
            "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", out var thresholdError);
        var thresholdsByInstance = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in thresholdRows)
        {
            if (Wmi.GetString(row, "InstanceName") is { } instance)
                thresholdsByInstance[instance] = SmartAttributeParser.ParseThresholds(row.GetValueOrDefault("VendorSpecific") as byte[]);
        }

        if (statusError is not null && dataError is not null)
        {
            // What this refusal MEANS is looked up now instead of asserted. The sentence that
            // stood here blamed missing administrator rights without checking either half of the
            // claim, and on 2026-10-25 it told the operator of an elevated scan of an NVMe-only
            // ThinkPad to go and fix a UAC problem that did not exist, while that drive's health
            // was already in the same report under disk.reliability (SMART-001). Both facts it
            // needed were to hand: context.IsElevated, and the bus each disk sits on - the same
            // MSFT_PhysicalDisk.BusType the reliability and speed checks already read.
            var busRows = Wmi.TryQuery(@"root\Microsoft\Windows\Storage",
                "SELECT BusType FROM MSFT_PhysicalDisk", out var busError);
            var busTypes = busRows.ConvertAll(row => Wmi.Get<ushort>(row, "BusType"));
            var reason = SmartAvailability.Diagnose(context.IsElevated, busError is null, busTypes);

            if (reason == SmartUnavailableReason.NoAtaDrivePresent)
            {
                // A deliberate non-check, not a broken one, so it must not degrade the module:
                // nearly every laptop made this decade is NVMe, and failing here fires the
                // report's "some checks could not finish" banner - and the fleet kit's CHECK
                // AGAIN - on healthy machines until nobody reads either. The code twelve lines
                // below already takes exactly this position for the mixed case ("a USB or NVMe
                // drive that has no ATA SMART table to give is a fact about the hardware"); it
                // was simply unreachable when EVERY drive is like that, because then the WMI
                // classes refuse outright and the early return fired first.
                //
                // This does not let an unread drive pass as healthy. The module emits no metric
                // and no verdict either way, so nothing in the report says the drive is fine;
                // whether the drive was actually read is disk.reliability's answer to give, and
                // when that check comes back empty too the fleet row still says "No drive inside
                // this laptop reported its own health, so drive health was NOT checked".
                // Resolve() still returns Failed if the disk inventory above could not be read,
                // because then this machine's drives are not known well enough to excuse it.
                failures.Note(SmartAvailability.Explain(reason, statusError));
                return Task.FromResult(failures.Resolve(ModuleStatus.Skipped, gotAnything: false));
            }

            notes.Add(SmartAvailability.Explain(reason, statusError));
            return Task.FromResult(ModuleStatus.Failed);
        }

        // The two classes are two different halves of the SMART check: the status class carries the
        // drive's own "I am about to fail" flag, the data class carries the attribute table with the
        // reallocated and pending sector counts. Requiring BOTH to fail before saying anything meant
        // that losing one half left a report with no trace of the loss — a drive whose attribute
        // table would not read looked exactly like a drive with nothing wrong with it.
        failures.RecordIf(
            "The drives' own failure-prediction flag could not be read, so this scan cannot say whether any drive is warning about itself",
            statusError);
        failures.RecordIf(
            "The SMART attribute tables could not be read, so reallocated and pending sectors — the strongest warning signs there are — were NOT checked",
            dataError);
        if (dataError is null)
        {
            failures.RecordIf(
                "The drives' own failure thresholds could not be read, so whether any SMART attribute has reached its limit was NOT checked",
                thresholdError);
        }

        foreach (var row in status)
        {
            var subject = ResolveSubject(Wmi.GetString(row, "InstanceName"), byPnpId);
            if (Wmi.Get<bool>(row, "PredictFailure") is { } predict)
            {
                metrics.Add(new Metric("smart.predict_failure", "Drive self-assessment", !predict,
                    HealthCategory.Storage, Subject: subject,
                    Detail: predict
                        ? "The drive's own firmware predicts imminent failure. Treat the data on it as at risk right now."
                        : "The drive's firmware reports it is not predicting failure. (Note: about a quarter of failures give no warning.)"));
            }
        }

        var drivesWithAttributes = 0;
        foreach (var row in data)
        {
            var subject = ResolveSubject(Wmi.GetString(row, "InstanceName"), byPnpId);
            var vendorSpecific = row.GetValueOrDefault("VendorSpecific") as byte[];
            var attributes = SmartAttributeParser.Parse(vendorSpecific);
            if (attributes.Count == 0) continue;
            drivesWithAttributes++;
            AddAttributeMetrics(metrics, subject, attributes);
            if (Wmi.GetString(row, "InstanceName") is { } instance &&
                thresholdsByInstance.TryGetValue(instance, out var thresholds) && thresholds.Count > 0)
            {
                AddThresholdMetric(metrics, subject, attributes, thresholds);
            }
        }

        var withoutSmart = Math.Max(0, drives.Count - Math.Max(status.Count, data.Count));
        if (withoutSmart > 0)
        {
            // Not a failure: a USB or NVMe drive that has no ATA SMART table to give is a fact
            // about the hardware, not a check that broke, and must not raise the report's banner.
            failures.Note($"{withoutSmart} of {drives.Count} drives do not expose SMART through Windows " +
                          "(typical for USB-connected drives and for NVMe SSDs, whose health is covered by the storage reliability check).");
        }
        if (drivesWithAttributes == 0 && metrics.Count == 0)
        {
            failures.Note("No drive returned a readable SMART attribute table.");
            return Task.FromResult(failures.Resolve(ModuleStatus.PartiallyCompleted, gotAnything: false));
        }

        return Task.FromResult(failures.Resolve());
    }

    private static void AddAttributeMetrics(List<Metric> metrics, string subject, IReadOnlyList<SmartAttribute> attributes)
    {
        var map = attributes.ToDictionary(a => a.Id);

        void AddCounter(int id, string metricId, string label, string detail)
        {
            if (!map.TryGetValue(id, out var attribute)) return;
            metrics.Add(new Metric(metricId, label, (double)attribute.MeaningfulRaw, HealthCategory.Storage,
                Subject: subject, Detail: $"{detail} (SMART attribute {id}, normalized value {attribute.Current}/{attribute.Worst} worst)"));
        }

        AddCounter(5, "smart.reallocated", "Reallocated sectors",
            "Sectors the drive has already retired and replaced with spares.");
        AddCounter(187, "smart.reported_uncorrectable", "Uncorrectable errors reported",
            "Errors the drive could not correct — strongly associated with imminent failure.");
        AddCounter(188, "smart.command_timeout", "Command timeouts",
            "Commands that never completed; often cabling or power, sometimes the drive.");
        AddCounter(197, "smart.pending_sectors", "Pending (unstable) sectors",
            "Sectors waiting to be re-read or retired. Growth here is the strongest warning sign there is.");
        AddCounter(198, "smart.offline_uncorrectable", "Offline uncorrectable sectors",
            "Sectors that failed the drive's own background scan.");
        AddCounter(199, "smart.crc_errors", "Interface (CRC) errors",
            "Corrupted transfers between the drive and the PC — usually the DATA CABLE, not the drive itself.");
        AddCounter(193, "smart.load_cycles", "Load/unload cycles",
            "Head park cycles; most drives are rated for around 600,000.");

        if (map.TryGetValue(194, out var temperature) || map.TryGetValue(190, out temperature))
        {
            metrics.Add(new Metric("smart.temperature_c", "Drive temperature", (double)temperature.MeaningfulRaw,
                HealthCategory.Storage, Unit: "°C", Subject: subject,
                Detail: "Mechanical drives last longest below about 40 °C."));
        }

        if (map.TryGetValue(9, out var hours))
        {
            var years = Math.Round(hours.MeaningfulRaw / 8760.0, 1);
            metrics.Add(new Metric("smart.power_on_hours", "Power-on hours", (double)hours.MeaningfulRaw,
                HealthCategory.Storage, Unit: "h", Subject: subject, InfoOnly: true,
                Detail: $"About {years} years of powered-on time."));
        }

        foreach (var id in SmartAttributeParser.SsdLifeLeftAttributes)
        {
            if (!map.TryGetValue(id, out var life)) continue;
            var consumed = Math.Clamp(100 - life.Current, 0, 100);
            metrics.Add(new Metric("smart.ssd_percent_consumed", "SSD endurance used", (double)consumed,
                HealthCategory.Storage, Unit: "%", Subject: subject,
                Detail: $"Estimated from SMART attribute {id} ({SmartAttributeParser.NameFor(id)} = {life.Current}). " +
                        "Vendors vary in how they report this, so treat it as approximate."));
            break;
        }
    }

    private static void AddThresholdMetric(
        List<Metric> metrics, string subject, IReadOnlyList<SmartAttribute> attributes, IReadOnlyDictionary<int, int> thresholds)
    {
        var (now, past) = SmartAttributeParser.AtThreshold(attributes, thresholds);
        string Describe(IEnumerable<SmartAttribute> list, Func<SmartAttribute, int> value) => string.Join(", ",
            list.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Name} ({value(a)}, limit {thresholds[a.Id]})")));

        var detail = now.Count > 0
            ? $"The drive's own firmware rates these as failed, at or below the limit its maker set: {Describe(now, a => a.Current)}."
            : "No attribute is at the failure limit the drive's maker set for it.";
        if (past.Count > 0)
            detail += $" These reached their limit at some point and have since recovered: {Describe(past, a => a.Worst)}.";

        metrics.Add(new Metric("smart.attributes_at_threshold", "Attributes at the drive's own failure limit", (double)now.Count,
            HealthCategory.Storage, Subject: subject, Detail: detail));
    }

    /// <summary>WMI SMART instance names are the device's PNPDeviceID with a "_0" suffix.</summary>
    private static string ResolveSubject(string? instanceName, Dictionary<string, string> byPnpId)
    {
        if (string.IsNullOrEmpty(instanceName)) return "Unknown drive";
        var trimmed = instanceName.EndsWith("_0", StringComparison.Ordinal) ? instanceName[..^2] : instanceName;
        return byPnpId.TryGetValue(trimmed, out var friendly) ? friendly : trimmed;
    }
}
