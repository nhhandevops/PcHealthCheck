using System.Text.Json;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.Modules.Windows.Modules;

/// <summary>
/// Fast system-file integrity check. Uses Repair-WindowsImage -CheckHealth, which
/// returns a typed ImageHealthState (Healthy / Repairable / NonRepairable) rather than
/// localized console text, so the verdict works on any Windows display language.
/// It only reads the flag Windows already recorded — it repairs nothing and takes seconds.
/// </summary>
internal sealed class IntegrityQuickModule : WindowsModuleBase
{
    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "integrity.quick",
        Name = "System file integrity (quick)",
        Description = "Asks Windows whether it has already recorded corruption in the component store. Reads the recorded flag only — repairs nothing.",
        Reads = ["Windows component store health flag (DISM API)"],
        NeedsAdmin = true,
        EstimatedDuration = TimeSpan.FromSeconds(15),
    };

    protected override async Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        const string script = "$r = Repair-WindowsImage -Online -CheckHealth; " +
                              "[pscustomobject]@{ State = [string]$r.ImageHealthState } | ConvertTo-Json -Compress";

        var run = await context.Processes.RunAsync(Manifest.Id, "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{script}\"", TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);

        if (!run.Succeeded)
        {
            notes.Add($"Component store check did not complete (exit {run.ExitCode}). {Trim(run.StdErr)}");
            return ModuleStatus.Failed;
        }

        string? state = null;
        try
        {
            using var document = JsonDocument.Parse(run.StdOut);
            state = document.RootElement.TryGetProperty("State", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            notes.Add("Could not read the component store health result.");
        }

        if (string.IsNullOrWhiteSpace(state)) return ModuleStatus.PartiallyCompleted;

        metrics.Add(new Metric("integrity.image_health", "Windows component store", state,
            HealthCategory.SoftwareOs,
            Detail: state.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                ? "Windows has not recorded any corruption in its system files."
                : "Windows has recorded corruption in its system files."));

        return ModuleStatus.Completed;
    }

    private static string Trim(string text) => text.Length > 200 ? text[..200] : text.Trim();
}
