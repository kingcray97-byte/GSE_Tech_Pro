using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1: pass/fail masks built from a known-good capture.</summary>
public sealed class WaveformMaskTests
{
    private static ScopeCapture Capture(double rate, int ch, double[] samples) => new()
    {
        SampleRateHz = rate,
        SampleCount = samples.Length,
        TimebaseSecondsPerDivision = samples.Length / rate / 10,
        Channels = new Dictionary<int, double[]> { [ch] = samples },
    };

    private static double[] Square(int n, int period, double low = 0, double high = 5) =>
        Enumerable.Range(0, n).Select(i => (i % period) < period / 2 ? low : high).ToArray();

    [Fact]
    public void Envelope_IsWidenedByVoltageAndTimeTolerance()
    {
        var mask = WaveformMask.Create(new[] { 0.0, 0, 0, 5, 5, 5, 0, 0 }, 1000, 1, 0.5, 0.001);
        // radius 1 sample: the step widens by one sample each side.
        Assert.Equal(new[] { 0.5, 0.5, 5.5, 5.5, 5.5, 5.5, 5.5, 0.5 }, mask.Upper);
        Assert.Equal(new[] { -0.5, -0.5, -0.5, -0.5, 4.5, -0.5, -0.5, -0.5 }, mask.Lower);
    }

    [Fact]
    public void SameWaveform_Passes_AndAGlitch_Fails_WithItsTime()
    {
        var good = Square(1000, 100);
        var mask = WaveformMask.Create(good, 10_000, 1, 0.3, 0.0002);
        Assert.True(mask.Test(Capture(10_000, 1, good)).Passed);

        var bad = (double[])good.Clone();
        bad[525] = 2.5; // mid-level glitch well inside a high half-period
        bad[526] = 2.5;
        var r = mask.Test(Capture(10_000, 1, bad));
        Assert.False(r.Passed);
        Assert.Equal(2, r.Violations);
        Assert.Equal(0.0525, r.FirstViolationSeconds!.Value, 9);
        Assert.Contains("FAIL", r.Message);
    }

    [Fact]
    public void MismatchedCapture_GivesAnError_NotAVerdict()
    {
        var mask = WaveformMask.Create(Square(1000, 100), 10_000, 1, 0.3, 0);
        Assert.Null(mask.Test(Capture(20_000, 1, Square(1000, 100))).Passed);
        Assert.Null(mask.Test(Capture(10_000, 1, Square(500, 100))).Passed);
        var missing = mask.Test(Capture(10_000, 2, Square(1000, 100)));
        Assert.Null(missing.Passed);
        Assert.Contains("Channel A", missing.Message);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, -1)]
    public void ToleranceMustBeEntered(double vTol, double tTol) =>
        Assert.Throws<ArgumentException>(() => WaveformMask.Create(new[] { 1.0 }, 1000, 1, vTol, tTol));

    [Fact]
    public void SlidingExtreme_MatchesBruteForce()
    {
        var rng = new Random(7);
        var x = Enumerable.Range(0, 300).Select(_ => rng.NextDouble()).ToArray();
        foreach (int r in new[] { 1, 3, 17 })
        {
            var mx = WaveformMask.SlidingExtreme(x, r, max: true);
            var mn = WaveformMask.SlidingExtreme(x, r, max: false);
            for (int i = 0; i < x.Length; i++)
            {
                var w = x.Skip(Math.Max(0, i - r)).Take(Math.Min(x.Length, i + r + 1) - Math.Max(0, i - r)).ToArray();
                Assert.Equal(w.Max(), mx[i]);
                Assert.Equal(w.Min(), mn[i]);
            }
        }
    }
}
