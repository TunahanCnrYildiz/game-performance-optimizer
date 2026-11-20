using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.App.Views;
using Optimizer.Core.Benchmarks;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Sessions;

namespace Optimizer.App;

public sealed class App : Application
{
    private SessionMonitor? _monitor;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var platform = PlatformFactory.Create();
            var settings = new SettingsStore(platform.Paths.Settings);
            var sessionStore = new SessionStore(platform.Paths.Sessions);
            var engine = new TweakEngine(platform.Executor, new Journal(platform.Paths.Journal), AppPaths.Version);

            _monitor = new SessionMonitor(
                platform.CreateSampler(),
                () =>
                {
                    var s = settings.Load();
                    return new GameMatcher(platform.FindGameRoots(), s.UserGames, s.IgnoredGames);
                },
                // Without efficiency-mode support the throttle list is ignored, so nothing is ever left changed.
                platform.SupportsEfficiencyMode ? settings.Load : () => settings.Load() with { EfficiencyModeApps = [] },
                engine,
                sessionStore);

            var sessions = new SessionsViewModel(_monitor, settings, sessionStore, platform.SupportsEfficiencyMode);
            var dialogs = new UserDialogs(() => desktop.MainWindow);
            var changes = new ChangeService(engine, platform);
            var benchmarks = new BenchmarksViewModel(platform.FrameCapture, new BenchmarkStore(platform.Paths.Benchmarks), _monitor, dialogs);
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(
                platform.Inventory,
                platform.CreateDiagnostics(),
                platform.Tweaks,
                changes,
                dialogs,
                sessions,
                benchmarks,
                new AdvancedViewModel(changes, dialogs, platform));

            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PerformanceOptimizer/Assets/icon.png")));
            window.Icon = icon;
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Opened += async (_, _) => await viewModel.InitializeAsync();

            // While game sessions are watched, closing the window only hides it to the tray.
            window.Closing += (_, e) =>
            {
                if (!_exiting && sessions.IsMonitoring)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
            window.Closed += (_, _) => Exit(desktop);

            void Show()
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }

            var showItem = new NativeMenuItem("Aç");
            showItem.Click += (_, _) => Show();
            var exitItem = new NativeMenuItem("Çık");
            exitItem.Click += (_, _) => Exit(desktop);

            var tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "Performance Optimizer",
                Menu = new NativeMenu { Items = { showItem, new NativeMenuItemSeparator(), exitItem } },
            };
            tray.Clicked += (_, _) => Show();
            TrayIcon.SetIcons(this, [tray]);

            desktop.Exit += (_, _) => _monitor?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Ends any running session first, so throttled apps are restored before the process exits.</summary>
    private void Exit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting)
            return;

        _exiting = true;
        _monitor?.Dispose();
        _monitor = null;
        desktop.Shutdown();
    }
}

internal static class AppPaths
{
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
