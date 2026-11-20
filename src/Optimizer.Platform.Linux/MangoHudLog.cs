using System.Globalization;
using System.Text.RegularExpressions;
using Optimizer.Core.Benchmarks;

namespace Optimizer.Platform.Linux;

/// <summary>
/// Linux has no tool that can attach to an already running game and read its frame times
/// without injecting into it, so frame times come from MangoHud's own log:
/// a system-info header, then "fps,frametime,..." per frame (frame time in milliseconds).
/// </summary>
public sealed partial class MangoHudLog : IFrameCapture
{
    public bool CanAttach => false;
    public bool CanImport => true;
    public string? UnavailableReason => null;
    public IReadOnlyList<string> ImportPatterns => ["*.csv"];

    public string? ImportHint =>
        "Linux'ta çalışan bir oyuna dışarıdan bağlanıp kare süresi okumak mümkün değil; oyunun kendisinin kayıt tutması gerekiyor. "
        + "MangoHud kurun ve Steam'de oyunun başlatma seçeneklerine şunu yazın: "
        + "MANGOHUD_CONFIG=output_folder=$HOME/mangohud,autostart_log=10,log_duration=60 mangohud %command% — "
        + "oyun açıldıktan 10 saniye sonra 60 saniyelik bir kayıt ~/mangohud klasörüne yazılır (\"_summary\" ile bitmeyen dosyayı seçin). "
        + "Her ölçümü aynı sahnede yapın ve bir etiket verin.";

    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CaptureResult(false, [], "Linux'ta doğrudan ölçüm yok; MangoHud kaydını içe aktarın."));

    public (string? GameName, IReadOnlyList<FrameRecord> Frames) Import(string path)
    {
        if (path.EndsWith("_summary.csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bu MangoHud'un özet dosyası; kare kaydı aynı addaki \"_summary\" olmayan dosyada.");

        using var reader = new StreamReader(path);
        var frames = Parse(reader);
        var name = NameRegex().Match(Path.GetFileNameWithoutExtension(path));
        return (name.Success ? name.Groups[1].Value : null, frames);
    }

    public static IReadOnlyList<FrameRecord> Parse(TextReader reader)
    {
        string? header = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("fps,", StringComparison.OrdinalIgnoreCase))
            {
                header = line;
                break;
            }
        }

        if (header is null)
            throw new FormatException("MangoHud kare başlığı (fps,frametime,...) bulunamadı.");

        var columns = header.Split(',').Select(c => c.Trim()).ToList();
        var frametime = columns.IndexOf("frametime");
        if (frametime < 0)
            throw new FormatException("MangoHud kaydında frametime sütunu yok.");

        var frames = new List<FrameRecord>();
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split(',');
            if (cells.Length > frametime
                && double.TryParse(cells[frametime], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                && ms > 0)
            {
                // MangoHud logs only presented frames and has no CPU/GPU busy split.
                frames.Add(new FrameRecord("", 0, "", ms, null, null, true));
            }
        }

        return frames;
    }

    // "<program>_YYYY-MM-DD_HH-MM-SS"
    [GeneratedRegex(@"^(.+)_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$")]
    private static partial Regex NameRegex();
}
