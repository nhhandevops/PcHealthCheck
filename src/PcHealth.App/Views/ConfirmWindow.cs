using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PcHealth.App.Views;

/// <summary>What the app is asking the user to approve, with the exact command shown.</summary>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string? CommandText,
    string YesLabel = "Yes",
    string NoLabel = "No");

/// <summary>
/// A plain yes/no dialog that always shows the exact command about to run — the app never
/// executes an install/removal the user hasn't read first.
/// </summary>
public sealed class ConfirmWindow : Window
{
    public ConfirmWindow(ConfirmRequest request)
    {
        Title = request.Title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };

        panel.Children.Add(new TextBlock
        {
            Text = request.Message,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(request.CommandText))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Command that will run (with administrator rights):",
                FontSize = 12,
                Opacity = 0.75,
            });
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#20808080")),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = new SelectableTextBlock
                {
                    Text = request.CommandText,
                    FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        var yes = new Button { Content = request.YesLabel, MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = request.NoLabel, MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        yes.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { no, yes },
        });

        Content = panel;
    }

    public static async Task<bool> ShowAsync(Window owner, ConfirmRequest request) =>
        await new ConfirmWindow(request).ShowDialog<bool?>(owner) ?? false;
}
