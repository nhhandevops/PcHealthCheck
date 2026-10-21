using System.Text.Json;
using System.Text.RegularExpressions;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Storage reliability counters — the health path that works for NVMe SSDs (which do
/// not expose the classic ATA SMART table through Windows): endurance used, temperature,
/// uncorrected read/write errors, power-on hours. Also checks each volume's "dirty" bit.
/// </summary>
internal sealed partial class StorageReliabilityModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "disk.reliability",
        Name = "Drive wear & error counters",
        Description = "SSD endurance used, drive temperature, and uncorrected read/write errors as reported by the storage driver. This is how NVMe SSD health is measured.",
        Reads = ["Storage reliability counters (WMI)", "Volume dirty flag (fsutil, read-only query)"],
        NeedsAdmin = true,
        EstimatedDuration = TimeSpan.FromSeconds(8),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        var disks = new Dictionary<string, (string Name, ushort MediaType, ushort BusType)>(StringComparer.OrdinalIgnoreCase);
        var physicalDisks = Wmi.TryQuery(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk",
            failures, "Physical disk inventory");
        foreach (var disk in physicalDisks)
        {
            var deviceId = Wmi.GetString(disk, "DeviceId");
            if (deviceId is null) continue;
            var sizeGb = Wmi.Get<ulong>(disk, "Size") is { } size ? $" — {Math.Round(size / 1e9)} GB" : "";
            disks[deviceId] = ($"{Wmi.GetString(disk, "FriendlyName")?.Trim()}{sizeGb}", Wmi.Get<ushort>(disk, "MediaType") ?? 0,
                Wmi.Get<ushort>(disk, "BusType") ?? 0);
        }

        var counters = Wmi.TryQuery(@"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_StorageReliabilityCounter", out var counterError);
        var associationRouteAnswered = false;
        if (counters.Count == 0)
        {
            // The class is normally reached through an association; fall back to the
            // PowerShell pipeline that does exactly that, projected as JSON (locale-proof).
            // A direct query that returned nothing is the ordinary case here, so its error only
            // becomes a failure once this second route has also failed to answer the question —
            // which is why the fallback reports whether it ran, not just what it found.
            failures.Note("Reliability counters were not directly enumerable; used the storage association instead.");
            var (rows, completed) = await QueryViaPowerShellAsync(context, failures, ct).ConfigureAwait(false);
            associationRouteAnswered = completed;
            foreach (var row in rows) counters.Add(row);
        }

        if (counters.Count == 0 && associationRouteAnswered)
        {
            // The association route ran to completion and found nothing, which IS the answer the
            // direct query failed to give: no drive here reports counters. Recording the direct
            // query's error as a failure at this point would degrade the module — and fire the
            // report's amber "Some checks could not finish" banner — on every ordinary PC whose
            // drives (SATA HDDs, older SATA SSDs) expose no counters, while also suppressing the
            // true finding that the successful read earned.
            //
            // The other way out of an empty result, an association route that did not complete,
            // adds nothing here: it has already recorded its own failure, so the module degrades
            // correctly, and repeating the direct query's error would put two entries in that
            // banner's semicolon-joined reason list for one unanswered question.
            failures.Note(counterError is null
                ? "No drive on this system exposes reliability counters."
                : $"No drive on this system exposes reliability counters (the direct query was unavailable: {counterError}).");
        }

        foreach (var counter in counters)
        {
            var deviceId = Wmi.GetString(counter, "DeviceId");
            var subject = deviceId is not null && disks.TryGetValue(deviceId, out var disk)
                ? disk.Name
                : $"Disk {deviceId ?? "?"}";
            var isSsd = deviceId is not null && disks.TryGetValue(deviceId, out var d) && d.MediaType == 4;

            if (ReliabilityCounterCheck.IsPlaceholder(
                    Wmi.Get<ushort>(counter, "Wear"), Wmi.Get<ushort>(counter, "Temperature"),
                    Wmi.Get<ulong>(counter, "PowerOnHours"), Wmi.Get<ulong>(counter, "ReadErrorsTotal"),
                    Wmi.Get<ulong>(counter, "WriteErrorsTotal")))
            {
                // Expected for USB enclosures, so a note rather than a failure: degrading here
                // would fire the "could not finish" banner every time a USB drive is plugged in.
                failures.Note($"{subject}: Windows returned an empty health record for this drive (typical of USB " +
                              "enclosures), so its wear and temperature were not read - not the same as healthy.");
                continue;
            }

            var onUsb = deviceId is not null && disks.TryGetValue(deviceId, out var u) && u.BusType == ReliabilityCounterCheck.BusTypeUsb;

            if (Wmi.Get<ushort>(counter, "Wear") is { } wear && (isSsd || wear > 0))
            {
                if (ReliabilityCounterCheck.WearIsTrustworthy(wear, onUsb))
                {
                    metrics.Add(new Metric("storage.wear_pct", "SSD endurance used", (double)wear,
                        HealthCategory.Storage, Unit: "%", Subject: subject,
                        Detail: "Percentage of the drive's rated write endurance consumed. 100% does not mean instant failure, but it is past its warranty life."));
                }
                else
                {
                    failures.Note($"{subject}: connected over USB, whose adapter reported 0% wear. USB adapters often send a " +
                                  "placeholder, and some report hard disks as SSDs, so this was not graded.");
                }
            }

            if (Wmi.Get<ushort>(counter, "Temperature") is { } temp && temp > 0)
            {
                metrics.Add(new Metric("storage.temperature_c", "Drive temperature", (double)temp,
                    HealthCategory.Storage, Unit: "°C", Subject: subject,
                    Detail: Wmi.Get<ushort>(counter, "TemperatureMax") is { } max && max > 0
                        ? $"The drive reports its own maximum safe temperature as {max} °C."
                        : "NVMe SSDs commonly run 50–70 °C under load, which is within spec."));
            }

            AddErrorCounter(counter, "ReadErrorsUncorrected", "storage.read_errors_uncorrected",
                "Uncorrected read errors", "Data the drive could not read back correctly.", subject, metrics);
            AddErrorCounter(counter, "WriteErrorsUncorrected", "storage.write_errors_uncorrected",
                "Uncorrected write errors", "Writes the drive could not complete correctly.", subject, metrics);

            if (Wmi.Get<ulong>(counter, "PowerOnHours") is { } hours && hours > 0)
            {
                metrics.Add(new Metric("storage.power_on_hours", "Power-on hours", (double)hours,
                    HealthCategory.Storage, Unit: "h", Subject: subject, InfoOnly: true,
                    Detail: $"About {Math.Round(hours / 8760.0, 1)} years of powered-on time."));
            }
        }

        await AddVolumeDirtyFlagsAsync(context, metrics, failures, ct).ConfigureAwait(false);

        // Counting metrics cannot answer "did this module get what it came for": the dirty-flag
        // check adds one metric per fixed volume whatever happened to the reliability counters, so
        // the count is never zero on a real PC. Losing the wear, temperature and error counters
        // entirely therefore reported Completed. Only a recorded failure decides that now; the
        // count is left to distinguish a partial result from having gathered nothing at all.
        var gotAnything = metrics.Count > 0;
        return failures.Resolve(gotAnything ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted, gotAnything);
    }

    private static void AddErrorCounter(
        Dictionary<string, object?> counter, string property, string metricId,
        string label, string detail, string subject, List<Metric> metrics)
    {
        if (Wmi.Get<ulong>(counter, property) is not { } value) return;
        metrics.Add(new Metric(metricId, label, (double)value, HealthCategory.Storage,
            Subject: subject, Detail: detail));
    }

    /// <summary>
    /// Runs the storage-association route. <c>Completed</c> says whether the route actually
    /// answered the question, which no row count can express: zero rows from a run that finished
    /// is the finding "no drive here reports counters", zero rows from a run that broke is a
    /// question never asked. Returning only rows left the caller unable to tell them apart.
    /// </summary>
    private static async Task<(List<Dictionary<string, object?>> Rows, bool Completed)> QueryViaPowerShellAsync(
        ModuleContext context, ModuleFailures failures, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        const string script = "Get-PhysicalDisk | ForEach-Object { $d = $_; $c = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
                              "if ($c) { [pscustomobject]@{ DeviceId = $d.DeviceId; Wear = $c.Wear; Temperature = $c.Temperature; " +
                              "TemperatureMax = $c.TemperatureMax; PowerOnHours = $c.PowerOnHours; " +
                              "ReadErrorsUncorrected = $c.ReadErrorsUncorrected; WriteErrorsUncorrected = $c.WriteErrorsUncorrected } } } | ConvertTo-Json -Compress";

        var run = await context.Processes.RunAsync("disk.reliability", "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{script}\"", TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            // This is the route that actually reaches the counters on most systems, so a run that
            // could not complete leaves their health unknown rather than absent. Empty output from
            // a run that DID complete is different: it means no drive here reports counters.
            failures.Record("Reliability counters via the storage association",
                run.TimedOut ? "the query timed out" : $"exit {run.ExitCode}");
            return (rows, false);
        }
        if (string.IsNullOrWhiteSpace(run.StdOut)) return (rows, true);

        try
        {
            using var document = JsonDocument.Parse(run.StdOut);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];
            foreach (var element in elements)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    row[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.Number => property.Value.TryGetUInt64(out var u) ? u : property.Value.GetDouble(),
                        JsonValueKind.String => property.Value.GetString(),
                        _ => null,
                    };
                }
                rows.Add(row);
            }
        }
        catch (JsonException e)
        {
            // Output arrived and could not be understood, which is not the same as a drive having
            // nothing to report — treating it as "no data" hid a read this scan never managed.
            // Unanswered for the same reason, so the caller must not conclude "no drive here
            // reports counters" from the empty list this leaves behind.
            failures.Record("Reliability counters returned output that could not be read", e.Message);
            return (rows, false);
        }
        return (rows, true);
    }

    private static async Task AddVolumeDirtyFlagsAsync(
        ModuleContext context, List<Metric> metrics, ModuleFailures failures, CancellationToken ct)
    {
        // FileSystem comes back with the letter because the dirty bit is a property of the file
        // system, not of the drive, and only the answer to "which file system" separates a volume
        // that refused to be read from one that had nothing to say.
        var volumes = Wmi.TryQuery(@"root\cimv2",
            "SELECT DeviceID, FileSystem FROM Win32_LogicalDisk WHERE DriveType = 3",
            failures, "Volume enumeration for dirty check");
        var noDirtyBit = new List<string>();
        foreach (var volume in volumes)
        {
            var letter = Wmi.GetString(volume, "DeviceID");
            if (string.IsNullOrEmpty(letter)) continue;

            // Only NTFS and ReFS carry a dirty bit; on anything else the FSCTL behind
            // "fsutil dirty query" is refused by the file system with ERROR_INVALID_FUNCTION,
            // and elevation cannot change that. DriveType 3 is no promise of either: on this
            // project's reference laptop the Google Drive mounts on G: and I: are reported as
            // fixed FAT32 volumes, as is any exFAT/FAT32 secondary internal data drive. Asking
            // those anyway and recording the refusal as a failure raised the report's amber
            // "Some checks could not finish" banner on every elevated scan of a healthy PC, and
            // a banner that is always on teaches the reader to ignore the one that matters.
            // Nothing here went unread, so this is a deliberate non-check in the same sense as a
            // USB drive that exposes no ATA SMART table.
            var fileSystem = Wmi.GetString(volume, "FileSystem")?.Trim();
            if (!string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase))
            {
                noDirtyBit.Add($"{letter} ({(string.IsNullOrEmpty(fileSystem) ? "file system not reported" : fileSystem)})");
                continue;
            }

            var run = await context.Processes.RunAsync("disk.reliability", "fsutil",
                $"dirty query {letter}", TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
            if (!run.Succeeded)
            {
                // This volume does have a dirty bit and would not give it up, so the question
                // went unanswered. Skipping it silently left the report with no row for it at
                // all, which reads as "nothing to say about that drive" rather than "this drive
                // was never asked whether Windows has queued a file-system repair for it".
                failures.Record($"Pending file-system check on {letter} could not be read",
                    run.TimedOut ? "the query timed out" : $"exit {run.ExitCode}");
                continue;
            }

            // Locale-proof: "NOT" appears in the negative form in most locales, so key on
            // the stable volume token and treat only an explicit non-dirty answer as clean.
            var isDirty = !NotDirtyRegex().IsMatch(run.StdOut);
            metrics.Add(new Metric("storage.volume_clean", "File system clean", !isDirty,
                HealthCategory.Storage, Subject: letter,
                Detail: isDirty
                    ? "Windows flagged this volume as needing a file-system check; it will run CHKDSK at the next restart."
                    : "The file system was shut down cleanly."));
        }

        // Said once for all of them rather than once each, so a PC with several cloud mounts does
        // not fill the module's note column with the same sentence repeated.
        if (noDirtyBit.Count > 0)
        {
            failures.Note($"No pending file-system check to read on {string.Join(", ", noDirtyBit)} — " +
                          "only NTFS and ReFS volumes carry a dirty bit.");
        }
    }

    [GeneratedRegex(@"\bNOT\b|\bnicht\b|\bpas\b|\bkhông\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotDirtyRegex();
}
