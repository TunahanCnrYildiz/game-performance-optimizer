using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.App.Views;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Platform.Windows;
using Optimizer.Platform.Windows.Changes;

namespace Optimizer.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(
                new WindowsInventoryCollector(),
                DiagnosticEngine.CreateDefault(),
                new WindowsTweakCatalog(),
                new TweakEngine(new WindowsChangeExecutor(), new Journal(AppPaths.JournalDirectory), AppPaths.Version),
                new UserDialogs(() => desktop.MainWindow));

            window.DataContext = viewModel;
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal static class AppPaths
{
    public static string JournalDirectory => WindowsAppPaths.JournalDirectory;

    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
