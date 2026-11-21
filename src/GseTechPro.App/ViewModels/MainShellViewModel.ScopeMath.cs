using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10D.1 — one math channel ("M") on the scope screen: A+B, A−B, A×B, −A, low-pass, RPM from frequency.
/// Computed from the capture on screen (live or from the waveform buffer) without changing it, drawn as a fifth
/// trace with its own auto scale. Filter cutoff and pulses-per-revolution are entered by the technician.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Picker entry for the math operation.</summary>
    public sealed record MathOperationOption(MathOperation Operation, string Display)
    {
        public override string ToString() => Display;
    }

    public IReadOnlyList<MathOperationOption> ScopeMathOperations { get; } = new[]
    {
        new MathOperationOption(MathOperation.Sum, "A + B"),
        new MathOperationOption(MathOperation.Difference, "A − B (voltage drop)"),
        new MathOperationOption(MathOperation.Product, "A × B"),
        new MathOperationOption(MathOperation.Invert, "Invert (−A)"),
        new MathOperationOption(MathOperation.LowPass, "Low-pass filter"),
        new MathOperationOption(MathOperation.RpmFromFrequency, "RPM from frequency"),
    };

    public IReadOnlyList<int> ScopeMathChannels { get; } = new[] { 1, 2, 3, 4 };

    [ObservableProperty]
    private bool _isScopeMathEnabled;

    [ObservableProperty]
    private MathOperationOption? _selectedScopeMathOperation;

    [ObservableProperty]
    private int _scopeMathSourceA = 1;

    [ObservableProperty]
    private int _scopeMathSourceB = 2;

    /// <summary>Cutoff (Hz) for the filter or pulses per revolution for RPM. NaN until the technician enters it.</summary>
    [ObservableProperty]
    private double _scopeMathParameter = double.NaN;

    [ObservableProperty]
    private string _scopeMathStatusText = "Math channel off.";

    [ObservableProperty]
    private PointCollection? _scopeMathPoints;

    public IReadOnlyList<Point> ScopeMathRawPoints { get; private set; } = Array.Empty<Point>();

    /// <summary>Last computed math result (for tests and readouts).</summary>
    public MathChannelResult? ScopeMathResult { get; private set; }

    public Visibility ScopeMathTraceVisibility =>
        IsScopeMathEnabled && ScopeMathRawPoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeMathSourceBVisibility =>
        CurrentScopeMathDefinition().UsesSourceB ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeMathParameterVisibility =>
        CurrentScopeMathDefinition().UsesParameter ? Visibility.Visible : Visibility.Collapsed;

    public string ScopeMathParameterHeader => SelectedScopeMathOperation?.Operation switch
    {
        MathOperation.LowPass => "Cutoff frequency (Hz)",
        MathOperation.RpmFromFrequency => "Pulses per revolution (from the sensor / tone-wheel spec)",
        _ => "Parameter",
    };

    public MathChannelDefinition CurrentScopeMathDefinition() => new(
        SelectedScopeMathOperation?.Operation ?? MathOperation.Difference,
        ScopeMathSourceA,
        ScopeMathSourceB,
        ScopeMathParameter);

    partial void OnIsScopeMathEnabledChanged(bool value) => _ = RefreshScopeMathAsync();
    partial void OnSelectedScopeMathOperationChanged(MathOperationOption? value)
    {
        OnPropertyChanged(nameof(ScopeMathSourceBVisibility));
        OnPropertyChanged(nameof(ScopeMathParameterVisibility));
        OnPropertyChanged(nameof(ScopeMathParameterHeader));
        _ = RefreshScopeMathAsync();
    }
    partial void OnScopeMathSourceAChanged(int value) => _ = RefreshScopeMathAsync();
    partial void OnScopeMathSourceBChanged(int value) => _ = RefreshScopeMathAsync();
    partial void OnScopeMathParameterChanged(double value) => _ = RefreshScopeMathAsync();

    /// <summary>Recomputes and redraws the math trace for the capture on screen (off the UI thread).</summary>
    public async Task RefreshScopeMathAsync()
    {
        SelectedScopeMathOperation ??= ScopeMathOperations[1];
        var capture = LastScopeCapture;
        if (!IsScopeMathEnabled || capture is null)
        {
            ScopeMathResult = null;
            ScopeMathRawPoints = Array.Empty<Point>();
            ScopeMathStatusText = IsScopeMathEnabled ? "Math channel on: press Run to compute it." : "Math channel off.";
            ApplyScopeMathPoints();
            return;
        }

        var definition = CurrentScopeMathDefinition();
        double zoom = ScopeZoomFactor;
        double panNorm = Math.Clamp(ScopePanPercent / 100.0, 0.0, 1.0);

        var (result, points) = await Task.Run(() =>
        {
            var r = MathChannels.Compute(capture, definition);
            var pts = r.IsOk
                ? WaveformRenderEngine.DecimateChannelSamples(
                    r.Values, NiceRange(r.Values), WaveformRenderEngine.DefaultCanvasWidth,
                    WaveformRenderEngine.DefaultCanvasHeight, zoom, panNorm)
                : Array.Empty<Point>();
            return (r, pts);
        });

        if (!ReferenceEquals(capture, LastScopeCapture))
            return; // a newer capture arrived while computing; its own refresh will draw

        ScopeMathResult = result;
        ScopeMathRawPoints = points;
        ScopeMathStatusText = result.IsOk ? $"M = {result.Label}: {Summarize(result)}" : $"M = {result.Label}: {result.Error}";
        ApplyScopeMathPoints();
    }

    private void ApplyScopeMathPoints()
    {
        void ApplyNow()
        {
            try
            {
                ScopeMathPoints = ToPointCollection(ScopeMathRawPoints);
            }
            catch
            {
                // Headless test runner: raw points are still set.
            }
            OnPropertyChanged(nameof(ScopeMathTraceVisibility));
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            _dispatcherQueue.TryEnqueue(ApplyNow);
        else
            ApplyNow();
    }

    /// <summary>Full-scale ± range for drawing: the peak magnitude rounded up to 1, 2 or 5 × 10ⁿ.</summary>
    internal static double NiceRange(IReadOnlyList<double> values)
    {
        double peak = 0;
        foreach (var v in values)
            if (double.IsFinite(v) && Math.Abs(v) > peak) peak = Math.Abs(v);
        if (peak <= 0) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (m * exp >= peak) return m * exp;
        return 10 * exp;
    }

    private static string Summarize(MathChannelResult r)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        int n = 0;
        foreach (var v in r.Values)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            n++;
        }
        if (n == 0) return "no finite values";
        string F(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : "0.###", CultureInfo.InvariantCulture);
        return $"min {F(min)} {r.Unit} · max {F(max)} {r.Unit} · mean {F(sum / n)} {r.Unit} · scale ±{F(NiceRange(r.Values))} {r.Unit}";
    }
}
