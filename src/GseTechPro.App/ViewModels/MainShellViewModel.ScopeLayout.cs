using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Bench-scope layout state for the Scope screen: Run/Stop button, timebase and trigger
/// steppers, graticule axis labels (CH A left, CH B right, time along the bottom) and the
/// bottom tool panel (Measurements, Rulers, Waveform library, Guided test).
/// </summary>
public sealed partial class MainShellViewModel
{
    public const string ScopePanelNone = "None";
    public const string ScopePanelMeasurements = "Measurements";
    public const string ScopePanelRulers = "Rulers";
    public const string ScopePanelLibrary = "Library";
    public const string ScopePanelGuided = "Guided";

    /// <summary>Number of graticule divisions on each axis.</summary>
    public const int ScopeDivisions = 10;

    private string? _lastScopeSectionKey;

    // ---------------------------------------------------------------------
    // Run / Stop
    // ---------------------------------------------------------------------

    public Visibility ScopeRunButtonVisibility =>
        IsScopeCapturing || IsScopeRepeatRunning ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ScopeStopButtonVisibility =>
        IsScopeCapturing || IsScopeRepeatRunning ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsScopeCapturingChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeRunButtonVisibility));
        OnPropertyChanged(nameof(ScopeStopButtonVisibility));
    }

    // ---------------------------------------------------------------------
    // Timebase stepper
    // ---------------------------------------------------------------------

    [RelayCommand]
    public void ScopeTimebaseStepUp()
    {
        var next = ScopeTimebaseOptions
            .Select(o => o.SecondsPerDivision)
            .Where(s => s > ScopeTimebaseSecondsPerDivision + 1e-12)
            .DefaultIfEmpty(0.0)
            .Min();
        if (next > 0.0)
            ScopeTimebaseSecondsPerDivision = next;
    }

    [RelayCommand]
    public void ScopeTimebaseStepDown()
    {
        var lower = ScopeTimebaseOptions
            .Select(o => o.SecondsPerDivision)
            .Where(s => s < ScopeTimebaseSecondsPerDivision - 1e-12)
            .DefaultIfEmpty(0.0)
            .Max();
        if (lower > 0.0)
            ScopeTimebaseSecondsPerDivision = lower;
    }

    /// <summary>Short timebase readout for the toolbar ("5 ms/div").</summary>
    public string ScopeTimebaseShortDisplay =>
        FormatAxisTime(ScopeTimebaseSecondsPerDivision, ScopeTimebaseSecondsPerDivision) + "/div";

    // ---------------------------------------------------------------------
    // Trigger stepper
    // ---------------------------------------------------------------------

    /// <summary>One trigger-level step: a tenth of the trigger channel's vertical range.</summary>
    public double ScopeTriggerLevelStepVolts
    {
        get
        {
            var ch = ScopeChannels.FirstOrDefault(c => c.Channel == ScopeTriggerChannel);
            double range = ch?.VoltageRangeVolts ?? 5.0;
            return Math.Max(0.01, range / ScopeDivisions);
        }
    }

    [RelayCommand]
    public void ScopeTriggerLevelUp() =>
        ScopeTriggerLevelVolts = Math.Round(ScopeTriggerLevelVolts + ScopeTriggerLevelStepVolts, 4);

    [RelayCommand]
    public void ScopeTriggerLevelDown() =>
        ScopeTriggerLevelVolts = Math.Round(ScopeTriggerLevelVolts - ScopeTriggerLevelStepVolts, 4);

    public string ScopeTriggerLevelDisplay =>
        ScopeTriggerMode == ScopeTriggerMode.None
            ? "Auto"
            : string.Create(CultureInfo.InvariantCulture, $"{ScopeTriggerLevelVolts:0.###} V");

    public string ScopeTriggerSummary
    {
        get
        {
            string letter = ((char)('A' + Math.Clamp(ScopeTriggerChannel, 1, 4) - 1)).ToString();
            return ScopeTriggerMode switch
            {
                ScopeTriggerMode.Rising => $"{letter} · Rising edge",
                ScopeTriggerMode.Falling => $"{letter} · Falling edge",
                _ => "Free run",
            };
        }
    }

    partial void OnScopeTriggerLevelVoltsChanged(double value) =>
        OnPropertyChanged(nameof(ScopeTriggerLevelDisplay));

    partial void OnScopeTriggerModeChanged(ScopeTriggerMode value)
    {
        OnPropertyChanged(nameof(ScopeTriggerLevelDisplay));
        OnPropertyChanged(nameof(ScopeTriggerSummary));
    }

    partial void OnScopeTriggerChannelChanged(int value) =>
        OnPropertyChanged(nameof(ScopeTriggerSummary));

    // ---------------------------------------------------------------------
    // Graticule axis labels
    // ---------------------------------------------------------------------

    /// <summary>11 labels, top to bottom, for CH A's range on the left edge.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _scopeLeftAxisLabels = BuildVoltageAxis(5.0);

    /// <summary>11 labels, top to bottom, for CH B's range on the right edge.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _scopeRightAxisLabels = BuildVoltageAxis(5.0);

    /// <summary>11 labels, left to right, for the visible time window.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _scopeTimeAxisLabels = BuildTimeAxis(0.0, 0.05);

    [ObservableProperty]
    private string _scopeLeftAxisUnit = "V";

    [ObservableProperty]
    private string _scopeRightAxisUnit = "V";

    [ObservableProperty]
    private string _scopeTimeAxisUnit = "ms";

    public Visibility ScopeLeftAxisVisibility =>
        !IsScopeStacked && ScopeChannels.Count > 0 && ScopeChannels[0].IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeRightAxisVisibility =>
        !IsScopeStacked && ScopeChannels.Count > 1 && ScopeChannels[1].IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Rebuilds the axis labels from CH A / CH B ranges and the visible time window.</summary>
    public void RebuildScopeAxisLabels()
    {
        if (ScopeChannels.Count >= 2)
        {
            var a = ScopeChannels[0];
            var b = ScopeChannels[1];
            ScopeLeftAxisLabels = BuildChannelAxis(a);
            ScopeLeftAxisUnit = a.CurrentScaleAmpsPerVolt > 0.0 ? "A" : "V";
            ScopeRightAxisLabels = BuildChannelAxis(b);
            ScopeRightAxisUnit = b.CurrentScaleAmpsPerVolt > 0.0 ? "A" : "V";
        }

        OnPropertyChanged(nameof(ScopeLeftAxisVisibility));
        OnPropertyChanged(nameof(ScopeRightAxisVisibility));
        OnPropertyChanged(nameof(ScopeTriggerLevelDisplay));
        OnPropertyChanged(nameof(ScopeTimebaseShortDisplay));

        double start;
        double span;
        var capture = LastScopeCapture;
        if (capture is not null && capture.SampleCount > 1 && capture.SampleRateHz > 0.0)
        {
            var (startIdx, count) = WaveformRenderEngine.GetVisibleWindow(
                capture.SampleCount,
                ScopeZoomFactor,
                Math.Clamp(ScopePanPercent / 100.0, 0.0, 1.0));
            start = startIdx / capture.SampleRateHz;
            span = Math.Max(1, count - 1) / capture.SampleRateHz;
        }
        else
        {
            start = 0.0;
            span = ScopeTimebaseSecondsPerDivision * ScopeDivisions / Math.Max(1.0, ScopeZoomFactor);
        }

        ScopeTimeAxisLabels = BuildTimeAxis(start, span);
        ScopeTimeAxisUnit = TimeUnitFor(span / ScopeDivisions);
    }

    private static IReadOnlyList<string> BuildChannelAxis(ScopeChannelSetupViewModel ch)
    {
        double fullScale = Math.Max(0.1, ch.VoltageRangeVolts);
        if (ch.CurrentScaleAmpsPerVolt > 0.0)
            fullScale *= ch.CurrentScaleAmpsPerVolt;
        return BuildVoltageAxis(fullScale);
    }

    /// <summary>Labels from +fullScale down to −fullScale in 10 equal divisions.</summary>
    public static IReadOnlyList<string> BuildVoltageAxis(double fullScale)
    {
        var labels = new string[ScopeDivisions + 1];
        for (int i = 0; i <= ScopeDivisions; i++)
        {
            double v = fullScale - i * (2.0 * fullScale / ScopeDivisions);
            if (Math.Abs(v) < fullScale * 1e-9)
                v = 0.0;
            labels[i] = FormatAxisNumber(v, fullScale);
        }
        return labels;
    }

    /// <summary>Labels from <paramref name="start"/> to start + span in 10 equal divisions.</summary>
    public static IReadOnlyList<string> BuildTimeAxis(double start, double span)
    {
        double perDiv = span / ScopeDivisions;
        double scale = TimeScaleFor(perDiv);
        var labels = new string[ScopeDivisions + 1];
        for (int i = 0; i <= ScopeDivisions; i++)
        {
            double t = (start + i * perDiv) * scale;
            labels[i] = FormatAxisNumber(t, perDiv * scale * ScopeDivisions);
        }
        return labels;
    }

    private static double TimeScaleFor(double perDivSeconds) =>
        perDivSeconds < 1e-3 ? 1e6 : perDivSeconds < 1.0 ? 1e3 : 1.0;

    private static string TimeUnitFor(double perDivSeconds) =>
        perDivSeconds < 1e-3 ? "µs" : perDivSeconds < 1.0 ? "ms" : "s";

    private static string FormatAxisTime(double seconds, double perDivSeconds)
    {
        double scaled = seconds * TimeScaleFor(perDivSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{scaled:0.###} {TimeUnitFor(perDivSeconds)}");
    }

    private static string FormatAxisNumber(double value, double span)
    {
        string format = Math.Abs(span) >= 20.0 ? "0.0" : Math.Abs(span) >= 2.0 ? "0.0#" : "0.0##";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------
    // Bottom tool panel
    // ---------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeBottomPanelVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeMeasurementsPanelVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeRulersPanelVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeLibraryPanelVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeGuidedPanelVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeRulerLinesVisibility))]
    private string _scopeBottomPanel = ScopePanelNone;

    public Visibility ScopeBottomPanelVisibility =>
        ScopeBottomPanel == ScopePanelNone ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ScopeMeasurementsPanelVisibility => PanelVisibility(ScopePanelMeasurements);
    public Visibility ScopeRulersPanelVisibility => PanelVisibility(ScopePanelRulers);
    public Visibility ScopeLibraryPanelVisibility => PanelVisibility(ScopePanelLibrary);
    public Visibility ScopeGuidedPanelVisibility => PanelVisibility(ScopePanelGuided);

    /// <summary>Cursor lines are drawn on the graticule only while the Rulers panel is open.</summary>
    public Visibility ScopeRulerLinesVisibility => PanelVisibility(ScopePanelRulers);

    private Visibility PanelVisibility(string key) =>
        ScopeBottomPanel == key ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Opens the named bottom panel, or closes it when it is already open.</summary>
    [RelayCommand]
    public void ToggleScopePanel(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == ScopePanelNone)
        {
            ScopeBottomPanel = ScopePanelNone;
            return;
        }

        ScopeBottomPanel = ScopeBottomPanel == key ? ScopePanelNone : key;
    }

    /// <summary>Opens the panel that matches a Scope sidebar page when the page changes.</summary>
    private void SyncScopePanelToSection()
    {
        var key = ActiveSectionKey;
        if (key == _lastScopeSectionKey)
            return;
        _lastScopeSectionKey = key;

        switch (key)
        {
            case "GuidedTests":
                ScopeBottomPanel = ScopePanelGuided;
                break;
            case "RefWaveforms":
                ScopeBottomPanel = ScopePanelLibrary;
                break;
        }
    }
}
