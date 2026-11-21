using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1: DeepMeasure per-cycle measurements.</summary>
public sealed class DeepMeasureTests
{
    private static double[] Square(int n, int period, int highSamples, double low = 0, double high = 5) =>
        Enumerable.Range(0, n).Select(i => (i % period) >= period - highSamples ? high : low).ToArray();

    [Fact]
    public void SquareWave_GivesOneRowPerCycle_WithPeriodDutyAndLevels()
    {
        // 1 kHz sample rate, period 100 samples (0.1 s), high for the last 25 samples of each period.
        var r = DeepMeasure.Analyze(Square(1000, 100, 25), 1000, 1);
        Assert.True(r.IsOk, r.Error);
        Assert.Equal(9, r.Cycles.Count); // 10 rising edges in 1000 samples → 9 full cycles
        foreach (var c in r.Cycles)
        {
            Assert.Equal(0.1, c.PeriodSeconds, 9);
            Assert.Equal(10.0, c.FrequencyHz, 6);
            Assert.Equal(25.0, c.DutyCyclePercent!.Value, 6);
            Assert.Equal(5.0, c.PeakToPeakVolts, 9);
            Assert.Equal(0.0008, c.RiseTimeSeconds!.Value, 9); // one-sample step: 10 %→90 % is 0.8 sample
            Assert.Equal(0.0008, c.FallTimeSeconds!.Value, 9);
        }
        Assert.Equal(1, r.Cycles[0].Number);
        Assert.Equal(0.0745, r.Cycles[0].StartSeconds, 9); // interpolated mid crossing between samples 74 and 75

        var period = r.StatisticsFor(CycleMeasureKind.Period)!;
        Assert.Equal(9, period.Count);
        Assert.Equal(0.0, period.StandardDeviation, 12);
    }

    [Fact]
    public void LinearRamp_RiseTimeIsTenToNinetyPercent()
    {
        // 0 V for 40 samples, ramp to 10 V over 20 samples, 10 V for 40 samples; repeated.
        double[] one = Enumerable.Range(0, 100).Select(i => i < 40 ? 0.0 : i < 60 ? (i - 40) * 0.5 : 10.0).ToArray();
        var x = Enumerable.Repeat(one, 4).SelectMany(a => a).ToArray();
        var r = DeepMeasure.Analyze(x, 10_000, 2);
        Assert.True(r.IsOk, r.Error);
        // 10 %→90 % of 10 V on a 0.5 V/sample ramp = 16 samples = 1.6 ms.
        Assert.Equal(0.0016, r.Cycles[0].RiseTimeSeconds!.Value, 9);
        Assert.Equal(49.5, r.Cycles[0].DutyCyclePercent!.Value, 6); // above mid from 50 to 99.5
    }

    [Fact]
    public void OddCycle_IsTheExtremeInStatistics()
    {
        var x = Square(2000, 100, 50);
        for (int i = 1050; i < 1100; i++) x[i] = 3.5; // one weak high half-cycle (e.g. a failing injector or sensor tooth)
        var r = DeepMeasure.Analyze(x, 1000, 1);
        Assert.True(r.IsOk, r.Error);
        var max = r.StatisticsFor(CycleMeasureKind.Maximum)!;
        Assert.Equal(3.5, max.Minimum, 9);
        Assert.Equal(5.0, max.Maximum, 9);
        var weak = r.Cycles.Single(c => c.Number == max.MinimumCycle);
        Assert.Equal(3.5, weak.MaximumVolts, 9);
        Assert.InRange(weak.StartSeconds, 1.04, 1.06);
    }

    [Fact]
    public void FlatOrOneShot_GivesAPlainError_NotRows()
    {
        var flat = DeepMeasure.Analyze(Enumerable.Repeat(1.2, 500).ToArray(), 1000, 1);
        Assert.False(flat.IsOk);
        Assert.Contains("flat", flat.Error);
        Assert.Empty(flat.Cycles);

        var oneEdge = DeepMeasure.Analyze(Enumerable.Range(0, 500).Select(i => i < 250 ? 0.0 : 5.0).ToArray(), 1000, 1);
        Assert.False(oneEdge.IsOk);
        Assert.Contains("fewer than two rising edges", oneEdge.Error);
    }

    [Fact]
    public void MissingChannel_AndCycleCap()
    {
        var capture = new ScopeCapture
        {
            SampleRateHz = 1000,
            SampleCount = 1000,
            IsSimulated = true,
            Channels = new Dictionary<int, double[]> { [1] = Square(1000, 10, 5) },
        };
        var missing = DeepMeasure.Analyze(capture, 3);
        Assert.Contains("Channel C is not in this capture", missing.Error);

        var capped = DeepMeasure.Analyze(capture, 1, maxCycles: 20);
        Assert.True(capped.IsOk);
        Assert.True(capped.IsSimulated);
        Assert.True(capped.Truncated);
        Assert.Equal(20, capped.Cycles.Count);
    }
}
