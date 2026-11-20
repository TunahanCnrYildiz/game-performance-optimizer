using Avalonia;

namespace Optimizer.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Short-lived elevated copy started by the platform's elevation runner: do the one job, no UI.
        if (args is ["--elevated", var request, var response])
            return PlatformFactory.Create().RunElevatedHelper(request, response);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
