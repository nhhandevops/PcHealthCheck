using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcHealth.App.Worker;
using PcHealth.Core.Consent;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Processes;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly Lazy<VerdictEngine> Engine = new(() => new VerdictEngine(RulesLoader.LoadEmbedded()));

    private readonly ConsentStore _consentStore = new();
    private readonly bool _alreadyElevated = Elevation.IsElevated();
    private CancellationTokenSource? _scanCts;
    private string? _elevationNote;

    public ObservableCollection<ModuleRowViewModel> Modules { get; } = [];
    public ObservableCollection<CategoryCardViewModel> CategoryCards { get; } = [];
    public ObservableCollection<string> ReceiptLines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelScanCommand))]
    private bool _isScanning;

    [ObservableProperty] private string _progressText = "Select the checks you consent to, then press Scan. Nothing runs without your selection.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private string _overallLabel = "";
    [ObservableProperty] private IBrush _overallBrush = Brushes.Gray;

    public ScanReport? LastReport { get; private set; }

    public MainWindowViewModel()
    {
        var consents = _consentStore.Load();
        foreach (var module in new WindowsModuleProvider().CreateModules())
        {
            var row = new ModuleRowViewModel(module);
            // Consent only carries over when it was given for the exact same capabilities.
            row.IsSelected = ConsentStore.IsConsentCurrent(
                consents.GetValueOrDefault(module.Manifest.Id), module.Manifest.CapabilityHash);
            row.SelectionChanged += UpdateSummary;
            Modules.Add(row);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = Modules.Where(m => m.IsSelected).ToList();
        var admin = selected.Count(m => m.Manifest.NeedsAdmin);
        var writes = selected.Count(m => m.Manifest.Writes is not null);
        var internet = selected.Count(m => m.Manifest.NeedsInternet);
        var adminNote = admin == 0 ? ""
            : _alreadyElevated ? " (already running as admin — no prompt)"
            : " (one UAC prompt)";
        SummaryLine = $"{selected.Count} of {Modules.Count} checks selected — " +
                      $"{admin} need admin{adminNote}, " +
                      $"{writes} write a temp file, {internet} use the internet.";
        ScanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var row in Modules) row.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Modules) row.IsSelected = false;
    }

    private bool CanScan() => !IsScanning && Modules.Any(m => m.IsSelected);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var selected = Modules.Where(m => m.IsSelected).Select(m => m.Module).ToList();
        // When the app is already running elevated there is nothing to escalate to:
        // everything runs in-process and the user sees no prompt at all.
        var localModules = _alreadyElevated ? selected : [.. selected.Where(m => !m.Manifest.NeedsAdmin)];
        var adminModules = _alreadyElevated ? [] : selected.Where(m => m.Manifest.NeedsAdmin).ToList();
        PersistConsent();

        IsScanning = true;
        HasResults = false;
        CategoryCards.Clear();
        ReceiptLines.Clear();
        _elevationNote = null;
        _scanCts = new CancellationTokenSource();

        try
        {
            var receipt = new ReceiptLog();
            var context = new ModuleContext
            {
                Processes = new ProcessRunner(receipt),
                Receipt = receipt,
                AppTempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHealthCheck", "temp"),
                IsElevated = _alreadyElevated,
                ScanStartedAt = DateTimeOffset.Now,
            };
            var progress = new Progress<ScanProgress>(p =>
                ProgressText = $"[{p.Index}/{p.Total}] {p.ModuleName}…");

            var orchestrator = new ScanOrchestrator(Engine.Value);
            var ct = _scanCts.Token;

            var results = new List<ModuleResult>(
                await Task.Run(() => orchestrator.RunModulesAsync(localModules, context, progress, ct), ct));
            var receiptEntries = new List<ReceiptEntry>(receipt.Snapshot());
            var ranElevated = _alreadyElevated;

            if (adminModules.Count > 0)
            {
                ProgressText = "Waiting for your approval of the Windows administrator prompt…";
                var adminProgress = new Progress<ScanProgress>(p =>
                    ProgressText = $"Administrator checks [{p.Index}/{p.Total}] {p.ModuleName}…");

                var outcome = await new ElevatedWorkerSession()
                    .RunAsync([.. adminModules.Select(m => m.Manifest.Id)], adminProgress, ct);

                results.AddRange(outcome.Results);
                receiptEntries.AddRange(outcome.Receipt);
                ranElevated = outcome.Status == ElevatedScanStatus.Completed && outcome.Results.Count > 0;
                _elevationNote = outcome.Message;
                if (outcome.RejectedModuleIds.Count > 0)
                {
                    _elevationNote = (_elevationNote is null ? "" : _elevationNote + " ") +
                        $"The helper refused unrecognized checks: {string.Join(", ", outcome.RejectedModuleIds)}.";
                }
            }

            var names = selected.ToDictionary(m => m.Manifest.Id, m => m.Manifest.Name, StringComparer.Ordinal);
            var report = orchestrator.BuildReport(results, receiptEntries, ranElevated, names);

            PresentReport(report);
            ProgressText = $"Scan finished {report.GeneratedAt:HH:mm:ss} — overall: {GradeVisuals.Label(report.OverallGrade)}. " +
                           (_elevationNote is null ? "Export the report below, or re-run anytime." : _elevationNote);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scan cancelled. Nothing was changed on the system.";
        }
        catch (Exception e)
        {
            ProgressText = $"Scan failed: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            IsScanning = false;
        }
    }

    private bool CanCancel() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelScan() => _scanCts?.Cancel();

    private void PersistConsent()
    {
        var state = _consentStore.Load();
        foreach (var row in Modules)
        {
            state[row.Manifest.Id] = new ModuleConsent
            {
                Enabled = row.IsSelected,
                LastConsentedCapabilityHash = row.IsSelected ? row.Manifest.CapabilityHash : null,
                Timestamp = DateTimeOffset.Now,
            };
        }
        try { _consentStore.Save(state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* consent persistence is convenience only */ }
    }

    private void PresentReport(ScanReport report)
    {
        LastReport = report;
        OverallLabel = GradeVisuals.Label(report.OverallGrade);
        OverallBrush = GradeVisuals.Brush(report.OverallGrade);

        foreach (var group in report.Verdicts
                     .GroupBy(v => v.Metric.Category)
                     .OrderBy(g => WorstRank(g)))
        {
            var grade = ScanReport.Worst(group.Select(v => v.Grade));
            var rows = group
                .OrderBy(v => Rank(v.Grade, v.Metric.InfoOnly))
                .Select(v => new VerdictRowViewModel(v))
                .ToList();
            CategoryCards.Add(new CategoryCardViewModel(CategoryLabel(group.Key), GradeVisuals.Label(grade), GradeVisuals.Brush(grade), rows));
        }

        foreach (var entry in report.Receipt)
        {
            ReceiptLines.Add(entry switch
            {
                ProcessReceipt p => $"{p.At:HH:mm:ss}  [{p.ModuleId}] ran: {p.FileName} {p.Arguments} (exit {p.ExitCode?.ToString() ?? "?"})",
                FileWriteReceipt f => $"{f.At:HH:mm:ss}  [{f.ModuleId}] wrote: {f.Path} ({f.Bytes:N0} bytes){(f.Deleted ? " — deleted afterwards" : "")}",
                _ => entry.ToString() ?? "",
            });
        }
        if (ReceiptLines.Count == 0) ReceiptLines.Add("No external processes were run and no files were written.");
        if (_elevationNote is not null) ReceiptLines.Add($"Administrator checks: {_elevationNote}");
        ReceiptLines.Add(report.RanElevated
            ? "Administrator checks ran in a separate helper process that exited when the scan finished."
            : "This scan used no administrator rights.");
        ReceiptLines.Add("Network connections made by PcHealthCheck: 0 (the app contains no network code).");

        HasResults = true;
    }

    public void ExportHtml(string path) => Export(() => HtmlReportExporter.WriteTo(LastReport!, path), path);

    public void ExportJson(string path) => Export(() => JsonReportExporter.WriteTo(LastReport!, path), path);

    private void Export(Action write, string path)
    {
        if (LastReport is null) return;
        try
        {
            write();
            ProgressText = $"Report saved to {path}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ProgressText = $"Could not save report: {e.Message}";
        }
    }

    private static int Rank(Grade grade, bool infoOnly) => infoOnly ? 4 : grade switch
    {
        Grade.Bad => 0,
        Grade.Normal => 1,
        Grade.Good => 2,
        _ => 3,
    };

    private static int WorstRank(IGrouping<HealthCategory, Verdict> group) =>
        ScanReport.Worst(group.Select(v => v.Grade)) switch
        {
            Grade.Bad => 0,
            Grade.Normal => 1,
            Grade.Good => 2,
            _ => 3,
        };

    private static string CategoryLabel(HealthCategory category) => category switch
    {
        HealthCategory.SoftwareOs => "Software / OS",
        _ => category.ToString(),
    };
}

public sealed partial class ModuleRowViewModel(IHealthModule module) : ObservableObject
{
    public IHealthModule Module { get; } = module;
    public ModuleManifest Manifest => Module.Manifest;

    [ObservableProperty] private bool _isSelected;

    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public string Name => Manifest.Name;
    public string Description => Manifest.Description;
    public string ReadsText => "Reads: " + string.Join(" · ", Manifest.Reads);
    public string DurationText => Manifest.EstimatedDuration.TotalSeconds is < 10 ? "~seconds" : $"~{Manifest.EstimatedDuration.TotalSeconds:0} s";
    public bool NeedsAdmin => Manifest.NeedsAdmin;
    public bool HasWrites => Manifest.Writes is not null;
    public string WritesText => Manifest.Writes is { } w ? $"WRITES: {w.PathDescription} ({w.Lifetime})" : "";
    public bool NeedsInternet => Manifest.NeedsInternet;
}

public sealed record CategoryCardViewModel(string Name, string GradeLabel, IBrush GradeBrush, IReadOnlyList<VerdictRowViewModel> Verdicts);

public sealed class VerdictRowViewModel(Verdict verdict)
{
    public string GradeLabel { get; } = verdict.Metric.InfoOnly ? "INFO" : GradeVisuals.Label(verdict.Grade);
    public IBrush GradeBrush { get; } = verdict.Metric.InfoOnly ? Brushes.SlateGray : GradeVisuals.Brush(verdict.Grade);
    public string Label { get; } = verdict.Metric.Label;
    public string Subject { get; } = verdict.Metric.Subject ?? "";
    public bool HasSubject { get; } = verdict.Metric.Subject is not null;
    public string ValueText { get; } = verdict.Metric.Value + (verdict.Metric.Unit is { } u ? $" {u}" : "");
    public string Detail { get; } = verdict.Metric.Detail ?? "";
    public bool HasDetail { get; } = !string.IsNullOrEmpty(verdict.Metric.Detail);
    public IReadOnlyList<string> Tips { get; } = verdict.Tips;
    public bool HasTips { get; } = verdict.Tips.Count > 0;
    public string Forecast { get; } = verdict.Forecast ?? "";
    public bool HasForecast { get; } = !string.IsNullOrEmpty(verdict.Forecast);
}

public static class GradeVisuals
{
    public static string Label(Grade grade) => grade switch
    {
        Grade.Good => "GOOD",
        Grade.Normal => "NORMAL",
        Grade.Bad => "ACTION NEEDED",
        _ => "NOT MEASURED",
    };

    public static IBrush Brush(Grade grade) => grade switch
    {
        Grade.Good => new SolidColorBrush(Color.Parse("#1a7f37")),
        Grade.Normal => new SolidColorBrush(Color.Parse("#b8860b")),
        Grade.Bad => new SolidColorBrush(Color.Parse("#c62828")),
        _ => new SolidColorBrush(Color.Parse("#607080")),
    };
}
