using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcHealth.App.Worker;
using PcHealth.Core.Consent;
using PcHealth.Core.History;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Processes;
using PcHealth.Core.Reporting;
using PcHealth.Core.Verdicts;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Benchmarks;
using PcHealth.Modules.Windows.Support;
using PcHealth.Net.Optional;

namespace PcHealth.App.ViewModels;

[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is created per scan and disposed in the scan's finally block; " +
                    "the view model itself lives for the lifetime of the window and is never disposed by Avalonia.")]
public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly Lazy<VerdictEngine> Engine = new(() => new VerdictEngine(RulesLoader.LoadEmbedded()));

    private readonly ConsentStore _consentStore = new();
    private readonly bool _alreadyElevated = Elevation.IsElevated();
    private CancellationTokenSource? _scanCts;
    private string? _elevationNote;
    private string? _historyNote;

    public ObservableCollection<TrendRowViewModel> Trends { get; } = [];

    /// <summary>
    /// Keeps a local record of the numbers each scan measured so growth can be spotted.
    /// Writes only to the app's own folder; the receipt says so after every scan.
    /// </summary>
    [ObservableProperty] private bool _rememberTrends = true;

    [ObservableProperty] private string _trendSummary = "";
    [ObservableProperty] private bool _hasTrends;

    public ObservableCollection<ModuleRowViewModel> Modules { get; } = [];

    /// <summary>
    /// The same checks split in two for display. With twenty-four cards in one scrolling
    /// column, an administrator check sat far below the fold with nothing to mark where the
    /// group began — findable only by scrolling past everything else.
    /// </summary>
    public ObservableCollection<ModuleRowViewModel> StandardModules { get; } = [];

    public ObservableCollection<ModuleRowViewModel> AdminModules { get; } = [];

    [ObservableProperty] private string _standardHeader = "";
    [ObservableProperty] private string _adminHeader = "";
    public ObservableCollection<CategoryCardViewModel> CategoryCards { get; } = [];
    public ObservableCollection<string> ReceiptLines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelScanCommand), nameof(DeleteHistoryCommand),
        nameof(RunQuickScanCommand), nameof(ScanFolderCommand), nameof(CheckReputationCommand))]
    private bool _isScanning;

    private bool IsNotScanning() => !IsScanning;

    [ObservableProperty] private string _progressText = "Select the checks you consent to, then press Scan. Nothing runs without your selection.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private bool _hasIncompleteChecks;
    [ObservableProperty] private string _incompleteChecksText = "";
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
            (module.Manifest.NeedsAdmin ? AdminModules : StandardModules).Add(row);
        }

        StandardHeader = $"Runs without administrator ({StandardModules.Count.ToString(CultureInfo.InvariantCulture)})";
        AdminHeader = $"Needs administrator ({AdminModules.Count.ToString(CultureInfo.InvariantCulture)})";
        UpdateSummary();
        InitializeDependencies();
        SweepLeftoverBenchmarkFiles();
    }

    /// <summary>
    /// A benchmark file left behind by a run that was killed or lost power is a gigabyte of
    /// the user's disk that nothing would ever reclaim — the module's own cleanup cannot run
    /// if the process never got to finish. Doing it at startup means it happens even if they
    /// never tick that check again, and saying so means it is not a silent deletion.
    /// </summary>
    private void SweepLeftoverBenchmarkFiles()
    {
        var removed = BenchmarkCleanup.SweepOrphans();
        if (removed.Count == 0) return;

        ProgressText = $"Cleaned up {removed.Count.ToString(CultureInfo.InvariantCulture)} leftover benchmark " +
                       $"file(s) from a run that did not finish: {string.Join(", ", removed)}. " +
                       "Select the checks you consent to, then press Scan.";
    }

    private void UpdateSummary()
    {
        var selected = Modules.Where(m => m.IsSelected).ToList();
        var admin = selected.Count(m => m.Manifest.NeedsAdmin);
        var writes = selected.Count(m => m.Manifest.Writes is not null);
        var internet = selected.Count(m => m.Manifest.NeedsInternet);
        var drivers = selected.Count(m => m.Manifest.LoadsDriver);
        var adminNote = admin == 0 ? ""
            : _alreadyElevated ? " (already running as admin — no prompt)"
            : " (one UAC prompt)";
        SummaryLine = $"{selected.Count} of {Modules.Count} checks selected — " +
                      $"{admin} need admin{adminNote}, " +
                      $"{writes} write a temp file, {internet} use the internet" +
                      (drivers > 0 ? $", {drivers} load a kernel driver (asked separately)." : ".");
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

    [RelayCommand(CanExecute = nameof(IsNotScanning))]
    private void DeleteHistory()
    {
        var (_, message) = HistoryService.Clear();
        Trends.Clear();
        HasTrends = false;
        TrendSummary = "Trend history deleted — the next scan starts a fresh baseline.";
        ProgressText = message;
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
            // Until the helper reports its first check, the UAC prompt is open somewhere on screen;
            // the normal checks keep the progress line busy, so say so on every update.
            var promptPending = adminModules.Count > 0;
            var progress = new Progress<ScanProgress>(p =>
                ProgressText = $"[{p.Index}/{p.Total}] {p.ModuleName}…" +
                               (promptPending ? " (the administrator prompt is waiting for you)" : ""));

            var orchestrator = new ScanOrchestrator(Engine.Value);
            var ct = _scanCts.Token;

            // The elevated helper starts first and runs beside the normal checks, so the prompt
            // appears at the start of the scan. It is awaited before the benchmarks, which must
            // have the machine to themselves (HANDOFF rule 11). Task.Run because starting the
            // helper blocks on the prompt until it is answered.
            Task<ElevatedScanOutcome>? adminRun = null;
            if (adminModules.Count > 0)
            {
                ProgressText = "Waiting for your approval of the Windows administrator prompt…";
                var adminProgress = new Progress<ScanProgress>(p =>
                {
                    promptPending = false;
                    ProgressText = $"Administrator checks [{p.Index}/{p.Total}] {p.ModuleName}…";
                });
                var ids = adminModules.Select(m => m.Manifest.Id).ToList();
                adminRun = Task.Run(() => new ElevatedWorkerSession().RunAsync([.. ids], adminProgress, ct), ct);
            }

            var results = new List<ModuleResult>(
                await Task.Run(() => orchestrator.RunModulesAsync(localModules, context, progress,
                    beforeRunsAlone: adminRun is null ? null : () => Task.WhenAny(adminRun), ct: ct), ct));
            var receiptEntries = new List<ReceiptEntry>(receipt.Snapshot());
            var ranElevated = _alreadyElevated;

            if (adminRun is not null)
            {
                var outcome = await adminRun;

                results.AddRange(outcome.Results);
                receiptEntries.AddRange(outcome.Receipt);
                // Whether the elevated helper actually gathered anything, which is a different
                // question from whether it exited cleanly. A helper that delivered every admin
                // result and then died before its completion message is a Failed scan carrying
                // real elevated results; reading elevation off Status would stamp "(standard scan,
                // no admin)" and "This scan used no administrator rights." onto a report full of
                // them. The receipt is this app's honesty artifact — it must not understate either.
                ranElevated = outcome.RanElevated;
                _elevationNote = outcome.Message;
                if (outcome.RejectedModuleIds.Count > 0)
                {
                    _elevationNote = (_elevationNote is null ? "" : _elevationNote + " ") +
                        $"The helper refused unrecognized checks: {string.Join(", ", outcome.RejectedModuleIds)}.";
                }

                // An admin check that produced no result did not run at all — the prompt was
                // declined, the helper could not start, the id was refused. Shared with the
                // headless path rather than written out twice: this logic was fixed here first
                // and the copy in HeadlessScan kept the original bug for another four days.
                results.AddRange(MissingAdminChecks.AsFailedResults(results, adminModules, outcome.Message));
            }

            var names = selected.ToDictionary(m => m.Manifest.Id, m => m.Manifest.Name, StringComparer.Ordinal);

            // Compare against previous scans, then store this one. Numbers only, in the
            // app's own folder — disclosed in the receipt and deletable from the UI.
            var history = RememberTrends
                ? await Task.Run(() => HistoryService.AnalyzeAndRecord(
                    results.SelectMany(r => r.Metrics), DateTimeOffset.Now), ct)
                : HistoryOutcome.Disabled;
            _historyNote = history.Error ?? (RememberTrends
                ? $"Recorded {history.MeasurementsRecorded} measurements to the local trend history " +
                  $"({history.Coverage.ScanCount} scans over {history.Coverage.DaysCovered:0} days)."
                : "Trend history is switched off, so nothing was recorded.");

            var report = orchestrator.BuildReport(results, receiptEntries, ranElevated, names,
                history.Trends, history.Coverage);

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

        // A check that could not finish must never read as "nothing wrong here".
        var incomplete = report.Modules
            .Where(m => m.Status is ModuleStatus.Failed or ModuleStatus.PartiallyCompleted)
            .ToList();

        // "GOOD" is the most reassuring word this app can say, and it is a verdict on the
        // checks that ran, not on the PC, whenever some of them did not.
        OverallLabel = incomplete.Count == 0
            ? GradeVisuals.Label(report.OverallGrade)
            : GradeVisuals.Label(report.OverallGrade) + " — INCOMPLETE";
        OverallBrush = GradeVisuals.Brush(report.OverallGrade);
        HasIncompleteChecks = incomplete.Count > 0;
        IncompleteChecksText = incomplete.Count == 0
            ? ""
            : "These results are missing, which is not the same as being healthy — " +
              string.Join("; ", incomplete.Select(m =>
                  $"{m.ModuleName}: {m.Error ?? (m.Notes.Count > 0 ? m.Notes[0] : "no reason given")}"));

        Trends.Clear();
        foreach (var trend in report.Trends.OrderBy(t => t.Grade switch { Grade.Bad => 0, Grade.Normal => 1, _ => 2 }))
        {
            Trends.Add(new TrendRowViewModel(trend));
        }
        HasTrends = Trends.Count > 0;
        TrendSummary = report.Trends.Count > 0
            ? "These measurements moved since an earlier scan — a counter that is still climbing matters far more than any single reading."
            : report.History is { EarlierScans: > 0 } coverage
                ? $"Nothing has moved: every measurement matches the {coverage.EarlierScans} earlier scan(s) {coverage.SpanText}."
                : report.History is { ScanCount: > 0 }
                    ? "First recorded scan — run again in a week or two and the app will tell you what changed."
                    : "This scan was not added to the history, so changes over time are not shown.";

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
                ProcessReceipt p => $"{p.At:HH:mm:ss}  [{p.ModuleId}] ran: {p.FileName} {p.Arguments} " +
                                    $"(exit {p.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})",
                FileWriteReceipt f => $"{f.At:HH:mm:ss}  [{f.ModuleId}] wrote: {f.Path} ({f.Bytes:N0} bytes){(f.Deleted ? " — deleted afterwards" : "")}",
                _ => entry.ToString() ?? "",
            });
        }
        if (ReceiptLines.Count == 0) ReceiptLines.Add("No external processes were run and no files were written.");
        if (_elevationNote is not null) ReceiptLines.Add($"Administrator checks: {_elevationNote}");
        if (_historyNote is not null) ReceiptLines.Add(_historyNote);
        ReceiptLines.Add(report.RanElevated
            ? "Administrator checks ran in a separate helper process that exited when the scan finished."
            : "This scan used no administrator rights.");
        // The claim has to keep pace with the app: an opt-in lookup now exists, so state the
        // counted number rather than an absolute that a future feature could quietly falsify.
        ReceiptLines.Add("Network connections made by this scan: 0 — no check in this app contains network code.");
        ReceiptLines.Add(NetworkActivityLog.Count == 0
            ? "Internet lookups you have started this session: 0."
            : $"Internet lookups you have started this session: {NetworkActivityLog.Count} " +
              $"(to {string.Join(", ", NetworkActivityLog.Snapshot().Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase))}).");

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
    public bool LoadsDriver => Manifest.LoadsDriver;
}

public sealed record CategoryCardViewModel(string Name, string GradeLabel, IBrush GradeBrush, IReadOnlyList<VerdictRowViewModel> Verdicts);

public sealed class TrendRowViewModel(TrendFinding trend)
{
    public string GradeLabel { get; } = GradeVisuals.Label(trend.Grade);
    public IBrush GradeBrush { get; } = GradeVisuals.Brush(trend.Grade);
    public string Label { get; } = trend.Label;
    public string Subject { get; } = trend.Subject ?? "";
    public bool HasSubject { get; } = !string.IsNullOrEmpty(trend.Subject);
    public string Message { get; } = trend.Message;
    public IReadOnlyList<string> Tips { get; } = trend.Tips;
    public bool HasTips { get; } = trend.Tips.Count > 0;
}

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
