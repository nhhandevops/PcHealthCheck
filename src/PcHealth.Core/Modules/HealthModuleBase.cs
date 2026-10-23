using System.Diagnostics;
using PcHealth.Core.Metrics;

namespace PcHealth.Core.Modules;

/// <summary>
/// Base for modules on every OS: timing, cancellation, and the failure contract
/// (an exception after some metrics were gathered = PartiallyCompleted, not lost work).
/// Moved here from the Windows assembly on 2026-10-23 so the Linux modules keep the same
/// contract instead of a copy of it that could drift.
/// </summary>
public abstract class HealthModuleBase : IHealthModule
{
    public abstract ModuleManifest Manifest { get; }

    public async Task<ModuleResult> RunAsync(ModuleContext context, IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var metrics = new List<Metric>();
        var notes = new List<string>();
        try
        {
            var status = await ExecuteAsync(context, metrics, notes, progress, ct).ConfigureAwait(false);
            return new ModuleResult
            {
                ModuleId = Manifest.Id,
                Status = status,
                Metrics = metrics,
                Notes = notes,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new ModuleResult
            {
                ModuleId = Manifest.Id,
                Status = metrics.Count > 0 ? ModuleStatus.PartiallyCompleted : ModuleStatus.Failed,
                Metrics = metrics,
                Notes = notes,
                Error = $"{e.GetType().Name}: {e.Message}",
                Elapsed = stopwatch.Elapsed,
            };
        }
    }

    protected abstract Task<ModuleStatus> ExecuteAsync(
        ModuleContext context,
        List<Metric> metrics,
        List<string> notes,
        IProgress<ModuleProgress>? progress,
        CancellationToken ct);
}
