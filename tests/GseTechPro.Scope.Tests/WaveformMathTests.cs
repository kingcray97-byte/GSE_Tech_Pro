using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Xunit;

namespace GseTechPro.Scope.Tests;

public sealed class WaveformMathTests
{
    [Fact]
    public void ComputeMeasurements_SineWave_ComputesExactPeakVppFrequencyDutyAndRipple()
    {
        // 100 Hz sine wave with 3.0 V amplitude (6.0 Vpp) riding on +1.0 V DC offset
        // Sampled at 100,000 Hz for 0.1 s (10 complete cycles = 10,000 samples)
        const double sampleRateHz = 100_000.0;
        const double freqHz = 100.0;
        const double amplitude = 3.0;
        const double dcOffset = 1.0;
        const int sampleCount = 10_000;

        double[] samples = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / sampleRateHz;
            samples[i] = dcOffset + amplitude * Math.Sin(2.0 * Math.PI * freqHz * t);
        }

        var config = new ScopeChannelConfig
        {
            Channel = 1,
            VoltageRangeVolts = 10.0,
            Coupling = ScopeCoupling.Dc,
            ProbeMultiplier = 1.0,
            CurrentScaleAmpsPerVolt = 0.0,
        };

        ScopeMeasurement[] results = WaveformMath.ComputeMeasurements(samples, sampleRateHz, config, channel: 1);

        var freq = GetByKind(results, MeasurementKind.Frequency);
        Assert.Equal(ScopeMeasurementStatus.Computed, freq.Status);
        Assert.Equal("Hz", freq.Unit);
        Assert.NotNull(freq.Value);
        Assert.InRange(freq.Value!.Value, 99.9, 100.1);

        var peak = GetByKind(results, MeasurementKind.Peak);
        Assert.Equal(ScopeMeasurementStatus.Computed, peak.Status);
        Assert.Equal("V", peak.Unit);
        Assert.NotNull(peak.Value);
        Assert.InRange(peak.Value!.Value, 3.999, 4.001);

        var vpp = GetByKind(results, MeasurementKind.PeakToPeak);
        Assert.Equal(ScopeMeasurementStatus.Computed, vpp.Status);
        Assert.Equal("V", vpp.Unit);
        Assert.NotNull(vpp.Value);
        Assert.InRange(vpp.Value!.Value, 5.998, 6.002);

        var duty = GetByKind(results, MeasurementKind.DutyCycle);
        Assert.Equal(ScopeMeasurementStatus.Computed, duty.Status);
        Assert.Equal("%", duty.Unit);
        Assert.NotNull(duty.Value);
        Assert.InRange(duty.Value!.Value, 49.8, 50.2);

        // AC RMS of sine with amplitude A is A / sqrt(2)
        double expectedRipple = amplitude / Math.Sqrt(2.0);
        var ripple = GetByKind(results, MeasurementKind.Ripple);
        Assert.Equal(ScopeMeasurementStatus.Computed, ripple.Status);
        Assert.Equal("V", ripple.Unit);
        Assert.NotNull(ripple.Value);
        Assert.InRange(ripple.Value!.Value, expectedRipple - 0.005, expectedRipple + 0.005);

        // Current without clamp -> NotConfigured
        var current = GetByKind(results, MeasurementKind.Current);
        Assert.Equal(ScopeMeasurementStatus.NotConfigured, current.Status);
        Assert.Null(current.Value);
        Assert.Equal("A", current.Unit);
    }

    [Fact]
    public void ComputeMeasurements_TrapezoidalWave_ComputesExactDutyCycleRiseTimeAndCurrent()
    {
        // 1,000 Hz waveform (period = 1 ms = 1,000 samples at 1 MS/s)
        // Rises linearly from 0 V to 10 V over 100 samples (100 µs => 10%-to-90% rise time = 80 µs)
        // Holds at 10 V for 200 samples, falls to 0 V over 100 samples, stays at 0 V for 600 samples.
        // Time above vMid (5 V): from sample 50 to sample 350 = 300 samples = 30.0% duty cycle.
        const double sampleRateHz = 1_000_000.0;
        const int samplesPerPeriod = 1_000;
        const int periods = 5;
        double[] samples = new double[samplesPerPeriod * periods];

        for (int p = 0; p < periods; p++)
        {
            int baseIdx = p * samplesPerPeriod;
            for (int i = 0; i < samplesPerPeriod; i++)
            {
                double v = i switch
                {
                    < 100 => 10.0 * (i / 100.0),
                    < 300 => 10.0,
                    < 400 => 10.0 * (1.0 - (i - 300) / 100.0),
                    _ => 0.0,
                };
                samples[baseIdx + i] = v;
            }
        }

        var config = new ScopeChannelConfig
        {
            Channel = 2,
            VoltageRangeVolts = 10.0,
            Coupling = ScopeCoupling.Dc,
            ProbeMultiplier = 1.0,
            CurrentScaleAmpsPerVolt = 15.0, // 15 A/V => peak 10 V * 15 = 150 A
        };

        ScopeMeasurement[] results = WaveformMath.ComputeMeasurements(samples, sampleRateHz, config, channel: 2);

        var duty = GetByKind(results, MeasurementKind.DutyCycle);
        Assert.Equal(ScopeMeasurementStatus.Computed, duty.Status);
        Assert.Equal("%", duty.Unit);
        Assert.NotNull(duty.Value);
        Assert.InRange(duty.Value!.Value, 29.9, 30.1);

        var rise = GetByKind(results, MeasurementKind.RiseTime);
        Assert.Equal(ScopeMeasurementStatus.Computed, rise.Status);
        Assert.Equal("s", rise.Unit);
        Assert.NotNull(rise.Value);
        // 10% (1 V at sample 10) to 90% (9 V at sample 90) = 80 samples = 80 µs = 8.0e-5 s
        Assert.InRange(rise.Value!.Value, 7.99e-5, 8.01e-5);

        var current = GetByKind(results, MeasurementKind.Current);
        Assert.Equal(ScopeMeasurementStatus.Computed, current.Status);
        Assert.Equal("A", current.Unit);
        Assert.NotNull(current.Value);
        Assert.InRange(current.Value!.Value, 149.99, 150.01);
    }

    [Fact]
    public void ComputeMeasurements_FlatSignal_ReturnsFlatSignalForCyclicKindsAndComputedForStaticKinds()
    {
        double[] samples = Enumerable.Repeat(12.5, 500).ToArray();
        var config = new ScopeChannelConfig
        {
            Channel = 1,
            VoltageRangeVolts = 20.0,
            Coupling = ScopeCoupling.Dc,
            CurrentScaleAmpsPerVolt = 10.0,
        };

        ScopeMeasurement[] results = WaveformMath.ComputeMeasurements(samples, 10_000.0, config, channel: 1);

        Assert.Equal(ScopeMeasurementStatus.FlatSignal, GetByKind(results, MeasurementKind.Frequency).Status);
        Assert.Null(GetByKind(results, MeasurementKind.Frequency).Value);

        Assert.Equal(ScopeMeasurementStatus.FlatSignal, GetByKind(results, MeasurementKind.DutyCycle).Status);
        Assert.Null(GetByKind(results, MeasurementKind.DutyCycle).Value);

        Assert.Equal(ScopeMeasurementStatus.FlatSignal, GetByKind(results, MeasurementKind.RiseTime).Status);
        Assert.Null(GetByKind(results, MeasurementKind.RiseTime).Value);

        var peak = GetByKind(results, MeasurementKind.Peak);
        Assert.Equal(ScopeMeasurementStatus.Computed, peak.Status);
        Assert.Equal(12.5, peak.Value!.Value, precision: 5);

        var vpp = GetByKind(results, MeasurementKind.PeakToPeak);
        Assert.Equal(ScopeMeasurementStatus.Computed, vpp.Status);
        Assert.Equal(0.0, vpp.Value!.Value, precision: 5);

        var ripple = GetByKind(results, MeasurementKind.Ripple);
        Assert.Equal(ScopeMeasurementStatus.Computed, ripple.Status);
        Assert.Equal(0.0, ripple.Value!.Value, precision: 5);

        var current = GetByKind(results, MeasurementKind.Current);
        Assert.Equal(ScopeMeasurementStatus.Computed, current.Status);
        Assert.Equal(125.0, current.Value!.Value, precision: 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ComputeMeasurements_TooFewSamples_ReturnsTooFewSamplesStatus(int length)
    {
        double[] samples = new double[length];
        var config = new ScopeChannelConfig { Channel = 1, VoltageRangeVolts = 5.0, CurrentScaleAmpsPerVolt = 10.0 };

        ScopeMeasurement[] results = WaveformMath.ComputeMeasurements(samples, 10_000.0, config, channel: 1);

        Assert.All(results, m =>
        {
            Assert.Equal(ScopeMeasurementStatus.TooFewSamples, m.Status);
            Assert.Null(m.Value);
        });
    }

    [Fact]
    public void ComputeMeasurements_NaNOrInfinityInSamples_GuardsAndReturnsTooFewSamples()
    {
        double[] samples = [1.0, 2.0, double.NaN, 4.0];
        var config = new ScopeChannelConfig { Channel = 1, VoltageRangeVolts = 5.0 };

        ScopeMeasurement[] results = WaveformMath.ComputeMeasurements(samples, 10_000.0, config, channel: 1);

        Assert.Equal(ScopeMeasurementStatus.TooFewSamples, GetByKind(results, MeasurementKind.Peak).Status);
        Assert.Equal(ScopeMeasurementStatus.NotConfigured, GetByKind(results, MeasurementKind.Current).Status);
    }

    [Fact]
    public void ComputeMeasurements_SubThresholdAdcNoiseOnUnconnectedChannel_ReturnsFlatSignalForFrequencyDutyAndRiseTime()
    {
        // Real-hardware Hantek 6074BE case: unconnected CH1 on 8 V range has ~6 LSB of 8-bit ADC noise
        // (min 0.0627 V, max 0.2510 V, Vpp ≈ 0.1883 V < 5% of 8 V = 0.40 V noise floor).
        const int sampleCount = 1_000;
        const double sampleRateHz = 250_000.0;
        double[] levels = [0.0627, 0.0941, 0.1255, 0.1569, 0.1882, 0.2196, 0.2510];
        var rng = new Random(42);
        double[] samples = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = levels[rng.Next(levels.Length)];
        // Guarantee exact min and max from the hardware capture
        samples[0] = 0.0627;
        samples[1] = 0.2510;

        var config = new ScopeChannelConfig
        {
            Channel = 1,
            VoltageRangeVolts = 8.0,
            Coupling = ScopeCoupling.Dc,
            ProbeMultiplier = 1.0,
            CurrentScaleAmpsPerVolt = 0.0,
        };

        ScopeMeasurement[] results = WaveformMath.ComputeMeasurements(samples, sampleRateHz, config, channel: 1);

        var freq = GetByKind(results, MeasurementKind.Frequency);
        Assert.Equal(ScopeMeasurementStatus.FlatSignal, freq.Status);
        Assert.Null(freq.Value);

        var duty = GetByKind(results, MeasurementKind.DutyCycle);
        Assert.Equal(ScopeMeasurementStatus.FlatSignal, duty.Status);
        Assert.Null(duty.Value);

        var rise = GetByKind(results, MeasurementKind.RiseTime);
        Assert.Equal(ScopeMeasurementStatus.FlatSignal, rise.Status);
        Assert.Null(rise.Value);

        // Static amplitude measurements (Peak, PeakToPeak, Ripple) remain Computed with actual measured values
        var peak = GetByKind(results, MeasurementKind.Peak);
        Assert.Equal(ScopeMeasurementStatus.Computed, peak.Status);
        Assert.Equal(0.2510, peak.Value!.Value, precision: 4);

        var vpp = GetByKind(results, MeasurementKind.PeakToPeak);
        Assert.Equal(ScopeMeasurementStatus.Computed, vpp.Status);
        Assert.Equal(0.2510 - 0.0627, vpp.Value!.Value, precision: 4);

        var ripple = GetByKind(results, MeasurementKind.Ripple);
        Assert.Equal(ScopeMeasurementStatus.Computed, ripple.Status);
        Assert.NotNull(ripple.Value);
    }

    [Fact]
    public void ComputeMeasurements_SmallRealSignalAboveNoiseFloor_StillComputesFrequencyDutyAndRiseTime()
    {
        // 200 mVpp (0.10 V amplitude) 300 Hz alternator ripple on a 1.0 V AC range:
        // Vpp = 0.20 V >= max(0.02 V, 0.05 * 1.0 V = 0.05 V), so cyclic/edge measurements are computed.
        const double sampleRateHz = 100_000.0;
        const double freqHz = 300.0;
        const double amplitude = 0.10; // 0.20 Vpp
        const int sampleCount = 10_000;

        double[] samples = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = amplitude * Math.Sin(2.0 * Math.PI * freqHz * (i / sampleRateHz));

        var config = new ScopeChannelConfig
        {
            Channel = 1,
            VoltageRangeVolts = 1.0,
            Coupling = ScopeCoupling.Ac,
            ProbeMultiplier = 1.0,
        };

        ScopeMeasurement[] results = WaveformMath.ComputeMeasurements(samples, sampleRateHz, config, channel: 1);

        var freq = GetByKind(results, MeasurementKind.Frequency);
        Assert.Equal(ScopeMeasurementStatus.Computed, freq.Status);
        Assert.InRange(freq.Value!.Value, 299.0, 301.0);

        var duty = GetByKind(results, MeasurementKind.DutyCycle);
        Assert.Equal(ScopeMeasurementStatus.Computed, duty.Status);
        Assert.InRange(duty.Value!.Value, 49.5, 50.5);

        var rise = GetByKind(results, MeasurementKind.RiseTime);
        Assert.Equal(ScopeMeasurementStatus.Computed, rise.Status);
        Assert.NotNull(rise.Value);
    }

    private static ScopeMeasurement GetByKind(IEnumerable<ScopeMeasurement> measurements, MeasurementKind kind) =>
        measurements.Single(m => m.Kind == kind);
}
