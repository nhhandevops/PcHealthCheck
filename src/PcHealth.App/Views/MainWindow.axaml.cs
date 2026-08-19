using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PcHealth.App.ViewModels;

namespace PcHealth.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
