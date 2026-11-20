using Optimizer.Core.Platform;

namespace Optimizer.App;

internal static class PlatformFactory
{
    public static IPlatformServices Create() =>
#if WINDOWS
        new Optimizer.Platform.Windows.WindowsPlatform();
#else
        new Optimizer.Platform.Linux.LinuxPlatform();
#endif
}
