using System.Globalization;

namespace GseTechPro.Scope;

/// <summary>What the technician entered for a relative compression test.</summary>
public sealed record RelativeCompressionInputs
{
    /// <summary>Number of cylinders, from the engine (required, 1–16).</summary>
    public int Cylinders { get; init; }

    /// <summary>2 or 4 strokes per engine cycle, from the engine (required). Used only for the cranking speed.</summary>
    public int StrokesPerCycle { get; init; }

    /// <summary>Largest allowed spread between the strongest and weakest position, %, from a manual; or null.</summary>
    public double? MaximumSpreadPercent { get; init; }

    /// <summary>Where <see cref="MaximumSpreadPercent"/> comes from (manual and page). Required for a verdict.</summary>
    public string? MaximumSpreadSource { get; init; }
}

/// <summary>Result of <see cref="RelativeCompressionTest.Analyse"/>.</summary>
public sealed record RelativeCompressionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<OneTapResultRow> Rows { get; init; } = Array.Empty<OneTapResultRow>();
    /// <summary>Mean height per firing position (index 0 = position 1), as % of the strongest position.</summary>
    public IReadOnlyList<double> PositionPercent { get; init; } = Array.Empty<double>();
    public double CrankingRpm { get; init; } = double.NaN;
    public int CompressionsMeasured { get; init; }
    public bool IsSimulated { get; init; }
}

/// <summary>
/// Relative compression (Phase 10D.3b, PicoDiagnostics-style one-tap test). While the engine is
/// cranked without starting, every compression stroke loads the starter, so the starter current rises
/// (and the battery voltage dips) once per compression. A cylinder with less compression gives a
/// smaller bump. This test finds the crank (with <see cref="CrankingTest"/>), leaves out the inrush,
/// measures the height of each bump above the mean trough, and averages the heights per firing
/// position over whole engine cycles. Results are relative: % of the strongest position.
///
/// Without a cylinder 1 reference signal, position 1 is simply the first compression measured, not
/// necessarily cylinder 1. It only measures; the only verdict is the spread between strongest and
/// weakest position against a maximum the technician entered from a manual with its source.
/// </summary>
public static class RelativeCompressionTest
{
    /// <summary>Moving average used to take the noise off the bumps (signal processing, not a spec).</summary>
    public const double SmoothSeconds = 0.005;

    /// <summary>Hysteresis as a fraction of the 5–95 % range of the cranking signal.</summary>
    public const double HysteresisFraction = 0.10;

    /// <summary>
    /// Analyses one capture. <paramref name="volts"/> is the battery voltage (channel A, always
    /// needed: it finds the crank); <paramref name="amps"/> the starter-cable current, or null to
    /// use the voltage dips instead.
    /// </summary>
    public static RelativeCompressionResult Analyse(
        double[] volts,
        double[]? amps,
        double sampleRateHz,
        RelativeCompressionInputs inputs,
        bool isSimulated)
    {
        ArgumentNullException.ThrowIfNull(volts);
        ArgumentNullException.ThrowIfNull(inputs);
        var c = CultureInfo.InvariantCulture;

        if (inputs.Cylinders is < 1 or > 16)
            return Fail("Enter the number of cylinders (1–16) from the engine.", isSimulated);
        if (inputs.StrokesPerCycle is not (2 or 4))
            return Fail("Pick 4-stroke or 2-stroke for the engine.", isSimulated);

        var crank = CrankingTest.Analyse(volts, amps, sampleRateHz, new CrankingTestInputs(), isSimulated);
        if (!crank.Success)
            return Fail(crank.Message, isSimulated);

        bool useCurrent = amps is not null && amps.Length == volts.Length;
        double[] source = useCurrent ? amps! : volts;
        double sign = useCurrent ? 1.0 : -1.0; // voltage dips at each compression: flip so bumps point up

        int from = (int)Math.Round((crank.CrankStartSeconds + CrankingTest.InrushSeconds) * sampleRateHz);
        int to = Math.Min(source.Length, (int)Math.Round(crank.CrankEndSeconds * sampleRateHz));
        int win = Math.Max(1, (int)Math.Round(SmoothSeconds * sampleRateHz));
        if (to - from < win * 10)
            return Fail("The crank is too short to measure compressions. Crank for at least 2 s with the engine kept from starting.", isSimulated);

        // Smoothed, sign-corrected cranking signal.
        int len = to - from;
        var x = new double[len];
        double run = 0.0;
        for (int i = 0; i < len; i++)
        {
            run += sign * source[from + i];
            if (i >= win)
                run -= sign * source[from + i - win];
            x[i] = run / Math.Min(i + 1, win);
        }

        var sorted = (double[])x.Clone();
        Array.Sort(sorted);
        double p5 = sorted[(int)(0.05 * (len - 1))];
        double p95 = sorted[(int)(0.95 * (len - 1))];
        double range = p95 - p5;
        double scaleRef = Math.Max(Math.Abs(sorted[len / 2]), 1e-9);
        if (!(range > scaleRef * 0.005))
            return Fail(useCurrent
                ? "No compression bumps in the cranking current. Check the clamp is around the starter cable and the engine is cranking."
                : "No compression dips in the battery voltage. Use a current clamp on the starter cable for a clearer signal.", isSimulated);

        double mid = (p5 + p95) / 2.0;
        double hyst = range * HysteresisFraction;

        // Rising crossings of the mid level split the crank into one cycle per compression.
        var rises = new List<int>();
        bool high = x[0] > mid;
        for (int i = 1; i < len; i++)
        {
            if (!high && x[i] > mid + hyst) { high = true; rises.Add(i); }
            else if (high && x[i] < mid - hyst) { high = false; }
        }

        int cycles = rises.Count - 1;
        int n = inputs.Cylinders;
        if (cycles < 2 * n)
            return Fail(string.Create(c,
                $"Only {Math.Max(0, cycles)} compressions found; at least {2 * n} (two engine cycles) are needed. Crank longer with the engine kept from starting."), isSimulated);

        var peaks = new double[cycles];
        var troughs = new double[cycles];
        var periods = new double[cycles];
        for (int k = 0; k < cycles; k++)
        {
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            for (int i = rises[k]; i < rises[k + 1]; i++)
            {
                if (x[i] > max) max = x[i];
                if (x[i] < min) min = x[i];
            }
            peaks[k] = max;
            troughs[k] = min;
            periods[k] = (rises[k + 1] - rises[k]) / sampleRateHz;
        }

        int used = cycles / n * n; // whole engine cycles only, so each position has the same count
        double trough = troughs.Take(used).Average();
        var sums = new double[n];
        var sumSq = new double[n];
        for (int k = 0; k < used; k++)
        {
            double h = peaks[k] - trough;
            sums[k % n] += h;
            sumSq[k % n] += h * h;
        }
        int perPos = used / n;
        var mean = sums.Select(s => s / perPos).ToArray();
        double best = mean.Max();
        if (!(best > 0.0))
            return Fail("The compression bumps could not be measured.", isSimulated);

        var percent = mean.Select(m => m / best * 100.0).ToArray();
        double meanPeriod = periods.Take(used).Average();
        double compressionsPerSecond = 1.0 / meanPeriod;
        double rpm = compressionsPerSecond * (inputs.StrokesPerCycle == 4 ? 120.0 : 60.0) / n;
        double spread = 100.0 - percent.Min();
        int weakest = Array.IndexOf(percent, percent.Min()) + 1;

        var rows = new List<OneTapResultRow>
        {
            new("Cranking speed", string.Create(c, $"{rpm:0} rpm"),
                string.Create(c, $"From {compressionsPerSecond:0.0#} compressions per second, {n} cylinders, {inputs.StrokesPerCycle}-stroke.")),
            new("Compressions measured", string.Create(c, $"{used}"),
                string.Create(c, $"{perPos} engine cycles, after the first {CrankingTest.InrushSeconds:0.#} s (inrush). Signal: {(useCurrent ? "starter current" : "battery voltage dips (no clamp)")}.")),
        };
        for (int p = 0; p < n; p++)
        {
            double sd = perPos > 1 ? Math.Sqrt(Math.Max(0.0, (sumSq[p] - perPos * mean[p] * mean[p]) / (perPos - 1))) : 0.0;
            string unit = useCurrent ? "A" : "V";
            string note = p == 0 ? " Position 1 is the first compression measured, not necessarily cylinder 1 (no cylinder 1 reference)." : "";
            rows.Add(new($"Position {p + 1}", string.Create(c, $"{percent[p]:0} %"),
                string.Create(c, $"Bump {mean[p]:0.0#} {unit} above the trough, ±{sd:0.0#} {unit} over {perPos} cycles.{note}")));
        }

        var (verdict, comment) = CompareSpread(spread, weakest, inputs);
        rows.Add(new("Spread (strongest − weakest)", string.Create(c, $"{spread:0} %"), comment, verdict));

        return new RelativeCompressionResult
        {
            Success = true,
            Message = string.Create(c, $"{used} compressions measured at about {rpm:0} rpm cranking; weakest is position {weakest} at {percent.Min():0} %."),
            Rows = rows,
            PositionPercent = percent,
            CrankingRpm = rpm,
            CompressionsMeasured = used,
            IsSimulated = isSimulated,
        };
    }

    private static (OneTapVerdict, string) CompareSpread(double spread, int weakest, RelativeCompressionInputs inputs)
    {
        var c = CultureInfo.InvariantCulture;
        string where = string.Create(c, $"Weakest: position {weakest}.");
        if (inputs.MaximumSpreadPercent is not double limit)
            return (OneTapVerdict.None, $"{where} No maximum entered: compare with the engine manual, and confirm a low position with a compression gauge.");
        if (string.IsNullOrWhiteSpace(inputs.MaximumSpreadSource))
            return (OneTapVerdict.None, string.Create(c, $"{where} A {limit:0.#} % maximum was entered without its source. Enter the manual and page to get a verdict."));
        string src = inputs.MaximumSpreadSource.Trim();
        return spread <= limit
            ? (OneTapVerdict.Pass, string.Create(c, $"PASS: within the {limit:0.#} % maximum ({src}). {where}"))
            : (OneTapVerdict.Fail, string.Create(c, $"FAIL: more than the {limit:0.#} % maximum ({src}). {where}"));
    }

    private static RelativeCompressionResult Fail(string message, bool isSimulated) =>
        new() { Success = false, Message = message, IsSimulated = isSimulated };
}
