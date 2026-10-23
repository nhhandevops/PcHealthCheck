using PcHealth.App.Worker;
using PcHealth.Core.History;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Processes;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.App;

/// <summary>
/// Developer/automation mode:
///   PcHealthCheck --headless-scan [--out DIR] [--include-admin]
/// Runs the modules without the UI and writes HTML+JSON reports to DIR.
/// Without --include-admin only the unelevated modules run (no UAC prompt); with it,
/// admin modules run in-process when already elevated, otherwise through the same
/// elevated worker the GUI uses, which raises exactly one UAC prompt.
///
/// Exit codes, for the scripts that run this unattended:
///   0 — the scan ran and every selected check finished.
///   2 — the scan ran, but some checks could not finish (a query failed, the UAC prompt was
///       declined, the elevated helper died). Their results are missing from the report.
///   3 — the scan could not run: no check produced a result, so the report has nothing in it.
///   1 is left alone, so an unhandled crash keeps whatever the runtime reports.
///
/// The health grade is deliberately NOT an exit code. Bad is a true answer about the PC, not a
/// failure of the run, and a code that fires on every unhealthy PC is a code every script learns
/// to ignore — including on the runs where the tool really did break. Until this was written the
/// method returned 0 unconditionally, so a clean scan, four partial modules and a declined UAC
/// prompt were indistinguishable to anything driving it.
/// </summary>
internal static class HeadlessScan
{
    private const int ExitOk = 0;
    private const int ExitChecksIncomplete = 2;
    private const int ExitScanDidNotRun = 3;

    public static async Task<int> RunAsync(string[] args)
    {
        var outDir = Directory.GetCurrentDirectory();
        var outIndex = Array.IndexOf(args, "--out");
        if (outIndex >= 0 && outIndex + 1 < args.Length) outDir = args[outIndex + 1];
        Directory.CreateDirectory(outDir);

        var includeAdmin = args.Contains("--include-admin");
        var alreadyElevated = Elevation.IsElevated();

        var allModules = new WindowsModuleProvider().CreateModules();
        var localModules = (alreadyElevated && includeAdmin
                ? allModules
                : allModules.Where(m => !m.Manifest.NeedsAdmin))
            .ToList();
        var adminModules = includeAdmin && !alreadyElevated
            ? allModules.Where(m => m.Manifest.NeedsAdmin).ToList()
            : [];

        var receipt = new ReceiptLog();
        var context = new ModuleContext
        {
            Processes = new ProcessRunner(receipt),
            Receipt = receipt,
            AppTempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHealthCheck", "temp"),
            IsElevated = alreadyElevated,
        };

        var orchestrator = new ScanOrchestrator(new VerdictEngine(RulesLoader.LoadEmbedded()));
        var progress = new Progress<ScanProgress>(p => Console.WriteLine($"[{p.Index}/{p.Total}] {p.ModuleName} — {p.Message}"));

        // The elevated helper starts first and runs beside the normal checks, so the UAC prompt
        // appears at the start of the scan instead of a minute in. It is awaited before the
        // benchmarks, which must have the machine to themselves (HANDOFF rule 11); that also stops
        // the sensors check reading a processor the CPU benchmark has just heated.
        Task<ElevatedScanOutcome>? adminRun = null;
        if (adminModules.Count > 0)
        {
            Console.WriteLine("Requesting administrator rights for the admin checks (one UAC prompt); the other checks run meanwhile…");
            var adminProgress = new Progress<ScanProgress>(p => Console.WriteLine($"[admin {p.Index}/{p.Total}] {p.ModuleName} — {p.Message}"));
            // Task.Run: starting the helper blocks on the UAC prompt until someone answers it.
            var ids = adminModules.Select(m => m.Manifest.Id).ToList();
            adminRun = Task.Run(() => new ElevatedWorkerSession().RunAsync([.. ids], adminProgress, CancellationToken.None));
        }

        var results = new List<ModuleResult>(
            await orchestrator.RunModulesAsync(localModules, context, progress,
                beforeRunsAlone: adminRun is null ? null : () => Task.WhenAny(adminRun), ct: CancellationToken.None));
        var receiptEntries = new List<ReceiptEntry>(receipt.Snapshot());
        var ranElevated = alreadyElevated;

        if (adminRun is not null)
        {
            var outcome = await adminRun;

            Console.WriteLine($"Elevated helper: {outcome.Status}{(outcome.Message is null ? "" : $" — {outcome.Message}")}");
            if (outcome.RejectedModuleIds.Count > 0)
                Console.WriteLine($"Rejected by helper: {string.Join(", ", outcome.RejectedModuleIds)}");

            results.AddRange(outcome.Results);
            receiptEntries.AddRange(outcome.Receipt);
            // Elevation is a fact about what ran, not a verdict on how the helper session ended.
            // A helper that delivers every admin result and then dies before its end-of-scan
            // message is reported Failed, and reading elevation from that status printed
            // "(standard scan, no admin)" beside a report holding all seven admin results. The
            // checks that genuinely never reported are still called out below, from their absence.
            ranElevated = outcome.RanElevated;

            // An admin check that produced no result did not run at all, and until this was
            // written it simply vanished from the headless report: nothing in it knew the check
            // had been selected, so the "some checks could not finish" warning had nothing to
            // find and a declined UAC prompt printed "Overall: GOOD" with an entire check
            // missing. The GUI was fixed for exactly this on 2026-08-22; this path kept the bug
            // because each entry point merged the elevated outcome its own way.
            results.AddRange(MissingAdminChecks.AsFailedResults(results, adminModules, outcome.Message));
        }

        var names = allModules.ToDictionary(m => m.Manifest.Id, m => m.Manifest.Name, StringComparer.Ordinal);

        var history = args.Contains("--no-history")
            ? HistoryOutcome.Disabled
            : HistoryService.AnalyzeAndRecord(results.SelectMany(r => r.Metrics), DateTimeOffset.Now);
        if (history.Error is not null) Console.WriteLine(history.Error);

        var report = orchestrator.BuildReport(results, receiptEntries, ranElevated, names,
            history.Trends, history.Coverage);

        var htmlPath = Path.Combine(outDir, "pc-health-report.html");
        var jsonPath = Path.Combine(outDir, "pc-health-report.json");
        HtmlReportExporter.WriteTo(report, htmlPath);
        JsonReportExporter.WriteTo(report, jsonPath);

        Console.WriteLine();
        if (report.Trends.Count > 0)
        {
            Console.WriteLine("Changes since earlier scans:");
            foreach (var trend in report.Trends)
                Console.WriteLine($"  [{trend.Grade}] {trend.Label} ({trend.Subject}): {trend.Message}");
            Console.WriteLine();
        }
        else if (report.History is { EarlierScans: > 0 } coverage)
        {
            Console.WriteLine($"No changes vs {coverage.EarlierScans} earlier scan(s) {coverage.SpanText}.");
        }
        else if (report.History is { ScanCount: > 0 })
        {
            Console.WriteLine("First recorded scan — future scans will report what changed.");
        }

        // The same set the HTML report and the UI call out above the results, so the console
        // says what they say rather than a quieter version of it.
        var incomplete = report.Modules
            .Where(m => m.Status is ModuleStatus.Failed or ModuleStatus.PartiallyCompleted)
            .ToList();

        // "GOOD" is the most reassuring word this app can say, and whenever some checks did not
        // finish it is a verdict on the ones that did, not on the PC. The GUI marks that headline
        // INCOMPLETE; a console headline left plain makes the same claim without the caveat, and
        // this line is the one a person reads first.
        Console.WriteLine($"Overall: {report.OverallGrade}{(report.RanElevated ? " (elevated scan)" : "")}{(incomplete.Count > 0 ? " — INCOMPLETE" : "")}");
        foreach (var (category, grade) in report.CategoryGrades.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {category}: {grade}");
        Console.WriteLine();
        foreach (var m in report.Modules)
            Console.WriteLine($"  {m.ModuleId}: {m.Status} ({m.Elapsed.TotalSeconds:0.0}s){(m.Error is null ? "" : $" — {m.Error}")}");
        Console.WriteLine();
        if (incomplete.Count > 0)
        {
            Console.WriteLine("Some checks could not finish. Their results are missing from this report — " +
                              "that is not the same as being healthy:");
            foreach (var m in incomplete)
                Console.WriteLine($"  {m.ModuleName}: {m.Error ?? (m.Notes.Count > 0 ? string.Join("; ", m.Notes) : "no reason given")}");
            Console.WriteLine();
        }
        Console.WriteLine($"Reports written: {htmlPath} | {jsonPath}");

        // Nothing ran, or everything that ran failed outright: the report contains no findings,
        // only excuses. A script told "ran, with gaps" would go on to trust an empty report, so
        // that case gets its own code.
        if (report.Modules.Count == 0 || report.Modules.All(m => m.Status == ModuleStatus.Failed))
            return ExitScanDidNotRun;

        return incomplete.Count > 0 ? ExitChecksIncomplete : ExitOk;
    }
}
