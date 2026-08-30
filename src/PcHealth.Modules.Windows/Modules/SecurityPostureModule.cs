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
        var failures = new ModuleFailures(notes);

        // Registered AV products (Security Center) — works unelevated, covers third-party AVs.
        var avProducts = Wmi.TryQuery(@"root\SecurityCenter2",
            "SELECT displayName, productState FROM AntiVirusProduct", out var scError);
        if (scError is not null)
        {
            failures.Record("The antivirus products registered with Windows Security Center could not be read, " +
                            "so whether any antivirus is running was NOT established", scError);
        }

        var anyAvEnabled = false;
        var thirdPartyAvEnabled = false;
        var defenderRegistered = false;
        foreach (var av in avProducts)
        {
            var name = Wmi.GetString(av, "displayName") ?? "Antivirus";
            var isDefender = name.Contains("Defender", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("Windows Security", StringComparison.OrdinalIgnoreCase);
            // Whether Defender is registered here at all is what tells apart the two reasons its
            // own namespace can go missing further down: absent because this Windows edition has
            // no Defender, versus absent because the query broke on a PC that does have it.
            // Recorded before the productState guard, because a product whose state will not read
            // is still a product that is installed.
            if (isDefender) defenderRegistered = true;
            if (Wmi.Get<uint>(av, "productState") is not { } state) continue;
            var decoded = ProductStateDecoder.Decode(state);
            anyAvEnabled |= decoded.Enabled;
            if (decoded.Enabled && !isDefender) thirdPartyAvEnabled = true;

            metrics.Add(new Metric("security.av.up_to_date", "Antivirus definitions current", decoded.UpToDate,
                HealthCategory.Security, Subject: name,
                Detail: decoded.Enabled ? "Product is enabled." : "Product is registered but not enabled."));
        }
        // Only assert this when Security Center actually answered. An empty product list after a
        // failed query does not mean "no antivirus is registered", it means nobody managed to ask,
        // and either grade printed from it is a made-up fact in the security section of the report.
        if (scError is null)
        {
            metrics.Add(new Metric("security.av.registered", "An antivirus is active", anyAvEnabled, HealthCategory.Security,
                Detail: avProducts.Count == 0
                    ? "No antivirus products are registered with Windows Security Center."
                    : "Products: " + string.Join(", ", avProducts.Select(p => Wmi.GetString(p, "displayName")).Where(n => n is not null))));
        }

        // Defender specifics.
        var defender = Wmi.TryQuery(@"root\Microsoft\Windows\Defender",
            "SELECT AMRunningMode, RealTimeProtectionEnabled, AntivirusSignatureAge, IsTamperProtected FROM MSFT_MpComputerStatus",
            out var defError).FirstOrDefault();

        // "The Defender namespace is not on this machine" and "the Defender query broke" are
        // different findings and only the second one is a scan that failed. The namespace is
        // simply absent on images built without the antivirus feature (LTSC/IoT, Server SKUs
        // without the AV role, DisableAntiSpyware set by policy) and on PCs whose third-party
        // suite unregisters the provider — ordinary, deliberate machine configurations. Degrading
        // those would fire the report's "Some checks could not finish" banner on every scan of a
        // perfectly normal PC forever, and a banner that is always lit teaches the reader to
        // ignore it. Security Center's product list is the independent witness for which of the
        // two happened; MalwareDefenderModule reads the same absence of the same namespace as a
        // deliberate non-check and returns Skipped.
        var defenderUnavailable = defError is not null
            && (thirdPartyAvEnabled || (scError is null && !defenderRegistered));

        if (defenderUnavailable)
        {
            failures.Note($"Microsoft Defender is not available on this system: {defError}. Its real-time " +
                          "protection, signature age, tamper protection and exclusion list were therefore not " +
                          "checked. If another antivirus is in charge, check that product's own settings.");
        }
        else if (defError is not null)
        {
            // This one query carries real-time protection, signature age and tamper protection, so
            // losing it loses the heart of the antivirus check at once. Noting that and still
            // returning Completed produced a report indistinguishable from one where all three
            // were read and found switched on.
            failures.Record("Defender's own status could not be read, so real-time protection, signature age " +
                            "and tamper protection were NOT checked", defError);
        }
        if (defender is not null)
        {
            var runningMode = Wmi.GetString(defender, "AMRunningMode") ?? "Unknown";
            var rtp = Wmi.Get<bool>(defender, "RealTimeProtectionEnabled") ?? false;

            // thirdPartyAvEnabled is a measured fact only when Security Center answered; after a
            // failed query it is false because nobody managed to ask, not because it was measured
            // false. Grading the three metrics below off it alone meant a PC running Norton/ESET
            // with an unreadable root\SecurityCenter2 got a graded Bad for the passive-mode
            // Defender that is supposed to be off, told to treat it as "malware often disables
            // protection first" — an alarm asserted from a read that broke. Defender's own
            // AMRunningMode carries the same "who is in charge" fact from a namespace that did
            // answer, so it decides when Security Center could not, exactly as
            // MalwareDefenderModule already derives it.
            var defenderInCharge = !thirdPartyAvEnabled
                && runningMode.Equals("Normal", StringComparison.OrdinalIgnoreCase);
            var expectedOff = defenderInCharge ? ""
                : thirdPartyAvEnabled ? " A third-party antivirus is active, so Defender being off is expected."
                : " Defender is not the antivirus in charge of this PC, so it being off is expected.";

            // With another AV in charge, Defender is expected to be off — report as info, not a failure.
            metrics.Add(new Metric("security.defender.rtp", "Defender real-time protection", rtp, HealthCategory.Security,
                Detail: $"Defender running mode: {runningMode}.{expectedOff}",
                InfoOnly: !defenderInCharge));

            if (Wmi.Get<uint>(defender, "AntivirusSignatureAge") is { } sigAge && sigAge != uint.MaxValue && defenderInCharge)
            {
                metrics.Add(new Metric("security.defender.sig_age_days", "Defender signature age", (double)sigAge,
                    HealthCategory.Security, Unit: "days"));
            }

            if (Wmi.Get<bool>(defender, "IsTamperProtected") is { } tamper && defenderInCharge)
            {
                metrics.Add(new Metric("security.defender.tamper", "Defender tamper protection", tamper, HealthCategory.Security));
            }
        }

        // Defender exclusions — a top malware persistence technique is quietly excluding itself.
        var prefs = Wmi.TryQuery(@"root\Microsoft\Windows\Defender",
            "SELECT ExclusionPath, ExclusionExtension, ExclusionProcess FROM MSFT_MpPreference", out var prefError)
            .FirstOrDefault();
        // On a machine with no Defender this second query fails for the one reason already noted
        // above, so recording it here would degrade the module twice over for a check that was
        // never going to run. Any other failure of it is a real audit that could not finish.
        if (prefError is not null && !defenderUnavailable)
        {
            failures.Record("Defender's preferences could not be read, so its exclusion list — the place " +
                            "malware adds itself to stop being scanned — was NOT audited", prefError);
        }
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
                // Deliberately deferred rather than broken: the unelevated scan cannot see these and
                // the elevated one audits them, so this is a note and must not degrade the module.
                failures.Note("Defender exclusion lists are hidden from non-admin processes on this system; the elevated scan (M2) will audit them.");
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

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
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
