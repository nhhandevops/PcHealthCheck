using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Orchestration;

public sealed record ScanProgress(string ModuleId, string ModuleName, int Index, int Total, string Message);

/// <summary>
/// Runs the consented modules sequentially, grades every metric, and assembles the report.
/// Only modules explicitly passed in are run — consent is decided by the caller (UI).
/// </summary>
public sealed class ScanOrchestrator(VerdictEngine engine)
{
    public async Task<ScanReport> RunAsync(
        IReadOnlyList<IHealthModule> consentedModules,
        ModuleContext context,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var summaries = new List<ModuleRunSummary>();
        var allMetrics = new List<Metric>();

        for (var i = 0; i < consentedModules.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var module = consentedModules[i];
            var manifest = module.Manifest;
            progress?.Report(new ScanProgress(manifest.Id, manifest.Name, i + 1, consentedModules.Count, "running"));

            var stopwatch = Stopwatch.StartNew();
            ModuleResult result;
            try
            {
                var moduleProgress = progress is null
                    ? null
                    : new Progress<ModuleProgress>(p =>
                        progress.Report(new ScanProgress(manifest.Id, manifest.Name, i + 1, consentedModules.Count, p.Message)));
                result = await module.RunAsync(context, moduleProgress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                result = new ModuleResult
                {
                    ModuleId = manifest.Id,
                    Status = ModuleStatus.Failed,
                    Error = $"{e.GetType().Name}: {e.Message}",
                };
            }
            stopwatch.Stop();

            summaries.Add(new ModuleRunSummary(
                manifest.Id, manifest.Name, result.Status,
                result.Elapsed == TimeSpan.Zero ? stopwatch.Elapsed : result.Elapsed,
                result.Error, result.Notes));
            allMetrics.AddRange(result.Metrics);
        }

        var verdicts = engine.Evaluate(allMetrics);
        var categoryGrades = ScanReport.ComputeCategoryGrades(verdicts);

        return new ScanReport
        {
            GeneratedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev",
            RanElevated = context.IsElevated,
            Modules = summaries,
            Verdicts = verdicts,
            Receipt = context.Receipt.Snapshot(),
            CategoryGrades = categoryGrades,
            OverallGrade = ScanReport.Worst(categoryGrades.Values),
        };
    }
}
