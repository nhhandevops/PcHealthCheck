using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;

namespace PcHealth.Core.Orchestration;

public sealed record ScanProgress(string ModuleId, string ModuleName, int Index, int Total, string Message);

/// <summary>
/// Runs consented modules and assembles the graded report. Running and reporting are
/// separate steps so results gathered in another process (the elevated worker) can be
/// merged in before grading — one verdict engine decides everything, in one place.
/// </summary>
public sealed class ScanOrchestrator(VerdictEngine engine)
{
    /// <summary>Runs the given modules in this process, sequentially.</summary>
    public async Task<IReadOnlyList<ModuleResult>> RunModulesAsync(
        IReadOnlyList<IHealthModule> modules,
        ModuleContext context,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<ModuleResult>();
        for (var i = 0; i < modules.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var module = modules[i];
            var manifest = module.Manifest;
            progress?.Report(new ScanProgress(manifest.Id, manifest.Name, i + 1, modules.Count, "running"));

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var moduleProgress = progress is null
                    ? null
                    : new Progress<ModuleProgress>(p =>
                        progress.Report(new ScanProgress(manifest.Id, manifest.Name, i + 1, modules.Count, p.Message)));
                results.Add(await module.RunAsync(context, moduleProgress, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                results.Add(new ModuleResult
                {
                    ModuleId = manifest.Id,
                    Status = ModuleStatus.Failed,
                    Error = $"{e.GetType().Name}: {e.Message}",
                    Elapsed = stopwatch.Elapsed,
                });
            }
        }
        return results;
    }

    /// <summary>Grades every gathered metric and assembles the final report.</summary>
    public ScanReport BuildReport(
        IEnumerable<ModuleResult> results,
        IEnumerable<ReceiptEntry> receipt,
        bool ranElevated,
        IReadOnlyDictionary<string, string>? moduleNames = null,
        IReadOnlyList<TrendFinding>? trends = null,
        HistoryCoverage? history = null)
    {
        var resultList = results.ToList();
        var summaries = resultList
            .Select(r => new ModuleRunSummary(
                r.ModuleId,
                moduleNames?.GetValueOrDefault(r.ModuleId) ?? r.ModuleId,
                r.Status,
                r.Elapsed,
                r.Error,
                r.Notes))
            .ToList();

        var verdicts = engine.Evaluate(resultList.SelectMany(r => r.Metrics));
        var categoryGrades = ScanReport.ComputeCategoryGrades(verdicts);

        // A rising failure counter is worse news than any single snapshot reading, so
        // trend findings count toward the category and overall grades.
        if (trends is { Count: > 0 })
        {
            var merged = new Dictionary<HealthCategory, Grade>(categoryGrades);
            foreach (var group in trends.GroupBy(t => t.Category))
            {
                merged[group.Key] = ScanReport.Worst(
                    [merged.GetValueOrDefault(group.Key, Grade.Unknown), ScanReport.Worst(group.Select(t => t.Grade))]);
            }
            categoryGrades = merged;
        }

        return new ScanReport
        {
            GeneratedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            // The informational version keeps the pre-release suffix ("1.0.0-rc.1"); the
            // assembly version cannot carry one, and a report that claims to come from
            // "1.0.0" when it came from a release candidate is a traceability hole.
            AppVersion = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?.Split('+')[0]
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                ?? "dev",
            RanElevated = ranElevated,
            Modules = summaries,
            Verdicts = verdicts,
            Receipt = [.. receipt],
            Trends = trends ?? [],
            History = history,
            CategoryGrades = categoryGrades,
            OverallGrade = ScanReport.Worst(categoryGrades.Values),
        };
    }

    /// <summary>Convenience for single-process scans: run the modules, then build the report.</summary>
    public async Task<ScanReport> RunAsync(
        IReadOnlyList<IHealthModule> modules,
        ModuleContext context,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = await RunModulesAsync(modules, context, progress, ct).ConfigureAwait(false);
        var names = modules.ToDictionary(m => m.Manifest.Id, m => m.Manifest.Name, StringComparer.Ordinal);
        return BuildReport(results, context.Receipt.Snapshot(), context.IsElevated, names);
    }
}
