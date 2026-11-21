using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace GseTechPro.App.ViewModels;

/// <summary>Choice in the Views menu.</summary>
public sealed record SelectableScopeViewLayout(ScopeViewLayout Layout, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;

    public static IReadOnlyList<SelectableScopeViewLayout> All { get; } =
    [
        new(ScopeViewLayout.Overlay, "One view (channels overlaid)"),
        new(ScopeViewLayout.Stacked, "Stacked (one view per channel)"),
    ];
}

/// <summary>
/// Multi-view layouts (Phase 10D.1k). Overlay draws every trace over the full graticule; Stacked
/// gives each enabled channel (and the math channel M) its own horizontal view with its own
/// heading and range, all sharing the time axis, rulers and zoom. Display only: the capture,
/// measurements, masks and decoders are unchanged.
/// </summary>
public sealed partial class MainShellViewModel
{
    public IReadOnlyList<SelectableScopeViewLayout> ScopeViewLayouts => SelectableScopeViewLayout.All;

    [ObservableProperty]
    private SelectableScopeViewLayout _selectedScopeViewLayout = SelectableScopeViewLayout.All[0];

    /// <summary>Channel the reference overlay is drawn for (its lane when stacked).</summary>
    private int? _scopeReferenceOverlayChannel;

    public bool IsScopeStacked => SelectedScopeViewLayout?.Layout == ScopeViewLayout.Stacked;

    /// <summary>Lanes top to bottom (channel 1–4, 0 = math); empty in overlay.</summary>
    public IReadOnlyList<int> ScopeViewLaneKeys => ScopeViewLanes.Build(
        SelectedScopeViewLayout?.Layout ?? ScopeViewLayout.Overlay,
        ScopeChannels.Where(c => c.IsEnabled).Select(c => c.Channel),
        includeMath: IsScopeMathEnabled && ScopeMathRawPoints.Count > 0);

    public Visibility ScopeLaneOverlayVisibility =>
        IsScopeStacked && ScopeViewLaneKeys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public GridLength ScopeLane1Height => LaneHeight(1);
    public GridLength ScopeLane2Height => LaneHeight(2);
    public GridLength ScopeLane3Height => LaneHeight(3);
    public GridLength ScopeLane4Height => LaneHeight(4);
    public GridLength ScopeLaneMathHeight => LaneHeight(ScopeViewLanes.MathLane);

    public string ScopeLane1Header => LaneHeader(1);
    public string ScopeLane2Header => LaneHeader(2);
    public string ScopeLane3Header => LaneHeader(3);
    public string ScopeLane4Header => LaneHeader(4);
    public string ScopeLaneMathHeader => "M (own scale)";

    /// <summary>Status line for the Views tile.</summary>
    public string ScopeViewsStatusText => IsScopeStacked
        ? ScopeViewLaneKeys.Count switch
        {
            0 => "Stacked: turn on a channel to see it.",
            1 => "Stacked: 1 view.",
            var n => $"Stacked: {n} views, one per trace, sharing the time axis.",
        }
        : "One view: all channels drawn over the full graticule.";

    private GridLength LaneHeight(int key) =>
        IsScopeStacked && ScopeViewLaneKeys.Contains(key) ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    private string LaneHeader(int channel)
    {
        var ch = ScopeChannels.FirstOrDefault(c => c.Channel == channel);
        if (ch is null)
            return "";
        if (ch.PressureScaling is { } ps)
            return $"{ch.DisplayName}  {ch.RangeDisplay} ({ps.ZeroOutputVolts:0.###}–{ps.FullScaleOutputVolts:0.###} V)";
        return ch.CurrentScaleAmpsPerVolt > 0.0
            ? string.Create(CultureInfo.InvariantCulture, $"{ch.DisplayName}  ±{ch.VoltageRangeVolts * ch.CurrentScaleAmpsPerVolt:G4} A")
            : string.Create(CultureInfo.InvariantCulture, $"{ch.DisplayName}  ±{ch.VoltageRangeVolts:G4} V");
    }

    /// <summary>
    /// Points drawn for the full plot, moved into the lane of <paramref name="laneKey"/> when
    /// stacked (unchanged in overlay, or when that lane is not shown).
    /// </summary>
    public IReadOnlyList<Point> ToScopeLanePoints(IReadOnlyList<Point> raw, int? laneKey)
    {
        if (!IsScopeStacked || laneKey is null || raw.Count == 0)
            return raw;
        var lanes = ScopeViewLaneKeys;
        int lane = -1;
        for (int i = 0; i < lanes.Count; i++)
        {
            if (lanes[i] == laneKey.Value)
            {
                lane = i;
                break;
            }
        }
        if (lane < 0)
            return raw;

        double height = WaveformRenderEngine.DefaultCanvasHeight;
        var mapped = new Point[raw.Count];
        for (int i = 0; i < raw.Count; i++)
            mapped[i] = new Point(raw[i].X, ScopeViewLanes.MapY(raw[i].Y, lane, lanes.Count, height));
        return mapped;
    }

    partial void OnSelectedScopeViewLayoutChanged(SelectableScopeViewLayout value) => OnScopeLanesChanged();

    /// <summary>Re-lays out the lanes (layout, enabled channels, ranges, labels or math changed).</summary>
    private void OnScopeLanesChanged()
    {
        OnPropertyChanged(nameof(IsScopeStacked));
        OnPropertyChanged(nameof(ScopeViewLaneKeys));
        OnPropertyChanged(nameof(ScopeLaneOverlayVisibility));
        OnPropertyChanged(nameof(ScopeLane1Height));
        OnPropertyChanged(nameof(ScopeLane2Height));
        OnPropertyChanged(nameof(ScopeLane3Height));
        OnPropertyChanged(nameof(ScopeLane4Height));
        OnPropertyChanged(nameof(ScopeLaneMathHeight));
        OnPropertyChanged(nameof(ScopeLane1Header));
        OnPropertyChanged(nameof(ScopeLane2Header));
        OnPropertyChanged(nameof(ScopeLane3Header));
        OnPropertyChanged(nameof(ScopeLane4Header));
        OnPropertyChanged(nameof(ScopeViewsStatusText));
        OnPropertyChanged(nameof(ScopeLeftAxisVisibility));
        OnPropertyChanged(nameof(ScopeRightAxisVisibility));
        ApplyAllScopeTracePoints();
    }

    /// <summary>Builds every trace's point collection from its raw points, in its lane.</summary>
    private void ApplyAllScopeTracePoints()
    {
        void ApplyNow()
        {
            try
            {
                ScopeChannel1Points = ToPointCollection(ToScopeLanePoints(ScopeChannel1RawPoints, 1));
                ScopeChannel2Points = ToPointCollection(ToScopeLanePoints(ScopeChannel2RawPoints, 2));
                ScopeChannel3Points = ToPointCollection(ToScopeLanePoints(ScopeChannel3RawPoints, 3));
                ScopeChannel4Points = ToPointCollection(ToScopeLanePoints(ScopeChannel4RawPoints, 4));
                ScopeReferenceOverlayPoints = ToPointCollection(
                    ToScopeLanePoints(ScopeReferenceOverlayRawPoints, _scopeReferenceOverlayChannel));
                ScopeMathPoints = ToPointCollection(ToScopeLanePoints(ScopeMathRawPoints, ScopeViewLanes.MathLane));
                ScopeMaskUpperPoints = ToPointCollection(ToScopeLanePoints(ScopeMaskUpperRawPoints, ScopeMask?.Channel));
                ScopeMaskLowerPoints = ToPointCollection(ToScopeLanePoints(ScopeMaskLowerRawPoints, ScopeMask?.Channel));
            }
            catch
            {
                // Headless xUnit test runner without a WinUI XAML apartment: raw points are still set.
            }
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            _dispatcherQueue.TryEnqueue(ApplyNow);
        else
            ApplyNow();
    }
}
