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
        var failures = new ModuleFailures(notes);

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
        else if (tpmError is not null)
        {
            // "The TPM could not be read" and "this PC has no TPM" are different findings, and only
            // the second one is a fact. Grading tpm_ready as false for both tells the owner of a
            // perfectly good TPM that their PC cannot run Windows 11 and that their encryption is
            // weaker than it is, so a failed read suppresses the metric and degrades the module.
            failures.Record("The TPM could not be read, so whether this PC has one — and whether it is " +
                            "switched on — was NOT checked", tpmError);
        }
        else
        {
            // The query ran and reported no TPM instance at all, so the absence is a measured fact.
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
                // A missing value here is how Windows reports a legacy BIOS boot, not a read that
                // broke, so it stays a plain note. Degrading on it would raise the "some checks could
                // not finish" banner on every legacy-BIOS PC until the reader learned to ignore it.
                failures.Note("Secure Boot state not reported — this PC is likely booting in legacy BIOS mode.");
            }
        }

        // Drive encryption — status only, never keys.
        var volumes = Wmi.TryQuery(@"root\cimv2\Security\MicrosoftVolumeEncryption",
            "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume", out var bitlockerError);
        if (bitlockerError is not null)
        {
            // Windows Home has no BitLocker provider, so this query failing is the ordinary shape of
            // a Home edition rather than a read that broke, and it stays a note. What keeps that
            // honest is that no metric is emitted either way: an unreadable volume is never reported
            // as an unencrypted one.
            failures.Note($"Drive encryption status unavailable: {bitlockerError} (BitLocker is not present on Windows Home editions).");
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

        // Virtualization-based security. The query records its own failure: discarding the error with
        // "out _" meant a broken Device Guard read took security.vbs_running with it — no metric, no
        // note and a Completed status, leaving no way to tell "VBS is off" from "VBS was never read".
        var deviceGuard = Wmi.TryQuery(@"root\Microsoft\Windows\DeviceGuard",
            "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard",
            failures, "Virtualization-based security (Device Guard)").FirstOrDefault();
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

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }
}
