using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcHealth.App.Views;
using PcHealth.Core.Dependencies;
using PcHealth.Modules.Windows.Dependencies;

namespace PcHealth.App.ViewModels;

/// <summary>
/// Optional-dependency handling: offer installs before use (showing the exact command),
/// and offer a safe removal when the app closes — never touching anything that was
/// already on the PC, never removing while another app is using it, and logging every
/// removal with its reinstall command for 15 days.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private WindowsDependencyManager? _dependencyManager;
    private DependencyDefinition? _offeredDependency;

    /// <summary>Set by the view; shows a yes/no dialog with the command text.</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmInteraction { get; set; }

    [ObservableProperty] private bool _hasDependencyOffer;
    [ObservableProperty] private string _dependencyOfferText = "";

    private void InitializeDependencies()
    {
        _dependencyManager = new WindowsDependencyManager(new DependencyLedger());
        RefreshDependencyOffer();
    }

    private void RefreshDependencyOffer()
    {
        var missing = _dependencyManager?.GetMissing();
        _offeredDependency = missing is { Count: > 0 } ? missing[0] : null;
        HasDependencyOffer = _offeredDependency is not null;
        DependencyOfferText = _offeredDependency is { } dependency
            ? $"Optional: {dependency.Name} is not installed. {dependency.Purpose}"
            : "";
    }

    [RelayCommand]
    private async Task InstallDependencyAsync()
    {
        if (_dependencyManager is null || _offeredDependency is not { } dependency || ConfirmInteraction is null) return;

        var approved = await ConfirmInteraction(new ConfirmRequest(
            Title: $"Install {dependency.Name}?",
            Message: $"{dependency.Purpose}\n\n" +
                     "This downloads the tool from the internet and installs it with administrator " +
                     "rights (you will see one UAC prompt). Because this app installs it, the app " +
                     "will also offer to remove it again when you close the app.\n\n" +
                     "Nothing is installed if you choose No.",
            CommandText: dependency.InstallCommandDisplay,
            YesLabel: "Install",
            NoLabel: "Don't install"));
        if (!approved)
        {
            ProgressText = $"{dependency.Name} was not installed — the related readings simply stay out of the report.";
            return;
        }

        ProgressText = $"Installing {dependency.Name}… approve the administrator prompt to continue.";
        var (_, message) = await _dependencyManager.InstallAsync(dependency);
        ProgressText = message;
        RefreshDependencyOffer();
    }

    /// <summary>True when closing the app should first offer to clean up what it installed.</summary>
    public bool HasRemovableDependencies => _dependencyManager?.GetRemovable().Count > 0;

    /// <summary>
    /// The exit flow. For each tool THIS app installed: refuse removal while a known
    /// consumer is running, otherwise ask — with the exact command — and log the removal
    /// (with the reinstall command) to the 15-day history file.
    /// </summary>
    public async Task OfferExitCleanupAsync()
    {
        if (_dependencyManager is null || ConfirmInteraction is null) return;

        foreach (var (definition, record) in _dependencyManager.GetRemovable())
        {
            var consumers = _dependencyManager.GetRunningConsumers(definition);
            if (consumers.Count > 0)
            {
                await ConfirmInteraction(new ConfirmRequest(
                    Title: $"{definition.Name} left installed",
                    Message: $"This app installed {definition.Name} on {record.InstalledAt:yyyy-MM-dd} and would " +
                             "normally offer to remove it now. It was NOT removed, because these programs that " +
                             $"use it are currently running: {string.Join(", ", consumers)}.\n\n" +
                             "Removing it now could break them. Close them first if you want it removed on the next exit.",
                    CommandText: null,
                    YesLabel: "OK",
                    NoLabel: "Close"));
                continue;
            }

            var remove = await ConfirmInteraction(new ConfirmRequest(
                Title: $"Remove {definition.Name}?",
                Message: $"This app installed {definition.Name} on {record.InstalledAt:yyyy-MM-dd}.\n\n" +
                         "Safety checks passed: it was not on this PC before this app installed it, and no " +
                         "programs known to use it are running.\n\n" +
                         "If you remove it, the reinstall command is kept in dependency-history.txt for 15 days:\n" +
                         $"{_dependencyManager.Ledger.HistoryPath}\n\n" +
                         "Keeping it is also fine — the next scan will simply have full sensor readings without asking again.",
                CommandText: definition.UninstallCommandDisplay,
                YesLabel: "Remove it",
                NoLabel: "Keep it"));
            if (!remove) continue;

            var (_, message) = await _dependencyManager.UninstallAsync(definition, record);
            ProgressText = message;
        }
    }
}
