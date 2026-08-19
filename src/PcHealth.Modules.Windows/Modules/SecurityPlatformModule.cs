using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Platform security hardware: TPM, Secure Boot, drive encryption, and virtualization-based
/// security. Reports BitLocker protector STATUS only — recovery keys are never read.
/// </summary>
internal sealed class SecurityPlatformModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "security.platform",
        Name = "Platform security (TPM, Secure Boot, encryption)",
        Description = "TPM presence and readiness, Secure Boot state, whether drives are encrypted, and virtualization-based security. Never reads encryption keys.",
        Reads = ["TPM state (WMI)", "Secure Boot registry state", "Drive encryption status (WMI)", "Device Guard status (WMI)"],
        NeedsAdmin = true,
        EstimatedDuration = TimeSpan.FromSeconds(6),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        // TPM
        var tpm = Wmi.TryQuery(@"root\cimv2\Security\MicrosoftTpm",
            "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion, ManufacturerIdTxt FROM Win32_Tpm",
            out var tpmError).FirstOrDefault();
        if (tpm is not null)
        {
            var enabled = Wmi.Get<bool>(tpm, "IsEnabled_InitialValue") ?? false;
            var activated = Wmi.Get<bool>(tpm, "IsActivated_InitialValue") ?? false;
            var spec = Wmi.GetString(tpm, "SpecVersion")?.Split(',').FirstOrDefault()?.Trim();
            metrics.Add(new Metric("security.tpm_ready", "TPM ready", enabled && activated, HealthCategory.Security,
                Detail: $"TPM spec {spec ?? "unknown"} from {Wmi.GetString(tpm, "ManufacturerIdTxt")?.Trim()}. " +
                        "The TPM protects encryption keys and is required for Windows 11."));
            if (spec is not null)
            {
                metrics.Add(new Metric("security.tpm_version", "TPM version", spec, HealthCategory.Security, InfoOnly: true));
            }
        }
        else
        {
            notes.Add($"No TPM detected or not readable{(tpmError is null ? "" : $": {tpmError}")}.");
            metrics.Add(new Metric("security.tpm_ready", "TPM ready", false, HealthCategory.Security,
                Detail: "No usable TPM was found. Windows 11 requires TPM 2.0, and drive encryption is weaker without one."));
        }

        // Secure Boot (registry state is locale-proof and does not throw on legacy BIOS)
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            if (key?.GetValue("UEFISecureBootEnabled") is int secureBoot)
            {
                metrics.Add(new Metric("security.secure_boot", "Secure Boot", secureBoot != 0, HealthCategory.Security,
                    Detail: "Secure Boot blocks tampered boot loaders and bootkits."));
            }
            else
            {
                notes.Add("Secure Boot state not reported — this PC is likely booting in legacy BIOS mode.");
            }
        }

        // Drive encryption — status only, never keys.
        var volumes = Wmi.TryQuery(@"root\cimv2\Security\MicrosoftVolumeEncryption",
            "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume", out var bitlockerError);
        if (bitlockerError is not null)
        {
            notes.Add($"Drive encryption status unavailable: {bitlockerError} (BitLocker is not present on Windows Home editions).");
        }
        foreach (var volume in volumes)
        {
            var letter = Wmi.GetString(volume, "DriveLetter");
            if (string.IsNullOrEmpty(letter)) continue;
            var protection = Wmi.Get<uint>(volume, "ProtectionStatus") ?? 0;
            metrics.Add(new Metric("security.drive_encrypted", "Drive encryption", protection == 1,
                HealthCategory.Security, Subject: letter,
                Detail: protection switch
                {
                    1 => "This drive is encrypted and protected. If the PC is lost or stolen, the data stays unreadable.",
                    0 => "This drive is not encrypted — anyone who takes the drive out can read every file on it.",
                    _ => "Encryption status could not be determined.",
                }));
        }

        // Virtualization-based security
        var deviceGuard = Wmi.TryQuery(@"root\Microsoft\Windows\DeviceGuard",
            "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", out _).FirstOrDefault();
        if (deviceGuard is not null)
        {
            var vbs = Wmi.Get<uint>(deviceGuard, "VirtualizationBasedSecurityStatus") ?? 0;
            var services = deviceGuard.GetValueOrDefault("SecurityServicesRunning") as uint[] ?? [];
            metrics.Add(new Metric("security.vbs_running", "Virtualization-based security", vbs == 2,
                HealthCategory.Security, InfoOnly: vbs != 2,
                Detail: vbs == 2
                    ? $"Running{(services.Contains(2u) ? " with memory integrity (HVCI) enabled" : "")}."
                    : "Not running. Memory integrity adds protection against driver-level malware; it can be enabled in Windows Security > Device security."));
        }

        return Task.FromResult(metrics.Count > 0 ? ModuleStatus.Completed : ModuleStatus.PartiallyCompleted);
    }
}
