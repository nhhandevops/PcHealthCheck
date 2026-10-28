using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PcHealth.Core.Advice;
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
/// <paramref name="advice"/> works out the "What you can do" section from the graded report; by
/// default it is the built-in advice for the OS this process runs on.
/// </summary>
public sealed class ScanOrchestrator(VerdictEngine engine, AdviceEngine? advice = null)
{
    private readonly AdviceEngine _advice = advice ?? AdviceEngine.ForOs(RulesLoader.CurrentOs);

    /// <summary>
    /// How many modules may run side by side. Several of them start a child process
    /// (PowerShell, powercfg, fsutil), so this also bounds how many of those exist at once on
    /// a machine that may already be short of memory.
    /// </summary>
    public const int MaxConcurrentModules = 4;

    /// <summary>
    /// Runs the given modules in this process. Most only read, and the slow ones spend their
    /// time waiting on WMI, the event log or a child process, so running them one after another
    /// made a scan the sum of its checks: about 20 s on a ThinkPad T470p, of which one check took
    /// 7.6 s. They now run side by side, and a scan takes roughly as long as its slowest check.
    /// Modules marked <see cref="ModuleManifest.RunsAlone"/> run afterwards, one at a time, with
    /// nothing beside them, and so do those marked <see cref="ModuleManifest.RunsAfterOthers"/>.
    /// Results come back in the order the modules were given, whatever order they finished in,
    /// so reports do not reshuffle from one scan to the next.
    /// <paramref name="beforeRunsAlone"/> is awaited between the two phases: the scan entry
    /// points pass the elevated helper's run, so the benchmarks never share the machine with the
    /// admin checks either. It returns what the helper gathered, and that is graded together
    /// with this process's results into <see cref="ModuleContext.EarlierFindings"/>, so a
    /// benchmark can see a failing drive that only the elevated SMART check could read.
    /// </summary>
    public async Task<IReadOnlyList<ModuleResult>> RunModulesAsync(
        IReadOnlyList<IHealthModule> modules,
        ModuleContext context,
        IProgress<ScanProgress>? progress = null,
        Func<Task<IReadOnlyList<ModuleResult>>>? beforeRunsAlone = null,
        CancellationToken ct = default)
    {
        var results = new ModuleResult[modules.Count];
        var tracker = new ProgressTracker(modules, progress);

        using (var slots = new SemaphoreSlim(MaxConcurrentModules))
        {
            await Task.WhenAll(Enumerable.Range(0, modules.Count)
                .Where(i => !RunsLast(modules[i].Manifest))
                .Select(async i =>
                {
                    await slots.WaitAsync(ct).ConfigureAwait(false);
                    try { results[i] = await RunOneAsync(modules[i], context, tracker, ct).ConfigureAwait(false); }
                    finally { slots.Release(); }
                })).ConfigureAwait(false);
        }

        IReadOnlyList<ModuleResult> elsewhere = beforeRunsAlone is null ? [] : await beforeRunsAlone().ConfigureAwait(false);
        context.EarlierFindings = engine.Evaluate(
            results.Where(r => r is not null).Concat(elsewhere).SelectMany(r => r.Metrics));

        for (var i = 0; i < modules.Count; i++)
        {
            if (!RunsLast(modules[i].Manifest)) continue;
            ct.ThrowIfCancellationRequested();
            results[i] = await RunOneAsync(modules[i], context, tracker, ct).ConfigureAwait(false);
        }
        return results;
    }

    private static bool RunsLast(ModuleManifest manifest) => manifest.RunsAlone || manifest.RunsAfterOthers;

    /// <summary>
    /// The version a report names: the full informational version, pre-release suffix and build
    /// commit included ("1.0.0-rc.1+25a2cd5…"). Reports are collected from other machines, and the
    /// commit is the only thing that says which code produced one; the suffix alone names every
    /// rc.1 build ever made. Until 2026-10-23 the "+commit" part was cut off here, which failed
    /// acceptance row A5 ("matches the build exactly"). The assembly version is the fallback,
    /// because it cannot carry a suffix at all.
    /// </summary>
    public static string DescribeVersion(string? informationalVersion, Version? assemblyVersion) =>
        !string.IsNullOrWhiteSpace(informationalVersion) ? informationalVersion
        : assemblyVersion?.ToString(3) ?? "dev";

    private static async Task<ModuleResult> RunOneAsync(
        IHealthModule module, ModuleContext context, ProgressTracker tracker, CancellationToken ct)
    {
        var manifest = module.Manifest;
        tracker.Started(module);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Task.Run, because many modules do all their work synchronously before their first
            // await. Called directly, that work would run on the caller's thread and quietly turn
            // the concurrent scan back into a sequential one.
            return await Task.Run(() => module.RunAsync(context, tracker.ForModule(module), ct), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new ModuleResult
            {
                ModuleId = manifest.Id,
                Status = ModuleStatus.Failed,
                Error = $"{e.GetType().Name}: {e.Message}",
                Elapsed = stopwatch.Elapsed,
            };
        }
        finally
        {
            tracker.Finished(module);
        }
    }

    /// <summary>
    /// With several checks in flight, "[3/17] Startup programs" can no longer mean "the third
    /// check is the one running". Index is now the step being worked on (checks finished + 1),
    /// and the name is the earliest-listed check still running, which on a slow scan is the
    /// one holding everything up and so the one worth naming.
    /// </summary>
    private sealed class ProgressTracker(IReadOnlyList<IHealthModule> modules, IProgress<ScanProgress>? progress)
    {
        private readonly Lock _lock = new();
        private readonly HashSet<IHealthModule> _running = [];
        private int _finished;

        public void Started(IHealthModule module)
        {
            lock (_lock) { _running.Add(module); }
            Report(module, "running");
        }

        public void Finished(IHealthModule module)
        {
            IHealthModule? stillRunning;
            lock (_lock)
            {
                _running.Remove(module);
                _finished++;
                stillRunning = modules.FirstOrDefault(_running.Contains);
            }
            if (stillRunning is not null) Report(stillRunning, "running");
        }

        public Progress<ModuleProgress>? ForModule(IHealthModule module) =>
            progress is null ? null : new Progress<ModuleProgress>(p => Report(module, p.Message));

        private void Report(IHealthModule module, string message)
        {
            if (progress is null) return;
            int step;
            lock (_lock) { step = Math.Min(_finished + 1, modules.Count); }
            progress.Report(new ScanProgress(module.Manifest.Id, module.Manifest.Name, step, modules.Count, message));
        }
    }

    /// <summary>
    /// Grades every gathered metric and assembles the final report. The advice is worked out here, after
    /// the grades and from them, so the window, the headless scan and every export show one section:
    /// the window and the headless scan once disagreed because each did a step of its own.
    /// </summary>
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

        var trendList = trends ?? [];
        return new ScanReport
        {
            GeneratedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            AppVersion = DescribeVersion(
                Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Assembly.GetEntryAssembly()?.GetName().Version),
            RanElevated = ranElevated,
            Modules = summaries,
            Verdicts = verdicts,
            Receipt = [.. receipt],
            Trends = trendList,
            History = history,
            Advice = _advice.Derive(verdicts, trendList, summaries, AdviceContext.ForThisPc(ranElevated)),
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
        var results = await RunModulesAsync(modules, context, progress, ct: ct).ConfigureAwait(false);
        var names = modules.ToDictionary(m => m.Manifest.Id, m => m.Manifest.Name, StringComparer.Ordinal);
        return BuildReport(results, context.Receipt.Snapshot(), context.IsElevated, names);
    }
}
