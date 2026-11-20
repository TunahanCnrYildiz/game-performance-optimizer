using Optimizer.Platform.Windows.Benchmarks;

namespace Optimizer.Platform.Windows.Tests;

public class PresentMonCsvTests
{
    // Header copied from a real PresentMon 2.6.0 capture on this project's dev machine.
    private const string Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,"
        + "MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,"
        + "CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,"
        + "AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    private static string Row(string betweenPresents, string untilDisplayed, string cpuBusy = "5.1", string gpuBusy = "3.2") =>
        $"game.exe,1234,0x00000A1B,DXGI,0,0,1,Hardware: Independent Flip,100.0,NA,{betweenPresents},6.9,0.1,1.2,{untilDisplayed},"
        + $"90.0,6.9,{cpuBusy},1.8,0.4,3.0,{gpuBusy},3.7,NA,NA,NA,NA,NA";

    [Fact]
    public void Parses_real_header_and_marks_dropped_frames()
    {
        var csv = string.Join('\n', Header, Row("6.94", "2.5"), Row("7.10", "NA"), Row("NA", "NA"));

        var frames = PresentMonCsv.Parse(new StringReader(csv));

        Assert.Equal(2, frames.Count); // the row without a frame time is skipped
        Assert.Equal("game.exe", frames[0].Application);
        Assert.Equal(1234, frames[0].ProcessId);
        Assert.Equal(6.94, frames[0].MsBetweenPresents, 6);
        Assert.Equal(5.1, frames[0].MsCpuBusy!.Value, 6);
        Assert.Equal(3.2, frames[0].MsGpuBusy!.Value, 6);
        Assert.True(frames[0].Displayed);
        Assert.False(frames[1].Displayed);
    }

    [Fact]
    public void Missing_frame_time_column_is_an_error() =>
        Assert.Throws<FormatException>(() => PresentMonCsv.Parse(new StringReader("Application,ProcessID\ngame.exe,1")));
}
