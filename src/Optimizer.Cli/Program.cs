using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimizer.Core.Benchmarks;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Elevation;
using Optimizer.Core.Platform;
using Optimizer.Core.Sessions;

// Usage:
//   optimizer [--json]        read-only diagnostic report
//   optimizer history         list every change this app made
//   optimizer revert <id>     undo one change (id prefix is enough)
//   optimizer revert-all      undo everything — works even if the desktop app does not start
//   optimizer sample [sec]    measure what uses CPU/GPU/I-O right now (default 6 s)
//   optimizer bench <pid|name> [sec] [label] [--admin]   FPS capture (Windows, PresentMon)
//   optimizer import <log> [label]                       FPS log import (Linux, MangoHud)
//   optimizer --elevated <request> <response>           internal: elevated helper
Console.OutputEncoding = Encoding.UTF8;

IPlatformServices platform =
#if WINDOWS
    new Optimizer.Platform.Windows.WindowsPlatform();
#else
    new Optimizer.Platform.Linux.LinuxPlatform();
#endif

if (args is ["--elevated", var requestPath, var responsePath])
    return platform.RunElevatedHelper(requestPath, responsePath);

var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "report";
var engine = new TweakEngine(platform.Executor, new Journal(platform.Paths.Journal), "cli");

// Entries that touched machine-wide settings can only be reverted elevated: re-run this command elevated.
if (command is "revert" or "revert-all"
    && engine.Journal.LoadAll().Any(e => e.CanRevert && e.RequiresAdmin)
    && !ElevatedHost.IsElevated())
{
    Console.WriteLine("Bazı değişiklikler yönetici izni gerektiriyor; komut yönetici olarak yeniden çalıştırılıyor...");
    return RelaunchElevated(args);
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
    var rest = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Skip(1).ToArray();
    switch (command)
    {
        case "history":
            PrintHistory(engine.Journal);
            return 0;

        case "revert-all":
            return PrintReverts(engine.RevertAll());

        case "revert":
            var prefix = rest.ElementAtOrDefault(0);
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
            Sample(int.TryParse(rest.ElementAtOrDefault(0), out var sec) ? sec : 6);
            return 0;

        case "bench":
            return await Bench(rest, args.Contains("--admin"));

        case "import":
            return Import(rest);

        case "report":
            await Report(args.Contains("--json"));
            return 0;

        default:
            Console.Error.WriteLine($"Bilinmeyen komut: {command}. Komutlar: report, history, revert <id>, revert-all, sample, bench, import");
            return 1;
    }
}

static int RelaunchElevated(string[] args)
{
    var quoted = string.Join(" ", args.Select(a => $"\"{a}\""));
    try
    {
        using var elevated = OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = quoted + " --pause",
                UseShellExecute = true,
                Verb = "runas",
            })!
            : Process.Start(new ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"\"{Environment.ProcessPath}\" {quoted}",
                UseShellExecute = false,
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

async Task<int> Bench(string[] rest, bool admin)
{
    if (!platform.FrameCapture.CanAttach)
    {
        Console.Error.WriteLine(platform.FrameCapture.UnavailableReason ?? platform.FrameCapture.ImportHint ?? "Bu sistemde doğrudan ölçüm yok.");
        return 1;
    }

    var target = rest.ElementAtOrDefault(0);
    var seconds = int.TryParse(rest.ElementAtOrDefault(1), out var s) ? s : 20;
    var label = rest.ElementAtOrDefault(2) ?? "ölçüm";

    var process = int.TryParse(target, out var pid)
        ? Process.GetProcessById(pid)
        : Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target ?? "")).OrderByDescending(p => p.WorkingSet64).FirstOrDefault();
    if (process is null)
    {
        Console.Error.WriteLine($"Süreç bulunamadı: {target}");
        return 1;
    }

    Console.WriteLine($"{process.ProcessName} (PID {process.Id}) {seconds} sn ölçülüyor{(admin ? " (yönetici izniyle)" : "")}...");
    var result = await platform.FrameCapture.CaptureAsync(new CaptureRequest(process.Id, seconds, Elevated: admin));
    if (!result.Success)
    {
        Console.Error.WriteLine(result.Error + (result.MayNeedElevation ? " (--admin ile deneyin)" : ""));
        return 2;
    }

    return Summarize(process.ProcessName, label, result.Frames, DateTimeOffset.Now);
}

int Import(string[] rest)
{
    if (!platform.FrameCapture.CanImport || rest.ElementAtOrDefault(0) is not { } path)
    {
        Console.Error.WriteLine(platform.FrameCapture.CanImport ? "Kullanım: optimizer import <kayıt.csv> [etiket]" : "Bu sistemde kayıt içe aktarma yok.");
        return 1;
    }

    var (game, frames) = platform.FrameCapture.Import(path);
    return Summarize(game ?? Path.GetFileNameWithoutExtension(path), rest.ElementAtOrDefault(1) ?? "ölçüm", frames, File.GetLastWriteTime(path));
}

int Summarize(string game, string label, IReadOnlyList<FrameRecord> all, DateTimeOffset at)
{
    var summary = FrameStatistics.Compute(FrameStatistics.MainSwapChain(all));
    if (summary is null)
    {
        Console.Error.WriteLine($"Yeterli kare yok ({all.Count}).");
        return 2;
    }

    Console.WriteLine($"Kare: {summary.Frames}, süre {summary.DurationSeconds:0.0} sn");
    Console.WriteLine($"Ortalama FPS: {summary.AvgFps:0.0} | 1% low: {summary.Low1PercentFps:0.0} | 0.1% low: {summary.Low01PercentFps:0.0}");
    Console.WriteLine($"Kare süresi: medyan {summary.MedianFrameMs:0.00} ms, %99 {summary.P99FrameMs:0.00} ms, en uzun {summary.MaxFrameMs:0.0} ms, takılma {summary.Stutters}");
    Console.WriteLine($"CPU meşgul {summary.AvgCpuBusyMs:0.00} ms, GPU meşgul {summary.AvgGpuBusyMs:0.00} ms → sınırlayan: {summary.Bottleneck}; ekrana gelmeyen kare %{summary.DroppedPercent:0.0}");

    new BenchmarkStore(platform.Paths.Benchmarks).Save(new BenchmarkRun
    {
        Id = Guid.NewGuid(),
        GameName = game,
        Label = label,
        CapturedAt = at,
        Summary = summary,
    });
    return 0;
}

void Sample(int seconds)
{
    using var sampler = platform.CreateSampler();
    var roots = platform.FindGameRoots();
    Console.WriteLine("Oyun klasörleri: " + (roots.Count == 0 ? "(bulunamadı)" : string.Join(", ", roots)));

    var matcher = new GameMatcher(roots, [], []);
    var agg = new SessionAggregator("ölçüm", "", "", DateTimeOffset.Now);
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
        Console.WriteLine($"İşlemci: ort. kullanım %{cpu.AvgUtilizationPercent:0.0}"
            + (cpu.AvgEffectiveMhz > 0 ? $", ort. saat {cpu.AvgEffectiveMhz:0} MHz (en düşük {cpu.MinEffectiveMhz:0})" : ", saat hızı okunamıyor")
            + $", sınırlanan örnek %{cpu.LimitedSamplePercent:0}");
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

async Task Report(bool json)
{
    var inventory = await platform.Inventory.CollectAsync();
    var findings = platform.CreateDiagnostics().Evaluate(inventory);

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

    Console.WriteLine($"Sistem : {inventory.Os?.Name} ({inventory.Os?.Version})");
    Console.WriteLine($"İşlemci: {inventory.Cpu?.Name} — {inventory.Cpu?.PhysicalCores} çekirdek / {inventory.Cpu?.LogicalProcessors} iş parçacığı");
    foreach (var gpu in inventory.Gpus)
        Console.WriteLine($"GPU    : {gpu.Name} [{gpu.Kind}] sürücü {gpu.DriverVersion} {gpu.DriverDate:dd.MM.yyyy}");
    if (inventory.Memory is { } mem)
        Console.WriteLine(mem.Modules.Count == 0
            ? $"Bellek : {mem.TotalBytes / 1024 / 1024 / 1024} GB"
            : $"Bellek : {mem.TotalBytes / 1024 / 1024 / 1024} GB, {mem.Modules.Count} modül, {mem.Generation} @ {mem.ConfiguredSpeedMts} MT/s");
    foreach (var d in inventory.Displays)
        Console.WriteLine($"Ekran  : {d.MonitorName} {d.Current.Width}x{d.Current.Height} @ {d.Current.RefreshHz} Hz (en fazla {d.MaxRefreshHzAtCurrentResolution} Hz) → {d.AdapterName}");
    foreach (var disk in inventory.Disks)
        Console.WriteLine($"Disk   : {disk.RootPath} {disk.MediaType} {disk.BusType} {disk.FreeBytes / 1024 / 1024 / 1024} / {disk.TotalBytes / 1024 / 1024 / 1024} GB boş");
    if (inventory.Linux is { } linux)
        Console.WriteLine($"Linux  : çekirdek {linux.KernelVersion}, oturum {linux.SessionType ?? "?"}, governor {linux.CpuGovernor ?? "?"} ({linux.CpuFreqDriver ?? "?"}), güç profili {linux.PowerProfile ?? "?"}, vm.max_map_count {linux.MaxMapCount}, GameMode {(linux.GameModeInstalled ? "kurulu" : "yok")}");

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
        var fix = platform.Tweaks.FindFor(f) is { } tweak ? $"  (düzeltilebilir: {tweak.Title})" : "";
        Console.WriteLine($"{tag} {f.Title}{fix}");
        if (f.Severity > Severity.Ok && f.Recommendation is not null)
            Console.WriteLine($"        → {f.Recommendation}");
    }

    foreach (var e in inventory.Errors)
        Console.WriteLine($"[HATA ] {e.Collector}: {e.Message}");
}
