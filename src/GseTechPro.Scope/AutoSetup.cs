using GseTechPro.Core.Contracts;

namespace GseTechPro.Scope;

/// <summary>Auto setup suggestion for one channel.</summary>
/// <param name="Channel">1-based channel.</param>
/// <param name="RangeVolts">Smallest standard range that holds the probe peak with headroom.</param>
/// <param name="PeakVolts">Largest |sample| seen in the probe capture.</param>
/// <param name="MinVolts">Lowest sample in the probe capture.</param>
/// <param name="MaxVolts">Highest sample in the probe capture.</param>
/// <param name="FrequencyHz">Repeating-signal frequency, or null when no repeating signal was found.</param>
/// <param name="OverRange">True when the peak is above the largest range (an attenuator or different probe is needed).</param>
public sealed record AutoSetupChannel(
    int Channel,
    double RangeVolts,
    double PeakVolts,
    double MinVolts,
    double MaxVolts,
    double? FrequencyHz,
    bool OverRange);

/// <summary>
/// Result of <see cref="AutoSetup.Suggest"/>. Timebase, sample rate and trigger are null when no channel had a
/// repeating signal: the caller keeps the technician's current settings for those.
/// </summary>
public sealed record AutoSetupResult(
    IReadOnlyList<AutoSetupChannel> Channels,
    double? TimebaseSecondsPerDivision,
    int? SampleRateHz,
    int? TriggerChannel,
    double? TriggerLevelVolts,
    string Summary);

/// <summary>
/// Phase 10D.1 Auto setup (PicoScope 7 "Auto setup" workflow, our own implementation). From one probe capture taken
/// at the widest range with no trigger, picks per channel the smallest range that fits the signal, a timebase that
/// shows a few cycles of the first repeating signal, the fastest sample rate that keeps the capture within a memory
/// budget, and a rising-edge trigger at that signal's mid level. It only measures the signal; it never assumes what
/// the signal should be.
/// </summary>
public static class AutoSetup
{
    /// <summary>Peak must sit at or below this fraction of the range, so the trace never touches the edge.</summary>
    public const double RangeHeadroom = 0.9;

    /// <summary>Repeating signals are shown with at least this many cycles on screen (10 divisions).</summary>
    public const double TargetCyclesOnScreen = 3.0;

    /// <summary>Per-channel sample budget for the chosen sample rate (8 MB per channel as doubles).</summary>
    public const int MaxSamplesPerChannel = 1_000_000;

    /// <summary>Below this peak-to-peak (V) the channel is treated as flat (no repeating signal).</summary>
    public const double FlatSignalVolts = 0.02;

    /// <summary>Timebase for the probe capture: 200 ms on screen catches repeating signals down to about 10 Hz.</summary>
    public const double ProbeTimebaseSecondsPerDivision = 0.020;

    /// <summary>
    /// Builds the probe request: every channel in <paramref name="current"/> at the widest range (keeping its
    /// coupling, probe and clamp settings), <see cref="ProbeTimebaseSecondsPerDivision"/>, no trigger, at the fastest
    /// of <paramref name="sampleRates"/> that stays within <see cref="MaxSamplesPerChannel"/>.
    /// </summary>
    public static ScopeCaptureRequest BuildProbeRequest(
        ScopeCaptureRequest current,
        IReadOnlyList<double> ranges,
        IReadOnlyList<int> sampleRates)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (ranges is null || ranges.Count == 0) throw new ArgumentException("At least one range is required.", nameof(ranges));
        if (sampleRates is null || sampleRates.Count == 0) throw new ArgumentException("At least one sample rate is required.", nameof(sampleRates));

        double widest = ranges.Max();
        return new ScopeCaptureRequest
        {
            Channels = current.Channels.Select(c => c with { VoltageRangeVolts = widest }).ToList(),
            TimebaseSecondsPerDivision = ProbeTimebaseSecondsPerDivision,
            SampleRateHz = FastestRateWithinBudget(ProbeTimebaseSecondsPerDivision, sampleRates),
            TriggerMode = ScopeTriggerMode.None,
            TriggerChannel = current.Channels.Count > 0 ? current.Channels[0].Channel : 1,
        };
    }

    /// <summary>Suggests ranges, timebase, sample rate and trigger from a probe capture.</summary>
    public static AutoSetupResult Suggest(
        ScopeCapture probe,
        IReadOnlyList<double> ranges,
        IReadOnlyList<double> timebases,
        IReadOnlyList<int> sampleRates)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (ranges is null || ranges.Count == 0) throw new ArgumentException("At least one range is required.", nameof(ranges));
        if (timebases is null || timebases.Count == 0) throw new ArgumentException("At least one timebase is required.", nameof(timebases));
        if (sampleRates is null || sampleRates.Count == 0) throw new ArgumentException("At least one sample rate is required.", nameof(sampleRates));

        var sortedRanges = ranges.Where(r => r > 0 && double.IsFinite(r)).OrderBy(r => r).ToArray();
        var channels = new List<AutoSetupChannel>();
        foreach (var (channel, samples) in probe.Channels.OrderBy(kv => kv.Key))
            channels.Add(AnalyseChannel(channel, samples, probe.SampleRateHz, sortedRanges));

        if (channels.Count == 0)
            return new AutoSetupResult(channels, null, null, null, null, "Auto setup: the probe capture had no channels.");

        var lead = channels.FirstOrDefault(c => c.FrequencyHz is not null);
        double? timebase = null;
        int? rate = null;
        int? trigCh = null;
        double? trigLevel = null;
        if (lead?.FrequencyHz is double f)
        {
            double windowNeeded = TargetCyclesOnScreen / f;
            var sortedTb = timebases.Where(t => t > 0 && double.IsFinite(t)).OrderBy(t => t).ToArray();
            double pick = sortedTb.FirstOrDefault(t => t * 10.0 >= windowNeeded - 1e-12);
            timebase = pick > 0 ? pick : sortedTb[^1];
            rate = FastestRateWithinBudget(timebase.Value, sampleRates);
            trigCh = lead.Channel;
            trigLevel = Math.Round((lead.MinVolts + lead.MaxVolts) / 2.0, 3);
        }

        return new AutoSetupResult(channels, timebase, rate, trigCh, trigLevel, BuildSummary(channels, lead, timebase));
    }

    /// <summary>Fastest rate whose sample count over 10 divisions is within <see cref="MaxSamplesPerChannel"/>; the slowest rate if none is.</summary>
    public static int FastestRateWithinBudget(double timebaseSecondsPerDivision, IReadOnlyList<int> sampleRates)
    {
        var sorted = sampleRates.Where(r => r > 0).OrderBy(r => r).ToArray();
        if (sorted.Length == 0) throw new ArgumentException("At least one positive sample rate is required.", nameof(sampleRates));
        double window = timebaseSecondsPerDivision * 10.0;
        int best = sorted[0];
        foreach (var r in sorted)
            if (window * r <= MaxSamplesPerChannel) best = r;
        return best;
    }

    private static AutoSetupChannel AnalyseChannel(int channel, double[] samples, double sampleRateHz, double[] sortedRanges)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in samples)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (!double.IsFinite(min))
            return new AutoSetupChannel(channel, sortedRanges[^1], 0, 0, 0, null, false);

        double peak = Math.Max(Math.Abs(min), Math.Abs(max));
        double range = sortedRanges.FirstOrDefault(r => peak <= r * RangeHeadroom);
        bool over = range == 0;
        if (over) range = sortedRanges[^1];

        double? freq = null;
        if (max - min >= FlatSignalVolts && sampleRateHz > 0)
        {
            var edges = MathChannels.RisingEdges(samples);
            if (edges.Count >= 3)
            {
                var periods = new double[edges.Count - 1];
                for (int i = 1; i < edges.Count; i++) periods[i - 1] = edges[i] - edges[i - 1];
                Array.Sort(periods);
                double median = periods[periods.Length / 2];
                if (median > 0) freq = sampleRateHz / median;
            }
        }

        return new AutoSetupChannel(channel, range, peak, min, max, freq, over);
    }

    private static string BuildSummary(List<AutoSetupChannel> channels, AutoSetupChannel? lead, double? timebase)
    {
        var parts = channels.Select(c =>
        {
            string name = MathChannelDefinition.ChannelName(c.Channel);
            string text = FormattableString.Invariant($"{name} ±{c.RangeVolts:G4} V");
            if (c.OverRange) text += FormattableString.Invariant($" (peak {c.PeakVolts:G4} V is over range: use an attenuator or a higher-rated probe)");
            return text;
        }).ToList();

        if (lead?.FrequencyHz is double f && timebase is double tb)
            parts.Add(FormattableString.Invariant(
                $"{MathChannelDefinition.ChannelName(lead.Channel)} repeats at {FormatHz(f)}, timebase {FormatTime(tb)}/div, trigger rising at {(lead.MinVolts + lead.MaxVolts) / 2.0:0.###} V"));
        else
            parts.Add("no repeating signal found, timebase and trigger left as they were");

        return "Auto setup: " + string.Join(" · ", parts) + ".";
    }

    private static string FormatHz(double hz) => hz >= 1000
        ? FormattableString.Invariant($"{hz / 1000:0.##} kHz")
        : FormattableString.Invariant($"{hz:0.##} Hz");

    private static string FormatTime(double s) => s switch
    {
        < 1e-3 => FormattableString.Invariant($"{s * 1e6:0.##} µs"),
        < 1 => FormattableString.Invariant($"{s * 1e3:0.##} ms"),
        _ => FormattableString.Invariant($"{s:0.##} s"),
    };
}
