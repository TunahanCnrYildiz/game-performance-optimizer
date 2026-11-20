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
Console.OutputEncoding = Encoding.UTF8;

var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "report";
var engine = new TweakEngine(new WindowsChangeExecutor(), new Journal(WindowsAppPaths.JournalDirectory), "cli");

switch (command)
{
    case "history":
        PrintHistory(engine.Journal);
        return 0;

    case "revert-all":
        return PrintReverts(engine.RevertAll());

    case "revert":
        var prefix = args.ElementAtOrDefault(1);
        var matches = engine.Journal.LoadAll()
            .Where(e => prefix is not null && e.Id.ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            Console.Error.WriteLine(matches.Count == 0 ? "Kayıt bulunamadı." : "Bu önek birden fazla kayıtla eşleşiyor; daha uzun yazın.");
            return 1;
        }

        return PrintReverts([engine.Revert(matches[0].Id)]);

    case "report":
        await Report(args.Contains("--json"));
        return 0;

    default:
        Console.Error.WriteLine($"Bilinmeyen komut: {command}. Komutlar: report, history, revert <id>, revert-all");
        return 1;
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
