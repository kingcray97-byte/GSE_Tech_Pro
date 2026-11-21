using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1: Auto setup picks range, timebase, sample rate and trigger from a probe capture.</summary>
public sealed class AutoSetupTests
{
    private static readonly double[] Ranges = [0.2, 0.5, 1, 2, 5, 10, 20, 50, 100];
    private static readonly double[] Timebases = [0.00005, 0.0001, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5];
    private static readonly int[] Rates = [10_000, 50_000, 100_000, 500_000, 1_000_000, 5_000_000, 20_000_000];

    private static ScopeCapture Capture(int rateHz, params (int Ch, double[] Samples)[] channels) => new()
    {
        SampleRateHz = rateHz,
        SampleCount = channels[0].Samples.Length,
        TimebaseSecondsPerDivision = AutoSetup.ProbeTimebaseSecondsPerDivision,
        Channels = channels.ToDictionary(c => c.Ch, c => c.Samples),
    };

    private static double[] Square(int n, int period, double low, double high) =>
        Enumerable.Range(0, n).Select(i => (i % period) < period / 2 ? low : high).ToArray();

    [Fact]
    public void RepeatingSignal_GetsSmallestFittingRange_ThreeCycleTimebase_AndMidLevelTrigger()
    {
        // 0.25–2.75 V square at 200 Hz (period 5 ms) sampled at 100 kS/s for 200 ms.
        var cap = Capture(100_000, (1, Square(20_000, 500, 0.25, 2.75)));
        var r = AutoSetup.Suggest(cap, Ranges, Timebases, Rates);

        var a = Assert.Single(r.Channels);
        Assert.Equal(5.0, a.RangeVolts);            // 2.75 V > 2 V × 0.9, ≤ 5 V × 0.9
        Assert.False(a.OverRange);
        Assert.Equal(200.0, a.FrequencyHz!.Value, 3);
        Assert.Equal(0.002, r.TimebaseSecondsPerDivision); // 3 cycles = 15 ms → 2 ms/div (20 ms window)
        Assert.Equal(20_000_000, r.SampleRateHz);   // 20 ms × 20 MS/s = 400 k samples, within budget
        Assert.Equal(1, r.TriggerChannel);
        Assert.Equal(1.5, r.TriggerLevelVolts);
        Assert.Contains("A repeats at 200 Hz", r.Summary);
    }

    [Fact]
    public void FlatOrOneShotSignal_KeepsTimebaseAndTrigger()
    {
        var cap = Capture(100_000, (1, Enumerable.Repeat(12.6, 20_000).ToArray()));
        var r = AutoSetup.Suggest(cap, Ranges, Timebases, Rates);

        Assert.Equal(20.0, r.Channels[0].RangeVolts);
        Assert.Null(r.Channels[0].FrequencyHz);
        Assert.Null(r.TimebaseSecondsPerDivision);
        Assert.Null(r.SampleRateHz);
        Assert.Null(r.TriggerChannel);
        Assert.Contains("no repeating signal", r.Summary);
    }

    [Fact]
    public void PeakAboveLargestRange_IsFlaggedOverRange()
    {
        var cap = Capture(100_000, (2, Square(20_000, 1000, -150, 150)));
        var r = AutoSetup.Suggest(cap, Ranges, Timebases, Rates);

        Assert.True(r.Channels[0].OverRange);
        Assert.Equal(100.0, r.Channels[0].RangeVolts);
        Assert.Contains("over range", r.Summary);
        Assert.Equal(2, r.TriggerChannel);
    }

    [Fact]
    public void ProbeRequest_UsesWidestRange_NoTrigger_AndStaysWithinBudget()
    {
        var current = new ScopeCaptureRequest
        {
            Channels = [new ScopeChannelConfig { Channel = 1, VoltageRangeVolts = 2, Coupling = ScopeCoupling.Ac, ProbeMultiplier = 10 }],
            SampleRateHz = 1_000_000,
            TimebaseSecondsPerDivision = 0.001,
            TriggerMode = ScopeTriggerMode.Rising,
        };
        var probe = AutoSetup.BuildProbeRequest(current, Ranges, Rates);

        var ch = Assert.Single(probe.Channels);
        Assert.Equal(100.0, ch.VoltageRangeVolts);
        Assert.Equal(ScopeCoupling.Ac, ch.Coupling);
        Assert.Equal(10.0, ch.ProbeMultiplier);
        Assert.Equal(ScopeTriggerMode.None, probe.TriggerMode);
        Assert.Equal(AutoSetup.ProbeTimebaseSecondsPerDivision, probe.TimebaseSecondsPerDivision);
        Assert.Equal(5_000_000, probe.SampleRateHz); // 200 ms × 5 MS/s = 1 M samples
    }

    [Fact]
    public void SlowSignal_UsesLongTimebase_AndLowerRate()
    {
        // 10 Hz square sampled at 10 kS/s over 1 s.
        var cap = Capture(10_000, (1, Square(10_000, 1000, 0, 5)));
        var r = AutoSetup.Suggest(cap, Ranges, Timebases, Rates);

        Assert.Equal(10.0, r.Channels[0].FrequencyHz!.Value, 3);
        Assert.Equal(0.05, r.TimebaseSecondsPerDivision);  // 3 cycles = 300 ms → 50 ms/div (500 ms window)
        Assert.Equal(1_000_000, r.SampleRateHz);           // 0.5 s × 1 MS/s = 500 k samples
        Assert.Equal(10.0, r.Channels[0].RangeVolts);      // 5 V > 5 V × 0.9
    }
}
