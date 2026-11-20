using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Optimizer.App.ViewModels;
using Optimizer.App.Views;
using Optimizer.Core.Diagnostics;
using Optimizer.Platform.Windows;

namespace Optimizer.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(new WindowsInventoryCollector(), DiagnosticEngine.CreateDefault());
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.ScanCommand.ExecuteAsync(null);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
