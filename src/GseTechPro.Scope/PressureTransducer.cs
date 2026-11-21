using System.Globalization;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Scope;

/// <summary>Min / max / mean pressure of one channel, and how many samples were outside the transducer's output span.</summary>
public sealed record PressureSummary(
    double Min,
    double Max,
    double Mean,
    string Unit,
    int SampleCount,
    int BelowSpanCount,
    int AboveSpanCount)
{
    public string Text
    {
        get
        {
            var c = CultureInfo.InvariantCulture;
            string text = string.Create(c, $"min {PressureTransducer.Format(Min)} · max {PressureTransducer.Format(Max)} · mean {PressureTransducer.Format(Mean)} {Unit}");
            if (BelowSpanCount > 0)
                text += string.Create(c, $" · {BelowSpanCount:N0} samples below the zero-pressure output entered: check the transducer supply and wiring");
            if (AboveSpanCount > 0)
                text += string.Create(c, $" · {AboveSpanCount:N0} samples above the full-scale output entered: the pressure may be beyond the transducer's range");
            return text;
        }
    }
}

/// <summary>
/// Pressure transducer channel scaling (Phase 10D.4). Volts become pressure along the straight line
/// through the two points the technician entered from the transducer's published spec. Nothing here
/// has a default and nothing judges the pressure: the readout is a measurement, never a verdict.
/// </summary>
public static class PressureTransducer
{
    public static IReadOnlyList<string> Units { get; } = ["psi", "bar", "kPa", "MPa"];

    /// <summary>
    /// A sample counts as outside the output span when it is more than this fraction of the span
    /// beyond either end (allows for noise; a signal-processing choice, not a transducer spec).
    /// </summary>
    public const double SpanTolerance = 0.02;

    /// <summary>Why the scaling cannot be used, or null when it can.</summary>
    public static string? Validate(ScopePressureScaling? s)
    {
        if (s is null)
            return "No pressure scaling entered.";
        if (!Units.Contains(s.Unit))
            return "Pick a pressure unit (psi, bar, kPa or MPa).";
        if (!double.IsFinite(s.ZeroOutputVolts) || !double.IsFinite(s.FullScaleOutputVolts) ||
            !double.IsFinite(s.ZeroPressure) || !double.IsFinite(s.FullScalePressure))
            return "Enter all four numbers from the transducer spec.";
        if (Math.Abs(s.FullScaleOutputVolts - s.ZeroOutputVolts) < 0.01)
            return "The two output voltages must differ (e.g. 0.5 V and 4.5 V).";
        if (s.FullScalePressure == s.ZeroPressure)
            return "The two pressures must differ (e.g. 0 and 5000).";
        if (string.IsNullOrWhiteSpace(s.Source))
            return "Enter the transducer make/model or datasheet the numbers come from.";
        return null;
    }

    /// <summary>Pressure for <paramref name="volts"/> (no range check).</summary>
    public static double ToPressure(ScopePressureScaling s, double volts) =>
        s.ZeroPressure + (volts - s.ZeroOutputVolts) * (s.FullScalePressure - s.ZeroPressure) / (s.FullScaleOutputVolts - s.ZeroOutputVolts);

    /// <summary>Min / max / mean pressure of a channel's samples, with the out-of-span counts.</summary>
    public static PressureSummary Summarize(ScopePressureScaling s, IReadOnlyList<double> volts)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(volts);
        if (Validate(s) is string error)
            throw new ArgumentException(error, nameof(s));
        if (volts.Count == 0)
            return new PressureSummary(double.NaN, double.NaN, double.NaN, s.Unit, 0, 0, 0);

        double lowV = Math.Min(s.ZeroOutputVolts, s.FullScaleOutputVolts);
        double highV = Math.Max(s.ZeroOutputVolts, s.FullScaleOutputVolts);
        double tol = (highV - lowV) * SpanTolerance;
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity, sum = 0;
        int below = 0, above = 0;
        foreach (double v in volts)
        {
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
            sum += v;
            if (v < lowV - tol) below++;
            else if (v > highV + tol) above++;
        }

        double p1 = ToPressure(s, minV), p2 = ToPressure(s, maxV);
        return new PressureSummary(
            Math.Min(p1, p2), Math.Max(p1, p2), ToPressure(s, sum / volts.Count), s.Unit, volts.Count,
            // with a falling transducer (output drops as pressure rises) "below" and "above" swap
            s.FullScaleOutputVolts >= s.ZeroOutputVolts ? below : above,
            s.FullScaleOutputVolts >= s.ZeroOutputVolts ? above : below);
    }

    /// <summary>One-line description of the scaling ("0.5–4.5 V = 0–5000 psi (Acme PT-5000)").</summary>
    public static string Describe(ScopePressureScaling s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s.ZeroOutputVolts:0.###}–{s.FullScaleOutputVolts:0.###} V = {Format(s.ZeroPressure)}–{Format(s.FullScalePressure)} {s.Unit} ({s.Source.Trim()})");

    /// <summary>Pressure with sensible digits for its size.</summary>
    public static string Format(double p)
    {
        double a = Math.Abs(p);
        string f = a >= 1000 ? "0" : a >= 100 ? "0.#" : a >= 10 ? "0.##" : "0.###";
        return p.ToString(f, CultureInfo.InvariantCulture);
    }
}
