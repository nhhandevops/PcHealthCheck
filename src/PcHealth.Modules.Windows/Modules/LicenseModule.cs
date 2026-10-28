using System.Runtime.InteropServices;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Windows license authenticity: activation state (Software Licensing WMI), genuine check
/// (SLIsGenuineLocal — the only honest local "genuine" signal), license channel, and a
/// KMS-emulator heuristic. The OA3 BIOS product key is never read into a metric, never
/// logged — only its presence is reported.
/// </summary>
internal sealed partial class LicenseModule : WindowsModuleBase
{
    private static readonly Guid WindowsAppId = new("55c92734-d682-4d71-983e-d6ec3f16059f");

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "license",
        Name = "Windows license",
        Description = "Activation status, genuine check, and license channel (Retail/OEM/Volume). Detects localhost KMS-emulator activation cracks.",
        Reads = ["Software licensing state (WMI)", "Genuine state (Windows slc.dll API)"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(4),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        // Active Windows license: the row with a partial product key that is not an add-on. An
        // installed Extended Security Updates key is an add-on row with a key of its own.
        var products = Wmi.TryQuery(@"root\cimv2",
            "SELECT Name, Description, LicenseStatus, PartialProductKey, LicenseIsAddon, GracePeriodRemaining FROM SoftwareLicensingProduct " +
            $"WHERE ApplicationID = '{WindowsAppId}'",
            out var error);
        var failures = 0;
        if (error is not null)
        {
            notes.Add($"The Windows licence could not be read ({error}), so activation status was NOT checked.");
            failures++;
        }

        var active = WindowsSupport.WindowsLicence(products);
        string channel = "Unknown";
        if (active is not null)
        {
            var status = Wmi.Get<uint>(active, "LicenseStatus") ?? 0;
            var statusText = status switch
            {
                0 => "Unlicensed",
                1 => "Activated",
                2 => "Grace period (initial)",
                3 => "Grace period (additional)",
                4 => "Non-genuine grace period",
                5 => "Notification (activation problem)",
                6 => "Extended grace period",
                _ => $"Unknown ({status})",
            };
            var description = Wmi.GetString(active, "Description") ?? "";
            channel = description.Contains("VOLUME_KMSCLIENT", StringComparison.OrdinalIgnoreCase) ? "Volume (KMS client)"
                : description.Contains("VOLUME_MAK", StringComparison.OrdinalIgnoreCase) ? "Volume (MAK)"
                : description.Contains("OEM", StringComparison.OrdinalIgnoreCase) ? "OEM"
                : description.Contains("RETAIL", StringComparison.OrdinalIgnoreCase) ? "Retail"
                : "Unknown";

            metrics.Add(new Metric("license.activated", "Windows activation", status == 1, HealthCategory.SoftwareOs,
                Detail: $"{Wmi.GetString(active, "Name")} — {statusText}."));
            metrics.Add(new Metric("license.channel", "License channel", channel, HealthCategory.SoftwareOs, InfoOnly: true));
        }
        else
        {
            notes.Add("No active Windows license row found (no partial product key).");
        }

        // Genuine check — local, offline, no data leaves the machine.
        metrics.Add(new Metric("license.genuine", "Genuine Windows check", QueryGenuineState(notes, ref failures),
            HealthCategory.SoftwareOs,
            Detail: "Local validation via the Windows Software Licensing API (SLIsGenuineLocal)."));

        // KMS-emulator heuristic (activation cracks register a KMS host on this machine).
        var service = Wmi.TryQuery(@"root\cimv2",
            "SELECT KeyManagementServiceMachine, OA3xOriginalProductKey FROM SoftwareLicensingService", out var svcError)
            .FirstOrDefault();
        if (svcError is not null)
        {
            notes.Add($"Licensing service details could not be read ({svcError}), so the check for a fake KMS " +
                      "activation server — the usual sign of an activation crack — did NOT run.");
            failures++;
        }
        if (service is not null)
        {
            var kmsHost = Wmi.GetString(service, "KeyManagementServiceMachine")?.Trim();
            var isKmsClient = channel.Contains("KMS", StringComparison.OrdinalIgnoreCase);
            var suspicious = isKmsClient && kmsHost is not null &&
                (kmsHost.StartsWith("127.", StringComparison.Ordinal)
                 || kmsHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                 || kmsHost.Equals("::1", StringComparison.Ordinal));
            if (isKmsClient || suspicious)
            {
                metrics.Add(new Metric("license.kms_suspicious", "KMS activation looks legitimate", !suspicious,
                    HealthCategory.SoftwareOs,
                    Detail: suspicious
                        ? $"KMS host is {kmsHost} (this machine) — typical of activation cracks/emulators, which often carry malware."
                        : kmsHost is null ? "KMS client without a configured host." : $"KMS host: {kmsHost}."));
            }

            var oa3Present = !string.IsNullOrEmpty(Wmi.GetString(service, "OA3xOriginalProductKey"));
            metrics.Add(new Metric("license.oa3_key_present", "Product key embedded in firmware (OEM)", oa3Present,
                HealthCategory.SoftwareOs, InfoOnly: true,
                Detail: oa3Present ? "An OEM product key is stored in the firmware. The key itself is never read into reports." : "No firmware-embedded key (typical for retail/self-built PCs), or it needs the elevated scan to read."));
        }

        if (metrics.Count == 0) return Task.FromResult(ModuleStatus.Failed);
        return Task.FromResult(failures == 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted);
    }

    /// <summary>
    /// Returns "Unknown" when the check cannot run, and counts that as a failure. The metric
    /// value alone is honest, but without degrading the module's status the report's
    /// "some checks could not finish" banner never appears, and a licence section that never
    /// managed to validate anything reads exactly like one that validated fine.
    /// </summary>
    private static string QueryGenuineState(List<string> notes, ref int failures)
    {
        try
        {
            var appId = WindowsAppId;
            var hr = SLIsGenuineLocal(in appId, out var state, IntPtr.Zero);
            if (hr != 0)
            {
                notes.Add($"The genuine-Windows check could not complete (HRESULT 0x{hr:X8}), so whether this " +
                          "copy of Windows validates was NOT established.");
                failures++;
                return "Unknown";
            }
            return state switch
            {
                0 => "Genuine",
                1 => "InvalidLicense",
                2 => "Tampered",
                3 => "OfflineValidation",
                _ => "Unknown",
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            notes.Add($"The genuine-Windows check is unavailable on this system ({e.GetType().Name}), so it did NOT run.");
            failures++;
            return "Unknown";
        }
    }

    [LibraryImport("slwga.dll")]
    private static partial int SLIsGenuineLocal(in Guid pAppId, out uint pGenuineState, IntPtr pUiOptions);
}
