using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>
/// Phase 6 review fixes in the Hantek 6074BE wrapper that can be checked without hardware: the capture is stamped
/// with the device's reported sample rate, and the capture wait scales with the timebase.
/// </summary>
public sealed class HantekCaptureTimingTests
{
    [Theory]
    [InlineData(1_000_000.0, 250_000, 1_000_000)] // device rate wins over the request
    [InlineData(null, 250_000, 250_000)]          // SDK export missing -> requested rate
    [InlineData(0.0, 250_000, 250_000)]
    [InlineData(double.NaN, 250_000, 250_000)]
    [InlineData(1e12, 250_000, 250_000)]          // nonsense above int range -> requested rate
    public void SampleRate_UsesTheDeviceRate_WhenItIsUsable(double? reported, int requested, int expected) =>
        Assert.Equal(expected, Hantek6074BeDevice.ResolveSampleRateHz(reported, requested));

    [Theory]
    [InlineData(0.001, 2.0)]   // fast timebase keeps the old 2 s floor
    [InlineData(0.1, 3.0)]     // 1 s sweep -> 2 x 1 + 1
    [InlineData(1.0, 21.0)]    // 10 s sweep no longer cut off at 2 s
    [InlineData(100.0, 60.0)]  // capped
    [InlineData(double.NaN, 2.0)]
    public void CaptureTimeout_CoversTwoSweepsPlusMargin(double secondsPerDiv, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, Hantek6074BeDevice.CaptureTimeout(secondsPerDiv).TotalSeconds, 6);
}
