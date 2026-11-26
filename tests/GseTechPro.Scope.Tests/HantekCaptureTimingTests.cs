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

    [Theory]
    [InlineData(0.008, 0)]  // ±8 mV -> 2 mV/div (index 0)
    [InlineData(1.0, 7)]    // ±1 V  -> 250 mV/div needed -> 500 mV/div (index 7, ±2 V span)
    [InlineData(4.0, 8)]    // ±4 V  -> 1 V/div (index 8, ±4 V span)
    [InlineData(5.0, 9)]    // ±5 V  -> 1.25 V/div needed -> 2 V/div (index 9, ±8 V span; avoids clipping 4..5 V)
    [InlineData(8.0, 9)]    // ±8 V  -> 2 V/div (index 9, ±8 V span)
    [InlineData(20.0, 10)]  // ±20 V -> 5 V/div (index 10, ±20 V span)
    [InlineData(40.0, 11)]  // ±40 V -> 10 V/div (index 11, ±40 V span)
    [InlineData(100.0, 11)] // clamps to widest 10 V/div (index 11)
    public void SelectVoltDivIndex_BipolarRange_DividesByFourSoFullPlusMinusRangeFitsWithoutClipping(
        double bncBipolarRangeVolts,
        ushort expectedVoltDivIndex) =>
        Assert.Equal(expectedVoltDivIndex, Hantek6074BeDevice.SelectVoltDivIndex(bncBipolarRangeVolts));

    [Fact]
    public void ResolveCaptureSampleCounts_WhenSampleCountNull_DerivesTenDivisionsFromActualHardwareSampleRate()
    {
        // At 1 ms/div (10 ms total sweep) when requested was 100 kHz but Hantek reports actual 250 kHz:
        // 10 ms * 250 kHz = 2,500 samples; aligned buffer length = 2,560 (5 * 512).
        var (sampleCount, alignedLen) = Hantek6074BeDevice.ResolveCaptureSampleCounts(
            explicitSampleCount: null,
            timebaseSecondsPerDivision: 0.001,
            actualSampleRateHz: 250_000,
            enabledChannelCount: 1);

        Assert.Equal(2_500, sampleCount);
        Assert.Equal(2_560u, alignedLen);
        Assert.Equal(0u, alignedLen % 512u);
    }

    [Fact]
    public void ResolveCaptureSampleCounts_HonorsExplicitSampleCountAndEnforces512AlignmentAndMaxCap()
    {
        // Explicit sample count is honored rather than derived from timebase * actualSampleRateHz
        var (explicitCount, explicitAligned) = Hantek6074BeDevice.ResolveCaptureSampleCounts(
            explicitSampleCount: 1_000,
            timebaseSecondsPerDivision: 0.001,
            actualSampleRateHz: 250_000,
            enabledChannelCount: 1);

        Assert.Equal(1_000, explicitCount);
        Assert.Equal(1_024u, explicitAligned);

        // Sub-512 count clamps up to 512 minimum
        var (minCount, minAligned) = Hantek6074BeDevice.ResolveCaptureSampleCounts(
            explicitSampleCount: 100,
            timebaseSecondsPerDivision: 0.001,
            actualSampleRateHz: 10_000,
            enabledChannelCount: 1);

        Assert.Equal(512, minCount);
        Assert.Equal(512u, minAligned);

        // Very slow timebase / high rate clamps to WaveformFile.MaxTotalSamples / enabledChannelCount
        var (maxCount, maxAligned) = Hantek6074BeDevice.ResolveCaptureSampleCounts(
            explicitSampleCount: null,
            timebaseSecondsPerDivision: 10.0,
            actualSampleRateHz: 1_000_000,
            enabledChannelCount: 4);

        Assert.Equal(WaveformFile.MaxTotalSamples / 4, maxCount);
        Assert.True(maxAligned >= (uint)maxCount);
        Assert.Equal(0u, maxAligned % 512u);
    }

    [Fact]
    public void HantekCalibration_BufferLength_AtLeast578()
    {
        // Guard against shrinking _calibrationLevels back to DefMacro.h's stale NEW_CAL_LEVEL_LEN (400):
        // HTHardDll.dll writes ~577 ushorts (CHS SDK PDF requires 577; SDK VB demo allocates CalLevel(578)).
        Assert.True(
            Hantek6074BeDevice.CalibrationLevelLen >= 578,
            $"CalibrationLevelLen ({Hantek6074BeDevice.CalibrationLevelLen}) must be >= 578 to prevent HTHardDll.dll heap overrun.");
        Assert.True(
            Hantek6074BeDevice.CalibrationLevelRequestLen <= Hantek6074BeDevice.CalibrationLevelLen,
            "CalibrationLevelRequestLen must not exceed CalibrationLevelLen.");
        Assert.True(
            Hantek6074BeDevice.AmpCalibrationLen >= 578,
            $"AmpCalibrationLen ({Hantek6074BeDevice.AmpCalibrationLen}) must be >= 578.");
    }

    [Fact]
    public void ConvertRawCountsToVolts_DefaultSoftwareGainIsUnity_WhenHardwareAmpCalibrateIsUsed()
    {
        // Since dsoHTSetAmpCalibrate applies factory gain trim in hardware during capture setup,
        // sample conversion defaults amplitudeCalibrationFactor to 1.0 so gain is not double-applied.
        ushort[] raw = [127, 159, 191];
        var (voltsDefault, clippedDefault) = Hantek6074BeDevice.ConvertRawCountsToVolts(
            raw,
            sampleCount: 3,
            zeroCode: 127,
            voltDivVolts: 1.0,
            probeMultiplier: 1.0);

        var (voltsUnity, clippedUnity) = Hantek6074BeDevice.ConvertRawCountsToVolts(
            raw,
            sampleCount: 3,
            zeroCode: 127,
            voltDivVolts: 1.0,
            probeMultiplier: 1.0,
            amplitudeCalibrationFactor: 1.0);

        Assert.False(clippedDefault);
        Assert.False(clippedUnity);
        Assert.Equal(voltsUnity, voltsDefault);
        Assert.Equal(0.0, voltsDefault[0], precision: 6);
        // 32 counts at 1 V/div (8 V / 255 counts) = 32 * 8 / 255 = 1.00392157 V
        Assert.Equal(32.0 * 8.0 / 255.0, voltsDefault[1], precision: 6);
        // 64 counts at 1 V/div = 64 * 8 / 255 = 2.00784314 V
        Assert.Equal(64.0 * 8.0 / 255.0, voltsDefault[2], precision: 6);
    }
}
