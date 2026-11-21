using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Scope;

namespace GseTechPro.App.ViewModels;

/// <summary>One cycle row in the DeepMeasure table (display strings only).</summary>
public sealed record DeepMeasureRowViewModel(
    int Number,
    string Start,
    string Period,
    string Frequency,
    string Duty,
    string Maximum,
    string Minimum,
    string PeakToPeak,
    string Rise,
    string Fall);

/// <summary>One statistics row (one measurement over every cycle).</summary>
public sealed record DeepMeasureStatRowViewModel(
    string Name,
    string Minimum,
    string Maximum,
    string Mean,
    string StandardDeviation,
    string Extremes);

/// <summary>
/// Phase 10D.1 — DeepMeasure on the scope screen: every cycle of one channel of the capture on screen measured on its
/// own (period, frequency, duty, max/min/pk-pk, rise/fall), with min/max/mean/σ and which cycles hold the extremes, and
/// a sort to bring the odd cycle to the top. Computed off the UI thread only while switched on. It measures only;
/// verdicts stay with masks and reference waveforms.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Sort picker entry; <see cref="Kind"/> null sorts by cycle number.</summary>
    public sealed record DeepMeasureSortOption(CycleMeasureKind? Kind, string Display)
    {
        public override string ToString() => Display;
    }

    public IReadOnlyList<DeepMeasureSortOption> ScopeDeepMeasureSortOptions { get; } = new[]
    {
        new DeepMeasureSortOption(null, "Cycle number"),
        new DeepMeasureSortOption(CycleMeasureKind.Period, "Period"),
        new DeepMeasureSortOption(CycleMeasureKind.Frequency, "Frequency"),
        new DeepMeasureSortOption(CycleMeasureKind.DutyCycle, "Duty cycle"),
        new DeepMeasureSortOption(CycleMeasureKind.Maximum, "Maximum"),
        new DeepMeasureSortOption(CycleMeasureKind.Minimum, "Minimum"),
        new DeepMeasureSortOption(CycleMeasureKind.PeakToPeak, "Peak to peak"),
        new DeepMeasureSortOption(CycleMeasureKind.RiseTime, "Rise time"),
        new DeepMeasureSortOption(CycleMeasureKind.FallTime, "Fall time"),
    };

    public IReadOnlyList<int> ScopeDeepMeasureChannels { get; } = new[] { 1, 2, 3, 4 };

    [ObservableProperty]
    private bool _isScopeDeepMeasureEnabled;

    [ObservableProperty]
    private int _scopeDeepMeasureChannel = 1;

    [ObservableProperty]
    private DeepMeasureSortOption? _selectedScopeDeepMeasureSort;

    [ObservableProperty]
    private bool _isScopeDeepMeasureSortDescending;

    [ObservableProperty]
    private string _scopeDeepMeasureStatusText = "DeepMeasure off.";

    [ObservableProperty]
    private IReadOnlyList<DeepMeasureRowViewModel> _scopeDeepMeasureRows = Array.Empty<DeepMeasureRowViewModel>();

    [ObservableProperty]
    private IReadOnlyList<DeepMeasureStatRowViewModel> _scopeDeepMeasureStatRows = Array.Empty<DeepMeasureStatRowViewModel>();

    /// <summary>Last analysis (for tests and later work-order export).</summary>
    public DeepMeasureResult? ScopeDeepMeasureResult { get; private set; }

    partial void OnIsScopeDeepMeasureEnabledChanged(bool value) => _ = RefreshScopeDeepMeasureAsync();
    partial void OnScopeDeepMeasureChannelChanged(int value) => _ = RefreshScopeDeepMeasureAsync();
    partial void OnSelectedScopeDeepMeasureSortChanged(DeepMeasureSortOption? value) => ApplyScopeDeepMeasureRows();
    partial void OnIsScopeDeepMeasureSortDescendingChanged(bool value) => ApplyScopeDeepMeasureRows();

    /// <summary>Measures every cycle of the chosen channel of the capture on screen (off the UI thread).</summary>
    public async Task RefreshScopeDeepMeasureAsync()
    {
        var capture = LastScopeCapture;
        if (!IsScopeDeepMeasureEnabled || capture is null)
        {
            ScopeDeepMeasureResult = null;
            ScopeDeepMeasureStatusText = IsScopeDeepMeasureEnabled ? "DeepMeasure on: press Run to measure each cycle." : "DeepMeasure off.";
            ApplyScopeDeepMeasureRows();
            return;
        }

        int channel = ScopeDeepMeasureChannel;
        var result = await Task.Run(() => DeepMeasure.Analyze(capture, channel));
        if (!ReferenceEquals(capture, LastScopeCapture) || channel != ScopeDeepMeasureChannel || !IsScopeDeepMeasureEnabled)
            return; // superseded while computing

        ScopeDeepMeasureResult = result;
        string ch = MathChannelDefinition.ChannelName(channel);
        string provenance = result.IsSimulated ? " (simulated)" : "";
        ScopeDeepMeasureStatusText = result.IsOk
            ? $"Channel {ch}: {result.Cycles.Count} cycles{(result.Truncated ? $" (first {result.Cycles.Count} shown; use a shorter timebase)" : "")}{provenance}"
            : $"Channel {ch}: {result.Error}{provenance}";
        ApplyScopeDeepMeasureRows();
    }

    private void ApplyScopeDeepMeasureRows()
    {
        var result = ScopeDeepMeasureResult;
        IReadOnlyList<DeepMeasureRowViewModel> rows = Array.Empty<DeepMeasureRowViewModel>();
        IReadOnlyList<DeepMeasureStatRowViewModel> stats = Array.Empty<DeepMeasureStatRowViewModel>();
        if (result is { IsOk: true })
        {
            IEnumerable<CycleMeasurement> ordered = result.Cycles;
            var kind = SelectedScopeDeepMeasureSort?.Kind;
            bool desc = IsScopeDeepMeasureSortDescending;
            if (kind is CycleMeasureKind k)
            {
                // Cycles where the value could not be measured go last either way.
                ordered = desc
                    ? ordered.OrderBy(c => c.Get(k) is null).ThenByDescending(c => c.Get(k) ?? 0)
                    : ordered.OrderBy(c => c.Get(k) is null).ThenBy(c => c.Get(k) ?? 0);
            }
            else if (desc)
            {
                ordered = ordered.Reverse();
            }

            rows = ordered.Select(c => new DeepMeasureRowViewModel(
                c.Number,
                FormatDeep(c.StartSeconds, CycleMeasureKind.Period),
                FormatDeep(c.PeriodSeconds, CycleMeasureKind.Period),
                FormatDeep(c.FrequencyHz, CycleMeasureKind.Frequency),
                FormatDeep(c.DutyCyclePercent, CycleMeasureKind.DutyCycle),
                FormatDeep(c.MaximumVolts, CycleMeasureKind.Maximum),
                FormatDeep(c.MinimumVolts, CycleMeasureKind.Minimum),
                FormatDeep(c.PeakToPeakVolts, CycleMeasureKind.PeakToPeak),
                FormatDeep(c.RiseTimeSeconds, CycleMeasureKind.RiseTime),
                FormatDeep(c.FallTimeSeconds, CycleMeasureKind.FallTime))).ToArray();

            stats = result.Statistics.Select(s => new DeepMeasureStatRowViewModel(
                DeepMeasureName(s.Kind),
                FormatDeep(s.Minimum, s.Kind),
                FormatDeep(s.Maximum, s.Kind),
                FormatDeep(s.Mean, s.Kind),
                FormatDeep(s.StandardDeviation, s.Kind),
                s.Count == result.Cycles.Count
                    ? $"min #{s.MinimumCycle} · max #{s.MaximumCycle}"
                    : $"min #{s.MinimumCycle} · max #{s.MaximumCycle} ({s.Count} of {result.Cycles.Count})")).ToArray();
        }

        void ApplyNow()
        {
            ScopeDeepMeasureRows = rows;
            ScopeDeepMeasureStatRows = stats;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            _dispatcherQueue.TryEnqueue(ApplyNow);
        else
            ApplyNow();
    }

    public static string DeepMeasureName(CycleMeasureKind kind) => kind switch
    {
        CycleMeasureKind.Period => "Period",
        CycleMeasureKind.Frequency => "Frequency",
        CycleMeasureKind.DutyCycle => "Duty cycle",
        CycleMeasureKind.Maximum => "Maximum",
        CycleMeasureKind.Minimum => "Minimum",
        CycleMeasureKind.PeakToPeak => "Peak to peak",
        CycleMeasureKind.RiseTime => "Rise time",
        CycleMeasureKind.FallTime => "Fall time",
        _ => kind.ToString(),
    };

    /// <summary>Formats a DeepMeasure value with an SI prefix for its unit (s, Hz, V) or as a percentage; "—" when not measured.</summary>
    public static string FormatDeep(double? value, CycleMeasureKind kind)
    {
        if (value is not double v || !double.IsFinite(v))
            return "—";
        string unit = kind switch
        {
            CycleMeasureKind.Period or CycleMeasureKind.RiseTime or CycleMeasureKind.FallTime => "s",
            CycleMeasureKind.Frequency => "Hz",
            CycleMeasureKind.DutyCycle => "%",
            _ => "V",
        };
        var ci = CultureInfo.InvariantCulture;
        if (unit == "%")
            return string.Create(ci, $"{v:0.0} %");

        double a = Math.Abs(v);
        (double scale, string prefix) = a == 0 ? (1, "")
            : a >= 1e6 ? (1e-6, "M")
            : a >= 1e3 ? (1e-3, "k")
            : a >= 1 ? (1, "")
            : a >= 1e-3 ? (1e3, "m")
            : a >= 1e-6 ? (1e6, "µ")
            : (1e9, "n");
        if (unit == "V" && prefix is "k" or "M") (scale, prefix) = (1, ""); // scope voltages stay in V
        double s = v * scale;
        string number = Math.Abs(s) >= 100 ? s.ToString("0.0", ci) : Math.Abs(s) >= 10 ? s.ToString("0.00", ci) : s.ToString("0.000", ci);
        return $"{number} {prefix}{unit}";
    }
}
