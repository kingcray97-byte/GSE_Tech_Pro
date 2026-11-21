using GseTechPro.Core.Contracts;

namespace GseTechPro.Scope;

/// <summary>Math channel operations (Phase 10D.1, PicoScope 7 "Math channels" workflow, our own implementation).</summary>
public enum MathOperation
{
    /// <summary>A + B (V).</summary>
    Sum,
    /// <summary>A − B (V), e.g. a voltage drop measured with two probes.</summary>
    Difference,
    /// <summary>A × B (V², or W when B is a current clamp channel scaled in volts-per-amp upstream).</summary>
    Product,
    /// <summary>−A (V).</summary>
    Invert,
    /// <summary>A through a first-order low-pass filter at the cutoff the technician enters (Hz).</summary>
    LowPass,
    /// <summary>A's signal frequency × 60 ÷ pulses per revolution (rpm). Pulses per revolution is entered by the
    /// technician from the sensor/tone wheel; it is never assumed.</summary>
    RpmFromFrequency,
}

/// <summary>What the math channel computes. <see cref="Parameter"/> is the cutoff (Hz) for LowPass or the
/// pulses per revolution for RpmFromFrequency; it is unused by the other operations.</summary>
public sealed record MathChannelDefinition(MathOperation Operation, int SourceA = 1, int SourceB = 2, double Parameter = 0)
{
    public bool UsesSourceB => Operation is MathOperation.Sum or MathOperation.Difference or MathOperation.Product;

    public bool UsesParameter => Operation is MathOperation.LowPass or MathOperation.RpmFromFrequency;

    public string Unit => Operation switch
    {
        MathOperation.Product => "V²",
        MathOperation.RpmFromFrequency => "rpm",
        _ => "V",
    };

    public string Label => Operation switch
    {
        MathOperation.Sum => $"{ChannelName(SourceA)}+{ChannelName(SourceB)}",
        MathOperation.Difference => $"{ChannelName(SourceA)}−{ChannelName(SourceB)}",
        MathOperation.Product => $"{ChannelName(SourceA)}×{ChannelName(SourceB)}",
        MathOperation.Invert => $"−{ChannelName(SourceA)}",
        MathOperation.LowPass => $"LPF({ChannelName(SourceA)}, {Parameter:0.###} Hz)",
        MathOperation.RpmFromFrequency => $"RPM({ChannelName(SourceA)}, {Parameter:0.###} ppr)",
        _ => Operation.ToString(),
    };

    /// <summary>Channel 1–4 as A–D, matching the scope screen.</summary>
    public static string ChannelName(int channel) => channel is >= 1 and <= 26 ? ((char)('A' + channel - 1)).ToString() : $"CH{channel}";
}

/// <summary>Result of a math channel: one value per capture sample, or an error saying why nothing was computed.</summary>
public sealed record MathChannelResult(double[] Values, string Label, string Unit, string? Error)
{
    public bool IsOk => Error is null;

    public static MathChannelResult Fail(MathChannelDefinition d, string error) => new(Array.Empty<double>(), d.Label, d.Unit, error);
}

/// <summary>Pure math-channel computation over a capture. Never mutates the capture.</summary>
public static class MathChannels
{
    /// <summary>Hysteresis for edge detection, as a fraction of the signal's peak-to-peak.</summary>
    public const double EdgeHysteresisFraction = 0.1;

    public static MathChannelResult Compute(ScopeCapture capture, MathChannelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(definition);

        if (!capture.Channels.TryGetValue(definition.SourceA, out var a) || a.Length == 0)
            return MathChannelResult.Fail(definition, $"Channel {MathChannelDefinition.ChannelName(definition.SourceA)} is not in this capture.");

        double[]? b = null;
        if (definition.UsesSourceB)
        {
            if (!capture.Channels.TryGetValue(definition.SourceB, out b) || b.Length == 0)
                return MathChannelResult.Fail(definition, $"Channel {MathChannelDefinition.ChannelName(definition.SourceB)} is not in this capture.");
        }

        int n = b is null ? a.Length : Math.Min(a.Length, b.Length);
        double[] values;
        switch (definition.Operation)
        {
            case MathOperation.Sum:
                values = new double[n];
                for (int i = 0; i < n; i++) values[i] = a[i] + b![i];
                break;
            case MathOperation.Difference:
                values = new double[n];
                for (int i = 0; i < n; i++) values[i] = a[i] - b![i];
                break;
            case MathOperation.Product:
                values = new double[n];
                for (int i = 0; i < n; i++) values[i] = a[i] * b![i];
                break;
            case MathOperation.Invert:
                values = new double[n];
                for (int i = 0; i < n; i++) values[i] = -a[i];
                break;
            case MathOperation.LowPass:
            {
                double fc = definition.Parameter;
                double nyquist = capture.SampleRateHz / 2.0;
                if (!double.IsFinite(fc) || fc <= 0 || fc >= nyquist)
                    return MathChannelResult.Fail(definition, $"Enter a cutoff above 0 and below {nyquist:0.###} Hz (half the sample rate).");
                values = LowPass(a, capture.SampleRateHz, fc);
                break;
            }
            case MathOperation.RpmFromFrequency:
            {
                double ppr = definition.Parameter;
                if (!double.IsFinite(ppr) || ppr <= 0)
                    return MathChannelResult.Fail(definition, "Enter the pulses per revolution for this sensor (from the sensor or tone-wheel spec).");
                var rpm = RpmFromFrequency(a, capture.SampleRateHz, ppr);
                if (rpm is null)
                    return MathChannelResult.Fail(definition, "No repeating signal on the source channel (fewer than two rising edges).");
                values = rpm;
                break;
            }
            default:
                return MathChannelResult.Fail(definition, $"Unknown operation {definition.Operation}.");
        }

        return new MathChannelResult(values, definition.Label, definition.Unit, null);
    }

    /// <summary>First-order (RC) low-pass: y[i] = y[i-1] + α (x[i] − y[i-1]), α = dt / (RC + dt), RC = 1 / (2π fc).</summary>
    public static double[] LowPass(double[] x, double sampleRateHz, double cutoffHz)
    {
        var y = new double[x.Length];
        if (x.Length == 0) return y;
        double dt = 1.0 / sampleRateHz;
        double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        double alpha = dt / (rc + dt);
        double prev = double.IsFinite(x[0]) ? x[0] : 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double xi = double.IsFinite(x[i]) ? x[i] : prev;
            prev += alpha * (xi - prev);
            y[i] = prev;
        }
        return y;
    }

    /// <summary>
    /// Rising edges (with hysteresis around the mid level) give one period per edge pair; each sample holds the rpm
    /// of the period it falls in (samples before the first full period take the first value, after the last take
    /// the last). Returns null when there are fewer than two rising edges.
    /// </summary>
    public static double[]? RpmFromFrequency(double[] x, double sampleRateHz, double pulsesPerRevolution)
    {
        var edges = RisingEdges(x);
        if (edges.Count < 2) return null;

        var rpm = new double[x.Length];
        double first = PeriodRpm(edges[0], edges[1]);
        for (int i = 0; i < edges[1] && i < x.Length; i++) rpm[i] = first;
        double last = first;
        for (int k = 1; k < edges.Count; k++)
        {
            last = PeriodRpm(edges[k - 1], edges[k]);
            int end = k + 1 < edges.Count ? edges[k + 1] : x.Length;
            for (int i = edges[k]; i < end; i++) rpm[i] = last;
        }
        return rpm;

        double PeriodRpm(int from, int to) => sampleRateHz / (to - from) * 60.0 / pulsesPerRevolution;
    }

    /// <summary>Sample indices where the signal crosses up through mid + hysteresis after having been below mid − hysteresis.</summary>
    public static List<int> RisingEdges(double[] x)
    {
        var edges = new List<int>();
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in x)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (!double.IsFinite(min) || max - min <= 1e-12) return edges;

        double mid = (max + min) / 2.0;
        double hyst = (max - min) * EdgeHysteresisFraction;
        bool armed = false;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            if (!double.IsFinite(v)) continue;
            if (v < mid - hyst) armed = true;
            else if (armed && v > mid + hyst)
            {
                edges.Add(i);
                armed = false;
            }
        }
        return edges;
    }
}
