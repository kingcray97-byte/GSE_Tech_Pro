using GseTechPro.Core.Contracts;

namespace GseTechPro.Scope;

/// <summary>
/// Phase 10D.1 pass/fail mask (PicoScope 7 "Masks" workflow, our own implementation): an upper and lower envelope
/// built from a known-good capture of one channel, widened by a voltage tolerance and a time tolerance the
/// technician enters. A later capture passes when every sample stays inside the envelope. The mask is aligned by
/// sample index, so it only applies to captures with the same sample rate and length (use a trigger so the
/// waveform starts at the same point).
/// </summary>
public sealed record WaveformMask(
    int Channel,
    double SampleRateHz,
    double[] Upper,
    double[] Lower,
    double VoltageToleranceVolts,
    double TimeToleranceSeconds,
    bool FromSimulatedCapture)
{
    public int SampleCount => Upper.Length;

    /// <summary>
    /// Builds a mask from <paramref name="samples"/>: at each sample, the max (upper) and min (lower) over
    /// ± <paramref name="timeToleranceSeconds"/>, then ± <paramref name="voltageToleranceVolts"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Tolerances or samples are not usable.</exception>
    public static WaveformMask Create(
        double[] samples,
        double sampleRateHz,
        int channel,
        double voltageToleranceVolts,
        double timeToleranceSeconds,
        bool fromSimulatedCapture = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) throw new ArgumentException("The capture has no samples.", nameof(samples));
        if (!(sampleRateHz > 0) || !double.IsFinite(sampleRateHz))
            throw new ArgumentException("Sample rate must be positive.", nameof(sampleRateHz));
        if (!(voltageToleranceVolts > 0) || !double.IsFinite(voltageToleranceVolts))
            throw new ArgumentException("Enter a voltage tolerance above 0 V.", nameof(voltageToleranceVolts));
        if (!(timeToleranceSeconds >= 0) || !double.IsFinite(timeToleranceSeconds))
            throw new ArgumentException("Time tolerance must be 0 or more.", nameof(timeToleranceSeconds));

        int radius = (int)Math.Min(samples.Length, Math.Round(timeToleranceSeconds * sampleRateHz));
        var upper = SlidingExtreme(samples, radius, max: true);
        var lower = SlidingExtreme(samples, radius, max: false);
        for (int i = 0; i < samples.Length; i++)
        {
            upper[i] += voltageToleranceVolts;
            lower[i] -= voltageToleranceVolts;
        }
        return new WaveformMask(channel, sampleRateHz, upper, lower, voltageToleranceVolts, timeToleranceSeconds, fromSimulatedCapture);
    }

    /// <summary>Tests <paramref name="capture"/> against the mask. Mismatched captures give an error, never a verdict.</summary>
    public MaskTestResult Test(ScopeCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        string ch = MathChannelDefinition.ChannelName(Channel);
        if (!capture.Channels.TryGetValue(Channel, out var samples))
            return MaskTestResult.Error($"Channel {ch} is not in this capture.");
        if (Math.Abs(capture.SampleRateHz - SampleRateHz) > SampleRateHz * 1e-3 || samples.Length != SampleCount)
            return MaskTestResult.Error(FormattableString.Invariant(
                $"The mask was made at {SampleRateHz:G4} S/s with {SampleCount:N0} samples; this capture is {capture.SampleRateHz:G4} S/s with {samples.Length:N0}. Set the same timebase and sample rate, or make a new mask."));

        int violations = 0, first = -1;
        for (int i = 0; i < samples.Length; i++)
        {
            double v = samples[i];
            if (!double.IsFinite(v) || v > Upper[i] || v < Lower[i])
            {
                violations++;
                if (first < 0) first = i;
            }
        }

        if (violations == 0)
            return new MaskTestResult(true, 0, null, $"Mask on {ch}: PASS (every sample inside the mask).");
        double t = first / SampleRateHz;
        return new MaskTestResult(false, violations, t, FormattableString.Invariant(
            $"Mask on {ch}: FAIL, {violations:N0} sample(s) outside, first at {t * 1e3:0.###} ms."));
    }

    /// <summary>Max (or min) over [i − radius, i + radius] for every i, in O(n) with a monotonic deque.</summary>
    internal static double[] SlidingExtreme(double[] x, int radius, bool max)
    {
        int n = x.Length;
        var result = new double[n];
        if (radius <= 0)
        {
            Array.Copy(x, result, n);
            return result;
        }

        var deque = new int[n];
        int head = 0, tail = 0; // deque holds indices with monotonic values
        int next = 0;           // next index to push
        for (int i = 0; i < n; i++)
        {
            int hi = Math.Min(n - 1, i + radius);
            while (next <= hi)
            {
                double v = x[next];
                while (tail > head && (max ? x[deque[tail - 1]] <= v : x[deque[tail - 1]] >= v)) tail--;
                deque[tail++] = next++;
            }
            while (deque[head] < i - radius) head++;
            result[i] = x[deque[head]];
        }
        return result;
    }
}

/// <summary>Result of a mask test. <see cref="Passed"/> is null when the capture could not be tested.</summary>
public sealed record MaskTestResult(bool? Passed, int Violations, double? FirstViolationSeconds, string Message)
{
    public static MaskTestResult Error(string message) => new(null, 0, null, message);
}
