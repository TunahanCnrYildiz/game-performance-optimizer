using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Platform.Windows;
using Optimizer.Platform.Windows.Changes;

// Usage:
//   optimizer [--json]        read-only diagnostic report
//   optimizer history         list every change this app made
//   optimizer revert <id>     undo one change (id prefix is enough)
//   optimizer revert-all      undo everything — works even if the desktop app does not start
//   optimizer sample [sec]    measure what uses CPU/GPU/I-O right now (default 6 s)
//   optimizer bench <pid|name> [sec] [label] [--admin]   FPS capture with PresentMon
Console.OutputEncoding = Encoding.UTF8;

var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "report";
var engine = new TweakEngine(new WindowsChangeExecutor(), new Journal(WindowsAppPaths.JournalDirectory), "cli");

// Entries that touched machine-wide settings can only be reverted elevated: re-run this command via UAC.
if (command is "revert" or "revert-all"
    && engine.Journal.LoadAll().Any(e => e.CanRevert && e.RequiresAdmin)
    && !Optimizer.Platform.Windows.Elevation.ElevatedHost.IsElevated())
{
    Console.WriteLine("Bazı değişiklikler yönetici izni gerektiriyor; komut yönetici olarak yeniden çalıştırılıyor...");
    try
    {
        using var elevated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")) + " --pause",
            UseShellExecute = true,
            Verb = "runas",
        })!;
        elevated.WaitForExit();
        return elevated.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
    {
        Console.Error.WriteLine("Yönetici izni verilmedi; hiçbir şey geri alınmadı.");
        return 1;
    }
}

var exitCode = await RunCommand();
if (args.Contains("--pause"))
{
    Console.WriteLine("Kapatmak için bir tuşa basın.");
    Console.ReadKey();
}

return exitCode;

async Task<int> RunCommand()
{
switch (command)
{
    case "history":
        PrintHistory(engine.Journal);
        return 0;

    case "revert-all":
        return PrintReverts(engine.RevertAll());

    case "revert":
        var prefix = args.Where(a => a != "--pause").ElementAtOrDefault(1);
        var matches = engine.Journal.LoadAll()
            .Where(e => prefix is not null && e.Id.ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            Console.Error.WriteLine(matches.Count == 0 ? "Kayıt bulunamadı." : "Bu önek birden fazla kayıtla eşleşiyor; daha uzun yazın.");
            return 1;
        }

        return PrintReverts([engine.Revert(matches[0].Id)]);

    case "sample":
        Sample(int.TryParse(args.ElementAtOrDefault(1), out var sec) ? sec : 6);
        return 0;

    case "bench":
        return await Bench(args.Where(a => a != "--admin").Skip(1).ToArray(), args.Contains("--admin"));

    case "report":
        await Report(args.Contains("--json"));
        return 0;

    default:
        Console.Error.WriteLine($"Bilinmeyen komut: {command}. Komutlar: report, history, revert <id>, revert-all");
        return 1;
}
}

static async Task<int> Bench(string[] rest, bool admin)
{
    var runner = Optimizer.Platform.Windows.Benchmarks.PresentMonRunner.Locate();
    if (runner is null)
    {
        Console.Error.WriteLine(@"PresentMon bulunamadı (tools\PresentMon.exe).");
        return 1;
    }

    var target = rest.ElementAtOrDefault(0);
    var seconds = int.TryParse(rest.ElementAtOrDefault(1), out var s) ? s : 20;
    var label = rest.ElementAtOrDefault(2) ?? "ölçüm";

    var process = int.TryParse(target, out var pid)
        ? System.Diagnostics.Process.GetProcessById(pid)
        : System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target ?? ""))
            .OrderByDescending(p => p.WorkingSet64).FirstOrDefault();
    if (process is null)
    {
        Console.Error.WriteLine($"Süreç bulunamadı: {target}");
        return 1;
    }

    Console.WriteLine($"{process.ProcessName} (PID {process.Id}) {seconds} sn ölçülüyor{(admin ? " (yönetici izniyle)" : "")}...");
    var result = await runner.CaptureAsync(new Optimizer.Platform.Windows.Benchmarks.CaptureRequest(process.Id, seconds, Elevated: admin));
    if (!result.Success)
    {
        Console.Error.WriteLine(result.Error + (result.MayNeedElevation ? " (--admin ile deneyin)" : ""));
        return 2;
    }

    var frames = Optimizer.Core.Benchmarks.FrameStatistics.MainSwapChain(result.Frames);
    var summary = Optimizer.Core.Benchmarks.FrameStatistics.Compute(frames);
    if (summary is null)
    {
        Console.Error.WriteLine($"Yeterli kare yok ({result.Frames.Count}).");
        return 2;
    }

    Console.WriteLine($"Kare: {summary.Frames}, süre {summary.DurationSeconds:0.0} sn");
    Console.WriteLine($"Ortalama FPS: {summary.AvgFps:0.0} | 1% low: {summary.Low1PercentFps:0.0} | 0.1% low: {summary.Low01PercentFps:0.0}");
    Console.WriteLine($"Kare süresi: medyan {summary.MedianFrameMs:0.00} ms, %99 {summary.P99FrameMs:0.00} ms, en uzun {summary.MaxFrameMs:0.0} ms, takılma {summary.Stutters}");
    Console.WriteLine($"CPU meşgul {summary.AvgCpuBusyMs:0.00} ms, GPU meşgul {summary.AvgGpuBusyMs:0.00} ms → sınırlayan: {summary.Bottleneck}; ekrana gelmeyen kare %{summary.DroppedPercent:0.0}");

    new Optimizer.Core.Benchmarks.BenchmarkStore(WindowsAppPaths.BenchmarksDirectory).Save(new Optimizer.Core.Benchmarks.BenchmarkRun
    {
        Id = Guid.NewGuid(),
        GameName = process.ProcessName,
        Label = label,
        CapturedAt = DateTimeOffset.Now,
        Summary = summary,
    });
    return 0;
}

static void Sample(int seconds)
{
    using var sampler = new Optimizer.Platform.Windows.Sessions.WindowsSystemSampler();
    var roots = Optimizer.Platform.Windows.Sessions.GameLibraryLocator.FindGameRoots();
    Console.WriteLine("Oyun klasörleri: " + (roots.Count == 0 ? "(bulunamadı)" : string.Join(", ", roots)));

    var matcher = new Optimizer.Core.Sessions.GameMatcher(roots, [], []);
    var agg = new Optimizer.Core.Sessions.SessionAggregator("ölçüm", "", "", DateTimeOffset.Now);
    sampler.Sample(detailed: true);
    for (var i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);
        var s = sampler.Sample(detailed: true);
        agg.Add(s);
        if (i == 0 && matcher.FindGame(s) is { } game)
            Console.WriteLine($"Çalışan oyun: {game.Name} ({game.Path})");
    }

    var report = agg.Build(DateTimeOffset.Now);
    if (report.Cpu is { } cpu)
        Console.WriteLine($"İşlemci: ort. kullanım %{cpu.AvgUtilizationPercent:0.0}, ort. saat {cpu.AvgEffectiveMhz:0} MHz (en düşük {cpu.MinEffectiveMhz:0}), sınırlanan örnek %{cpu.LimitedSamplePercent:0} (izin verilen en düşük performans %{cpu.MinAllowedPerformancePercent:0})");
    Console.WriteLine();
    Console.WriteLine($"{"Süreç",-32} {"CPU %",7} {"GPU %",7} {"G/Ç MB/s",9} {"Bellek MB",10}");
    foreach (var p in report.Background.Take(15))
        Console.WriteLine($"{p.Name,-32} {p.AvgCpuPercent,7:0.0} {p.AvgGpuPercent,7:0.0} {p.AvgIoBytesPerSec / 1048576,9:0.00} {p.PeakWorkingSetBytes / 1048576,10:0}");
}

static void PrintHistory(Journal journal)
{
    var entries = journal.LoadAll();
    Console.WriteLine($"Günlük klasörü: {journal.DirectoryPath}");
    if (entries.Count == 0)
    {
        Console.WriteLine("Henüz hiçbir değişiklik yapılmadı.");
        return;
    }

    foreach (var e in entries)
    {
        Console.WriteLine($"{e.Id.ToString("N")[..8]}  {e.CreatedAt:dd.MM.yyyy HH:mm}  [{e.Status}]  {e.Title}");
        foreach (var c in e.Changes)
            Console.WriteLine($"          {c.Describe()}");
        foreach (var n in e.Notes)
            Console.WriteLine($"          not: {n}");
    }
}

static int PrintReverts(IReadOnlyList<RevertResult> results)
{
    if (results.Count == 0)
    {
        Console.WriteLine("Geri alınacak açık değişiklik yok.");
        return 0;
    }

    foreach (var r in results)
        Console.WriteLine($"{(r.Success ? "Geri alındı" : "SORUN")}: {r.Entry?.Title} {r.Error}");
    return results.All(r => r.Success) ? 0 : 2;
}

static async Task Report(bool json)
{
    var inventory = await new WindowsInventoryCollector().CollectAsync();
    var findings = DiagnosticEngine.CreateDefault().Evaluate(inventory);

    if (json)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };
        Console.WriteLine(JsonSerializer.Serialize(new { inventory, findings }, options));
        return;
    }

    var catalog = new WindowsTweakCatalog();

    Console.WriteLine($"Sistem : {inventory.Os?.Name} ({inventory.Os?.Version})");
    Console.WriteLine($"İşlemci: {inventory.Cpu?.Name} — {inventory.Cpu?.PhysicalCores} çekirdek / {inventory.Cpu?.LogicalProcessors} iş parçacığı");
    foreach (var gpu in inventory.Gpus)
        Console.WriteLine($"GPU    : {gpu.Name} [{gpu.Kind}] sürücü {gpu.DriverVersion} ({gpu.DriverDate:dd.MM.yyyy})");
    if (inventory.Memory is { } mem)
        Console.WriteLine($"Bellek : {mem.TotalBytes / 1024 / 1024 / 1024} GB, {mem.Modules.Count} modül, {mem.Generation} @ {mem.ConfiguredSpeedMts} MT/s");
    foreach (var d in inventory.Displays)
        Console.WriteLine($"Ekran  : {d.MonitorName} {d.Current.Width}x{d.Current.Height} @ {d.Current.RefreshHz} Hz (en fazla {d.MaxRefreshHzAtCurrentResolution} Hz) → {d.AdapterName}");
    foreach (var disk in inventory.Disks)
        Console.WriteLine($"Disk   : {disk.RootPath} {disk.MediaType} {disk.BusType} {disk.FreeBytes / 1024 / 1024 / 1024} / {disk.TotalBytes / 1024 / 1024 / 1024} GB boş");

    Console.WriteLine();
    foreach (var f in findings)
    {
        var tag = f.Severity switch
        {
            Severity.Warning => "[UYARI]",
            Severity.Suggestion => "[ÖNERİ]",
            Severity.Info => "[BİLGİ]",
            _ => "[  OK ]",
        };
        var fix = catalog.FindFor(f) is { } tweak ? $"  (düzeltilebilir: {tweak.Title})" : "";
        Console.WriteLine($"{tag} {f.Title}{fix}");
        if (f.Severity > Severity.Ok && f.Recommendation is not null)
            Console.WriteLine($"        → {f.Recommendation}");
    }

    foreach (var e in inventory.Errors)
        Console.WriteLine($"[HATA ] {e.Collector}: {e.Message}");
}
