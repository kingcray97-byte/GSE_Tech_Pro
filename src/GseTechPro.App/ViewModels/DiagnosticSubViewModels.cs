using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace GseTechPro.App.ViewModels;

public sealed record NavRailItem(string Key, string Label, string Glyph);

public sealed record OpenWorkOrderTab(string Id, string Title, string AssetNumber, string StatusText);

public sealed record DiagnosticLogEntry(string Timestamp, string Message);

/// <summary>
/// Live data display format matching Shop Foreman Pro's "Data Format" menu (screens 13–16).
/// </summary>
public enum LiveDataDisplayMode
{
    List,
    SnaplistEditor,
    Bar,
    DigitalMeter,
    AnalogGauge,
    LineGraph,
    CustomDisplay,
}

/// <summary>
/// Widget type inside the 6-slot Custom Display Builder (screen 15).
/// </summary>
public enum CustomSlotDisplayType
{
    BarGraph,
    Gauge,
    AnalogMeter,
    DigitalMeter,
}

/// <summary>
/// Readiness / On-Board Monitor row for Equipment Health (screens 04, 08, 09).
/// All expectations/states are derived from the connected protocol &amp; active DTC state — never invented specs.
/// </summary>
public sealed class ReadinessMonitorRowViewModel
{
    public ReadinessMonitorRowViewModel(
        string ecuId,
        string monitorGroup,
        string monitorName,
        string statusText,
        bool isCompleteOrPass,
        bool isSimulated,
        bool isDarkTheme,
        bool isSunlightMode)
    {
        EcuId = ecuId;
        MonitorGroup = monitorGroup;
        MonitorName = monitorName;
        StatusText = statusText;
        IsSimulated = isSimulated;
        ProvenanceBadgeText = isSimulated ? "SIMULATED" : "LIVE BUS";

        Color statusColor = (isCompleteOrPass, isSunlightMode, isDarkTheme) switch
        {
            (true, true, _) => Color.FromArgb(0xFF, 0x00, 0x71, 0x00),   // Sunlight #007100
            (true, false, true) => Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F), // Dark #6CCB5F
            (true, false, false) => Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F), // Light #0F7B0F
            (false, true, _) => Color.FromArgb(0xFF, 0x8A, 0x52, 0x00),  // Sunlight #8A5200
            (false, false, true) => Color.FromArgb(0xFF, 0xFC, 0xE1, 0x00), // Dark #FCE100
            (false, false, false) => Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00), // Light #9D5D00
        };
        StatusBrush = new SolidColorBrush(statusColor);
    }

    public string EcuId { get; }
    public string MonitorGroup { get; }
    public string MonitorName { get; }
    public string StatusText { get; }
    public bool IsSimulated { get; }
    public string ProvenanceBadgeText { get; }
    public SolidColorBrush StatusBrush { get; }
}

/// <summary>
/// Device option in the Device Selection / Scan Tool Setup bar &amp; dialog (screens 26, 28).
/// Combines in-process Simulator options and 32-bit Host devices from <c>DiagnosticAdapterFactory.ListDevicesAsync</c>.
/// </summary>
public sealed class SelectableDeviceItem
{
    public SelectableDeviceItem(DeviceInfo deviceInfo, bool isInProcessSimulator)
    {
        Device = deviceInfo;
        IsInProcessSimulator = isInProcessSimulator;
    }

    public DeviceInfo Device { get; }

    /// <summary>True when using the in-process simulator (<c>CreateSimulator</c>); false when routing through <c>ConnectHostAsync</c>.</summary>
    public bool IsInProcessSimulator { get; }

    public string Id => Device.Id;
    public string Name => Device.Name;
    public string KindLabel => Device.Kind.ToUpperInvariant();
    public bool IsSimulated => Device.IsSimulated;
    public string ProvenanceBadge => Device.IsSimulated ? "SIMULATED" : "REAL HARDWARE";
    public string ProtocolsSummary => Device.Protocols.Count > 0
        ? string.Join(", ", Device.Protocols)
        : "Auto";

    public string DisplayLabel =>
        $"{Name} [{KindLabel}{(IsInProcessSimulator ? " · In-Proc" : " · Host32")}] — {(IsSimulated ? "SIMULATED" : "REAL")}";

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Enriched DTC row for Equipment Health (screen 04).
/// Matches <c>(protocol, code)</c> against <c>dtc_library</c> via <see cref="DtcLibraryEntry"/>.
/// When no library match exists, displays the raw numeric identifiers + "no library match" — never invents descriptions.
/// Colors follow ARCHITECTURE.md §5.3: Active/Confirmed = Red, Pending/Inactive = Blue.
/// </summary>
public sealed class DtcRowViewModel
{
    public DtcRowViewModel(
        DtcReadout readout,
        DtcLibraryEntry? libraryMatch,
        bool adapterIsSimulated,
        bool isDarkTheme,
        bool isSunlightMode)
    {
        Readout = readout;
        LibraryMatch = libraryMatch;
        IsSimulated = readout.IsSimulated || adapterIsSimulated;
        HasFreezeFrame = readout.Status == DtcStatus.Active ? "FF" : "";

        // Raw numeric identifier summary (SPN/FMI or raw code)
        var spn = readout.Spn ?? libraryMatch?.Spn;
        var fmi = readout.Fmi ?? libraryMatch?.Fmi;
        RawIdentifiers = (spn, fmi) switch
        {
            (int s, int f) => $"SPN {s} / FMI {f}",
            (int s, null) => $"SPN {s}",
            (null, int f) => $"FMI {f}",
            _ => $"{readout.Protocol}: {readout.Code}",
        };

        if (libraryMatch is not null && !string.IsNullOrWhiteSpace(libraryMatch.Description))
        {
            HasLibraryMatch = true;
            Description = libraryMatch.Description!;
            SystemName = string.IsNullOrWhiteSpace(libraryMatch.System) ? "—" : libraryMatch.System!;
            LikelyCauses = string.IsNullOrWhiteSpace(libraryMatch.LikelyCauses) ? "—" : libraryMatch.LikelyCauses!;
            SourceCitation = string.IsNullOrWhiteSpace(libraryMatch.SourceCitation)
                ? "dtc_library match"
                : libraryMatch.SourceCitation!;
        }
        else
        {
            HasLibraryMatch = false;
            var rawBusDesc = !string.IsNullOrWhiteSpace(readout.Description)
                ? $" [Adapter text: {readout.Description}]"
                : "";
            Description = $"{RawIdentifiers} — no library match{rawBusDesc}";
            SystemName = "Unmatched";
            LikelyCauses = "No dtc_library entry for this (protocol, code). Refer to OEM service manual.";
            SourceCitation = "no library match";
        }

        Severity = libraryMatch?.Severity ?? readout.Severity ?? "—";
        StatusLabel = readout.Status switch
        {
            DtcStatus.Active => "Active / Confirmed",
            DtcStatus.Pending => "Pending",
            DtcStatus.Stored => "Stored / Inactive",
            _ => readout.Status.ToString(),
        };

        // ARCHITECTURE.md §5.3: red = active/confirmed, blue = pending/inactive
        Color rowColor = (readout.Status, isSunlightMode, isDarkTheme) switch
        {
            (DtcStatus.Active, true, _) => Color.FromArgb(0xFF, 0xB3, 0x00, 0x00),   // Sunlight #B30000
            (DtcStatus.Active, false, true) => Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4), // Dark #FF99A4
            (DtcStatus.Active, false, false) => Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C), // Light #C42B1C

            (_, true, _) => Color.FromArgb(0xFF, 0x1E, 0x2F, 0xB8),                  // Sunlight #1E2FB8
            (_, false, true) => Color.FromArgb(0xFF, 0x8C, 0x93, 0xF0),              // Dark #8C93F0
            (_, false, false) => Color.FromArgb(0xFF, 0x2E, 0x33, 0x91),             // Light #2E3391
        };

        RowBrush = new SolidColorBrush(rowColor);
    }

    public DtcReadout Readout { get; }
    public DtcLibraryEntry? LibraryMatch { get; }
    public string Code => Readout.Code;
    public string Protocol => Readout.Protocol.ToString();
    public string RawIdentifiers { get; }
    public bool HasLibraryMatch { get; }
    public string LibraryMatchLabel => HasLibraryMatch ? "Library Match" : "no library match";
    public string HasFreezeFrame { get; }
    public string Description { get; }
    public string SystemName { get; }
    public string LikelyCauses { get; }
    public string SourceCitation { get; }
    public string Severity { get; }
    public string StatusLabel { get; }
    public bool IsSimulated { get; }
    public Visibility SimulatedBadgeVisibility => IsSimulated ? Visibility.Visible : Visibility.Collapsed;
    public SolidColorBrush RowBrush { get; }
}

/// <summary>
/// A single live-data parameter row (screens 04, 13–16).
/// Canonicalizes reported <see cref="PidValue.Unit"/> via <see cref="UnitConverter.ToCanonical"/>
/// guarded by <see cref="UnitConverter.IsKnownUnit"/>, preserves the entered/reported unit, and supports
/// per-reading unit override switching (Imperial / Metric / Entered / Compatible units) plus a bounded
/// sample ring buffer for Bar, Digital Meter, Analog Gauge, and Line Graph views.
/// </summary>
public sealed partial class LivePidRowViewModel : ObservableObject
{
    public const int MaxHistorySamples = 120;

    private readonly List<(long FrameSeq, double TimeSec, double DisplayNumericValue)> _history = new(MaxHistorySamples);

    public LivePidRowViewModel(
        string name,
        double rawValue,
        string rawUnit,
        UnitSystem unitSystem,
        string timestamp,
        bool isSimulated = true)
    {
        Name = name;
        RebuildAvailableUnits(rawUnit);
        Update(rawValue, rawUnit, unitSystem, timestamp, isSimulated, sampleSequence: 0, elapsedSeconds: 0);
    }

    public string Name { get; }

    /// <summary>The value exactly as reported/entered by the adapter (preserved verbatim).</summary>
    public double EnteredValue { get; private set; }

    /// <summary>The unit exactly as reported/entered by the adapter (preserved verbatim).</summary>
    public string EnteredUnit { get; private set; } = "";

    /// <summary>Canonical SI value when <see cref="UnitConverter.IsKnownUnit"/> is true; otherwise raw value.</summary>
    public double CanonicalValue { get; private set; }

    /// <summary>Canonical SI unit when <see cref="UnitConverter.IsKnownUnit"/> is true; otherwise raw unit.</summary>
    public string CanonicalUnit { get; private set; } = "";

    /// <summary>Numeric value in the currently active display unit (for gauges, bars, and graph).</summary>
    [ObservableProperty]
    private double _displayNumericValue;

    [ObservableProperty]
    private string _displayValue = "";

    [ObservableProperty]
    private string _displayUnit = "";

    [ObservableProperty]
    private string _timestamp = "";

    [ObservableProperty]
    private string _canonicalSourceDisplay = "";

    [ObservableProperty]
    private bool _isSimulated = true;

    public Visibility SimulatedBadgeVisibility => IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether this PID is included in the custom Snaplist (screens 13–14).</summary>
    [ObservableProperty]
    private bool _isSelectedInSnaplist = true;

    /// <summary>Per-reading unit override: "Shop Default" or a specific unit string (e.g. "psi", "bar", "kPa", "Pa").</summary>
    [ObservableProperty]
    private string _selectedUnitOverride = "Shop Default";

    public ObservableCollection<string> AvailableUnits { get; } = new();

    // ---- Range & Gauge / Bar / Digital Meter tracking (observed min/max + dial range) ----

    [ObservableProperty]
    private double _observedMin = double.MaxValue;

    [ObservableProperty]
    private double _observedMax = double.MinValue;

    [ObservableProperty]
    private string _observedMinDisplay = "—";

    [ObservableProperty]
    private string _observedMaxDisplay = "—";

    [ObservableProperty]
    private double _rangeMin;

    [ObservableProperty]
    private double _rangeMax = 100;

    /// <summary>0..100 percentage across [RangeMin, RangeMax] for Bar Graph and Analog Gauge.</summary>
    [ObservableProperty]
    private double _normalizedPercent;

    /// <summary>Needle angle in degrees (-120° to +120°) for the Analog Gauge dial.</summary>
    [ObservableProperty]
    private double _gaugeNeedleAngle = -120;

    /// <summary>Endpoint X on a 180x110 Analog Gauge canvas (center 90, 95, radius 72).</summary>
    [ObservableProperty]
    private double _gaugeNeedleX = 90;

    /// <summary>Endpoint Y on a 180x110 Analog Gauge canvas (center 90, 95, radius 72).</summary>
    [ObservableProperty]
    private double _gaugeNeedleY = 23;

    public IReadOnlyList<(long FrameSeq, double TimeSec, double DisplayNumericValue)> History => _history;

    private UnitSystem _lastUnitSystem = UnitSystem.Imperial;

    partial void OnSelectedUnitOverrideChanged(string value)
    {
        FormatForUnitSystem(_lastUnitSystem);
    }

    public void Update(
        double rawValue,
        string rawUnit,
        UnitSystem unitSystem,
        string timestamp,
        bool isSimulated = true,
        long sampleSequence = 0,
        double elapsedSeconds = 0)
    {
        if (!string.Equals(EnteredUnit, rawUnit, StringComparison.Ordinal))
        {
            RebuildAvailableUnits(rawUnit);
        }

        EnteredValue = rawValue;
        EnteredUnit = rawUnit;
        Timestamp = timestamp;
        IsSimulated = isSimulated;
        OnPropertyChanged(nameof(SimulatedBadgeVisibility));

        if (UnitConverter.IsKnownUnit(rawUnit))
        {
            CanonicalValue = UnitConverter.ToCanonical(rawValue, rawUnit);
            CanonicalUnit = UnitConverter.CanonicalUnit(rawUnit);
            CanonicalSourceDisplay = $"{CanonicalValue:G6} {CanonicalUnit} (entered: {rawValue:G6} {rawUnit})";
        }
        else
        {
            CanonicalValue = rawValue;
            CanonicalUnit = rawUnit;
            CanonicalSourceDisplay = $"{rawValue:G6} {rawUnit}";
        }

        FormatForUnitSystem(unitSystem);

        // Append to bounded history ring buffer
        if (_history.Count >= MaxHistorySamples)
        {
            _history.RemoveAt(0);
        }
        _history.Add((sampleSequence, elapsedSeconds, DisplayNumericValue));
    }

    public void ClearHistory()
    {
        _history.Clear();
        ObservedMin = double.MaxValue;
        ObservedMax = double.MinValue;
        ObservedMinDisplay = "—";
        ObservedMaxDisplay = "—";
    }

    public void FormatForUnitSystem(UnitSystem unitSystem)
    {
        _lastUnitSystem = unitSystem;

        double convertedValue;
        string convertedUnit;

        if (UnitConverter.IsKnownUnit(EnteredUnit))
        {
            var reading = ReadingValue.FromEntered(EnteredValue, EnteredUnit);

            if (!string.IsNullOrWhiteSpace(SelectedUnitOverride) &&
                !string.Equals(SelectedUnitOverride, "Shop Default", StringComparison.OrdinalIgnoreCase))
            {
                var targetUnit = ExtractUnitToken(SelectedUnitOverride);
                if (UnitConverter.IsKnownUnit(targetUnit) &&
                    string.Equals(UnitConverter.CanonicalUnit(targetUnit), reading.UnitCanonical, StringComparison.Ordinal))
                {
                    convertedValue = UnitConverter.FromCanonical(reading.ValueCanonical, reading.UnitCanonical, targetUnit);
                    convertedUnit = targetUnit;
                }
                else
                {
                    (convertedValue, convertedUnit) = reading.ToDisplay(unitSystem);
                }
            }
            else
            {
                (convertedValue, convertedUnit) = reading.ToDisplay(unitSystem);
            }
        }
        else if (string.Equals(EnteredUnit, "m/s", StringComparison.OrdinalIgnoreCase))
        {
            var targetUnit = ExtractUnitToken(SelectedUnitOverride);
            if (string.Equals(targetUnit, "m/s", StringComparison.OrdinalIgnoreCase))
            {
                convertedValue = EnteredValue;
                convertedUnit = "m/s";
            }
            else if (string.Equals(targetUnit, "mph", StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(SelectedUnitOverride, "Shop Default", StringComparison.OrdinalIgnoreCase) && unitSystem == UnitSystem.Imperial))
            {
                convertedValue = EnteredValue * 2.23693629;
                convertedUnit = "mph";
            }
            else
            {
                convertedValue = EnteredValue * 3.6;
                convertedUnit = "km/h";
            }
        }
        else
        {
            convertedValue = EnteredValue;
            convertedUnit = EnteredUnit;
        }

        DisplayNumericValue = convertedValue;
        DisplayValue = FormatNumber(convertedValue, convertedUnit);
        DisplayUnit = convertedUnit;

        UpdateGaugeAndRangeMetrics(convertedValue, convertedUnit);
    }

    private void RebuildAvailableUnits(string rawUnit)
    {
        AvailableUnits.Clear();
        AvailableUnits.Add("Shop Default");

        if (!string.IsNullOrWhiteSpace(rawUnit))
        {
            AvailableUnits.Add($"Entered ({rawUnit})");
        }

        if (UnitConverter.IsKnownUnit(rawUnit))
        {
            var canonical = UnitConverter.CanonicalUnit(rawUnit);
            switch (canonical)
            {
                case UnitConverter.Pascal:
                    AddUniqueUnit(UnitConverter.Psi);
                    AddUniqueUnit(UnitConverter.Bar);
                    AddUniqueUnit(UnitConverter.KiloPascal);
                    AddUniqueUnit(UnitConverter.Pascal);
                    break;
                case UnitConverter.Celsius:
                    AddUniqueUnit(UnitConverter.Fahrenheit);
                    AddUniqueUnit(UnitConverter.Celsius);
                    break;
                case UnitConverter.Meter:
                    AddUniqueUnit(UnitConverter.Mile);
                    AddUniqueUnit(UnitConverter.Kilometer);
                    AddUniqueUnit(UnitConverter.Foot);
                    AddUniqueUnit(UnitConverter.Meter);
                    break;
                default:
                    AddUniqueUnit(canonical);
                    break;
            }
        }
        else if (string.Equals(rawUnit, "m/s", StringComparison.OrdinalIgnoreCase))
        {
            AddUniqueUnit("mph");
            AddUniqueUnit("km/h");
            AddUniqueUnit("m/s");
        }

        SelectedUnitOverride = "Shop Default";
    }

    private void AddUniqueUnit(string unit)
    {
        if (!AvailableUnits.Contains(unit))
            AvailableUnits.Add(unit);
    }

    private static string ExtractUnitToken(string selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return "";
        if (selection.StartsWith("Entered (", StringComparison.Ordinal) && selection.EndsWith(')'))
            return selection["Entered (".Length..^1];
        return selection;
    }

    private void UpdateGaugeAndRangeMetrics(double value, string unit)
    {
        if (value < ObservedMin) ObservedMin = value;
        if (value > ObservedMax) ObservedMax = value;

        ObservedMinDisplay = FormatNumber(ObservedMin, unit);
        ObservedMaxDisplay = FormatNumber(ObservedMax, unit);

        // Compute display scale range [RangeMin, RangeMax] from observed bounds (not invented spec ranges)
        var (minScale, maxScale) = ComputeDisplayScale(Name, unit, ObservedMin, ObservedMax, value);
        RangeMin = minScale;
        RangeMax = maxScale;

        var span = Math.Max(1e-6, maxScale - minScale);
        var pct = Math.Clamp((value - minScale) / span * 100.0, 0.0, 100.0);
        NormalizedPercent = Math.Round(pct, 1);

        // Analog dial geometry: -150 deg (left) to -30 deg (right) in standard screen coordinates
        var angleDeg = -150.0 + (pct / 100.0) * 120.0;
        GaugeNeedleAngle = -60.0 + (pct / 100.0) * 120.0;
        var rad = angleDeg * Math.PI / 180.0;
        GaugeNeedleX = 90.0 + 68.0 * Math.Cos(rad);
        GaugeNeedleY = 92.0 + 68.0 * Math.Sin(rad);
    }

    private static (double Min, double Max) ComputeDisplayScale(string name, string unit, double obsMin, double obsMax, double current)
    {
        if (unit == "%") return (0, 100);
        if (string.Equals(unit, "rpm", StringComparison.OrdinalIgnoreCase)) return (0, Math.Max(3000, Math.Ceiling(obsMax / 500.0) * 500.0));
        if (unit == UnitConverter.Volt) return (0, Math.Max(16, Math.Ceiling(obsMax + 2)));
        if (unit is "mph" or "km/h" or "m/s") return (0, Math.Max(60, Math.Ceiling(obsMax / 10.0) * 10.0));

        var lo = Math.Min(obsMin, current);
        var hi = Math.Max(obsMax, current);
        if (Math.Abs(hi - lo) < 1e-3)
        {
            var pad = Math.Max(1.0, Math.Abs(current) * 0.25);
            return (Math.Floor(current - pad), Math.Ceiling(current + pad));
        }
        var margin = (hi - lo) * 0.15;
        return (Math.Floor(lo - margin), Math.Ceiling(hi + margin));
    }

    public static string FormatNumber(double value, string unit) => unit switch
    {
        UnitConverter.Psi => value.ToString("F1"),
        UnitConverter.Bar => value.ToString("F2"),
        UnitConverter.KiloPascal => value.ToString("F1"),
        UnitConverter.Pascal => value.ToString("F0"),
        UnitConverter.Fahrenheit or UnitConverter.Celsius => value.ToString("F1"),
        UnitConverter.Volt or UnitConverter.Ampere => value.ToString("F2"),
        UnitConverter.LiterPerMinute => value.ToString("F3"),
        UnitConverter.Hour => value.ToString("F2"),
        _ => value.ToString("G6"),
    };
}

/// <summary>
/// One slot (1–6) in the Custom Display Builder (screen 15).
/// Binds to a selected <see cref="LivePidRowViewModel"/> and a <see cref="CustomSlotDisplayType"/>.
/// </summary>
public sealed partial class CustomDisplaySlotViewModel : ObservableObject
{
    public CustomDisplaySlotViewModel(int slotNumber, ObservableCollection<string> availablePidNames)
    {
        SlotNumber = slotNumber;
        AvailablePidNames = availablePidNames;
    }

    public int SlotNumber { get; }
    public string SlotHeader => $"Slot {SlotNumber}";

    public ObservableCollection<string> AvailablePidNames { get; }
    public IReadOnlyList<CustomSlotDisplayType> DisplayTypes { get; } = Enum.GetValues<CustomSlotDisplayType>();

    [ObservableProperty]
    private string? _selectedPidName;

    [ObservableProperty]
    private CustomSlotDisplayType _displayType = CustomSlotDisplayType.DigitalMeter;

    [ObservableProperty]
    private LivePidRowViewModel? _boundPid;

    public string BoundPidName => BoundPid?.Name ?? "";
    public string BoundPidDisplayValue => BoundPid?.DisplayValue ?? "—";
    public string BoundPidDisplayUnit => BoundPid?.DisplayUnit ?? "";
    public double BoundPidNormalizedPercent => BoundPid?.NormalizedPercent ?? 0;

    public bool HasBoundPid => BoundPid is not null;
    public Visibility BoundPidVisibility => BoundPid is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptySlotVisibility => BoundPid is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BarGraphVisibility =>
        BoundPid is not null && DisplayType == CustomSlotDisplayType.BarGraph ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DigitalMeterVisibility =>
        BoundPid is not null && DisplayType == CustomSlotDisplayType.DigitalMeter ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AnalogGaugeVisibility =>
        BoundPid is not null && (DisplayType == CustomSlotDisplayType.Gauge || DisplayType == CustomSlotDisplayType.AnalogMeter)
            ? Visibility.Visible
            : Visibility.Collapsed;

    partial void OnBoundPidChanging(LivePidRowViewModel? oldValue, LivePidRowViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnBoundPidPropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnBoundPidPropertyChanged;
    }

    partial void OnBoundPidChanged(LivePidRowViewModel? value) => NotifyModeVisibilities();
    partial void OnDisplayTypeChanged(CustomSlotDisplayType value) => NotifyModeVisibilities();

    private void OnBoundPidPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(BoundPidDisplayValue));
        OnPropertyChanged(nameof(BoundPidDisplayUnit));
        OnPropertyChanged(nameof(BoundPidNormalizedPercent));
    }

    private void NotifyModeVisibilities()
    {
        OnPropertyChanged(nameof(HasBoundPid));
        OnPropertyChanged(nameof(BoundPidVisibility));
        OnPropertyChanged(nameof(EmptySlotVisibility));
        OnPropertyChanged(nameof(BarGraphVisibility));
        OnPropertyChanged(nameof(DigitalMeterVisibility));
        OnPropertyChanged(nameof(AnalogGaugeVisibility));
        OnPropertyChanged(nameof(BoundPidName));
        OnPropertyChanged(nameof(BoundPidDisplayValue));
        OnPropertyChanged(nameof(BoundPidDisplayUnit));
        OnPropertyChanged(nameof(BoundPidNormalizedPercent));
    }
}
