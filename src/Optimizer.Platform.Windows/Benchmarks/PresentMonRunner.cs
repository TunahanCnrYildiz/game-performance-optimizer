using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Optimizer.Core.Benchmarks;

namespace Optimizer.Platform.Windows.Benchmarks;

public sealed record CaptureRequest(int ProcessId, int Seconds, int DelaySeconds = 0, bool Elevated = false);

public sealed record CaptureResult(bool Success, IReadOnlyList<FrameRecord> Frames, string? Error = null, bool MayNeedElevation = false);

/// <summary>
/// Runs Intel's PresentMon (MIT) for a timed capture of one process and parses its CSV.
/// Works without administrator rights for processes started by the same user; a game
/// running elevated or under another account needs <see cref="CaptureRequest.Elevated"/>,
/// which shows a UAC prompt for PresentMon only.
/// </summary>
public sealed class PresentMonRunner(string executablePath)
{
    private const string SessionName = "PerformanceOptimizer";

    public string ExecutablePath { get; } = executablePath;

    /// <summary>PresentMon shipped next to the app (tools\PresentMon.exe), or null.</summary>
    public static PresentMonRunner? Locate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");
        return File.Exists(path) ? new PresentMonRunner(path) : null;
    }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PerformanceOptimizer");
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, $"capture-{Guid.NewGuid():N}.csv");

        string[] args =
        [
            "--process_id", request.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--output_file", csv,
            "--timed", request.Seconds.ToString(CultureInfo.InvariantCulture),
            "--terminate_after_timed",
            "--delay", request.DelaySeconds.ToString(CultureInfo.InvariantCulture),
            "--session_name", SessionName,
            "--stop_existing_session",
            "--no_console_stats",
        ];

        var info = new ProcessStartInfo { FileName = ExecutablePath };
        if (request.Elevated)
        {
            // runas requires ShellExecute, which cannot redirect output; the CSV is all we need.
            info.UseShellExecute = true;
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.Arguments = string.Join(" ", args.Select(Quote));
        }
        else
        {
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            foreach (var a in args)
                info.ArgumentList.Add(a);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("PresentMon başlatılamadı.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new CaptureResult(false, [], "Yönetici izni verilmedi; ölçüm yapılmadı.");
        }

        using (process)
        {
            var stderr = request.Elevated ? Task.FromResult("") : process.StandardError.ReadToEndAsync(cancellationToken);
            if (!request.Elevated)
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Seconds + request.DelaySeconds + 30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                TryDelete(csv);
                return new CaptureResult(false, [], cancellationToken.IsCancellationRequested ? "Ölçüm iptal edildi." : "PresentMon zamanında bitmedi.");
            }

            var errors = (await stderr).Trim();
            if (!File.Exists(csv))
            {
                return new CaptureResult(
                    false, [],
                    "Bu süreçten hiç kare yakalanamadı. Oyun ölçüm sırasında ekranda değildi, kare üretmedi ya da yönetici olarak çalışıyor olabilir."
                    + (process.ExitCode != 0 && errors.Length > 0 ? $" PresentMon: {FirstLine(errors)}" : ""),
                    MayNeedElevation: !request.Elevated);
            }

            try
            {
                using var reader = new StreamReader(csv);
                var frames = PresentMonCsv.Parse(reader);
                return frames.Count == 0
                    ? new CaptureResult(false, [], "Ölçüm boş döndü.", MayNeedElevation: !request.Elevated)
                    : new CaptureResult(true, frames);
            }
            finally
            {
                TryDelete(csv);
            }
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Header-driven parser for PresentMon 2.x CSV (column order varies with options and versions).</summary>
public static class PresentMonCsv
{
    public static IReadOnlyList<FrameRecord> Parse(TextReader reader)
    {
        var header = reader.ReadLine();
        if (header is null)
            return [];

        var columns = header.Split(',').Select((name, i) => (name: name.Trim(), i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
        int Col(string name) => columns.TryGetValue(name, out var i) ? i : -1;

        var app = Col("Application");
        var pid = Col("ProcessID");
        var chain = Col("SwapChainAddress");
        var between = Col("MsBetweenPresents");
        var cpu = Col("MsCPUBusy");
        var gpu = Col("MsGPUBusy");
        // A frame that never reached the screen has no display timing ("NA").
        var displayed = Col("MsUntilDisplayed") >= 0 ? Col("MsUntilDisplayed") : Col("MsBetweenDisplayChange");

        if (between < 0)
            throw new FormatException("PresentMon çıktısında MsBetweenPresents sütunu yok.");

        var frames = new List<FrameRecord>();
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split(',');
            if (cells.Length <= between || Number(cells, between) is not { } frameMs)
                continue;

            frames.Add(new FrameRecord(
                app >= 0 && app < cells.Length ? cells[app] : "",
                pid >= 0 && int.TryParse(cells.ElementAtOrDefault(pid), out var p) ? p : 0,
                chain >= 0 ? cells.ElementAtOrDefault(chain) ?? "" : "",
                frameMs,
                Number(cells, cpu),
                Number(cells, gpu),
                displayed < 0 || Number(cells, displayed) is not null));
        }

        return frames;
    }

    private static double? Number(string[] cells, int index) =>
        index >= 0 && index < cells.Length
        && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
