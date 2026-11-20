using Avalonia;
using Optimizer.Platform.Windows.Elevation;

namespace Optimizer.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Short-lived, UAC-elevated copy started by ElevatedRunner: do the one job, no UI.
        if (args is ["--elevated", var request, var response])
            return ElevatedHost.Run(request, response);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
