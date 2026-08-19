using Avalonia;
using PcHealth.App.Worker;

namespace PcHealth.App;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Elevated helper mode: no UI, no window. Started only by the app itself,
        // through the UAC prompt, and only when the user consented to admin checks.
        if (args.Contains("--worker"))
        {
            return WorkerHost.RunAsync(args).GetAwaiter().GetResult();
        }

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
