using System.Globalization;

namespace GseTechPro.Scope;

/// <summary>What the technician entered for a cylinder balance test. Engine and tooth-wheel facts have no defaults.</summary>
public sealed record CylinderBalanceInputs
{
    /// <summary>Number of cylinders, from the engine (1–16).</summary>
    public int Cylinders { get; init; }

    /// <summary>2 or 4 strokes per engine cycle, from the engine.</summary>
    public int StrokesPerCycle { get; init; }

    /// <summary>Tooth positions on the crank wheel, counting the missing ones (60 for a 60-2 wheel), from the sensor/wheel spec.</summary>
    public int WheelTeeth { get; init; }

    /// <summary>Missing teeth in the reference gap (2 for a 60-2 wheel; 0 when the wheel has no gap).</summary>
    public int MissingTeeth { get; init; }

    /// <summary>Largest allowed spread between the strongest and weakest position, %, from a manual; or null.</summary>
    public double? MaximumSpreadPercent { get; init; }

    /// <summary>Where <see cref="MaximumSpreadPercent"/> comes from (manual and page). Required for a verdict.</summary>
    public string? MaximumSpreadSource { get; init; }
}

/// <summary>Result of <see cref="CylinderBalanceTest.Analyse"/>.</summary>
public sealed record CylinderBalanceResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<OneTapResultRow> Rows { get; init; } = Array.Empty<OneTapResultRow>();
    /// <summary>Mean speed rise per firing position (index 0 = position 1), as % of the strongest position.</summary>
    public IReadOnlyList<double> PositionPercent { get; init; } = Array.Empty<double>();
    public double MeanRpm { get; init; } = double.NaN;
    public int FiringsMeasured { get; init; }
    public bool IsSimulated { get; init; }
}

/// <summary>
/// Cylinder balance (Phase 10D.3c, PicoDiagnostics-style one-tap test) with the engine running. Each
/// cylinder's power stroke speeds the crankshaft up a little; a cylinder that contributes less gives a
/// smaller speed rise. From a crank position sensor (tooth wheel) on one channel this test times every
/// tooth (interpolated crossings of the signal's mid level), turns each tooth interval into crank speed
/// (the missing-tooth gap counts as the teeth it spans), splits the speed trace into one rise per firing,
/// and averages the rise (peak − trough) per firing position over whole engine cycles.
///
/// It checks the firing count it finds against the cylinders and engine cycle entered, so a wrong input
/// is reported rather than measured. Without a cam or cylinder 1 reference, position 1 is the first
/// firing measured, not necessarily cylinder 1. Results are relative (% of the strongest position); the
/// only verdict is the spread against a maximum the technician entered from a manual with its source.
/// </summary>
public static class CylinderBalanceTest
{
    /// <summary>A tooth interval this many times the local median is the missing-tooth gap.</summary>
    public const double GapRatio = 1.5;

    /// <summary>Hysteresis as a fraction of the signal's 5–95 % range (tooth edges and speed rises).</summary>
    public const double HysteresisFraction = 0.10;

    /// <summary>The firing rate found may differ from the one expected from the inputs by this fraction.</summary>
    public const double FiringRateTolerance = 0.25;

    public static CylinderBalanceResult Analyse(
        double[] sensorVolts,
        double sampleRateHz,
        CylinderBalanceInputs inputs,
        bool isSimulated,
        double? voltageRangeVolts = null)
    {
        ArgumentNullException.ThrowIfNull(sensorVolts);
        ArgumentNullException.ThrowIfNull(inputs);
        var c = CultureInfo.InvariantCulture;

        if (inputs.Cylinders is < 1 or > 16)
            return Fail("Enter the number of cylinders (1–16) from the engine.", isSimulated);
        if (inputs.StrokesPerCycle is not (2 or 4))
            return Fail("Pick 4-stroke or 2-stroke for the engine.", isSimulated);
        if (inputs.WheelTeeth is < 4 or > 720)
            return Fail("Enter the crank wheel's tooth count, counting missing teeth (e.g. 60 for 60-2), from the sensor or wheel spec.", isSimulated);
        if (inputs.MissingTeeth < 0 || inputs.MissingTeeth >= inputs.WheelTeeth / 2)
            return Fail("Enter the number of missing teeth in the gap (e.g. 2 for 60-2, 0 when there is no gap).", isSimulated);
        if (!(sampleRateHz > 0.0) || sensorVolts.Length < 100)
            return Fail("The capture is too short to measure.", isSimulated);

        // ---- 1. Tooth edges: rising crossings of the mid level, interpolated ----
        var sorted = (double[])sensorVolts.Clone();
        Array.Sort(sorted);
        double p5 = sorted[(int)(0.05 * (sorted.Length - 1))];
        double p95 = sorted[(int)(0.95 * (sorted.Length - 1))];
        double range = p95 - p5;
        if (!(range > 0.05))
            return Fail("No crank sensor signal on channel A. Check the sensor connection and that the engine is running.", isSimulated);
        double mid = (p5 + p95) / 2.0;
        double hyst = range * HysteresisFraction;

        var edges = new List<double>();
        bool high = sensorVolts[0] > mid;
        for (int i = 1; i < sensorVolts.Length; i++)
        {
            if (!high && sensorVolts[i] > mid + hyst)
            {
                high = true;
                // interpolate the mid crossing between the last sample at or below mid and the next
                int j = i;
                while (j > 0 && sensorVolts[j - 1] > mid) j--;
                double a = sensorVolts[Math.Max(0, j - 1)], b = sensorVolts[j];
                double frac = b != a ? (mid - a) / (b - a) : 0.0;
                edges.Add((j - 1 + Math.Clamp(frac, 0.0, 1.0)) / sampleRateHz);
            }
            else if (high && sensorVolts[i] < mid - hyst)
            {
                high = false;
            }
        }

        int teethPerRev = inputs.WheelTeeth;
        if (edges.Count < teethPerRev * 2)
            return Fail(string.Create(c, $"Only {edges.Count} teeth found; at least {teethPerRev * 2} (two revolutions) are needed. Use a longer timebase or check the engine is running."), isSimulated);

        // ---- 2. Crank speed per tooth interval; the gap spans (missing + 1) tooth pitches ----
        int nIntervals = edges.Count - 1;
        var intervals = new double[nIntervals];
        for (int k = 0; k < nIntervals; k++)
            intervals[k] = edges[k + 1] - edges[k];

        var speeds = new List<double>(nIntervals);
        int gaps = 0;
        const int medianHalf = 5;
        for (int k = 0; k < nIntervals; k++)
        {
            int lo = Math.Max(0, k - medianHalf), hi = Math.Min(nIntervals - 1, k + medianHalf);
            var local = new double[hi - lo + 1];
            Array.Copy(intervals, lo, local, 0, local.Length);
            Array.Sort(local);
            double median = local[local.Length / 2];
            int pitches = 1;
            if (intervals[k] > median * GapRatio)
            {
                if (inputs.MissingTeeth == 0)
                    return Fail("The crank signal has a missing-tooth gap, but 0 missing teeth was entered. Check the wheel spec.", isSimulated);
                pitches = inputs.MissingTeeth + 1;
                gaps++;
            }
            speeds.Add(60.0 / (intervals[k] / pitches * teethPerRev)); // rpm over this interval
        }

        double revolutions = (nIntervals + gaps * inputs.MissingTeeth) / (double)teethPerRev;
        if (inputs.MissingTeeth > 0 && gaps < Math.Floor(revolutions) - 1)
            return Fail(string.Create(c, $"Expected a missing-tooth gap every revolution but found {gaps} in about {revolutions:0.#} revolutions. Check the tooth count and missing teeth entered."), isSimulated);

        // Smooth the speed over 3 teeth to take tooth-spacing error off the trace.
        int m = speeds.Count;
        var s = new double[m];
        for (int k = 0; k < m; k++)
        {
            int lo = Math.Max(0, k - 1), hi = Math.Min(m - 1, k + 1);
            double sum = 0;
            for (int q = lo; q <= hi; q++) sum += speeds[q];
            s[k] = sum / (hi - lo + 1);
        }
        double meanRpm = speeds.Average();

        // ---- 3. One speed rise per firing ----
        var ss = (double[])s.Clone();
        Array.Sort(ss);
        double sp5 = ss[(int)(0.05 * (m - 1))], sp95 = ss[(int)(0.95 * (m - 1))];
        double srange = sp95 - sp5;
        if (!(srange > meanRpm * 0.0005))
            return Fail("No crank speed change per firing was found: the engine speed is too steady to compare cylinders.", isSimulated);
        double smid = (sp5 + sp95) / 2.0, shyst = srange * HysteresisFraction;

        var rises = new List<int>();
        bool up = s[0] > smid;
        for (int k = 1; k < m; k++)
        {
            if (!up && s[k] > smid + shyst) { up = true; rises.Add(k); }
            else if (up && s[k] < smid - shyst) { up = false; }
        }

        int n = inputs.Cylinders;
        int firings = rises.Count - 1;
        double revsPerCycle = inputs.StrokesPerCycle / 2.0;
        double expectedPerRev = n / revsPerCycle;
        if (firings >= 2)
        {
            double foundPerRev = firings / ((rises[^1] - rises[0]) / (double)teethPerRev);
            if (Math.Abs(foundPerRev - expectedPerRev) > expectedPerRev * FiringRateTolerance)
                return Fail(string.Create(c,
                    $"Found {foundPerRev:0.#} speed rises per revolution, but {n} cylinders {inputs.StrokesPerCycle}-stroke fire {expectedPerRev:0.#} times per revolution. Check the cylinders, engine cycle and tooth count entered."), isSimulated);
        }
        if (firings < 2 * n)
            return Fail(string.Create(c, $"Only {Math.Max(0, firings)} firings found; at least {2 * n} (two engine cycles) are needed. Use a longer timebase."), isSimulated);

        var gain = new double[firings];
        for (int k = 0; k < firings; k++)
        {
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            for (int q = rises[k]; q < rises[k + 1]; q++)
            {
                if (s[q] > max) max = s[q];
                if (s[q] < min) min = s[q];
            }
            gain[k] = max - min;
        }

        int used = firings / n * n;
        int perPos = used / n;
        var sums = new double[n];
        var sumSq = new double[n];
        for (int k = 0; k < used; k++)
        {
            sums[k % n] += gain[k];
            sumSq[k % n] += gain[k] * gain[k];
        }
        var mean = sums.Select(v => v / perPos).ToArray();
        double best = mean.Max();
        if (!(best > 0.0))
            return Fail("The speed rises could not be measured.", isSimulated);
        var percent = mean.Select(v => v / best * 100.0).ToArray();
        double spread = 100.0 - percent.Min();
        int weakest = Array.IndexOf(percent, percent.Min()) + 1;

        var rows = new List<OneTapResultRow>
        {
            new("Engine speed", string.Create(c, $"{meanRpm:0} rpm"), "Mean crank speed over the capture."),
            new("Speed change per firing", string.Create(c, $"{gain.Take(used).Average():0.0} rpm"),
                "Mean rise from the slowest to the fastest point of each firing."),
            new("Firings measured", string.Create(c, $"{used}"),
                string.Create(c, $"{perPos} engine cycles; {edges.Count} teeth, {gaps} reference gaps.")),
        };
        for (int p = 0; p < n; p++)
        {
            double sd = perPos > 1 ? Math.Sqrt(Math.Max(0.0, (sumSq[p] - perPos * mean[p] * mean[p]) / (perPos - 1))) : 0.0;
            string note = p == 0 ? " Position 1 is the first firing measured, not necessarily cylinder 1 (no cam or cylinder 1 reference)." : "";
            rows.Add(new($"Position {p + 1}", string.Create(c, $"{percent[p]:0} %"),
                string.Create(c, $"Speed rise {mean[p]:0.0} rpm, ±{sd:0.0} rpm over {perPos} cycles.{note}")));
        }

        var (verdict, comment) = CompareSpread(spread, weakest, inputs);
        rows.Add(new("Spread (strongest − weakest)", string.Create(c, $"{spread:0} %"), comment, verdict));

        if (voltageRangeVolts is double r && r > 0.0 && sensorVolts.Any(v => Math.Abs(v) >= r * CrankingTest.ClipFraction))
            rows.Add(new("Sensor channel over range", "—",
                "The sensor signal reached the edge of the range. Tooth timing is usually still good, but use a higher range to be sure."));

        return new CylinderBalanceResult
        {
            Success = true,
            Message = string.Create(c, $"{used} firings measured at {meanRpm:0} rpm; weakest is position {weakest} at {percent.Min():0} %."),
            Rows = rows,
            PositionPercent = percent,
            MeanRpm = meanRpm,
            FiringsMeasured = used,
            IsSimulated = isSimulated,
        };
    }

    private static (OneTapVerdict, string) CompareSpread(double spread, int weakest, CylinderBalanceInputs inputs)
    {
        var c = CultureInfo.InvariantCulture;
        string where = string.Create(c, $"Weakest: position {weakest}.");
        if (inputs.MaximumSpreadPercent is not double limit)
            return (OneTapVerdict.None, $"{where} No maximum entered: compare with the engine manual.");
        if (string.IsNullOrWhiteSpace(inputs.MaximumSpreadSource))
            return (OneTapVerdict.None, string.Create(c, $"{where} A {limit:0.#} % maximum was entered without its source. Enter the manual and page to get a verdict."));
        string src = inputs.MaximumSpreadSource.Trim();
        return spread <= limit
            ? (OneTapVerdict.Pass, string.Create(c, $"PASS: within the {limit:0.#} % maximum ({src}). {where}"))
            : (OneTapVerdict.Fail, string.Create(c, $"FAIL: more than the {limit:0.#} % maximum ({src}). {where}"));
    }

    private static CylinderBalanceResult Fail(string message, bool isSimulated) =>
        new() { Success = false, Message = message, IsSimulated = isSimulated };
}
