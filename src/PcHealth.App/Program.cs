using Avalonia;

namespace PcHealth.App;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--headless-scan"))
        {
            return HeadlessScan.RunAsync(args).GetAwaiter().GetResult();
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
