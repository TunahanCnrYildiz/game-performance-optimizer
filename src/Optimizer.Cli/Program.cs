using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimizer.Core.Diagnostics;
using Optimizer.Platform.Windows;

// Read-only diagnostic report. Usage: optimizer [--json]
Console.OutputEncoding = Encoding.UTF8;

var inventory = await new WindowsInventoryCollector().CollectAsync();
var findings = DiagnosticEngine.CreateDefault().Evaluate(inventory);

if (args.Contains("--json"))
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
    Console.WriteLine($"GPU    : {gpu.Name} [{gpu.Kind}] sürücü {gpu.DriverVersion} ({gpu.DriverDate:dd.MM.yyyy}) {(gpu.DedicatedMemoryBytes is { } m ? $"{m / 1024 / 1024 / 1024.0:0.#} GB" : "")}");
if (inventory.Memory is { } mem)
    Console.WriteLine($"Bellek : {mem.TotalBytes / 1024 / 1024 / 1024} GB, {mem.Modules.Count} modül, {mem.Generation} @ {mem.ConfiguredSpeedMts} MT/s");
foreach (var d in inventory.Displays)
    Console.WriteLine($"Ekran  : {d.MonitorName} {d.Current.Width}x{d.Current.Height} @ {d.Current.RefreshHz} Hz (en fazla {d.MaxRefreshHzAtCurrentResolution} Hz) → {d.AdapterName}");
foreach (var disk in inventory.Disks)
    Console.WriteLine($"Disk   : {disk.RootPath} {disk.MediaType} {disk.BusType} {disk.FreeBytes / 1024 / 1024 / 1024} / {disk.TotalBytes / 1024 / 1024 / 1024} GB boş{(disk.GameLibraries.Count > 0 ? " — oyun: " + string.Join(", ", disk.GameLibraries) : "")}");

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
    Console.WriteLine($"{tag} {f.Title}");
    if (f.Severity > Severity.Ok && f.Recommendation is not null)
        Console.WriteLine($"        → {f.Recommendation}");
}

foreach (var e in inventory.Errors)
    Console.WriteLine($"[HATA ] {e.Collector}: {e.Message}");
