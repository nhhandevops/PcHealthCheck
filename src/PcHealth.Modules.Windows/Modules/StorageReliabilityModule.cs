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
        var disks = new Dictionary<string, (string Name, ushort MediaType)>(StringComparer.OrdinalIgnoreCase);
        var physicalDisks = Wmi.TryQuery(@"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, MediaType, Size FROM MSFT_PhysicalDisk", out var diskError);
        foreach (var disk in physicalDisks)
        {
            var deviceId = Wmi.GetString(disk, "DeviceId");
            if (deviceId is null) continue;
            var sizeGb = Wmi.Get<ulong>(disk, "Size") is { } size ? $" — {Math.Round(size / 1e9)} GB" : "";
            disks[deviceId] = ($"{Wmi.GetString(disk, "FriendlyName")?.Trim()}{sizeGb}", Wmi.Get<ushort>(disk, "MediaType") ?? 0);
        }
        if (diskError is not null) notes.Add($"Physical disk inventory: {diskError}");

        var counters = Wmi.TryQuery(@"root\Microsoft\Windows\Storage",
            "SELECT * FROM MSFT_StorageReliabilityCounter", out var counterError);
        if (counters.Count == 0)
        {
            // The class is normally reached through an association; fall back to the
            // PowerShell pipeline that does exactly that, projected as JSON (locale-proof).
            notes.Add("Reliability counters were not directly enumerable; used the storage association instead.");
            foreach (var row in await QueryViaPowerShellAsync(context, ct).ConfigureAwait(false)) counters.Add(row);
            if (counterError is not null && counters.Count == 0) notes.Add($"Reliability counters: {counterError}");
        }

        foreach (var counter in counters)
        {
            var deviceId = Wmi.GetString(counter, "DeviceId");
            var subject = deviceId is not null && disks.TryGetValue(deviceId, out var disk)
                ? disk.Name
                : $"Disk {deviceId ?? "?"}";
            var isSsd = deviceId is not null && disks.TryGetValue(deviceId, out var d) && d.MediaType == 4;

            if (Wmi.Get<ushort>(counter, "Wear") is { } wear && (isSsd || wear > 0))
            {
                metrics.Add(new Metric("storage.wear_pct", "SSD endurance used", (double)wear,
                    HealthCategory.Storage, Unit: "%", Subject: subject,
                    Detail: "Percentage of the drive's rated write endurance consumed. 100% does not mean instant failure, but it is past its warranty life."));
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

        if (counters.Count == 0) notes.Add("No drive on this system exposes reliability counters.");

        await AddVolumeDirtyFlagsAsync(context, metrics, notes, ct).ConfigureAwait(false);

        return metrics.Count > 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted;
    }

    private static void AddErrorCounter(
        Dictionary<string, object?> counter, string property, string metricId,
        string label, string detail, string subject, List<Metric> metrics)
    {
        if (Wmi.Get<ulong>(counter, property) is not { } value) return;
        metrics.Add(new Metric(metricId, label, (double)value, HealthCategory.Storage,
            Subject: subject, Detail: detail));
    }

    private static async Task<List<Dictionary<string, object?>>> QueryViaPowerShellAsync(ModuleContext context, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        const string script = "Get-PhysicalDisk | ForEach-Object { $d = $_; $c = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
                              "if ($c) { [pscustomobject]@{ DeviceId = $d.DeviceId; Wear = $c.Wear; Temperature = $c.Temperature; " +
                              "TemperatureMax = $c.TemperatureMax; PowerOnHours = $c.PowerOnHours; " +
                              "ReadErrorsUncorrected = $c.ReadErrorsUncorrected; WriteErrorsUncorrected = $c.WriteErrorsUncorrected } } } | ConvertTo-Json -Compress";

        var run = await context.Processes.RunAsync("disk.reliability", "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{script}\"", TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false);
        if (!run.Succeeded || string.IsNullOrWhiteSpace(run.StdOut)) return rows;

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
        catch (JsonException) { /* unparseable output is treated as "no data" */ }
        return rows;
    }

    private static async Task AddVolumeDirtyFlagsAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes, CancellationToken ct)
    {
        var volumes = Wmi.TryQuery(@"root\cimv2",
            "SELECT DeviceID FROM Win32_LogicalDisk WHERE DriveType = 3", out var error);
        foreach (var volume in volumes)
        {
            var letter = Wmi.GetString(volume, "DeviceID");
            if (string.IsNullOrEmpty(letter)) continue;

            var run = await context.Processes.RunAsync("disk.reliability", "fsutil",
                $"dirty query {letter}", TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
            if (!run.Succeeded) continue;

            // Locale-proof: "NOT" appears in the negative form in most locales, so key on
            // the stable volume token and treat only an explicit non-dirty answer as clean.
            var isDirty = !NotDirtyRegex().IsMatch(run.StdOut);
            metrics.Add(new Metric("storage.volume_clean", "File system clean", !isDirty,
                HealthCategory.Storage, Subject: letter,
                Detail: isDirty
                    ? "Windows flagged this volume as needing a file-system check; it will run CHKDSK at the next restart."
                    : "The file system was shut down cleanly."));
        }
        if (error is not null) notes.Add($"Volume enumeration for dirty check: {error}");
    }

    [GeneratedRegex(@"\bNOT\b|\bnicht\b|\bpas\b|\bkhông\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotDirtyRegex();
}
