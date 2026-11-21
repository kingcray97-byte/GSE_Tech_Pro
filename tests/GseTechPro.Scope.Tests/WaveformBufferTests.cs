using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1: waveform buffer (last N captures, bounded by count and memory).</summary>
public sealed class WaveformBufferTests
{
    [Fact]
    public void Empty_HasNoCurrent_AndCannotStep()
    {
        var b = new WaveformBuffer<string>();
        Assert.True(b.IsEmpty);
        Assert.Equal(-1, b.Position);
        Assert.Null(b.Current);
        Assert.False(b.StepBack());
        Assert.False(b.StepForward());
        Assert.Equal("0 / 0", b.PositionText);
        Assert.Equal(64, b.Capacity);
    }

    [Fact]
    public void Add_ShowsNewest_AndStepsBackAndForward()
    {
        var b = new WaveformBuffer<string>();
        b.Add("a", 10); b.Add("b", 10); b.Add("c", 10);
        Assert.Equal("c", b.Current);
        Assert.Equal("3 / 3", b.PositionText);
        Assert.True(b.StepBack()); Assert.True(b.StepBack());
        Assert.Equal("a", b.Current);
        Assert.False(b.StepBack());
        Assert.True(b.StepForward());
        Assert.Equal("b", b.Current);
        b.Add("d", 10); // a new capture always jumps to the end
        Assert.Equal("d", b.Current);
        Assert.Equal("4 / 4", b.PositionText);
        Assert.True(b.MoveFirst()); Assert.Equal("a", b.Current);
        Assert.True(b.MoveLast()); Assert.Equal("d", b.Current);
    }

    [Fact]
    public void Capacity_DropsTheOldest()
    {
        var b = new WaveformBuffer<int>(capacity: 3);
        for (var i = 1; i <= 5; i++) b.Add(i, 1);
        Assert.Equal(3, b.Count);
        b.MoveFirst();
        Assert.Equal(3, b.Current);
    }

    [Fact]
    public void MemoryBudget_DropsTheOldest_ButKeepsAnOversizedNewCapture()
    {
        var b = new WaveformBuffer<int>(capacity: 64, memoryBudgetBytes: 100);
        b.Add(1, 40); b.Add(2, 40);
        b.Add(3, 40); // 120 > 100: oldest goes
        Assert.Equal(2, b.Count);
        Assert.Equal(80, b.BytesHeld);
        b.Add(4, 500); // larger than the whole budget: still kept and shown
        Assert.Equal(1, b.Count);
        Assert.Equal(4, b.Current);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var b = new WaveformBuffer<int>();
        b.Add(1, 5);
        b.Clear();
        Assert.True(b.IsEmpty);
        Assert.Equal(0, b.BytesHeld);
        Assert.Equal(-1, b.Position);
    }

    [Fact]
    public void EstimateCaptureBytes_IsChannelsTimesSamplesTimesEight()
    {
        Assert.Equal(4L * 10_000 * 8 + 1024, WaveformBuffer<int>.EstimateCaptureBytes(4, 10_000));
        // 64 maximum-size captures (4 M samples = 32 MB each) would be 2 GB; the default 256 MB budget keeps ~8.
        var maxCapture = WaveformBuffer<int>.EstimateCaptureBytes(1, WaveformFile.MaxTotalSamples);
        Assert.True(64 * maxCapture > WaveformBuffer<int>.DefaultMemoryBudgetBytes);
        var b = new WaveformBuffer<int>();
        for (var i = 0; i < 64; i++) b.Add(i, maxCapture);
        Assert.InRange(b.Count, 7, 8);
        Assert.True(b.BytesHeld <= WaveformBuffer<int>.DefaultMemoryBudgetBytes);
    }
}
