using PcHealth.App.Worker;
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
/// </summary>
internal static class HeadlessScan
{
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

        var results = new List<ModuleResult>(
            await orchestrator.RunModulesAsync(localModules, context, progress, CancellationToken.None));
        var receiptEntries = new List<ReceiptEntry>(receipt.Snapshot());
        var ranElevated = alreadyElevated;

        if (adminModules.Count > 0)
        {
            Console.WriteLine("Requesting administrator rights for the admin checks (one UAC prompt)…");
            var adminProgress = new Progress<ScanProgress>(p => Console.WriteLine($"[admin {p.Index}/{p.Total}] {p.ModuleName} — {p.Message}"));
            var outcome = await new ElevatedWorkerSession()
                .RunAsync([.. adminModules.Select(m => m.Manifest.Id)], adminProgress, CancellationToken.None);

            Console.WriteLine($"Elevated helper: {outcome.Status}{(outcome.Message is null ? "" : $" — {outcome.Message}")}");
            if (outcome.RejectedModuleIds.Count > 0)
                Console.WriteLine($"Rejected by helper: {string.Join(", ", outcome.RejectedModuleIds)}");

            results.AddRange(outcome.Results);
            receiptEntries.AddRange(outcome.Receipt);
            ranElevated = outcome.Status == ElevatedScanStatus.Completed && outcome.Results.Count > 0;
        }

        var names = allModules.ToDictionary(m => m.Manifest.Id, m => m.Manifest.Name, StringComparer.Ordinal);
        var report = orchestrator.BuildReport(results, receiptEntries, ranElevated, names);

        var htmlPath = Path.Combine(outDir, "pc-health-report.html");
        var jsonPath = Path.Combine(outDir, "pc-health-report.json");
        HtmlReportExporter.WriteTo(report, htmlPath);
        JsonReportExporter.WriteTo(report, jsonPath);

        Console.WriteLine();
        Console.WriteLine($"Overall: {report.OverallGrade}{(report.RanElevated ? " (elevated scan)" : "")}");
        foreach (var (category, grade) in report.CategoryGrades.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {category}: {grade}");
        Console.WriteLine();
        foreach (var m in report.Modules)
            Console.WriteLine($"  {m.ModuleId}: {m.Status} ({m.Elapsed.TotalSeconds:0.0}s){(m.Error is null ? "" : $" — {m.Error}")}");
        Console.WriteLine();
        Console.WriteLine($"Reports written: {htmlPath} | {jsonPath}");
        return 0;
    }
}
