using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1: math channels (A+B, A−B, A×B, invert, low-pass, RPM from frequency).</summary>
public sealed class MathChannelTests
{
    private static ScopeCapture Capture(int rateHz, params (int Ch, double[] Samples)[] channels) => new()
    {
        SampleRateHz = rateHz,
        SampleCount = channels.Length == 0 ? 0 : channels[0].Samples.Length,
        TimebaseSecondsPerDivision = 0.001,
        Channels = channels.ToDictionary(c => c.Ch, c => c.Samples),
    };

    private static double[] Square(int n, int period, double low = 0, double high = 5) =>
        Enumerable.Range(0, n).Select(i => (i % period) < period / 2 ? low : high).ToArray();

    [Fact]
    public void Arithmetic_IsSamplewise_AndLabelled()
    {
        var cap = Capture(1000, (1, new[] { 1.0, 2.0, 3.0 }), (2, new[] { 0.5, 0.5, 1.0 }));
        Assert.Equal(new[] { 1.5, 2.5, 4.0 }, MathChannels.Compute(cap, new(MathOperation.Sum)).Values);
        Assert.Equal(new[] { 0.5, 1.5, 2.0 }, MathChannels.Compute(cap, new(MathOperation.Difference)).Values);
        Assert.Equal(new[] { 0.5, 1.0, 3.0 }, MathChannels.Compute(cap, new(MathOperation.Product)).Values);
        Assert.Equal(new[] { -1.0, -2.0, -3.0 }, MathChannels.Compute(cap, new(MathOperation.Invert)).Values);
        Assert.Equal("A−B", new MathChannelDefinition(MathOperation.Difference).Label);
        Assert.Equal("V²", new MathChannelDefinition(MathOperation.Product).Unit);
    }

    [Fact]
    public void MissingChannel_ReportsAnError_InsteadOfInventingData()
    {
        var cap = Capture(1000, (1, new[] { 1.0, 2.0 }));
        var r = MathChannels.Compute(cap, new(MathOperation.Sum, 1, 2));
        Assert.False(r.IsOk);
        Assert.Contains("B", r.Error);
        Assert.Empty(r.Values);
    }

    [Fact]
    public void LowPass_PassesDc_AndSmoothsAStep_AndRejectsABadCutoff()
    {
        var dc = Enumerable.Repeat(2.0, 100).ToArray();
        Assert.All(MathChannels.LowPass(dc, 1000, 10), v => Assert.Equal(2.0, v, 9));

        var step = Enumerable.Range(0, 2000).Select(i => i < 1000 ? 0.0 : 1.0).ToArray();
        var y = MathChannels.LowPass(step, 10_000, 50);
        Assert.InRange(y[1001], 0.0, 0.1);   // does not jump
        Assert.InRange(y[1999], 0.95, 1.0);  // settles (≈ 32 time constants later)

        var cap = Capture(1000, (1, dc));
        Assert.False(MathChannels.Compute(cap, new(MathOperation.LowPass, Parameter: 0)).IsOk);
        Assert.False(MathChannels.Compute(cap, new(MathOperation.LowPass, Parameter: 600)).IsOk); // above Nyquist
        Assert.True(MathChannels.Compute(cap, new(MathOperation.LowPass, Parameter: 100)).IsOk);
    }

    [Fact]
    public void Rpm_IsFrequencyTimes60OverPulsesPerRev()
    {
        // 100 kS/s, period 1000 samples = 100 Hz. With 4 pulses per revolution: 100 × 60 / 4 = 1500 rpm.
        var cap = Capture(100_000, (1, Square(10_000, 1000)));
        var r = MathChannels.Compute(cap, new(MathOperation.RpmFromFrequency, Parameter: 4));
        Assert.True(r.IsOk, r.Error);
        Assert.Equal("rpm", r.Unit);
        Assert.Equal(10_000, r.Values.Length);
        Assert.All(r.Values, v => Assert.Equal(1500.0, v, 6));
    }

    [Fact]
    public void Rpm_NeedsPulsesPerRev_AndARepeatingSignal()
    {
        var cap = Capture(100_000, (1, Square(10_000, 1000)));
        var noPpr = MathChannels.Compute(cap, new(MathOperation.RpmFromFrequency, Parameter: 0));
        Assert.False(noPpr.IsOk);
        Assert.Contains("pulses per revolution", noPpr.Error);

        var flat = Capture(100_000, (1, Enumerable.Repeat(1.0, 1000).ToArray()));
        Assert.Contains("repeating", MathChannels.Compute(flat, new(MathOperation.RpmFromFrequency, Parameter: 1)).Error);
    }

    [Fact]
    public void RisingEdges_IgnoreNoiseInsideTheHysteresisBand()
    {
        // Square wave 0/5 V with ±0.2 V ripple: one edge per period, not dozens.
        var rnd = new Random(7);
        var noisy = Square(4000, 1000).Select(v => v + (rnd.NextDouble() - 0.5) * 0.4).ToArray();
        Assert.Equal(4, MathChannels.RisingEdges(noisy).Count);
    }
}
