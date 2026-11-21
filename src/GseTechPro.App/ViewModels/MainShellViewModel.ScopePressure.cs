using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Pressure transducer channels on the Scope screen (Phase 10D.4). A channel with a valid scaling
/// (entered from the transducer's published spec) shows pressure on its axis, tile, stacked-view
/// heading and rulers, and a min / max / mean pressure readout for the capture on screen. Samples,
/// measurements, masks and saved files stay in volts; the readout is a measurement, never a verdict.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>One line per pressure channel ("A Lift pressure: min 0 · max 2010 · mean 980 psi"), or "".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopePressureReadoutVisibility))]
    private string _scopePressureReadoutText = "";

    public Visibility ScopePressureReadoutVisibility =>
        string.IsNullOrEmpty(ScopePressureReadoutText) ? Visibility.Collapsed : Visibility.Visible;

    private object? _scopePressureReadoutCapture;
    private string? _scopePressureReadoutKey;

    /// <summary>Rebuilds the readout from the capture on screen and the channels' scalings (skipped when neither changed, e.g. zoom/pan).</summary>
    public void UpdateScopePressureReadout()
    {
        var capture = LastScopeCapture;
        string key = string.Join("|", ScopeChannels.Select(c => $"{c.IsEnabled}:{c.DisplayName}:{c.PressureScaling}"));
        if (ReferenceEquals(capture, _scopePressureReadoutCapture) && key == _scopePressureReadoutKey)
            return;
        _scopePressureReadoutCapture = capture;
        _scopePressureReadoutKey = key;

        var lines = new List<string>();
        foreach (var ch in ScopeChannels)
        {
            if (!ch.IsEnabled || ch.PressureScaling is not { } scaling)
                continue;
            if (capture is null || !capture.Channels.TryGetValue(ch.Channel, out var samples))
            {
                lines.Add($"{ch.DisplayName}: pressure {PressureTransducer.Describe(scaling)} · no capture yet");
                continue;
            }
            var summary = PressureTransducer.Summarize(scaling, samples);
            lines.Add($"{ch.DisplayName}: {summary.Text}{(capture.IsSimulated ? " (simulated)" : "")}");
        }
        ScopePressureReadoutText = string.Join(Environment.NewLine, lines);
    }

    /// <summary>"  (1250 psi)" for a pressure channel, "  (2.50 A)" for a current clamp, else "".</summary>
    private static string ScaledValueSuffix(ScopeChannelSetupViewModel ch, double volts)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        if (ch.PressureScaling is { } s)
            return $" ({PressureTransducer.Format(PressureTransducer.ToPressure(s, volts))} {s.Unit})";
        return ch.CurrentScaleAmpsPerVolt > 0.0
            ? string.Create(c, $" ({volts * ch.CurrentScaleAmpsPerVolt:F2} A)")
            : "";
    }
}
