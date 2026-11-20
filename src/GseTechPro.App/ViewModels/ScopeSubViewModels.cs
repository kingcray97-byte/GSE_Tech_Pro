using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Discovered oscilloscope device option in the Scope Device Picker (ADR-011 §3).
/// Always includes the built-in Simulator entry (<c>IsSimulated = true</c>) alongside any
/// discovered PicoScope or Hantek 6074BE hardware.
/// </summary>
public sealed class SelectableScopeDeviceItem
{
    public SelectableScopeDeviceItem(ScopeDeviceInfo device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public ScopeDeviceInfo Device { get; }

    public string Id => Device.Id;

    public string Name => Device.Name;

    public ScopeDeviceKind Kind => Device.Kind;

    public string KindCode => DbEnum.ToCode(Device.Kind);

    public string KindLabel => Device.Kind switch
    {
        ScopeDeviceKind.PicoScope => "PICOSCOPE",
        ScopeDeviceKind.Hantek => "HANTEK 6074BE",
        _ => "SIMULATOR",
    };

    public int ChannelCount => Device.ChannelCount;

    public string? Serial => Device.Serial;

    public bool IsSimulated => Device.IsSimulated;

    public string ProvenanceBadgeText => Device.IsSimulated ? "SIMULATED" : "REAL HARDWARE";

    public Visibility SimulatedBadgeVisibility =>
        Device.IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RealBadgeVisibility =>
        !Device.IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    public string DisplayLabel =>
        $"{Device.Name} [{KindLabel} · {Device.ChannelCount} CH] — {(Device.IsSimulated ? "SIMULATED" : "REAL")}";

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Per-channel setup view model (CH1–CH4) for the Scope Channel Setup Bar (ADR-011 §3).
/// Controls enable state, vertical voltage range, coupling (DC/AC), probe multiplier, and
/// current-clamp scale (A/V).
/// </summary>
public sealed partial class ScopeChannelSetupViewModel : ObservableObject
{
    public static IReadOnlyList<double> StandardVoltageRanges { get; } =
    [
        0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0,
    ];

    public static IReadOnlyList<ScopeCoupling> AvailableCouplings { get; } =
    [
        ScopeCoupling.Dc,
        ScopeCoupling.Ac,
    ];

    public static IReadOnlyList<double> StandardProbeMultipliers { get; } =
    [
        1.0, 10.0, 20.0, 100.0,
    ];

    public static IReadOnlyList<double> StandardClampScales { get; } =
    [
        0.0, 1.0, 10.0, 20.0, 60.0, 100.0, 200.0,
    ];

    public event EventHandler? ConfigurationChanged;

    public ScopeChannelSetupViewModel(int channel, bool isEnabled = false)
    {
        if (channel < 1 || channel > 4)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 1..4.");

        Channel = channel;
        _isEnabled = isEnabled;
    }

    /// <summary>1-based channel index (1..4).</summary>
    public int Channel { get; }

    public string ChannelLabel => $"CH{Channel}";

    public IReadOnlyList<double> VoltageRanges => StandardVoltageRanges;

    public IReadOnlyList<ScopeCoupling> Couplings => AvailableCouplings;

    public IReadOnlyList<double> ProbeMultipliers => StandardProbeMultipliers;

    public IReadOnlyList<double> ClampScales => StandardClampScales;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(EnabledOpacity))]
    [NotifyPropertyChangedFor(nameof(RangeDisplay))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(RangeDisplay))]
    private double _voltageRangeVolts = 5.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(DetailDisplay))]
    private ScopeCoupling _coupling = ScopeCoupling.Dc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(DetailDisplay))]
    private double _probeMultiplier = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(HasCurrentClamp))]
    [NotifyPropertyChangedFor(nameof(RangeDisplay))]
    [NotifyPropertyChangedFor(nameof(DetailDisplay))]
    private double _currentScaleAmpsPerVolt;

    public double EnabledOpacity => IsEnabled ? 1.0 : 0.65;

    /// <summary>Compact range readout for the scope channel tile ("±5 V", "±20 A", or "Off").</summary>
    public string RangeDisplay
    {
        get
        {
            if (!IsEnabled)
                return "Off";
            return CurrentScaleAmpsPerVolt > 0.0
                ? string.Create(CultureInfo.InvariantCulture, $"±{VoltageRangeVolts * CurrentScaleAmpsPerVolt:G4} A")
                : string.Create(CultureInfo.InvariantCulture, $"±{VoltageRangeVolts:G4} V");
        }
    }

    /// <summary>Coupling and probe line for the scope channel tile ("DC x1", "AC 20 A/V").</summary>
    public string DetailDisplay
    {
        get
        {
            var couplingText = Coupling == ScopeCoupling.Ac ? "AC" : "DC";
            return CurrentScaleAmpsPerVolt > 0.0
                ? string.Create(CultureInfo.InvariantCulture, $"{couplingText} {CurrentScaleAmpsPerVolt:G4} A/V")
                : string.Create(CultureInfo.InvariantCulture, $"{couplingText} x{ProbeMultiplier:G4}");
        }
    }

    /// <summary>Single-letter channel name used on the tile and axis headers (A–D).</summary>
    public string ChannelLetter => ((char)('A' + Channel - 1)).ToString();

    /// <summary>Steps the vertical range up one standard step; turns the channel on if it was off.</summary>
    [RelayCommand]
    public void RangeUp()
    {
        if (!IsEnabled)
        {
            IsEnabled = true;
            return;
        }

        var next = StandardVoltageRanges.FirstOrDefault(r => r > VoltageRangeVolts + 1e-9);
        if (next > 0.0)
            VoltageRangeVolts = next;
    }

    /// <summary>Steps the vertical range down one standard step; below the smallest range turns the channel off.</summary>
    [RelayCommand]
    public void RangeDown()
    {
        if (!IsEnabled)
            return;

        var lower = StandardVoltageRanges.LastOrDefault(r => r < VoltageRangeVolts - 1e-9);
        if (lower > 0.0)
            VoltageRangeVolts = lower;
        else
            IsEnabled = false;
    }

    [RelayCommand]
    public void ToggleCoupling() =>
        Coupling = Coupling == ScopeCoupling.Ac ? ScopeCoupling.Dc : ScopeCoupling.Ac;

    public bool HasCurrentClamp => CurrentScaleAmpsPerVolt > 0.0;

    public string SummaryText
    {
        get
        {
            if (!IsEnabled)
                return $"{ChannelLabel}: OFF";

            var couplingText = Coupling == ScopeCoupling.Ac ? "AC" : "DC";
            var clampText = CurrentScaleAmpsPerVolt > 0.0
                ? string.Create(CultureInfo.InvariantCulture, $" · {CurrentScaleAmpsPerVolt:G4} A/V")
                : string.Create(CultureInfo.InvariantCulture, $" · {ProbeMultiplier:G4}x");
            return string.Create(CultureInfo.InvariantCulture, $"{ChannelLabel}: ±{VoltageRangeVolts:G4} V · {couplingText}{clampText}");
        }
    }

    partial void OnIsEnabledChanged(bool value) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    partial void OnVoltageRangeVoltsChanged(double value) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    partial void OnCouplingChanged(ScopeCoupling value) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    partial void OnProbeMultiplierChanged(double value) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    partial void OnCurrentScaleAmpsPerVoltChanged(double value) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);

    public ScopeChannelConfig ToChannelConfig() => new()
    {
        Channel = Channel,
        VoltageRangeVolts = VoltageRangeVolts > 0.0 && double.IsFinite(VoltageRangeVolts) ? VoltageRangeVolts : 5.0,
        Coupling = Coupling,
        ProbeMultiplier = ProbeMultiplier > 0.0 && double.IsFinite(ProbeMultiplier) ? ProbeMultiplier : 1.0,
        CurrentScaleAmpsPerVolt = CurrentScaleAmpsPerVolt >= 0.0 && double.IsFinite(CurrentScaleAmpsPerVolt) ? CurrentScaleAmpsPerVolt : 0.0,
    };

    public void ApplyConfig(ScopeChannelConfig config, bool enable = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        IsEnabled = enable;
        VoltageRangeVolts = config.VoltageRangeVolts > 0.0 ? config.VoltageRangeVolts : 5.0;
        Coupling = config.Coupling;
        ProbeMultiplier = config.ProbeMultiplier > 0.0 ? config.ProbeMultiplier : 1.0;
        CurrentScaleAmpsPerVolt = config.CurrentScaleAmpsPerVolt >= 0.0 ? config.CurrentScaleAmpsPerVolt : 0.0;
    }
}

/// <summary>
/// Human-readable timebase (seconds/division) option for the Scope Timebase picker.
/// </summary>
public sealed record SelectableTimebaseOption(double SecondsPerDivision, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;

    public static IReadOnlyList<SelectableTimebaseOption> StandardOptions { get; } =
    [
        new(0.00005, "50 µs/div (CAN 250 kbps)"),
        new(0.0001, "100 µs/div"),
        new(0.0005, "500 µs/div"),
        new(0.001, "1 ms/div (Injector)"),
        new(0.002, "2 ms/div"),
        new(0.005, "5 ms/div (Crank CKP)"),
        new(0.010, "10 ms/div (Ripple / Solenoid)"),
        new(0.020, "20 ms/div"),
        new(0.050, "50 ms/div"),
        new(0.100, "100 ms/div (Starter Inrush)"),
        new(0.200, "200 ms/div"),
        new(0.500, "500 ms/div"),
    ];
}

/// <summary>
/// Human-readable sample rate (Hz) option for the Scope Sample Rate picker.
/// </summary>
public sealed record SelectableSampleRateOption(int SampleRateHz, string DisplayLabel)
{
    public override string ToString() => DisplayLabel;

    public static IReadOnlyList<SelectableSampleRateOption> StandardOptions { get; } =
    [
        new(10_000, "10 kS/s"),
        new(50_000, "50 kS/s"),
        new(100_000, "100 kS/s"),
        new(500_000, "500 kS/s"),
        new(1_000_000, "1 MS/s"),
        new(5_000_000, "5 MS/s"),
        new(20_000_000, "20 MS/s"),
    ];
}

/// <summary>
/// Picker item for a seeded <see cref="GuidedTest"/> and its optional linked <see cref="ReferenceWaveform"/>
/// (ADR-011 §7).
/// </summary>
public sealed class SelectableGuidedTestItem
{
    public SelectableGuidedTestItem(GuidedTest guidedTest, ReferenceWaveform? referenceWaveform = null)
    {
        GuidedTest = guidedTest ?? throw new ArgumentNullException(nameof(guidedTest));
        ReferenceWaveform = referenceWaveform;
    }

    public GuidedTest GuidedTest { get; }

    public ReferenceWaveform? ReferenceWaveform { get; }

    public string Id => GuidedTest.Id;

    public string Name => GuidedTest.Name;

    public string System => string.IsNullOrWhiteSpace(GuidedTest.System) ? "General" : GuidedTest.System!;

    public string Description => GuidedTest.Description ?? "";

    public string Procedure => GuidedTest.Procedure ?? "";

    public string? ScopeSetupJson => GuidedTest.ScopeSetupJson;

    public string? SafetyWarningIds => GuidedTest.SafetyWarningIds;

    public string? ReferenceWaveformId => GuidedTest.ReferenceWaveformId;

    public string Citation => !string.IsNullOrWhiteSpace(ReferenceWaveform?.Citation)
        ? ReferenceWaveform!.Citation!
        : TroubleshootingUnitHelper.DefaultTodoSpecSource;

    public string DisplayLabel => $"{GuidedTest.Name} [{System.ToUpperInvariant()}]";

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// View-model row for a computed and compared <see cref="ScopeMeasurement"/> (ADR-011 §4/§5).
/// Displays the channel, measurement kind, value + unit, status (<c>computed</c>, <c>flat_signal</c>,
/// <c>not_configured</c>, <c>no_trigger</c>, <c>too_few_samples</c>, <c>out_of_range</c>),
/// the <c>SIMULATED</c> provenance badge when <see cref="ScopeMeasurement.IsSimulated"/> is true,
/// and the reference comparison verdict (<c>pass</c> / <c>fail</c> / <c>na</c>) with the applied
/// tolerance or <c>"reference TODO — needs spec from manual"</c>.
/// </summary>
public sealed class ScopeMeasurementRowViewModel
{
    public ScopeMeasurementRowViewModel(
        ScopeMeasurement measurement,
        bool isDarkTheme = false,
        bool isSunlightMode = false,
        bool isTodoExpectation = false)
    {
        Measurement = measurement ?? throw new ArgumentNullException(nameof(measurement));
        IsTodoExpectation = isTodoExpectation ||
                            (!string.IsNullOrWhiteSpace(measurement.Detail) &&
                             measurement.Detail.Contains("reference TODO", StringComparison.OrdinalIgnoreCase));

        ChannelBadge = $"CH{measurement.Channel}";
        KindCode = FormatKindCode(measurement.Kind);
        KindDisplay = FormatKindDisplay(measurement.Kind);
        StatusCode = FormatStatusCode(measurement.Status);

        if (measurement.Status == ScopeMeasurementStatus.Computed &&
            measurement.Value is double v &&
            double.IsFinite(v))
        {
            ValueDisplay = FormatMeasurementNumber(v, measurement.Kind);
            UnitDisplay = measurement.Unit;
            ValueAndUnitDisplay = $"{ValueDisplay} {UnitDisplay}";
        }
        else
        {
            ValueDisplay = "—";
            UnitDisplay = measurement.Unit;
            ValueAndUnitDisplay = $"— ({StatusCode})";
        }

        StatusDisplay = measurement.Status == ScopeMeasurementStatus.Computed
            ? "computed"
            : string.IsNullOrWhiteSpace(measurement.Detail)
                ? StatusCode
                : $"{StatusCode} — {measurement.Detail}";

        Verdict = measurement.Verdict ?? ScopeVerdict.Na;
        VerdictCode = Verdict switch
        {
            ScopeVerdict.Pass => "pass",
            ScopeVerdict.Fail => "fail",
            _ => "na",
        };
        VerdictBadgeText = Verdict switch
        {
            ScopeVerdict.Pass => "PASS",
            ScopeVerdict.Fail => "FAIL",
            _ => "N/A",
        };

        ToleranceOrReasonDisplay = BuildToleranceOrReasonDisplay(measurement, IsTodoExpectation);
        VerdictColor = GetVerdictColor(Verdict, isDarkTheme, isSunlightMode);
        VerdictBrush = CreateSafeBrush(VerdictColor);
    }

    public ScopeMeasurement Measurement { get; }

    public int Channel => Measurement.Channel;

    public string ChannelBadge { get; }

    public MeasurementKind Kind => Measurement.Kind;

    public string KindCode { get; }

    public string KindDisplay { get; }

    public double? Value => Measurement.Value;

    public string Unit => Measurement.Unit;

    public string ValueDisplay { get; }

    public string UnitDisplay { get; }

    public string ValueAndUnitDisplay { get; }

    public ScopeMeasurementStatus Status => Measurement.Status;

    public string StatusCode { get; }

    public string StatusDisplay { get; }

    public bool IsSimulated => Measurement.IsSimulated;

    public Visibility SimulatedBadgeVisibility =>
        Measurement.IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    public bool IsTodoExpectation { get; }

    public ScopeVerdict Verdict { get; }

    public string VerdictCode { get; }

    public string VerdictBadgeText { get; }

    public string ToleranceOrReasonDisplay { get; }

    public Color VerdictColor { get; }

    public SolidColorBrush? VerdictBrush { get; }

    public static string FormatKindCode(MeasurementKind kind) => kind switch
    {
        MeasurementKind.Frequency => "frequency",
        MeasurementKind.Peak => "peak",
        MeasurementKind.PeakToPeak => "peak_to_peak",
        MeasurementKind.DutyCycle => "duty_cycle",
        MeasurementKind.RiseTime => "rise_time",
        MeasurementKind.Ripple => "ripple",
        MeasurementKind.Current => "current",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static string FormatKindDisplay(MeasurementKind kind) => kind switch
    {
        MeasurementKind.Frequency => "Frequency",
        MeasurementKind.Peak => "Peak (Vp)",
        MeasurementKind.PeakToPeak => "Peak-to-Peak (Vpp)",
        MeasurementKind.DutyCycle => "Duty Cycle",
        MeasurementKind.RiseTime => "Rise Time (10–90%)",
        MeasurementKind.Ripple => "AC Ripple (RMS)",
        MeasurementKind.Current => "Peak Current",
        _ => kind.ToString(),
    };

    public static string FormatStatusCode(ScopeMeasurementStatus status) => status switch
    {
        ScopeMeasurementStatus.Computed => "computed",
        ScopeMeasurementStatus.NoTrigger => "no_trigger",
        ScopeMeasurementStatus.TooFewSamples => "too_few_samples",
        ScopeMeasurementStatus.OutOfRange => "out_of_range",
        ScopeMeasurementStatus.FlatSignal => "flat_signal",
        ScopeMeasurementStatus.NotConfigured => "not_configured",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string FormatMeasurementNumber(double value, MeasurementKind kind) => kind switch
    {
        MeasurementKind.RiseTime => value < 1e-3
            ? string.Create(CultureInfo.InvariantCulture, $"{value:G4} ({value * 1e6:F1} µs)")
            : string.Create(CultureInfo.InvariantCulture, $"{value:G4}"),
        MeasurementKind.Frequency => value >= 1000.0
            ? string.Create(CultureInfo.InvariantCulture, $"{value:F1}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F2}"),
        MeasurementKind.DutyCycle => string.Create(CultureInfo.InvariantCulture, $"{value:F1}"),
        MeasurementKind.Ripple => string.Create(CultureInfo.InvariantCulture, $"{value:F4}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:F3}"),
    };

    private static string BuildToleranceOrReasonDisplay(ScopeMeasurement m, bool isTodo)
    {
        if (isTodo)
        {
            return WaveformComparer.TodoReason;
        }

        var unit = !string.IsNullOrWhiteSpace(m.ExpectedUnit) ? m.ExpectedUnit! : m.Unit;

        if (m.ExpectedNominal is double nom && m.ExpectedTolerance is double tol)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Nominal {nom:G5} ± {tol:G5} {unit}");
        }

        if (m.ExpectedMin is double min && m.ExpectedMax is double max)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Range {min:G5} – {max:G5} {unit}");
        }

        if (!string.IsNullOrWhiteSpace(m.Detail))
        {
            return m.Detail!;
        }

        return "No reference expectation for this channel/measurement (na)";
    }

    public static Color GetVerdictColor(ScopeVerdict verdict, bool isDarkTheme, bool isSunlightMode) =>
        (verdict, isSunlightMode, isDarkTheme) switch
        {
            (ScopeVerdict.Pass, true, _) => Color.FromArgb(0xFF, 0x00, 0x71, 0x00),      // Sunlight #007100
            (ScopeVerdict.Pass, false, true) => Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),  // Dark #6CCB5F
            (ScopeVerdict.Pass, false, false) => Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F), // Light #0F7B0F

            (ScopeVerdict.Fail, true, _) => Color.FromArgb(0xFF, 0xB3, 0x00, 0x00),      // Sunlight #B30000
            (ScopeVerdict.Fail, false, true) => Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4),  // Dark #FF99A4
            (ScopeVerdict.Fail, false, false) => Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C), // Light #C42B1C

            (_, true, _) => Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),                      // Sunlight #1A1A1A
            (_, false, true) => Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0),                  // Dark #A0A0A0
            (_, false, false) => Color.FromArgb(0xFF, 0x5C, 0x5C, 0x5C),                 // Light #5C5C5C
        };

    public static SolidColorBrush? CreateSafeBrush(Color color)
    {
        try
        {
            return new SolidColorBrush(color);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// View-model item for a saved <see cref="Waveform"/> row in the Saved Waveform Library for the active work order.
/// </summary>
public sealed class SavedWaveformRowViewModel
{
    public SavedWaveformRowViewModel(Waveform waveform, string? guidedTestName = null)
    {
        Waveform = waveform ?? throw new ArgumentNullException(nameof(waveform));
        GuidedTestName = !string.IsNullOrWhiteSpace(guidedTestName)
            ? guidedTestName!
            : !string.IsNullOrWhiteSpace(waveform.GuidedTestId)
                ? waveform.GuidedTestId!
                : "Manual Scope Capture";

        ScopeDeviceCode = DbEnum.ToCode(waveform.ScopeDevice);
        IsSimulated = waveform.ScopeDevice == ScopeDeviceKind.Simulator;

        var configs = ScopeJson.DeserializeChannelConfigs(waveform.ChannelConfigJson);
        ChannelSummary = configs.Count > 0
            ? string.Join(", ", configs.Select(c => $"CH{c.Channel} (±{c.VoltageRangeVolts:G4}V {c.Coupling.ToString().ToUpperInvariant()})"))
            : "CH1";

        TimebaseDisplay = FormatTimebase(waveform.Timebase ?? 0.001);
        SampleRateDisplay = FormatSampleRate(waveform.SampleRate ?? 100_000);
        SampleCountDisplay = $"{waveform.SampleCount ?? 0:N0} samples";

        var measurements = ScopeJson.DeserializeMeasurements(waveform.MeasurementsJson);
        bool anyFail = measurements.Any(m => m.Verdict == ScopeVerdict.Fail);
        bool anyPass = measurements.Any(m => m.Verdict == ScopeVerdict.Pass);
        OverallVerdict = anyFail
            ? ScopeVerdict.Fail
            : anyPass
                ? ScopeVerdict.Pass
                : ScopeVerdict.Na;

        OverallVerdictBadge = OverallVerdict switch
        {
            ScopeVerdict.Pass => "PASS",
            ScopeVerdict.Fail => "FAIL",
            _ => "N/A",
        };
    }

    public Waveform Waveform { get; }

    public string Id => Waveform.Id;

    public string WorkOrderId => Waveform.WorkOrderId;

    public ScopeDeviceKind ScopeDevice => Waveform.ScopeDevice;

    public string ScopeDeviceCode { get; }

    public bool IsSimulated { get; }

    public Visibility SimulatedBadgeVisibility =>
        IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    public string GuidedTestName { get; }

    public string ChannelSummary { get; }

    public string TimebaseDisplay { get; }

    public string SampleRateDisplay { get; }

    public string SampleCountDisplay { get; }

    public string DataPath => Waveform.DataPath;

    public string CreatedAt => Waveform.CreatedAt;

    public ScopeVerdict OverallVerdict { get; }

    public string OverallVerdictBadge { get; }

    public string DisplayLabel =>
        $"{GuidedTestName} · {ChannelSummary} · {TimebaseDisplay} · {SampleRateDisplay} ({CreatedAt})";

    public static string FormatTimebase(double secondsPerDiv)
    {
        if (secondsPerDiv < 1e-3)
            return string.Create(CultureInfo.InvariantCulture, $"{secondsPerDiv * 1e6:G4} µs/div");
        if (secondsPerDiv < 1.0)
            return string.Create(CultureInfo.InvariantCulture, $"{secondsPerDiv * 1e3:G4} ms/div");
        return string.Create(CultureInfo.InvariantCulture, $"{secondsPerDiv:G4} s/div");
    }

    public static string FormatSampleRate(double sampleRateHz)
    {
        if (sampleRateHz >= 1_000_000)
            return string.Create(CultureInfo.InvariantCulture, $"{sampleRateHz / 1_000_000.0:G4} MS/s");
        if (sampleRateHz >= 1_000)
            return string.Create(CultureInfo.InvariantCulture, $"{sampleRateHz / 1_000.0:G4} kS/s");
        return string.Create(CultureInfo.InvariantCulture, $"{sampleRateHz:F0} S/s");
    }

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Pure, thread-safe waveform decimation and cursor/ruler math helper.
/// Runs on a background worker thread so the UI thread only applies the resulting points.
/// </summary>
public static class WaveformRenderEngine
{
    // 10 × 10 divisions of 100 × 60 px; the XAML Viewbox stretches this to fill the scope view.
    public const double DefaultCanvasWidth = 1000.0;
    public const double DefaultCanvasHeight = 600.0;

    /// <summary>
    /// Decimates a channel's voltage samples to screen width, preserving min/max envelope peaks
    /// per horizontal pixel column so narrow pulses (CKP teeth, CAN bits, starter inrush) are never lost.
    /// Safe to run on any thread.
    /// </summary>
    public static Point[] DecimateChannelSamples(
        double[]? samples,
        double configuredRangeVolts,
        double canvasWidth = DefaultCanvasWidth,
        double canvasHeight = DefaultCanvasHeight,
        double zoomFactor = 1.0,
        double panNormalized = 0.0)
    {
        if (samples is null || samples.Length == 0 || canvasWidth <= 1.0 || canvasHeight <= 1.0)
            return Array.Empty<Point>();

        var (startIdx, visibleCount) = GetVisibleWindow(samples.Length, zoomFactor, panNormalized);
        if (visibleCount <= 0)
            return Array.Empty<Point>();

        // Determine effective full-scale vertical bound (±V) so out-of-range traces still fit inside the grid
        double peakAbs = Math.Max(0.1, configuredRangeVolts);
        for (int i = startIdx; i < startIdx + visibleCount; i++)
        {
            double v = Math.Abs(samples[i]);
            if (double.IsFinite(v) && v > peakAbs)
                peakAbs = v;
        }

        double usableHalfHeight = (canvasHeight - 16.0) * 0.5;
        double midY = canvasHeight * 0.5;

        double MapY(double volts)
        {
            if (!double.IsFinite(volts))
                return midY;
            double norm = Math.Clamp(volts / peakAbs, -1.0, 1.0);
            return midY - norm * usableHalfHeight;
        }

        int targetBuckets = Math.Max(2, (int)Math.Round(canvasWidth));

        // Direct 1:1 point mapping when visible sample count fits within pixel columns
        if (visibleCount <= targetBuckets)
        {
            var direct = new Point[visibleCount];
            for (int i = 0; i < visibleCount; i++)
            {
                double x = visibleCount == 1
                    ? 0.0
                    : (i / (double)(visibleCount - 1)) * canvasWidth;
                direct[i] = new Point(x, MapY(samples[startIdx + i]));
            }
            return direct;
        }

        // Min/max envelope decimation across pixel buckets
        var points = new List<Point>(targetBuckets * 2);
        double samplesPerBucket = visibleCount / (double)targetBuckets;

        for (int b = 0; b < targetBuckets; b++)
        {
            int bStart = startIdx + (int)Math.Floor(b * samplesPerBucket);
            int bEnd = startIdx + (int)Math.Floor((b + 1) * samplesPerBucket);
            if (bEnd <= bStart)
                bEnd = Math.Min(startIdx + visibleCount, bStart + 1);

            int minIdx = bStart;
            int maxIdx = bStart;
            double minVal = samples[bStart];
            double maxVal = samples[bStart];

            for (int i = bStart + 1; i < bEnd; i++)
            {
                double v = samples[i];
                if (v < minVal)
                {
                    minVal = v;
                    minIdx = i;
                }
                if (v > maxVal)
                {
                    maxVal = v;
                    maxIdx = i;
                }
            }

            double x = (b / (double)(targetBuckets - 1)) * canvasWidth;
            if (minIdx == maxIdx || Math.Abs(maxVal - minVal) < 1e-12)
            {
                points.Add(new Point(x, MapY(minVal)));
            }
            else if (minIdx < maxIdx)
            {
                points.Add(new Point(x, MapY(minVal)));
                points.Add(new Point(x, MapY(maxVal)));
            }
            else
            {
                points.Add(new Point(x, MapY(maxVal)));
                points.Add(new Point(x, MapY(minVal)));
            }
        }

        return points.ToArray();
    }

    public static (int StartIndex, int Count) GetVisibleWindow(
        int totalSamples,
        double zoomFactor,
        double panNormalized)
    {
        if (totalSamples <= 0)
            return (0, 0);

        double clampedZoom = Math.Clamp(zoomFactor, 1.0, 64.0);
        int visibleCount = Math.Max(2, (int)Math.Round(totalSamples / clampedZoom));
        visibleCount = Math.Min(totalSamples, visibleCount);

        int maxStart = Math.Max(0, totalSamples - visibleCount);
        double clampedPan = Math.Clamp(panNormalized, 0.0, 1.0);
        int startIdx = (int)Math.Round(clampedPan * maxStart);

        return (startIdx, visibleCount);
    }

    public static (int SampleIndex, double TimeSeconds) MapCursorToSample(
        int totalSamples,
        double sampleRateHz,
        double cursorPercent,
        double zoomFactor,
        double panNormalized)
    {
        if (totalSamples <= 0 || sampleRateHz <= 0.0)
            return (0, 0.0);

        var (startIdx, visibleCount) = GetVisibleWindow(totalSamples, zoomFactor, panNormalized);
        double frac = Math.Clamp(cursorPercent / 100.0, 0.0, 1.0);
        int idx = startIdx + (int)Math.Round(frac * Math.Max(0, visibleCount - 1));
        idx = Math.Clamp(idx, 0, totalSamples - 1);
        double t = idx / sampleRateHz;
        return (idx, t);
    }

    public static string FormatTimeValue(double seconds)
    {
        double abs = Math.Abs(seconds);
        if (abs < 1e-3 && abs > 0.0)
            return string.Create(CultureInfo.InvariantCulture, $"{seconds * 1e6:F1} µs");
        if (abs < 1.0 && abs > 0.0)
            return string.Create(CultureInfo.InvariantCulture, $"{seconds * 1e3:F3} ms");
        return string.Create(CultureInfo.InvariantCulture, $"{seconds:F4} s");
    }
}
