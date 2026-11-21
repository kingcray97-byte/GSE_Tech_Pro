using System.Globalization;

namespace GseTechPro.Scope;

/// <summary>Verdict of one result row. Only rows compared with a technician-entered, cited limit get one.</summary>
public enum OneTapVerdict
{
    None,
    Pass,
    Fail,
}

/// <summary>One row of a one-tap test result table (Parameter / Value / Comment).</summary>
public sealed record OneTapResultRow(string Parameter, string Value, string Comment, OneTapVerdict Verdict = OneTapVerdict.None);

/// <summary>
/// What the technician entered for a battery / cranking test. Everything is optional. Nothing here
/// has a default value, because a rating or a limit must come from the battery label or a manual
/// (AGENTS.md rule 5).
/// </summary>
public sealed record CrankingTestInputs
{
    /// <summary>Battery rating from its label (e.g. 760), or null when not entered.</summary>
    public double? RatedCca { get; init; }

    /// <summary>Which rating the label gives ("SAE CCA", "EN", "DIN", "IEC"). Display text only.</summary>
    public string? RatingStandard { get; init; }

    /// <summary>Minimum cranking voltage from the equipment manual, or null.</summary>
    public double? MinimumCrankingVolts { get; init; }

    /// <summary>Where <see cref="MinimumCrankingVolts"/> comes from (manual and page). Required for a verdict.</summary>
    public string? MinimumCrankingSource { get; init; }

    /// <summary>Battery temperature the technician measured, °C. Recorded only.</summary>
    public double? BatteryTemperatureC { get; init; }

    /// <summary>Battery type from its label. Recorded only.</summary>
    public string? BatteryType { get; init; }
}

/// <summary>Result of <see cref="CrankingTest.Analyse"/>.</summary>
public sealed record CrankingTestResult
{
    /// <summary>False when the capture holds no cranking event that can be measured; <see cref="Message"/> says why.</summary>
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<OneTapResultRow> Rows { get; init; } = Array.Empty<OneTapResultRow>();
    /// <summary>Crank start and end, seconds from the start of the capture (NaN when not found).</summary>
    public double CrankStartSeconds { get; init; } = double.NaN;
    public double CrankEndSeconds { get; init; } = double.NaN;
    public bool IsSimulated { get; init; }
}

/// <summary>
/// Battery / cranking test (Phase 10D.3a, PicoDiagnostics-style one-tap test): battery voltage on
/// one channel and, when the technician has a current clamp, starter-cable current on another.
/// It finds the crank in the capture and measures it: resting voltage, lowest voltage (inrush dip),
/// cranking voltage and current, cranking time and the voltage after cranking.
///
/// It only measures. The single verdict it gives is the lowest cranking voltage against a minimum
/// the technician entered from a manual, and only when they also entered where that minimum comes
/// from. A rated CCA from the battery label is shown as a comparison with the peak current, never
/// as a pass/fail. The detection fractions below are signal-processing choices for finding the crank
/// in the trace, not equipment specifications.
/// </summary>
public static class CrankingTest
{
    /// <summary>Crank start: voltage (1 ms average) falls more than this fraction below the resting voltage.</summary>
    public const double StartDropFraction = 0.10;

    /// <summary>Crank end (voltage only): the 100 ms average is back within this fraction of the resting voltage.</summary>
    public const double EndRecoverFraction = 0.05;

    /// <summary>With a clamp, cranking is where the current (1 ms average) is above this fraction of its peak.</summary>
    public const double CurrentThresholdFraction = 0.10;

    /// <summary>The first part of the crank is the starter inrush; cranking averages leave it out.</summary>
    public const double InrushSeconds = 0.2;

    /// <summary>At least this much signal before the crank to measure the resting voltage.</summary>
    public const double MinimumRestSeconds = 0.1;

    /// <summary>Samples within this fraction of the channel range are treated as clipped.</summary>
    public const double ClipFraction = 0.98;

    /// <summary>
    /// Analyses one capture. <paramref name="volts"/> is the battery voltage; <paramref name="amps"/>,
    /// when not null, the starter-cable current already scaled to amps. Ranges (full scale, same
    /// units) are used only to report clipping; pass null when not known.
    /// </summary>
    public static CrankingTestResult Analyse(
        double[] volts,
        double[]? amps,
        double sampleRateHz,
        CrankingTestInputs inputs,
        bool isSimulated,
        double? voltageRangeVolts = null,
        double? currentRangeAmps = null)
    {
        ArgumentNullException.ThrowIfNull(volts);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!(sampleRateHz > 0.0) || !double.IsFinite(sampleRateHz))
            return Fail("The capture has no valid sample rate.", isSimulated);

        int n = volts.Length;
        if (amps is not null && amps.Length != n)
            amps = null;

        int restSamples = Math.Max(1, (int)Math.Round(MinimumRestSeconds * sampleRateHz));
        int ms1 = Math.Max(1, (int)Math.Round(0.001 * sampleRateHz));
        int ms100 = Math.Max(1, (int)Math.Round(0.1 * sampleRateHz));
        if (n < restSamples * 4)
            return Fail("The capture is too short. Use a timebase of 500 ms/div so the capture holds the whole crank.", isSimulated);

        var vPrefix = Prefix(volts);
        double[]? aPrefix = amps is null ? null : Prefix(amps);

        // Resting voltage estimate from the first 10 ms (short, so a crank that starts too soon is
        // still found and reported as too soon), then the crank start.
        double restEstimate = Mean(vPrefix, 0, Math.Max(1, ms1 * 10));
        if (!(restEstimate > 0.0))
            return Fail("Channel A does not read a positive battery voltage. Check the probe is on battery positive and ground on battery negative.", isSimulated);

        int start = -1;
        int end = -1;
        double peakAmps = double.NaN;
        bool usedCurrent = false;

        if (aPrefix is not null)
        {
            peakAmps = MaxWindowMean(aPrefix, ms1, 0, n, out _);
            if (peakAmps > 0.0)
            {
                double threshold = peakAmps * CurrentThresholdFraction;
                for (int i = 0; i + ms1 <= n; i++)
                {
                    if (Mean(aPrefix, i, i + ms1) > threshold) { start = i; break; }
                }
                if (start >= 0)
                {
                    for (int i = n - ms1; i >= start; i--)
                    {
                        if (Mean(aPrefix, i, i + ms1) > threshold) { end = i + ms1; break; }
                    }
                    usedCurrent = true;
                }
            }
        }

        if (start < 0)
        {
            double startLevel = restEstimate * (1.0 - StartDropFraction);
            for (int i = 0; i + ms1 <= n; i++)
            {
                if (Mean(vPrefix, i, i + ms1) < startLevel) { start = i; break; }
            }
        }

        if (start < 0)
            return Fail("No crank found: the battery voltage did not drop. Tap Start, then crank the engine within about 1 s (the capture is 5 s).", isSimulated);

        if (start < restSamples)
            return Fail("Cranking started too soon after Start, so there is no resting voltage to measure. Tap Start, wait a moment, then crank.", isSimulated);

        // Resting voltage: everything before the crank, less a 20 ms margin.
        int restEnd = Math.Max(1, start - Math.Max(1, (int)Math.Round(0.02 * sampleRateHz)));
        double rest = Mean(vPrefix, 0, restEnd);

        bool ranPastEnd = false;
        if (!usedCurrent)
        {
            // Voltage only: the crank ends when the 100 ms average recovers to near the resting voltage.
            double recover = rest * (1.0 - EndRecoverFraction);
            int searchFrom = start + Math.Max(ms100, (int)Math.Round(InrushSeconds * sampleRateHz));
            for (int i = searchFrom; i + ms100 <= n; i++)
            {
                if (Mean(vPrefix, i, i + ms100) >= recover) { end = i; break; }
            }
        }

        if (end < 0 || end >= n - ms1)
        {
            end = n;
            ranPastEnd = true;
        }

        if (end <= start)
            return Fail("The crank could not be measured in this capture.", isSimulated);

        double crankSeconds = (end - start) / sampleRateHz;
        double minVolts = MinWindowMean(vPrefix, ms1, start, end, out int minIdx);
        int inrush = (int)Math.Round(InrushSeconds * sampleRateHz);
        bool hasSustained = end - start > inrush * 2;
        int sustainedStart = hasSustained ? start + inrush : start;
        double crankVolts = Mean(vPrefix, sustainedStart, end);
        double lowestSustained = hasSustained ? MinWindowMean(vPrefix, ms100, sustainedStart, end, out _) : double.NaN;

        var rows = new List<OneTapResultRow>();
        var c = CultureInfo.InvariantCulture;
        string sustainedNote = hasSustained ? $"after the first {InrushSeconds:0.#} s (inrush)" : "whole crank (too short to leave out the inrush)";

        rows.Add(new("Resting voltage", V(rest), "Mean before the crank started."));
        rows.Add(new("Lowest voltage (inrush)", V(minVolts),
            string.Create(c, $"1 ms average, {(minIdx - start) / sampleRateHz * 1000.0:0} ms after the crank started.")));
        rows.Add(new("Cranking voltage", V(crankVolts), $"Mean, {sustainedNote}."));

        if (!double.IsNaN(lowestSustained))
        {
            var (verdict, comment) = CompareMinimum(lowestSustained, inputs);
            rows.Add(new("Lowest cranking voltage", V(lowestSustained), comment, verdict));
        }
        else if (inputs.MinimumCrankingVolts is not null)
        {
            rows.Add(new("Lowest cranking voltage", "—", "Crank too short to measure after the inrush, so no verdict."));
        }

        rows.Add(new("Cranking time", string.Create(c, $"{crankSeconds:0.00} s"),
            ranPastEnd
                ? "Cranking was still going at the end of the capture: the time is at least this long."
                : usedCurrent ? "Time the current was above 10 % of its peak." : "Time the voltage was below the resting level."));

        if (aPrefix is not null && peakAmps > 0.0)
        {
            double crankAmps = Mean(aPrefix, sustainedStart, end);
            rows.Add(new("Peak current (inrush)", A(peakAmps), "1 ms average."));
            rows.Add(new("Cranking current", A(crankAmps), $"Mean, {sustainedNote}."));
            if (inputs.RatedCca is double cca && cca > 0.0)
            {
                string standard = string.IsNullOrWhiteSpace(inputs.RatingStandard) ? "" : $" ({inputs.RatingStandard.Trim()})";
                rows.Add(new("Peak current / battery rating", string.Create(c, $"{peakAmps / cca * 100.0:0} %"),
                    string.Create(c, $"Peak current compared with the label rating of {cca:0} A{standard}. A comparison, not a pass/fail.")));
            }
        }
        else if (aPrefix is not null)
        {
            rows.Add(new("Current", "—", "The clamp channel read no current. Check the clamp is on, zeroed and around the starter cable."));
        }
        else
        {
            rows.Add(new("Current", "—", "No current clamp entered: voltage only."));
        }

        if (!ranPastEnd && n - end > ms100 * 3)
        {
            int tail = Math.Min(n - end, (int)Math.Round(0.5 * sampleRateHz));
            rows.Add(new("Voltage after cranking", V(Mean(vPrefix, n - tail, n)),
                string.Create(c, $"Mean of the last {tail / sampleRateHz:0.0#} s of the capture.")));
        }

        if (inputs.BatteryTemperatureC is double tC)
            rows.Add(new("Battery temperature", string.Create(c, $"{tC:0.#} °C"), "Entered by the technician. Recorded only."));
        if (!string.IsNullOrWhiteSpace(inputs.BatteryType))
            rows.Add(new("Battery type", inputs.BatteryType.Trim(), "Entered by the technician. Recorded only."));

        AddClipRow(rows, volts, voltageRangeVolts, "Voltage channel");
        if (amps is not null)
            AddClipRow(rows, amps, currentRangeAmps, "Current channel");

        string message = string.Create(c,
            $"Crank found at {start / sampleRateHz:0.00} s, {crankSeconds:0.00} s long{(ranPastEnd ? " (ran past the end of the capture)" : "")}.");
        return new CrankingTestResult
        {
            Success = true,
            Message = message,
            Rows = rows,
            CrankStartSeconds = start / sampleRateHz,
            CrankEndSeconds = end / sampleRateHz,
            IsSimulated = isSimulated,
        };
    }

    private static (OneTapVerdict, string) CompareMinimum(double lowest, CrankingTestInputs inputs)
    {
        var c = CultureInfo.InvariantCulture;
        if (inputs.MinimumCrankingVolts is not double limit)
            return (OneTapVerdict.None, "100 ms average after the inrush. No minimum entered: compare with the equipment manual.");
        if (string.IsNullOrWhiteSpace(inputs.MinimumCrankingSource))
            return (OneTapVerdict.None, string.Create(c, $"A {limit:0.0#} V minimum was entered without its source. Enter the manual and page to get a verdict."));

        string src = inputs.MinimumCrankingSource.Trim();
        return lowest >= limit
            ? (OneTapVerdict.Pass, string.Create(c, $"PASS: at or above the {limit:0.0#} V minimum ({src})."))
            : (OneTapVerdict.Fail, string.Create(c, $"FAIL: below the {limit:0.0#} V minimum ({src})."));
    }

    private static void AddClipRow(List<OneTapResultRow> rows, double[] samples, double? range, string name)
    {
        if (range is not double r || !(r > 0.0))
            return;
        double limit = r * ClipFraction;
        int clipped = 0;
        foreach (var s in samples)
        {
            if (Math.Abs(s) >= limit)
                clipped++;
        }
        if (clipped > 0)
            rows.Add(new($"{name} over range", string.Create(CultureInfo.InvariantCulture, $"{clipped:N0} samples"),
                "The signal reached the edge of the range, so values there are not trustworthy. Use a higher range and test again."));
    }

    private static CrankingTestResult Fail(string message, bool isSimulated) =>
        new() { Success = false, Message = message, IsSimulated = isSimulated };

    private static string V(double v) => v.ToString("0.00", CultureInfo.InvariantCulture) + " V";

    private static string A(double a) => a.ToString("0", CultureInfo.InvariantCulture) + " A";

    private static double[] Prefix(double[] x)
    {
        var p = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++)
            p[i + 1] = p[i] + x[i];
        return p;
    }

    /// <summary>Mean of samples [from, to).</summary>
    private static double Mean(double[] prefix, int from, int to) =>
        to > from ? (prefix[to] - prefix[from]) / (to - from) : double.NaN;

    private static double MinWindowMean(double[] prefix, int window, int from, int to, out int at)
    {
        at = from;
        double best = double.PositiveInfinity;
        int last = Math.Max(from, to - window);
        for (int i = from; i <= last; i++)
        {
            double m = Mean(prefix, i, Math.Min(to, i + window));
            if (m < best) { best = m; at = i; }
        }
        return best;
    }

    private static double MaxWindowMean(double[] prefix, int window, int from, int to, out int at)
    {
        at = from;
        double best = double.NegativeInfinity;
        int last = Math.Max(from, to - window);
        for (int i = from; i <= last; i++)
        {
            double m = Mean(prefix, i, Math.Min(to, i + window));
            if (m > best) { best = m; at = i; }
        }
        return best;
    }
}
