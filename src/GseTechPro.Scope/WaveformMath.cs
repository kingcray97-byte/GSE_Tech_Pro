using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Scope;

/// <summary>
/// Deterministic waveform measurement and reference-comparison math engine implementing
/// ADR-011 §4 and §5. All functions are pure and thread-safe.
/// </summary>
public static class WaveformMath
{
    public const string UnitHz = "Hz";
    public const string UnitVolt = "V";
    public const string UnitPercent = "%";
    public const string UnitSecond = "s";
    public const string UnitAmpere = "A";

    /// <summary>
    /// Minimum peak-to-peak swing as a fraction of the channel's configured <see cref="ScopeChannelConfig.VoltageRangeVolts"/>
    /// required before cyclic/edge measurements (<see cref="MeasurementKind.Frequency"/>, <see cref="MeasurementKind.DutyCycle"/>,
    /// <see cref="MeasurementKind.RiseTime"/>) are evaluated.
    /// <para>
    /// Rationale: An 8-bit USB oscilloscope (e.g. Hantek 6074BE) has 255 counts across the full 8-division vertical span
    /// (<c>2 × VoltageRangeVolts</c> peak-to-peak for a bipolar <c>±VoltageRangeVolts</c> setting), so 1 LSB is
    /// <c>2 / 255 ≈ 0.78%</c> of <c>VoltageRangeVolts</c> (or <c>1 / 255 ≈ 0.39%</c> of a unipolar span). An unconnected
    /// input exhibits ~5–7 LSB of ADC quantization/thermal noise (e.g. ~0.188 Vpp on an 8 V range, ~2.35% of
    /// <c>VoltageRangeVolts</c>). At 5% of <c>VoltageRangeVolts</c> (~6.4 LSB bipolar / ~12.8 LSB unipolar, with a
    /// <see cref="MinActiveSignalAbsoluteVolts"/> floor of 20 mV matching <see cref="AutoSetup.FlatSignalVolts"/>),
    /// baseline ADC noise is rejected as <see cref="ScopeMeasurementStatus.FlatSignal"/> while small real signals
    /// (such as a 200 mVpp alternator ripple on a 1 V AC range, which is 20% of range) are measured normally.
    /// </para>
    /// </summary>
    public const double MinActiveSignalRangeFraction = 0.05;

    /// <summary>
    /// Absolute minimum peak-to-peak voltage swing (20 mV, matching <see cref="AutoSetup.FlatSignalVolts"/>) required
    /// for cyclic/edge measurements when <see cref="ScopeChannelConfig.VoltageRangeVolts"/> is very small or unset.
    /// </summary>
    public const double MinActiveSignalAbsoluteVolts = 0.02;

    private static readonly MeasurementKind[] AllMeasurementKinds =
    [
        MeasurementKind.Frequency,
        MeasurementKind.Peak,
        MeasurementKind.PeakToPeak,
        MeasurementKind.DutyCycle,
        MeasurementKind.RiseTime,
        MeasurementKind.Ripple,
        MeasurementKind.Current,
    ];

    /// <summary>
    /// Computes the measurements defined in ADR-011 §4 for one 1-based channel.
    /// </summary>
    public static ScopeMeasurement[] ComputeMeasurements(
        double[] samples,
        double sampleRateHz,
        ScopeChannelConfig config,
        int channel) =>
        ComputeMeasurements(samples, sampleRateHz, config, channel, isSimulated: false, noTrigger: false, outOfRange: false);

    /// <summary>
    /// Computes the measurements defined in ADR-011 §4 for one 1-based channel, carrying
    /// simulator provenance and optional hardware trigger/range status overrides.
    /// </summary>
    public static ScopeMeasurement[] ComputeMeasurements(
        double[]? samples,
        double sampleRateHz,
        ScopeChannelConfig config,
        int channel,
        bool isSimulated,
        bool noTrigger = false,
        bool outOfRange = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (channel < 1)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel index must be 1-based (>= 1).");

        bool clampConfigured = double.IsFinite(config.CurrentScaleAmpsPerVolt) && config.CurrentScaleAmpsPerVolt > 0.0;

        if (noTrigger)
        {
            return BuildUniformStatus(
                channel,
                isSimulated,
                clampConfigured,
                ScopeMeasurementStatus.NoTrigger,
                "No trigger fired within capture timeout.");
        }

        if (samples is null || samples.Length < 2)
        {
            return BuildUniformStatus(
                channel,
                isSimulated,
                clampConfigured,
                ScopeMeasurementStatus.TooFewSamples,
                "At least 2 samples are required to compute waveform measurements.");
        }

        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            return BuildUniformStatus(
                channel,
                isSimulated,
                clampConfigured,
                ScopeMeasurementStatus.TooFewSamples,
                "Sample rate must be a finite positive frequency in Hz.");
        }

        int n = samples.Length;
        double vMin = double.PositiveInfinity;
        double vMax = double.NegativeInfinity;
        double sum = 0.0;

        for (int i = 0; i < n; i++)
        {
            double v = samples[i];
            if (!double.IsFinite(v))
            {
                return BuildUniformStatus(
                    channel,
                    isSimulated,
                    clampConfigured,
                    ScopeMeasurementStatus.TooFewSamples,
                    "Samples contain NaN or Infinity.");
            }

            if (v < vMin) vMin = v;
            if (v > vMax) vMax = v;
            sum += v;
        }

        if (outOfRange)
        {
            return BuildUniformStatus(
                channel,
                isSimulated,
                clampConfigured,
                ScopeMeasurementStatus.OutOfRange,
                "Signal exceeded the configured vertical range.");
        }

        double vpp = vMax - vMin;
        double mean = sum / n;

        // 1. Peak (Vp) = max(v)
        var peakMeasurement = new ScopeMeasurement
        {
            Kind = MeasurementKind.Peak,
            Channel = channel,
            Value = vMax,
            Unit = UnitVolt,
            Status = ScopeMeasurementStatus.Computed,
            IsSimulated = isSimulated,
        };

        // 2. Peak-to-peak (Vpp) = max(v) - min(v)
        var vppMeasurement = new ScopeMeasurement
        {
            Kind = MeasurementKind.PeakToPeak,
            Channel = channel,
            Value = vpp,
            Unit = UnitVolt,
            Status = ScopeMeasurementStatus.Computed,
            IsSimulated = isSimulated,
        };

        // 3. Ripple = AC RMS = sqrt( sum((v_i - mean)^2) / N )
        double sqDiffSum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = samples[i] - mean;
            sqDiffSum += d * d;
        }
        double acRms = Math.Sqrt(sqDiffSum / n);
        var rippleMeasurement = new ScopeMeasurement
        {
            Kind = MeasurementKind.Ripple,
            Channel = channel,
            Value = acRms,
            Unit = UnitVolt,
            Status = ScopeMeasurementStatus.Computed,
            IsSimulated = isSimulated,
        };

        // 4. Current = max(v) * CurrentScaleAmpsPerVolt (requires clamp scale > 0)
        ScopeMeasurement currentMeasurement = clampConfigured
            ? new ScopeMeasurement
            {
                Kind = MeasurementKind.Current,
                Channel = channel,
                Value = vMax * config.CurrentScaleAmpsPerVolt,
                Unit = UnitAmpere,
                Status = ScopeMeasurementStatus.Computed,
                IsSimulated = isSimulated,
            }
            : new ScopeMeasurement
            {
                Kind = MeasurementKind.Current,
                Channel = channel,
                Value = null,
                Unit = UnitAmpere,
                Status = ScopeMeasurementStatus.NotConfigured,
                Detail = "Current clamp scale (A/V) is not configured on this channel.",
                IsSimulated = isSimulated,
            };

        // 5 & 6. Frequency and DutyCycle from rising/falling 50% crossings of vMid = (min + max) / 2
        double minActiveVpp = ComputeMinimumActiveVpp(config.VoltageRangeVolts);
        var (frequencyMeasurement, dutyCycleMeasurement) = ComputeFrequencyAndDutyCycle(
            samples,
            sampleRateHz,
            vMin,
            vMax,
            minActiveVpp,
            channel,
            isSimulated);

        // 7. RiseTime = t(90% of Vpp) - t(10% of Vpp) on first clean rising edge
        var riseTimeMeasurement = ComputeRiseTime(
            samples,
            sampleRateHz,
            vMin,
            vMax,
            minActiveVpp,
            channel,
            isSimulated);

        return
        [
            frequencyMeasurement,
            peakMeasurement,
            vppMeasurement,
            dutyCycleMeasurement,
            riseTimeMeasurement,
            rippleMeasurement,
            currentMeasurement,
        ];
    }

    /// <summary>
    /// Computes the minimum peak-to-peak swing (V) required before a trace is treated as an active cyclic/edge
    /// signal rather than baseline ADC noise (<see cref="ScopeMeasurementStatus.FlatSignal"/>).
    /// Equals <c>max(MinActiveSignalAbsoluteVolts, VoltageRangeVolts × MinActiveSignalRangeFraction)</c>.
    /// </summary>
    public static double ComputeMinimumActiveVpp(double voltageRangeVolts)
    {
        double rangeThreshold = double.IsFinite(voltageRangeVolts) && voltageRangeVolts > 0.0
            ? voltageRangeVolts * MinActiveSignalRangeFraction
            : 0.0;
        return Math.Max(MinActiveSignalAbsoluteVolts, rangeThreshold);
    }

    /// <summary>
    /// Compares computed measurements against reference expectations per ADR-011 §5.
    /// </summary>
    public static ScopeComparisonResult CompareMeasurements(
        IReadOnlyList<ScopeMeasurement> measurements,
        ReferenceExpectations? expectations,
        string? referenceWaveformId = null) =>
        WaveformComparer.CompareMeasurements(measurements, expectations, referenceWaveformId);

    private static (ScopeMeasurement Frequency, ScopeMeasurement DutyCycle) ComputeFrequencyAndDutyCycle(
        double[] samples,
        double sampleRateHz,
        double vMin,
        double vMax,
        double minActiveVpp,
        int channel,
        bool isSimulated)
    {
        double vpp = vMax - vMin;

        if (vpp < minActiveVpp)
        {
            string detail = vpp <= 1e-9
                ? "Signal amplitude is zero (flat signal)."
                : FormattableString.Invariant($"Signal swing ({vpp:0.####} Vpp) is below the active-signal noise floor ({minActiveVpp:0.####} Vpp).");
            return (
                BuildFlat(MeasurementKind.Frequency, UnitHz, channel, isSimulated, detail),
                BuildFlat(MeasurementKind.DutyCycle, UnitPercent, channel, isSimulated, detail));
        }

        double vMid = 0.5 * (vMin + vMax);
        double hysteresis = 0.05 * vpp;
        double lowArmLevel = vMid - hysteresis;
        double highArmLevel = vMid + hysteresis;

        var risingCrossings = new List<double>();
        var fallingCrossings = new List<double>();

        // State machine with 5% Vpp hysteresis to prevent false noise chatter at vMid
        // while still linearly interpolating the exact vMid crossing time.
        bool armedForRising = samples[0] <= vMid;
        bool armedForFalling = false;

        int n = samples.Length;
        for (int i = 0; i < n - 1; i++)
        {
            double v0 = samples[i];
            double v1 = samples[i + 1];

            if (v0 <= lowArmLevel)
                armedForRising = true;
            if (v0 >= highArmLevel)
                armedForFalling = true;

            if (armedForRising && v0 <= vMid && v1 > vMid)
            {
                double frac = (vMid - v0) / (v1 - v0);
                double tRise = (i + frac) / sampleRateHz;
                risingCrossings.Add(tRise);
                armedForRising = false;
                if (v1 >= highArmLevel)
                    armedForFalling = true;
            }
            else if (armedForFalling && risingCrossings.Count > 0 && v0 >= vMid && v1 < vMid)
            {
                double frac = (v0 - vMid) / (v0 - v1);
                double tFall = (i + frac) / sampleRateHz;
                if (fallingCrossings.Count < risingCrossings.Count)
                    fallingCrossings.Add(tFall);
                armedForFalling = false;
                if (v1 <= lowArmLevel)
                    armedForRising = true;
            }
        }

        if (risingCrossings.Count < 2)
        {
            return (
                BuildFlat(MeasurementKind.Frequency, UnitHz, channel, isSimulated, "Fewer than 2 rising crossings of vMid."),
                BuildFlat(MeasurementKind.DutyCycle, UnitPercent, channel, isSimulated, "No complete cycle found."));
        }

        int k = risingCrossings.Count;
        double sumPeriods = 0.0;
        double sumDutyPercent = 0.0;
        int validDutyCycles = 0;

        for (int c = 0; c < k - 1; c++)
        {
            double tStart = risingCrossings[c];
            double tEnd = risingCrossings[c + 1];
            double period = tEnd - tStart;
            if (period <= 0.0)
                continue;

            sumPeriods += period;

            if (c < fallingCrossings.Count)
            {
                double tFall = fallingCrossings[c];
                if (tFall > tStart && tFall < tEnd)
                {
                    double highTime = tFall - tStart;
                    sumDutyPercent += 100.0 * (highTime / period);
                    validDutyCycles++;
                }
            }
        }

        if (sumPeriods <= 0.0)
        {
            return (
                BuildFlat(MeasurementKind.Frequency, UnitHz, channel, isSimulated, "Measured period was zero."),
                BuildFlat(MeasurementKind.DutyCycle, UnitPercent, channel, isSimulated, "Measured period was zero."));
        }

        double frequencyHz = (k - 1) / sumPeriods;
        var freqResult = new ScopeMeasurement
        {
            Kind = MeasurementKind.Frequency,
            Channel = channel,
            Value = frequencyHz,
            Unit = UnitHz,
            Status = ScopeMeasurementStatus.Computed,
            IsSimulated = isSimulated,
        };

        ScopeMeasurement dutyResult = validDutyCycles > 0
            ? new ScopeMeasurement
            {
                Kind = MeasurementKind.DutyCycle,
                Channel = channel,
                Value = sumDutyPercent / validDutyCycles,
                Unit = UnitPercent,
                Status = ScopeMeasurementStatus.Computed,
                IsSimulated = isSimulated,
            }
            : BuildFlat(MeasurementKind.DutyCycle, UnitPercent, channel, isSimulated, "No complete rising-to-falling cycle found.");

        return (freqResult, dutyResult);
    }

    private static ScopeMeasurement ComputeRiseTime(
        double[] samples,
        double sampleRateHz,
        double vMin,
        double vMax,
        double minActiveVpp,
        int channel,
        bool isSimulated)
    {
        double vpp = vMax - vMin;

        if (vpp < minActiveVpp)
        {
            string detail = vpp <= 1e-9
                ? "Signal amplitude is zero (flat signal)."
                : FormattableString.Invariant($"Signal swing ({vpp:0.####} Vpp) is below the active-signal noise floor ({minActiveVpp:0.####} Vpp).");
            return BuildFlat(MeasurementKind.RiseTime, UnitSecond, channel, isSimulated, detail);
        }

        double v10 = vMin + 0.10 * vpp;
        double v90 = vMin + 0.90 * vpp;

        double? t10 = null;
        int n = samples.Length;

        for (int i = 0; i < n - 1; i++)
        {
            double v0 = samples[i];
            double v1 = samples[i + 1];

            // Record (or refresh if the signal fell back below 10% before reaching 90%) the 10% crossing
            if (v0 <= v10 && v1 > v10)
            {
                double frac10 = (v10 - v0) / (v1 - v0);
                t10 = (i + frac10) / sampleRateHz;
            }
            else if (v1 <= v10)
            {
                t10 = null;
            }

            // Check 90% crossing (can occur in the same sample interval for a 1-sample step edge)
            if (t10.HasValue && v0 < v90 && v1 >= v90)
            {
                double frac90 = (v90 - v0) / (v1 - v0);
                double t90 = (i + frac90) / sampleRateHz;
                double riseTime = t90 - t10.Value;
                if (riseTime >= 0.0)
                {
                    return new ScopeMeasurement
                    {
                        Kind = MeasurementKind.RiseTime,
                        Channel = channel,
                        Value = riseTime,
                        Unit = UnitSecond,
                        Status = ScopeMeasurementStatus.Computed,
                        IsSimulated = isSimulated,
                    };
                }
            }
        }

        return BuildFlat(MeasurementKind.RiseTime, UnitSecond, channel, isSimulated, "No rising edge reached 90% of Vpp.");
    }

    private static ScopeMeasurement BuildFlat(
        MeasurementKind kind,
        string unit,
        int channel,
        bool isSimulated,
        string detail) =>
        new()
        {
            Kind = kind,
            Channel = channel,
            Value = null,
            Unit = unit,
            Status = ScopeMeasurementStatus.FlatSignal,
            Detail = detail,
            IsSimulated = isSimulated,
        };

    private static ScopeMeasurement[] BuildUniformStatus(
        int channel,
        bool isSimulated,
        bool clampConfigured,
        ScopeMeasurementStatus status,
        string detail)
    {
        var results = new ScopeMeasurement[AllMeasurementKinds.Length];
        for (int i = 0; i < AllMeasurementKinds.Length; i++)
        {
            var kind = AllMeasurementKinds[i];
            bool isUnconfiguredCurrent = kind == MeasurementKind.Current && !clampConfigured;
            results[i] = new ScopeMeasurement
            {
                Kind = kind,
                Channel = channel,
                Value = null,
                Unit = GetCanonicalUnit(kind),
                Status = isUnconfiguredCurrent ? ScopeMeasurementStatus.NotConfigured : status,
                Detail = isUnconfiguredCurrent
                    ? "Current clamp scale (A/V) is not configured on this channel."
                    : detail,
                IsSimulated = isSimulated,
            };
        }
        return results;
    }

    public static string GetCanonicalUnit(MeasurementKind kind) => kind switch
    {
        MeasurementKind.Frequency => UnitHz,
        MeasurementKind.Peak => UnitVolt,
        MeasurementKind.PeakToPeak => UnitVolt,
        MeasurementKind.DutyCycle => UnitPercent,
        MeasurementKind.RiseTime => UnitSecond,
        MeasurementKind.Ripple => UnitVolt,
        MeasurementKind.Current => UnitAmpere,
        _ => "",
    };
}
