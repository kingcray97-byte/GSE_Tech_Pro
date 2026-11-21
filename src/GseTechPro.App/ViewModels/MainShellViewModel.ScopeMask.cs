using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10D.1 — pass/fail mask on the scope screen. The technician captures a known-good waveform, enters a
/// voltage and time tolerance, and taps Create mask; every later capture on screen is tested against it
/// (off the UI thread) and the envelope is drawn over the channel. Pass/fail counts each new capture once.
/// The mask lives in memory only.
/// </summary>
public sealed partial class MainShellViewModel
{
    private readonly ConditionalWeakTable<ScopeCapture, object> _scopeMaskCounted = new();

    public IReadOnlyList<int> ScopeMaskChannels { get; } = new[] { 1, 2, 3, 4 };

    [ObservableProperty]
    private int _scopeMaskChannel = 1;

    /// <summary>Voltage tolerance (V) the technician enters; NaN until entered.</summary>
    [ObservableProperty]
    private double _scopeMaskVoltageTolerance = double.NaN;

    /// <summary>Time tolerance in horizontal divisions (0 = none).</summary>
    [ObservableProperty]
    private double _scopeMaskTimeToleranceDivisions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopeMask))]
    [NotifyCanExecuteChangedFor(nameof(ClearScopeMaskCommand))]
    private WaveformMask? _scopeMask;

    [ObservableProperty]
    private string _scopeMaskStatusText = "No mask. Capture a known-good waveform, enter tolerances, then Create mask.";

    [ObservableProperty]
    private int _scopeMaskPassCount;

    [ObservableProperty]
    private int _scopeMaskFailCount;

    [ObservableProperty]
    private PointCollection? _scopeMaskUpperPoints;

    [ObservableProperty]
    private PointCollection? _scopeMaskLowerPoints;

    public bool HasScopeMask => ScopeMask is not null;

    public MaskTestResult? LastScopeMaskResult { get; private set; }

    public IReadOnlyList<Point> ScopeMaskUpperRawPoints { get; private set; } = Array.Empty<Point>();

    public IReadOnlyList<Point> ScopeMaskLowerRawPoints { get; private set; } = Array.Empty<Point>();

    public Visibility ScopeMaskTraceVisibility =>
        ScopeMask is not null && ScopeMaskUpperRawPoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string ScopeMaskCountText => $"Pass {ScopeMaskPassCount} · Fail {ScopeMaskFailCount}";

    partial void OnScopeMaskPassCountChanged(int value) => OnPropertyChanged(nameof(ScopeMaskCountText));
    partial void OnScopeMaskFailCountChanged(int value) => OnPropertyChanged(nameof(ScopeMaskCountText));

    /// <summary>Builds the mask from the chosen channel of the capture on screen.</summary>
    [RelayCommand]
    public async Task CreateScopeMaskAsync()
    {
        var capture = LastScopeCapture;
        if (capture is null)
        {
            ScopeMaskStatusText = "Press Run to capture a known-good waveform first.";
            return;
        }
        if (!capture.Channels.TryGetValue(ScopeMaskChannel, out var samples))
        {
            ScopeMaskStatusText = $"Channel {MathChannelDefinition.ChannelName(ScopeMaskChannel)} is not in the capture on screen.";
            return;
        }

        double timeTolSeconds = (double.IsFinite(ScopeMaskTimeToleranceDivisions) ? ScopeMaskTimeToleranceDivisions : 0)
                                * capture.TimebaseSecondsPerDivision;
        double vTol = ScopeMaskVoltageTolerance;
        int channel = ScopeMaskChannel;
        try
        {
            var mask = await Task.Run(() => WaveformMask.Create(
                samples, capture.SampleRateHz, channel, vTol, timeTolSeconds, capture.IsSimulated));
            ScopeMask = mask;
            ScopeMaskPassCount = 0;
            ScopeMaskFailCount = 0;
            _scopeMaskCounted.AddOrUpdate(capture, this); // the capture it was made from is not counted
            await RefreshScopeMaskAsync();
        }
        catch (ArgumentException ex)
        {
            ScopeMaskStatusText = ex.Message.Split(" (Parameter", 2)[0];
        }
    }

    private bool CanClearScopeMask() => ScopeMask is not null;

    [RelayCommand(CanExecute = nameof(CanClearScopeMask))]
    public void ClearScopeMask()
    {
        ScopeMask = null;
        LastScopeMaskResult = null;
        ScopeMaskPassCount = 0;
        ScopeMaskFailCount = 0;
        ScopeMaskUpperRawPoints = Array.Empty<Point>();
        ScopeMaskLowerRawPoints = Array.Empty<Point>();
        ScopeMaskStatusText = "Mask cleared.";
        ApplyScopeMaskPoints();
    }

    /// <summary>Tests the capture on screen against the mask and redraws the envelope (off the UI thread).</summary>
    public async Task RefreshScopeMaskAsync()
    {
        var mask = ScopeMask;
        var capture = LastScopeCapture;
        if (mask is null || capture is null)
        {
            ScopeMaskUpperRawPoints = Array.Empty<Point>();
            ScopeMaskLowerRawPoints = Array.Empty<Point>();
            ApplyScopeMaskPoints();
            return;
        }

        var tile = ScopeChannels.FirstOrDefault(c => c.Channel == mask.Channel);
        double range = Math.Max(0.1, tile?.VoltageRangeVolts ?? 5.0);
        double zoom = ScopeZoomFactor;
        double panNorm = Math.Clamp(ScopePanPercent / 100.0, 0.0, 1.0);

        var (result, upper, lower) = await Task.Run(() =>
        {
            var r = mask.Test(capture);
            // Draw on the same scale as the channel trace: its range, widened only if the trace itself goes beyond it.
            double scale = range;
            if (capture.Channels.TryGetValue(mask.Channel, out var s))
                foreach (var v in s)
                    if (double.IsFinite(v) && Math.Abs(v) > scale) scale = Math.Abs(v);
            double[] Clip(double[] e) => e.Select(v => Math.Clamp(v, -scale, scale)).ToArray();
            var up = WaveformRenderEngine.DecimateChannelSamples(Clip(mask.Upper), scale,
                WaveformRenderEngine.DefaultCanvasWidth, WaveformRenderEngine.DefaultCanvasHeight, zoom, panNorm);
            var lo = WaveformRenderEngine.DecimateChannelSamples(Clip(mask.Lower), scale,
                WaveformRenderEngine.DefaultCanvasWidth, WaveformRenderEngine.DefaultCanvasHeight, zoom, panNorm);
            return (r, up, lo);
        });

        if (!ReferenceEquals(capture, LastScopeCapture) || !ReferenceEquals(mask, ScopeMask))
            return; // superseded while computing

        LastScopeMaskResult = result;
        ScopeMaskUpperRawPoints = upper;
        ScopeMaskLowerRawPoints = lower;

        bool newlyFailed = false;
        if (result.Passed is bool passed && !_scopeMaskCounted.TryGetValue(capture, out _))
        {
            _scopeMaskCounted.AddOrUpdate(capture, this);
            if (passed) ScopeMaskPassCount++;
            else ScopeMaskFailCount++;
            newlyFailed = !passed;
        }

        string provenance = capture.IsSimulated || mask.FromSimulatedCapture ? " (simulated)" : "";
        ScopeMaskStatusText = result.Message + provenance;
        ApplyScopeMaskPoints();
        if (newlyFailed)
            await RunScopeMaskFailActionsAsync(result);
    }

    private void ApplyScopeMaskPoints()
    {
        void ApplyNow()
        {
            try
            {
                ScopeMaskUpperPoints = ToPointCollection(ToScopeLanePoints(ScopeMaskUpperRawPoints, ScopeMask?.Channel));
                ScopeMaskLowerPoints = ToPointCollection(ToScopeLanePoints(ScopeMaskLowerRawPoints, ScopeMask?.Channel));
            }
            catch
            {
                // Headless test runner: raw points are still set.
            }
            OnPropertyChanged(nameof(ScopeMaskTraceVisibility));
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            _dispatcherQueue.TryEnqueue(ApplyNow);
        else
            ApplyNow();
    }
}
