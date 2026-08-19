using Microsoft.Win32;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Antivirus/Defender state, signature age, Defender exclusions (the classic
/// "engine hollowed out" persistence trick), firewall per profile, UAC, SmartScreen.
/// </summary>
internal sealed class SecurityPostureModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "security.posture",
        Name = "Security posture",
        Description = "Antivirus status and signature age, Defender exclusion audit, firewall per profile, UAC and SmartScreen settings.",
        Reads = ["Defender status (WMI)", "Registered antivirus products (Security Center)", "Firewall/UAC/SmartScreen registry values"],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(4),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        // Registered AV products (Security Center) — works unelevated, covers third-party AVs.
        var avProducts = Wmi.TryQuery(@"root\SecurityCenter2",
            "SELECT displayName, productState FROM AntiVirusProduct", out var scError);
        if (scError is not null) notes.Add($"SecurityCenter2 query: {scError}");

        var anyAvEnabled = false;
        var thirdPartyAvEnabled = false;
        foreach (var av in avProducts)
        {
            var name = Wmi.GetString(av, "displayName") ?? "Antivirus";
            if (Wmi.Get<uint>(av, "productState") is not { } state) continue;
            var decoded = ProductStateDecoder.Decode(state);
            anyAvEnabled |= decoded.Enabled;
            var isDefender = name.Contains("Defender", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("Windows Security", StringComparison.OrdinalIgnoreCase);
            if (decoded.Enabled && !isDefender) thirdPartyAvEnabled = true;

            metrics.Add(new Metric("security.av.up_to_date", "Antivirus definitions current", decoded.UpToDate,
                HealthCategory.Security, Subject: name,
                Detail: decoded.Enabled ? "Product is enabled." : "Product is registered but not enabled."));
        }
        metrics.Add(new Metric("security.av.registered", "An antivirus is active", anyAvEnabled, HealthCategory.Security,
            Detail: avProducts.Count == 0
                ? "No antivirus products are registered with Windows Security Center."
                : "Products: " + string.Join(", ", avProducts.Select(p => Wmi.GetString(p, "displayName")).Where(n => n is not null))));

        // Defender specifics.
        var defender = Wmi.TryQuery(@"root\Microsoft\Windows\Defender",
            "SELECT AMRunningMode, RealTimeProtectionEnabled, AntivirusSignatureAge, IsTamperProtected FROM MSFT_MpComputerStatus",
            out var defError).FirstOrDefault();
        if (defError is not null) notes.Add($"Defender status unavailable: {defError}");
        if (defender is not null)
        {
            var runningMode = Wmi.GetString(defender, "AMRunningMode") ?? "Unknown";
            var rtp = Wmi.Get<bool>(defender, "RealTimeProtectionEnabled") ?? false;

            // With a third-party AV active, Defender is expected to be off — report as info, not a failure.
            metrics.Add(new Metric("security.defender.rtp", "Defender real-time protection", rtp, HealthCategory.Security,
                Detail: $"Defender running mode: {runningMode}." + (thirdPartyAvEnabled ? " A third-party antivirus is active, so Defender being off is expected." : ""),
                InfoOnly: thirdPartyAvEnabled));

            if (Wmi.Get<uint>(defender, "AntivirusSignatureAge") is { } sigAge && sigAge != uint.MaxValue && !thirdPartyAvEnabled)
            {
                metrics.Add(new Metric("security.defender.sig_age_days", "Defender signature age", (double)sigAge,
                    HealthCategory.Security, Unit: "days"));
            }

            if (Wmi.Get<bool>(defender, "IsTamperProtected") is { } tamper && !thirdPartyAvEnabled)
            {
                metrics.Add(new Metric("security.defender.tamper", "Defender tamper protection", tamper, HealthCategory.Security));
            }
        }

        // Defender exclusions — a top malware persistence technique is quietly excluding itself.
        var prefs = Wmi.TryQuery(@"root\Microsoft\Windows\Defender",
            "SELECT ExclusionPath, ExclusionExtension, ExclusionProcess FROM MSFT_MpPreference", out var prefError)
            .FirstOrDefault();
        if (prefError is not null) notes.Add($"Defender preferences unavailable: {prefError}");
        if (prefs is not null)
        {
            // On newer builds an unelevated read returns a single "N/A: Must be an administrator…"
            // placeholder per list instead of the real values — that is "unavailable", not "3 exclusions".
            static string[] Real(object? value) => value is string[] arr
                ? [.. arr.Where(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))]
                : [];
            static bool Restricted(object? value) =>
                value is string[] arr && arr.Any(s => s?.StartsWith("N/A", StringComparison.OrdinalIgnoreCase) == true);

            var pathValues = prefs.GetValueOrDefault("ExclusionPath");
            var extValues = prefs.GetValueOrDefault("ExclusionExtension");
            var procValues = prefs.GetValueOrDefault("ExclusionProcess");

            if (Restricted(pathValues) || Restricted(extValues) || Restricted(procValues))
            {
                notes.Add("Defender exclusion lists are hidden from non-admin processes on this system; the elevated scan (M2) will audit them.");
            }
            else
            {
                var paths = Real(pathValues);
                var exclusions = paths.Length + Real(extValues).Length + Real(procValues).Length;
                var sample = paths.Length > 0
                    ? " Paths: " + string.Join("; ", paths.Take(5)) + (paths.Length > 5 ? " …" : "")
                    : "";
                metrics.Add(new Metric("security.defender.exclusions_count", "Defender scan exclusions", exclusions,
                    HealthCategory.Security,
                    Detail: exclusions == 0
                        ? "No files or folders are excluded from Defender scans."
                        : $"{exclusions} exclusion(s) configured — malware sometimes hides here; make sure you recognize every one.{sample}"));
            }
        }

        // Firewall per profile — registry read, locale-proof, unelevated.
        foreach (var (subKey, label) in new[]
                 {
                     ("DomainProfile", "Domain"),
                     ("StandardProfile", "Private"),
                     ("PublicProfile", "Public"),
                 })
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{subKey}");
            if (key?.GetValue("EnableFirewall") is int enabled)
            {
                metrics.Add(new Metric("security.firewall.enabled", "Firewall enabled", enabled != 0,
                    HealthCategory.Security, Subject: $"{label} profile"));
            }
        }

        // UAC.
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            if (key?.GetValue("EnableLUA") is int lua)
            {
                metrics.Add(new Metric("security.uac.enabled", "User Account Control (UAC)", lua != 0, HealthCategory.Security,
                    Detail: "UAC is the prompt that stands between malware and admin rights."));
            }
            if (key?.GetValue("PromptOnSecureDesktop") is int secure)
            {
                metrics.Add(new Metric("security.uac.secure_desktop", "UAC secure desktop", secure != 0, HealthCategory.Security));
            }
        }

        // SmartScreen (best-effort; the setting moved between builds).
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer"))
        {
            if (key?.GetValue("SmartScreenEnabled") is string smartScreen)
            {
                metrics.Add(new Metric("security.smartscreen", "SmartScreen", smartScreen, HealthCategory.Security,
                    Detail: "SmartScreen warns before running unrecognized downloaded apps."));
            }
        }

        return Task.FromResult(metrics.Count > 0 ? ModuleStatus.Completed : ModuleStatus.Failed);
    }
}

/// <summary>
/// Decodes Security Center productState (community 0xWWSSDD byte scheme:
/// SS byte 0x10/0x11 = enabled, DD byte 0x00 = definitions up to date).
/// Public so the fixture tests can pin known values.
/// </summary>
public static class ProductStateDecoder
{
    public readonly record struct AvProductState(bool Enabled, bool UpToDate);

    public static AvProductState Decode(uint productState)
    {
        var scanner = (productState >> 8) & 0xFF;
        var definitions = productState & 0xFF;
        return new AvProductState(Enabled: scanner >= 0x10, UpToDate: definitions == 0x00);
    }
}
