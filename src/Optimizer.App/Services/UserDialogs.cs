using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Optimizer.App.Services;

public interface IUserDialogs
{
    /// <summary>Yes/no question. Closing the window counts as "no".</summary>
    Task<bool> ConfirmAsync(string title, string message, string yes, string no);

    /// <summary>
    /// "Keep this change?" with a countdown. Anything other than an explicit click on
    /// "keep" — timeout, Escape, closing the window — returns false and the change is reverted.
    /// </summary>
    Task<bool> ConfirmWithTimeoutAsync(string title, string message, int seconds);

    /// <summary>Open-file dialog; null when cancelled.</summary>
    Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns);
}

public sealed class UserDialogs(Func<Window?> owner) : IUserDialogs
{
    public async Task<bool> ConfirmAsync(string title, string message, string yes, string no)
    {
        var (window, yesButton, noButton, _) = Build(title, message, yes, no);
        var result = false;
        yesButton.Click += (_, _) => { result = true; window.Close(); };
        noButton.Click += (_, _) => window.Close();
        await Show(window);
        return result;
    }

    public async Task<bool> ConfirmWithTimeoutAsync(string title, string message, int seconds)
    {
        var (window, keepButton, revertButton, countdown) = Build(title, message, "Değişikliği koru", "Geri al");
        var result = false;
        var remaining = seconds;
        countdown.Text = Countdown(remaining);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            countdown.Text = Countdown(remaining);
            if (remaining <= 0)
                window.Close();
        };

        keepButton.Click += (_, _) => { result = true; window.Close(); };
        revertButton.Click += (_, _) => window.Close();
        window.Closed += (_, _) => timer.Stop();

        // Default focus on "revert" so an accidental Enter never keeps a broken mode.
        window.Opened += (_, _) => { revertButton.Focus(); timer.Start(); };

        await Show(window);
        return result;
    }

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns)
    {
        if (owner() is not { } window)
            return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = patterns.Count == 0
                ? null
                : [new Avalonia.Platform.Storage.FilePickerFileType(title) { Patterns = patterns.ToList() }],
        });
        return files.FirstOrDefault()?.Path is { IsFile: true } uri ? uri.LocalPath : null;
    }

    private static string Countdown(int seconds) => $"{Math.Max(seconds, 0)} saniye içinde onaylamazsanız önceki ayara dönülecek.";

    private async Task Show(Window window)
    {
        if (owner() is { } parent)
            await window.ShowDialog(parent);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
    }

    private static (Window Window, Button Primary, Button Secondary, TextBlock Countdown) Build(
        string title, string message, string primaryText, string secondaryText)
    {
        var primary = new Button { Content = primaryText, Classes = { "accent" }, MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        var secondary = new Button { Content = secondaryText, MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        var countdown = new TextBlock { Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = true,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    countdown,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { secondary, primary },
                    },
                },
            },
        };

        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                window.Close();
        };

        return (window, primary, secondary, countdown);
    }
}
