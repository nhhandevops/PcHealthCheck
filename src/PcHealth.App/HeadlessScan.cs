using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Processes;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.App;

/// <summary>
/// Developer/automation mode: `PcHealthCheck --headless-scan [--out DIR]` runs every
/// module without the UI and writes the HTML+JSON report to DIR (default: current dir).
/// Same engine, same receipts, same offline behavior as the GUI — used for end-to-end
/// verification and future CI smoke tests.
/// </summary>
internal static class HeadlessScan
{
    public static async Task<int> RunAsync(string[] args)
    {
        var outDir = Directory.GetCurrentDirectory();
        var outIndex = Array.IndexOf(args, "--out");
        if (outIndex >= 0 && outIndex + 1 < args.Length) outDir = args[outIndex + 1];
        Directory.CreateDirectory(outDir);

        var receipt = new ReceiptLog();
        var context = new ModuleContext
        {
            Processes = new ProcessRunner(receipt),
            Receipt = receipt,
            AppTempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHealthCheck", "temp"),
            IsElevated = Elevation.IsElevated(),
        };

        var modules = new WindowsModuleProvider().CreateModules();
        var orchestrator = new ScanOrchestrator(new VerdictEngine(RulesLoader.LoadEmbedded()));
        var progress = new Progress<ScanProgress>(p => Console.WriteLine($"[{p.Index}/{p.Total}] {p.ModuleName} — {p.Message}"));

        var report = await orchestrator.RunAsync(modules, context, progress, CancellationToken.None);

        var htmlPath = Path.Combine(outDir, "pc-health-report.html");
        var jsonPath = Path.Combine(outDir, "pc-health-report.json");
        HtmlReportExporter.WriteTo(report, htmlPath);
        JsonReportExporter.WriteTo(report, jsonPath);

        Console.WriteLine();
        Console.WriteLine($"Overall: {report.OverallGrade}");
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
