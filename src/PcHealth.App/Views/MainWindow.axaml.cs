using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PcHealth.App.ViewModels;

namespace PcHealth.App.Views;

public partial class MainWindow : Window
{
    private bool _exitCleanupDone;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
        WireViewModel();
    }

    private void WireViewModel()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ConfirmInteraction = request => ConfirmWindow.ShowAsync(this, request);
            vm.PickFolderInteraction = PickFolderAsync;
        }
    }

    /// <summary>
    /// On exit, offer to remove anything the app itself installed (never anything
    /// pre-existing, never anything in use) — then close for real.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_exitCleanupDone) return;
        if (DataContext is not MainWindowViewModel vm || !vm.HasRemovableDependencies)
        {
            _exitCleanupDone = true;
            return;
        }

        e.Cancel = true;
        _ = RunExitCleanupAsync(vm);
    }

    private async Task RunExitCleanupAsync(MainWindowViewModel vm)
    {
        try
        {
            await vm.OfferExitCleanupAsync();
        }
        finally
        {
            _exitCleanupDone = true;
            Close();
        }
    }

    private async void OnExportHtml(object? sender, RoutedEventArgs e)
    {
        var path = await PickSavePathAsync("HTML report", "*.html", $"pc-health-report-{DateTime.Now:yyyyMMdd-HHmm}.html");
        if (path is not null) (DataContext as MainWindowViewModel)?.ExportHtml(path);
    }

    private async void OnExportJson(object? sender, RoutedEventArgs e)
    {
        var path = await PickSavePathAsync("JSON report", "*.json", $"pc-health-report-{DateTime.Now:yyyyMMdd-HHmm}.json");
        if (path is not null) (DataContext as MainWindowViewModel)?.ExportJson(path);
    }

    /// <summary>Lets the user name the one folder an on-demand scan is allowed to read.</summary>
    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder to scan (only this folder is read)",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSavePathAsync(string typeName, string pattern, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save report (stays on this PC)",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [pattern] }],
        });
        return file?.TryGetLocalPath();
    }
}
