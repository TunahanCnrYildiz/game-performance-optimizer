namespace Optimizer.Core.Benchmarks;

public sealed record CaptureRequest(int ProcessId, int Seconds, int DelaySeconds = 0, bool Elevated = false);

public sealed record CaptureResult(bool Success, IReadOnlyList<FrameRecord> Frames, string? Error = null, bool MayNeedElevation = false);

/// <summary>
/// Frame-time source of a platform. Windows attaches to a running game (PresentMon);
/// Linux cannot, and instead imports a log the game wrote itself (MangoHud).
/// </summary>
public interface IFrameCapture
{
    /// <summary>Can measure an already running process for a fixed time.</summary>
    bool CanAttach { get; }

    /// <summary>Can read a frame-time log file written by an overlay/logger.</summary>
    bool CanImport { get; }

    /// <summary>Why neither works right now (e.g. tool missing), or null.</summary>
    string? UnavailableReason { get; }

    /// <summary>How to produce an importable log, shown to the user.</summary>
    string? ImportHint { get; }

    /// <summary>File patterns for the import dialog, e.g. "*.csv".</summary>
    IReadOnlyList<string> ImportPatterns { get; }

    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a log; the game name is taken from the log when it records one.</summary>
    (string? GameName, IReadOnlyList<FrameRecord> Frames) Import(string path);
}
