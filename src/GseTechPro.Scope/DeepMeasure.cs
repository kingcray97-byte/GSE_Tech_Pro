using GseTechPro.Core.Contracts;

namespace GseTechPro.Scope;

/// <summary>Per-cycle measurements DeepMeasure reports (Phase 10D.1, PicoScope 7 "DeepMeasure" workflow, our own implementation).</summary>
public enum CycleMeasureKind
{
    Period,
    Frequency,
    DutyCycle,
    Maximum,
    Minimum,
    PeakToPeak,
    RiseTime,
    FallTime,
}

/// <summary>
/// One cycle of a repeating signal, from one rising mid-level crossing to the next. Times are seconds from the start
/// of the capture. A value is null when it could not be measured on this cycle (for example no falling edge inside it).
/// </summary>
public sealed record CycleMeasurement(
    int Number,
    double StartSeconds,
    double PeriodSeconds,
    double FrequencyHz,
    double? DutyCyclePercent,
    double MaximumVolts,
    double MinimumVolts,
    double PeakToPeakVolts,
    double? RiseTimeSeconds,
    double? FallTimeSeconds)
{
    public double? Get(CycleMeasureKind kind) => kind switch
    {
        CycleMeasureKind.Period => PeriodSeconds,
        CycleMeasureKind.Frequency => FrequencyHz,
        CycleMeasureKind.DutyCycle => DutyCyclePercent,
        CycleMeasureKind.Maximum => MaximumVolts,
        CycleMeasureKind.Minimum => MinimumVolts,
        CycleMeasureKind.PeakToPeak => PeakToPeakVolts,
        CycleMeasureKind.RiseTime => RiseTimeSeconds,
        CycleMeasureKind.FallTime => FallTimeSeconds,
        _ => null,
    };
}

/// <summary>Statistics of one measurement over every cycle where it was measured, with the cycles holding the extremes.</summary>
public sealed record CycleStatistics(
    CycleMeasureKind Kind,
    int Count,
    double Minimum,
    double Maximum,
    double Mean,
    double StandardDeviation,
    int MinimumCycle,
    int MaximumCycle);

/// <summary>DeepMeasure result for one channel: every cycle plus statistics, or an error saying why nothing was measured.</summary>
public sealed record DeepMeasureResult(
    int Channel,
    IReadOnlyList<CycleMeasurement> Cycles,
    IReadOnlyList<CycleStatistics> Statistics,
    bool Truncated,
    bool IsSimulated,
    string? Error)
{
    public bool IsOk => Error is null;

    public CycleStatistics? StatisticsFor(CycleMeasureKind kind) => Statistics.FirstOrDefault(s => s.Kind == kind);

    public static DeepMeasureResult Fail(int channel, bool isSimulated, string error) =>
        new(channel, Array.Empty<CycleMeasurement>(), Array.Empty<CycleStatistics>(), false, isSimulated, error);
}

/// <summary>
/// Pure per-cycle analysis of one capture channel. Cycles are split at rising crossings of the capture's mid level
/// ((min + max) / 2) with the same 10 % hysteresis as the math channel, and edge times are interpolated between samples.
/// Rise/fall times are 10 %–90 % of the cycle's own peak-to-peak. It only measures; it never judges a cycle good or bad.
/// </summary>
public static class DeepMeasure
{
    /// <summary>Most cycles kept in one result, so a long fast capture cannot make a table the tablet cannot show.</summary>
    public const int DefaultMaxCycles = 10_000;

    public static DeepMeasureResult Analyze(ScopeCapture capture, int channel, int maxCycles = DefaultMaxCycles)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.Channels.TryGetValue(channel, out var samples) || samples.Length == 0)
            return DeepMeasureResult.Fail(channel, capture.IsSimulated,
                $"Channel {MathChannelDefinition.ChannelName(channel)} is not in this capture.");
        return Analyze(samples, capture.SampleRateHz, channel, capture.IsSimulated, maxCycles);
    }

    public static DeepMeasureResult Analyze(double[] x, double sampleRateHz, int channel, bool isSimulated = false,
        int maxCycles = DefaultMaxCycles)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0)
            return DeepMeasureResult.Fail(channel, isSimulated, "The capture has no valid sample rate.");
        if (maxCycles < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCycles), maxCycles, "Must be at least 1.");

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in x)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (!double.IsFinite(min) || max - min <= 1e-12)
            return DeepMeasureResult.Fail(channel, isSimulated, "The signal is flat: there are no cycles to measure.");

        double mid = (max + min) / 2.0;
        double hyst = (max - min) * MathChannels.EdgeHysteresisFraction;

        // Interpolated rising and falling mid-level crossings, each confirmed by passing the hysteresis band.
        var rising = new List<double>();
        var falling = new List<double>();
        int state = 0; // 0 unknown, -1 low, +1 high
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            if (!double.IsFinite(v)) continue;
            if (v < mid - hyst)
            {
                if (state == 1) falling.Add(Crossing(x, i, mid, rising: false));
                state = -1;
            }
            else if (v > mid + hyst)
            {
                if (state == -1) rising.Add(Crossing(x, i, mid, rising: true));
                state = 1;
            }
        }

        if (rising.Count < 2)
            return DeepMeasureResult.Fail(channel, isSimulated,
                "No repeating signal on this channel (fewer than two rising edges). Use a longer timebase.");

        double dt = 1.0 / sampleRateHz;
        var cycles = new List<CycleMeasurement>();
        bool truncated = false;
        int f = 0;
        for (int k = 0; k + 1 < rising.Count; k++)
        {
            if (cycles.Count >= maxCycles)
            {
                truncated = true;
                break;
            }

            double start = rising[k], end = rising[k + 1];
            double period = (end - start) * dt;

            while (f < falling.Count && falling[f] <= start) f++;
            double? fallAt = f < falling.Count && falling[f] < end ? falling[f] : null;

            int i0 = Math.Max(0, (int)Math.Floor(start));
            int i1 = Math.Min(x.Length - 1, (int)Math.Floor(end)); // the next cycle's first sample is not in this one
            double cMin = double.PositiveInfinity, cMax = double.NegativeInfinity;
            for (int i = i0; i <= i1; i++)
            {
                double v = x[i];
                if (!double.IsFinite(v)) continue;
                if (v < cMin) cMin = v;
                if (v > cMax) cMax = v;
            }
            double pp = cMax - cMin;
            double lo = cMin + 0.1 * pp, hi = cMin + 0.9 * pp;

            // Rise: the edge at the start of this cycle, searched back to the previous falling edge and forward to this one.
            double riseFrom = FallingBefore(falling, start) ?? 0;
            double? rise = TransitionTime(x, start, riseFrom, fallAt ?? end, lo, hi, rising: true);
            double? fall = fallAt is double fa ? TransitionTime(x, fa, start, end, lo, hi, rising: false) : null;

            cycles.Add(new CycleMeasurement(
                Number: cycles.Count + 1,
                StartSeconds: start * dt,
                PeriodSeconds: period,
                FrequencyHz: 1.0 / period,
                DutyCyclePercent: fallAt is double fb ? (fb - start) / (end - start) * 100.0 : null,
                MaximumVolts: cMax,
                MinimumVolts: cMin,
                PeakToPeakVolts: pp,
                RiseTimeSeconds: rise * dt,
                FallTimeSeconds: fall * dt));
        }

        var stats = Enum.GetValues<CycleMeasureKind>()
            .Select(kind => Statistics(cycles, kind))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToArray();
        return new DeepMeasureResult(channel, cycles, stats, truncated, isSimulated, null);
    }

    public static CycleStatistics? Statistics(IReadOnlyList<CycleMeasurement> cycles, CycleMeasureKind kind)
    {
        // Welford's running mean/variance: stable when every cycle is nearly the same.
        int n = 0, minCycle = 0, maxCycle = 0;
        double mean = 0, m2 = 0, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var c in cycles)
        {
            if (c.Get(kind) is not double v || !double.IsFinite(v)) continue;
            n++;
            double d = v - mean;
            mean += d / n;
            m2 += d * (v - mean);
            if (v < lo) { lo = v; minCycle = c.Number; }
            if (v > hi) { hi = v; maxCycle = c.Number; }
        }
        if (n == 0) return null;
        double variance = n > 1 ? Math.Max(0, m2 / (n - 1)) : 0;
        return new CycleStatistics(kind, n, lo, hi, mean, Math.Sqrt(variance), minCycle, maxCycle);
    }

    /// <summary>Fractional sample index where the signal crosses <paramref name="level"/>, walking back from index i.</summary>
    private static double Crossing(double[] x, int i, double level, bool rising)
    {
        int k = i;
        while (k > 0 && double.IsFinite(x[k - 1]) && (rising ? x[k - 1] >= level : x[k - 1] <= level)) k--;
        if (k == 0 || !double.IsFinite(x[k - 1])) return k;
        double a = x[k - 1], b = x[k];
        return Math.Abs(b - a) < 1e-15 ? k : (k - 1) + (level - a) / (b - a);
    }

    private static double? FallingBefore(List<double> falling, double t)
    {
        double? last = null;
        foreach (var f in falling)
        {
            if (f >= t) break;
            last = f;
        }
        return last;
    }

    /// <summary>
    /// 10 %→90 % (rising) or 90 %→10 % (falling) time in samples around the crossing at <paramref name="at"/>, staying
    /// inside [from, to]. Null when either level is not reached inside that window.
    /// </summary>
    private static double? TransitionTime(double[] x, double at, double from, double to, double lo, double hi, bool rising)
    {
        double startLevel = rising ? lo : hi, endLevel = rising ? hi : lo;
        int first = Math.Max(1, (int)Math.Ceiling(from));
        int last = Math.Min(x.Length - 1, (int)Math.Floor(to));
        int c = Math.Clamp((int)Math.Floor(at), first - 1, last);

        // Back from the crossing to the last sample still on the start side.
        double? tStart = null;
        for (int i = Math.Min(c + 1, last); i >= first; i--)
        {
            double a = x[i - 1], b = x[i];
            if (!double.IsFinite(a) || !double.IsFinite(b)) return null;
            if (rising ? (a <= startLevel && b > startLevel) : (a >= startLevel && b < startLevel))
            {
                tStart = (i - 1) + (startLevel - a) / (b - a);
                break;
            }
        }
        // Forward from the crossing to the first sample past the end level.
        double? tEnd = null;
        for (int i = Math.Max(first, c + 1); i <= last; i++)
        {
            double a = x[i - 1], b = x[i];
            if (!double.IsFinite(a) || !double.IsFinite(b)) return null;
            if (rising ? (a < endLevel && b >= endLevel) : (a > endLevel && b <= endLevel))
            {
                tEnd = (i - 1) + (endLevel - a) / (b - a);
                break;
            }
        }
        return tStart is double s && tEnd is double e && e >= s ? e - s : null;
    }
}
