using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using GseTechPro.Core.Ai;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// View-model wrapper for a <see cref="Citation"/> attached to an assistant <see cref="ChatResponse"/>.
/// Supports tappable navigation to a manual page, work order, or DTC entry.
/// </summary>
public sealed class CitationItemViewModel
{
    public CitationItemViewModel(Citation citation)
    {
        Citation = citation ?? throw new ArgumentNullException(nameof(citation));

        KindBadge = citation.Kind switch
        {
            CitationKind.ManualPage => "MANUAL PAGE",
            CitationKind.WorkOrder => "WORK ORDER",
            CitationKind.Dtc => "DTC",
            _ => "SOURCE",
        };

        Glyph = citation.Kind switch
        {
            CitationKind.ManualPage => "\uE82D",
            CitationKind.WorkOrder => "\uE8A5",
            CitationKind.Dtc => "\uE7BA",
            _ => "\uE71B",
        };

        var pageNum = citation.PageStart ?? 1;
        FallbackManualLabel = $"Manual, page {pageNum}";

        Label = citation.Kind switch
        {
            CitationKind.ManualPage => !string.IsNullOrWhiteSpace(citation.DisplayText)
                ? citation.DisplayText
                : FallbackManualLabel,
            CitationKind.WorkOrder => !string.IsNullOrWhiteSpace(citation.DisplayText)
                ? citation.DisplayText
                : $"WO #{citation.WorkOrderNumber ?? "Unknown"}",
            CitationKind.Dtc => !string.IsNullOrWhiteSpace(citation.DisplayText)
                ? citation.DisplayText
                : "DTC Reference",
            _ => !string.IsNullOrWhiteSpace(citation.DisplayText)
                ? citation.DisplayText
                : "Citation",
        };
    }

    public Citation Citation { get; }

    public CitationKind Kind => Citation.Kind;

    public string KindBadge { get; }

    public string Glyph { get; }

    public string Label { get; }

    /// <summary>
    /// Canonical fallback label ("Manual, page N") shown when the manual is not yet imported locally.
    /// </summary>
    public string FallbackManualLabel { get; }

    public string? ManualId => Citation.ManualId;

    public int? PageStart => Citation.PageStart;

    public string? WorkOrderNumber => Citation.WorkOrderNumber;
}

/// <summary>
/// A single message row in the Tech Chat screen.
/// Preserves <see cref="ChatResponse.IsSuggestion"/> to drive the mandatory "AI suggestion" safety badge
/// and exposes tappable <see cref="Citations"/>.
/// </summary>
public sealed class ChatMessageViewModel
{
    public ChatMessageViewModel(
        string role,
        string content,
        bool isSuggestion,
        IReadOnlyList<Citation>? citations = null,
        string? timestamp = null,
        bool isDegradedNotice = false,
        AiRoute? route = null,
        string? model = null)
    {
        Role = role;
        Route = route;
        Model = string.IsNullOrWhiteSpace(model) ? null : model;
        Content = content;
        IsSuggestion = isSuggestion;
        IsDegradedNotice = isDegradedNotice;
        Timestamp = timestamp ?? DateTimeOffset.Now.ToString("HH:mm:ss");

        RoleLabel = role switch
        {
            AiChatRoles.User => "Technician",
            AiChatRoles.Assistant => "AI Assistant",
            _ => "System Status",
        };

        RoleGlyph = role switch
        {
            AiChatRoles.User => "\uE77B",
            AiChatRoles.Assistant => "\uE8BD",
            _ => "\uE946",
        };

        if (citations is not null)
        {
            foreach (var c in citations)
            {
                Citations.Add(new CitationItemViewModel(c));
            }
        }
    }

    public string Role { get; }

    public string RoleLabel { get; }

    public string RoleGlyph { get; }

    public string Content { get; }

    public string Timestamp { get; }

    /// <summary>
    /// True for every model-generated answer (<see cref="ChatResponse.IsSuggestion"/>);
    /// drives the explicit "AI output is a suggestion" label (ARCHITECTURE.md §4.5 #3).
    /// </summary>
    public bool IsSuggestion { get; }

    public string SuggestionBadgeText => "AI SUGGESTION — Technician makes the final call";

    public Visibility SuggestionBadgeVisibility =>
        IsSuggestion ? Visibility.Visible : Visibility.Collapsed;

    public bool IsDegradedNotice { get; }

    /// <summary>Route that produced this answer (ADR-013); null for technician and status rows.</summary>
    public AiRoute? Route { get; }

    /// <summary>Model label that answered, from the AI service config.</summary>
    public string? Model { get; }

    /// <summary>"This tablet", "Shop server" or "CLOUD" (upper-case so it is never mistaken for an on-tablet answer).</summary>
    public string RouteBadgeText => AiRouting.BadgeText(Route);

    public string RouteBadgeToolTip => Model is null
        ? $"Answered by: {RouteBadgeText}"
        : $"Answered by: {RouteBadgeText} — model {Model}";

    public bool IsCloudAnswer => Route == AiRoute.Cloud;

    public Visibility RouteBadgeVisibility =>
        Route is not null && !IsCloudAnswer ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloudRouteBadgeVisibility =>
        IsCloudAnswer ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DegradedNoticeVisibility =>
        IsDegradedNotice ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<CitationItemViewModel> Citations { get; } = new();

    public bool HasCitations => Citations.Count > 0;

    public Visibility CitationsVisibility =>
        Citations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public HorizontalAlignment CardHorizontalAlignment =>
        string.Equals(Role, AiChatRoles.User, StringComparison.OrdinalIgnoreCase)
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
}

/// <summary>
/// View-model row for a manual chunk returned by <see cref="GseTechPro.Core.Abstractions.IAiClient.SearchManualsFullAsync"/>.
/// Badges results as "TEXT-ONLY (BM25 FALLBACK)" when <see cref="SearchManualsResponse.Fallback"/> is true.
/// </summary>
public sealed class ManualSearchResultViewModel
{
    public ManualSearchResultViewModel(SearchResult result, bool isFallback)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        IsFallback = isFallback;
    }

    public SearchResult Result { get; }

    public bool IsFallback { get; }

    public string ChunkId => Result.ChunkId;

    public string ManualId => Result.ManualId;

    public string ManualTitle => string.IsNullOrWhiteSpace(Result.ManualTitle) ? "Repair Manual" : Result.ManualTitle;

    public int PageStart => Result.PageStart > 0 ? Result.PageStart : 1;

    public int PageEnd => Result.PageEnd >= PageStart ? Result.PageEnd : PageStart;

    public string PageRangeLabel =>
        PageStart == PageEnd ? $"Page {PageStart}" : $"Pages {PageStart}–{PageEnd}";

    public string Section => string.IsNullOrWhiteSpace(Result.Section) ? "General" : Result.Section;

    public string Text => Result.Text;

    public string Citation => !string.IsNullOrWhiteSpace(Result.Citation)
        ? Result.Citation
        : $"{ManualTitle}, page {PageStart}";

    public string ScoreLabel => $"Score: {Result.Score:F2}";

    public Visibility FallbackBadgeVisibility =>
        IsFallback ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Equipment model option in the Guided Troubleshooting and Manual Readings screens.
/// </summary>
public sealed class SelectableEquipmentModelItem
{
    public SelectableEquipmentModelItem(string id, string displayLabel, EquipmentModel? model = null)
    {
        Id = id;
        DisplayLabel = displayLabel;
        Model = model;
    }

    public SelectableEquipmentModelItem(EquipmentModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        Id = model.Id;
        var cat = FormatCategoryLabel(model.Category);
        DisplayLabel = $"{model.Make} {model.Model} [{cat}]";
    }

    public EquipmentModel? Model { get; }

    public string Id { get; }

    public string DisplayLabel { get; }

    public static string FormatCategoryLabel(EquipmentCategory? category) => category switch
    {
        EquipmentCategory.BeltLoader => "Belt Loader",
        EquipmentCategory.Tug => "Tug / Tractor",
        EquipmentCategory.Gpu => "Ground Power Unit",
        EquipmentCategory.AirStart => "Air Start Unit",
        EquipmentCategory.Pushback => "Pushback Tractor",
        EquipmentCategory.Deicer => "Deicer",
        EquipmentCategory.LavatoryTruck => "Lavatory Truck",
        EquipmentCategory.Loader => "Cargo Loader",
        _ => category?.ToString() ?? "Equipment",
    };

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Symptom option in the Guided Troubleshooting screen.
/// </summary>
public sealed class SelectableSymptomItem
{
    public SelectableSymptomItem(string? id, string displayLabel)
    {
        Id = id;
        DisplayLabel = displayLabel;
    }

    public SelectableSymptomItem(Symptom symptom)
    {
        Id = symptom.Id;
        DisplayLabel = $"{symptom.Name} ({symptom.Category})";
    }

    public string? Id { get; }

    public string DisplayLabel { get; }

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Deterministic unit &amp; spec conversion helpers for Phase 4 GSE troubleshooting (ADR-009 §2).
/// Maps <see cref="ReadingType"/> to canonical SI units, formats canonical specs in the shop's display
/// unit system, and computes <see cref="PassFail"/> verdicts without ever inventing thresholds.
/// </summary>
public static class TroubleshootingUnitHelper
{
    public const string DefaultTodoSpecSource = "TODO: needs spec from manual";

    /// <summary>
    /// Canonical SI unit for a <see cref="ReadingType"/> per ADR-009 §2:
    /// hydraulic_pressure / fuel_pressure → Pa, temperature → °C, flow → L/min, voltage → V, current → A.
    /// </summary>
    public static string? TryGetCanonicalUnit(ReadingType? readingType) => readingType switch
    {
        ReadingType.HydraulicPressure or ReadingType.FuelPressure => UnitConverter.Pascal,
        ReadingType.Temperature => UnitConverter.Celsius,
        ReadingType.Flow => UnitConverter.LiterPerMinute,
        ReadingType.Voltage => UnitConverter.Volt,
        ReadingType.Current => UnitConverter.Ampere,
        _ => null,
    };

    /// <summary>
    /// Default display unit for a <see cref="ReadingType"/> under the given <see cref="UnitSystem"/>.
    /// </summary>
    public static string GetDefaultDisplayUnit(ReadingType? readingType, UnitSystem unitSystem)
    {
        var canonical = TryGetCanonicalUnit(readingType);
        if (canonical is not null && UnitConverter.IsKnownUnit(canonical))
        {
            return UnitConverter.DisplayUnit(canonical, unitSystem);
        }

        return UnitConverter.Volt;
    }

    /// <summary>
    /// Computes <see cref="PassFail"/> of a canonical SI value against canonical SI bounds (ADR-009 §5).
    /// Returns <see cref="PassFail.NotApplicable"/> when both bounds are null OR when both bounds are
    /// present and equal (<c>specMin == specMax</c>, a single-value setting with no documented tolerance).
    /// Never fabricates a threshold or judges by strict equality.
    /// </summary>
    public static PassFail ComputePassFail(double valueCanonical, double? specMinCanonical, double? specMaxCanonical)
    {
        if (specMinCanonical is null && specMaxCanonical is null)
            return PassFail.NotApplicable;

        if (specMinCanonical is double eqMin && specMaxCanonical is double eqMax && eqMin == eqMax)
            return PassFail.NotApplicable;

        if (double.IsNaN(valueCanonical) || double.IsInfinity(valueCanonical))
            return PassFail.NotApplicable;

        if (specMinCanonical is double min && valueCanonical < min)
            return PassFail.Fail;

        if (specMaxCanonical is double max && valueCanonical > max)
            return PassFail.Fail;

        return PassFail.Pass;
    }

    /// <summary>
    /// Formats a display-unit spec value cleanly (e.g. 2300 psi → "2,300", 12.6 V → "12.6").
    /// </summary>
    public static string FormatSpecNumber(double value, string? unit = null)
    {
        if (unit is UnitConverter.Psi or UnitConverter.Pascal or UnitConverter.KiloPascal)
        {
            if (Math.Abs(value - Math.Round(value)) < 0.05)
                return Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);

            return value.ToString("#,0.#", CultureInfo.InvariantCulture);
        }

        if (Math.Abs(value - Math.Round(value)) < 1e-4)
            return Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);

        return value.ToString("#,0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats canonical SI <paramref name="specMinCanonical"/>/<paramref name="specMaxCanonical"/> into the
    /// shop's <paramref name="unitSystem"/> (e.g. "Expected: 2,300 – 2,500 psi (Citation)").
    /// When there is no numeric spec, returns the citation/source only.
    /// </summary>
    public static string FormatCanonicalSpecSummary(
        ReadingType? readingType,
        double? specMinCanonical,
        double? specMaxCanonical,
        string? specSource,
        UnitSystem unitSystem)
    {
        var source = string.IsNullOrWhiteSpace(specSource)
            ? DefaultTodoSpecSource
            : specSource.Trim();

        if (specMinCanonical is null && specMaxCanonical is null)
            return source;

        var canonicalUnit = TryGetCanonicalUnit(readingType);
        if (canonicalUnit is not null && UnitConverter.IsKnownUnit(canonicalUnit))
        {
            if (specMinCanonical is double minCanon && specMaxCanonical is double maxCanon)
            {
                var (minDisp, dispUnit) = UnitConverter.ToDisplay(minCanon, canonicalUnit, unitSystem);
                var (maxDisp, _) = UnitConverter.ToDisplay(maxCanon, canonicalUnit, unitSystem);
                return $"Expected: {FormatSpecNumber(minDisp, dispUnit)} – {FormatSpecNumber(maxDisp, dispUnit)} {dispUnit} ({source})";
            }

            if (specMinCanonical is double minOnlyCanon)
            {
                var (minDisp, dispUnit) = UnitConverter.ToDisplay(minOnlyCanon, canonicalUnit, unitSystem);
                return $"Expected: >= {FormatSpecNumber(minDisp, dispUnit)} {dispUnit} ({source})";
            }

            if (specMaxCanonical is double maxOnlyCanon)
            {
                var (maxDisp, dispUnit) = UnitConverter.ToDisplay(maxOnlyCanon, canonicalUnit, unitSystem);
                return $"Expected: <= {FormatSpecNumber(maxDisp, dispUnit)} {dispUnit} ({source})";
            }
        }

        if (specMinCanonical is double rawMin && specMaxCanonical is double rawMax)
            return $"Expected: {rawMin:G6} – {rawMax:G6} ({source})";
        if (specMinCanonical is double rawMinOnly)
            return $"Expected: >= {rawMinOnly:G6} ({source})";
        if (specMaxCanonical is double rawMaxOnly)
            return $"Expected: <= {rawMaxOnly:G6} ({source})";

        return source;
    }

    /// <summary>
    /// ADR-009 §3 safety warning severity order:
    /// hydraulic_pressure (0), lockout_tagout (1), high_voltage (2), aircraft_proximity (3), general (4).
    /// </summary>
    public static int GetSafetyWarningSeverityRank(SafetyWarningCategory category) => category switch
    {
        SafetyWarningCategory.HydraulicPressure => 0,
        SafetyWarningCategory.LockoutTagout => 1,
        SafetyWarningCategory.HighVoltage => 2,
        SafetyWarningCategory.AircraftProximity => 3,
        SafetyWarningCategory.General => 4,
        _ => 5,
    };

    /// <summary>
    /// Parses a JSON array of safety-warning slug IDs (e.g. <c>["sw-high-voltage","sw-lockout-tagout"]</c>).
    /// </summary>
    public static IReadOnlyList<string> ParseSafetyWarningIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list is null || list.Count == 0)
                return Array.Empty<string>();

            return list
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Theme-aware <see cref="Color"/> for <see cref="PassFail"/> verdicts:
    /// Pass = success green, Fail = danger red, NotApplicable = neutral secondary text.
    /// </summary>
    public static Color GetPassFailColor(PassFail? verdict, bool isDarkTheme, bool isSunlightMode) =>
        (verdict ?? PassFail.NotApplicable, isSunlightMode, isDarkTheme) switch
        {
            (PassFail.Pass, true, _) => Color.FromArgb(0xFF, 0x00, 0x71, 0x00),       // Sunlight #007100
            (PassFail.Pass, false, true) => Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),   // Dark #6CCB5F
            (PassFail.Pass, false, false) => Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F),  // Light #0F7B0F

            (PassFail.Fail, true, _) => Color.FromArgb(0xFF, 0xB3, 0x00, 0x00),       // Sunlight #B30000
            (PassFail.Fail, false, true) => Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4),   // Dark #FF99A4
            (PassFail.Fail, false, false) => Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C),  // Light #C42B1C

            (_, true, _) => Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),                   // Sunlight #1A1A1A
            (_, false, true) => Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0),               // Dark #A0A0A0
            (_, false, false) => Color.FromArgb(0xFF, 0x5C, 0x5C, 0x5C),              // Light #5C5C5C
        };

    /// <summary>
    /// Theme-aware brush for <see cref="PassFail"/> verdicts:
    /// Pass = success green, Fail = danger red, NotApplicable = neutral secondary text.
    /// Safe when called in headless xUnit tests where no WinUI XAML thread is active.
    /// </summary>
    public static SolidColorBrush? CreatePassFailBrush(PassFail? verdict, bool isDarkTheme, bool isSunlightMode)
    {
        var color = GetPassFailColor(verdict, isDarkTheme, isSunlightMode);
        try
        {
            return new SolidColorBrush(color);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatReadingTypeLabel(ReadingType readingType) => readingType switch
    {
        ReadingType.HydraulicPressure => "Hydraulic Pressure (hydraulic_pressure)",
        ReadingType.FuelPressure => "Fuel Pressure (fuel_pressure)",
        ReadingType.Temperature => "Temperature (temperature)",
        ReadingType.Flow => "Flow (flow)",
        ReadingType.Voltage => "Voltage (voltage)",
        ReadingType.Current => "Current (current)",
        _ => "Other (other)",
    };
}

/// <summary>
/// View-model item for a resolved <see cref="SafetyWarning"/> displayed prominently before reading entry.
/// </summary>
public sealed class SafetyWarningItemViewModel
{
    public SafetyWarningItemViewModel(SafetyWarning warning)
    {
        Warning = warning ?? throw new ArgumentNullException(nameof(warning));
        SeverityRank = TroubleshootingUnitHelper.GetSafetyWarningSeverityRank(warning.Category);
        CategoryBadge = warning.Category switch
        {
            SafetyWarningCategory.HydraulicPressure => "STORED HYDRAULIC PRESSURE",
            SafetyWarningCategory.LockoutTagout => "LOCKOUT / TAGOUT",
            SafetyWarningCategory.HighVoltage => "HIGH VOLTAGE / BATTERY ENERGY",
            SafetyWarningCategory.AircraftProximity => "AIRCRAFT PROXIMITY / RAMP",
            _ => "GENERAL SAFETY",
        };
        SourceCitation = string.IsNullOrWhiteSpace(warning.SourceCitation)
            ? TroubleshootingUnitHelper.DefaultTodoSpecSource
            : warning.SourceCitation!;
    }

    public SafetyWarning Warning { get; }

    public string Id => Warning.Id;

    public SafetyWarningCategory Category => Warning.Category;

    public int SeverityRank { get; }

    public string CategoryBadge { get; }

    public string Text => Warning.Text;

    public string SourceCitation { get; }
}

/// <summary>
/// Picker item for a <see cref="TestPoint"/> (or the Free-Entry mode option when <see cref="TestPoint"/> is null).
/// Canonicalizes <see cref="TestPoint.SpecMin"/>/<see cref="TestPoint.SpecMax"/> from <see cref="TestPoint.ExpectedUnit"/>
/// per ADR-009 §2.
/// </summary>
public sealed class SelectableTestPointItem
{
    public SelectableTestPointItem(TestPoint? testPoint)
    {
        TestPoint = testPoint;

        if (testPoint is not null &&
            (testPoint.SpecMin.HasValue || testPoint.SpecMax.HasValue) &&
            !string.IsNullOrWhiteSpace(testPoint.ExpectedUnit) &&
            UnitConverter.IsKnownUnit(testPoint.ExpectedUnit))
        {
            var expectedCanon = UnitConverter.CanonicalUnit(testPoint.ExpectedUnit);
            var typeCanon = TroubleshootingUnitHelper.TryGetCanonicalUnit(testPoint.ReadingType);
            if (typeCanon is null || string.Equals(expectedCanon, typeCanon, StringComparison.Ordinal))
            {
                CanonicalSpecUnit = expectedCanon;
                CanonicalSpecMin = testPoint.SpecMin.HasValue
                    ? UnitConverter.ToCanonical(testPoint.SpecMin.Value, testPoint.ExpectedUnit)
                    : null;
                CanonicalSpecMax = testPoint.SpecMax.HasValue
                    ? UnitConverter.ToCanonical(testPoint.SpecMax.Value, testPoint.ExpectedUnit)
                    : null;
            }
        }
    }

    public TestPoint? TestPoint { get; }

    public bool IsFreeEntry => TestPoint is null;

    public string? Id => TestPoint?.Id;

    public string EquipmentModelId => TestPoint?.EquipmentModelId ?? "";

    public string Name => TestPoint?.Name ?? "Free-Entry Reading (No Test Point)";

    public string System => string.IsNullOrWhiteSpace(TestPoint?.System) ? "Custom / Free Entry" : TestPoint!.System!;

    public string Location => string.IsNullOrWhiteSpace(TestPoint?.Location)
        ? "Free-entry measurement (no predefined test port selected)."
        : TestPoint!.Location!;

    public ReadingType ReadingType => TestPoint?.ReadingType ?? ReadingType.Voltage;

    public string ReadingTypeLabel => IsFreeEntry
        ? "Free-Entry (Select reading type below)"
        : TroubleshootingUnitHelper.FormatReadingTypeLabel(ReadingType);

    public string ExpectedUnit => !string.IsNullOrWhiteSpace(TestPoint?.ExpectedUnit)
        ? TestPoint!.ExpectedUnit!
        : UnitConverter.Volt;

    public double? SpecMin => TestPoint?.SpecMin;

    public double? SpecMax => TestPoint?.SpecMax;

    public double? CanonicalSpecMin { get; }

    public double? CanonicalSpecMax { get; }

    public string? CanonicalSpecUnit { get; }

    public string SpecSource => string.IsNullOrWhiteSpace(TestPoint?.SpecSource)
        ? TroubleshootingUnitHelper.DefaultTodoSpecSource
        : TestPoint!.SpecSource!;

    public string ProcedureRef => string.IsNullOrWhiteSpace(TestPoint?.ProcedureRef)
        ? "Follow manufacturer service manual safety and test procedures."
        : TestPoint!.ProcedureRef!;

    public string AuthoredSpecRangeText
    {
        get
        {
            if (IsFreeEntry)
                return $"Free-Entry — {TroubleshootingUnitHelper.DefaultTodoSpecSource}";

            if (SpecMin is double min && SpecMax is double max)
            {
                return $"{TroubleshootingUnitHelper.FormatSpecNumber(min, ExpectedUnit)} – {TroubleshootingUnitHelper.FormatSpecNumber(max, ExpectedUnit)} {ExpectedUnit}";
            }

            if (SpecMin is double minOnly)
            {
                return $">= {TroubleshootingUnitHelper.FormatSpecNumber(minOnly, ExpectedUnit)} {ExpectedUnit}";
            }

            if (SpecMax is double maxOnly)
            {
                return $"<= {TroubleshootingUnitHelper.FormatSpecNumber(maxOnly, ExpectedUnit)} {ExpectedUnit}";
            }

            return TroubleshootingUnitHelper.DefaultTodoSpecSource;
        }
    }

    public string DisplayLabel => IsFreeEntry
        ? "Free-Entry Reading (No Test Point)"
        : $"{Name} [{EquipmentModelId} · {System} · {ExpectedUnit}]";

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// View-model row for a saved <see cref="Reading"/> on the active work order.
/// Surfaces the entered value + unit, canonical SI value + unit, spec range + citation,
/// pass/fail color (pass = success green, fail = danger red, na = neutral), and the
/// "SIMULATED" provenance badge when <see cref="Reading.IsSimulated"/> is true (ADR-009 §4).
/// </summary>
public sealed class SavedReadingRowViewModel
{
    public SavedReadingRowViewModel(
        Reading reading,
        string? testPointName,
        UnitSystem unitSystem,
        bool isDarkTheme,
        bool isSunlightMode)
    {
        Reading = reading ?? throw new ArgumentNullException(nameof(reading));

        TargetName = !string.IsNullOrWhiteSpace(testPointName)
            ? testPointName!
            : !string.IsNullOrWhiteSpace(reading.ChannelComponent)
                ? reading.ChannelComponent!
                : !string.IsNullOrWhiteSpace(reading.TestPointId)
                    ? reading.TestPointId!
                    : "Free-Entry Reading";

        SystemLabel = string.IsNullOrWhiteSpace(reading.System) ? reading.ReadingType.ToString() : reading.System!;
        ReadingTypeLabel = TroubleshootingUnitHelper.FormatReadingTypeLabel(reading.ReadingType);

        var enteredVal = reading.EnteredValue ?? reading.ValueCanonical;
        var enteredUnit = !string.IsNullOrWhiteSpace(reading.EnteredUnit) ? reading.EnteredUnit! : reading.UnitCanonical;
        EnteredValueAndUnit = $"{TroubleshootingUnitHelper.FormatSpecNumber(enteredVal, enteredUnit)} {enteredUnit}";
        CanonicalValueAndUnit = $"{reading.ValueCanonical:G6} {reading.UnitCanonical}";

        if (UnitConverter.IsKnownUnit(reading.UnitCanonical))
        {
            var (dispVal, dispUnit) = UnitConverter.ToDisplay(reading.ValueCanonical, reading.UnitCanonical, unitSystem);
            DisplayValueAndUnit = $"{TroubleshootingUnitHelper.FormatSpecNumber(dispVal, dispUnit)} {dispUnit}";
        }
        else
        {
            DisplayValueAndUnit = EnteredValueAndUnit;
        }

        SpecSummary = TroubleshootingUnitHelper.FormatCanonicalSpecSummary(
            reading.ReadingType,
            reading.SpecMin,
            reading.SpecMax,
            reading.SpecSource,
            unitSystem);

        PassFail = reading.PassFail ?? PassFail.NotApplicable;
        PassFailLabel = PassFail switch
        {
            PassFail.Pass => "PASS",
            PassFail.Fail => "FAIL",
            _ => "N/A",
        };

        PassFailColor = TroubleshootingUnitHelper.GetPassFailColor(PassFail, isDarkTheme, isSunlightMode);
        PassFailBrush = TroubleshootingUnitHelper.CreatePassFailBrush(PassFail, isDarkTheme, isSunlightMode);
        IsSimulated = reading.IsSimulated;
        ProvenanceBadgeText = IsSimulated ? "SIMULATED" : "MANUAL";
    }

    public Reading Reading { get; }

    public string Id => Reading.Id;

    public string WorkOrderId => Reading.WorkOrderId;

    public string? TestPointId => Reading.TestPointId;

    public string TargetName { get; }

    public string SystemLabel { get; }

    public string ReadingTypeLabel { get; }

    public string EnteredValueAndUnit { get; }

    public string CanonicalValueAndUnit { get; }

    public string DisplayValueAndUnit { get; }

    public string SpecSummary { get; }

    public string SpecSource => string.IsNullOrWhiteSpace(Reading.SpecSource)
        ? TroubleshootingUnitHelper.DefaultTodoSpecSource
        : Reading.SpecSource!;

    public PassFail PassFail { get; }

    public string PassFailLabel { get; }

    public Color PassFailColor { get; }

    public SolidColorBrush? PassFailBrush { get; }

    public bool IsSimulated { get; }

    public string ProvenanceBadgeText { get; }

    public Visibility SimulatedBadgeVisibility => IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ManualBadgeVisibility => !IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    public string EnteredAt => Reading.EnteredAt;
}

/// <summary>
/// View-model item for a single <see cref="RepairCandidate"/> returned by
/// <see cref="GseTechPro.Core.Abstractions.IAiClient.SearchRepairHistoryAsync"/> (ADR-010).
/// Preserves the service-assigned <see cref="Rank"/> (0 = best), renders the smoothed
/// <see cref="RepairCandidate.SuccessRate"/> alongside the raw <see cref="RepairCandidate.Evidence"/>,
/// and surfaces <see cref="RepairCandidate.AverageLaborHours"/> and <see cref="RepairCandidate.LastDoneAt"/>
/// when present.
/// </summary>
public sealed partial class RepairCandidateItemViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public RepairCandidateItemViewModel(
        RepairCandidate candidate,
        int rank,
        bool isFallback,
        string? recommendationRowId = null,
        bool isAccepted = false)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Rank = rank;
        IsFallback = isFallback;
        RecommendationRowId = recommendationRowId;
        _isAccepted = isAccepted;

        RankBadgeText = $"#{rank + 1}";
        SystemDisplay = string.IsNullOrWhiteSpace(candidate.System) ? "General / Unspecified" : candidate.System!;
        SuccessRateDisplay = string.Create(CultureInfo.InvariantCulture, $"{candidate.SuccessRate * 100.0:F1}%");
        SimilarityScoreDisplay = string.Create(CultureInfo.InvariantCulture, $"{candidate.SimilarityScore * 100.0:F0}%");
        RawCountsDisplay = string.Create(CultureInfo.InvariantCulture, $"Fixed {candidate.SuccessCount} of {candidate.AttemptCount}");

        TierBadgeText = candidate.MatchTier switch
        {
            MatchTier.Model => "SAME MODEL",
            MatchTier.Family => "SAME FAMILY",
            _ => "ALL FLEET",
        };

        if (candidate.AverageLaborHours is double avgHours && double.IsFinite(avgHours))
        {
            HasAverageLaborHours = true;
            AverageLaborHoursDisplay = string.Create(CultureInfo.InvariantCulture, $"Avg {avgHours:F1} hrs");
        }
        else
        {
            HasAverageLaborHours = false;
            AverageLaborHoursDisplay = "";
        }

        if (!string.IsNullOrWhiteSpace(candidate.LastDoneAt))
        {
            HasLastDoneAt = true;
            if (DateTimeOffset.TryParse(
                    candidate.LastDoneAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedUtc))
            {
                LastDoneAtDisplay = $"Last done: {parsedUtc.UtcDateTime:yyyy-MM-dd}";
            }
            else
            {
                LastDoneAtDisplay = $"Last done: {candidate.LastDoneAt}";
            }
        }
        else
        {
            HasLastDoneAt = false;
            LastDoneAtDisplay = "";
        }
    }

    public RepairCandidate Candidate { get; }

    public int Rank { get; }

    public string RankBadgeText { get; }

    public bool IsFallback { get; }

    public string? RecommendationRowId { get; set; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(AcceptedBadgeVisibility))]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(AcceptButtonLabel))]
    private bool _isAccepted;

    public string CandidateRepairId => Candidate.CandidateRepairId;

    public string RepairDescription => Candidate.RepairDescription;

    public string? System => Candidate.System;

    public string SystemDisplay { get; }

    public MatchTier MatchTier => Candidate.MatchTier;

    public string TierBadgeText { get; }

    public double SimilarityScore => Candidate.SimilarityScore;

    public string SimilarityScoreDisplay { get; }

    public double SuccessRate => Candidate.SuccessRate;

    public string SuccessRateDisplay { get; }

    public int SuccessCount => Candidate.SuccessCount;

    public int AttemptCount => Candidate.AttemptCount;

    public string RawCountsDisplay { get; }

    public double? AverageLaborHours => Candidate.AverageLaborHours;

    public bool HasAverageLaborHours { get; }

    public string AverageLaborHoursDisplay { get; }

    public Visibility AverageLaborHoursVisibility =>
        HasAverageLaborHours ? Visibility.Visible : Visibility.Collapsed;

    public string? LastDoneAt => Candidate.LastDoneAt;

    public bool HasLastDoneAt { get; }

    public string LastDoneAtDisplay { get; }

    public Visibility LastDoneAtVisibility =>
        HasLastDoneAt ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<string> SourceWorkOrderIds => Candidate.SourceWorkOrderIds;

    public string Evidence => Candidate.Evidence;

    public string SuggestionBadgeText => "AI SUGGESTION — Technician makes the final call";

    public Visibility FallbackBadgeVisibility =>
        IsFallback ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AcceptedBadgeVisibility =>
        IsAccepted ? Visibility.Visible : Visibility.Collapsed;

    public string AcceptButtonLabel =>
        IsAccepted ? "Repair Applied to WO" : "Use This Repair";
}

/// <summary>
/// Group of <see cref="RepairCandidateItemViewModel"/> items sharing a <see cref="MatchTier"/>
/// (Model / Same family / All). Preserves the exact order returned by the service without re-sorting.
/// </summary>
public sealed class RecommendationTierGroupViewModel
{
    public RecommendationTierGroupViewModel(MatchTier tier, IEnumerable<RepairCandidateItemViewModel> candidates)
    {
        Tier = tier;
        HeaderTitle = FormatTierHeader(tier);
        HeaderSubtitle = FormatTierSubtitle(tier);
        foreach (var item in candidates)
        {
            Candidates.Add(item);
        }
    }

    public MatchTier Tier { get; }

    public string HeaderTitle { get; }

    public string HeaderSubtitle { get; }

    public ObservableCollection<RepairCandidateItemViewModel> Candidates { get; } = new();

    public int Count => Candidates.Count;

    public static string FormatTierHeader(MatchTier tier) => tier switch
    {
        MatchTier.Model => "Model Matches (Exact Equipment Model)",
        MatchTier.Family => "Same Family Matches (Same Make & Category)",
        _ => "All Equipment / Fleet Matches",
    };

    public static string FormatTierSubtitle(MatchTier tier) => tier switch
    {
        MatchTier.Model => "Tier 1 — Past closed repairs on the same equipment model",
        MatchTier.Family => "Tier 2 — Past closed repairs on the same manufacturer make and equipment category",
        _ => "Tier 3 — Past closed repairs across all other shop equipment and fleet vehicles",
    };
}

/// <summary>
/// View-model wrapper for a <see cref="WorkOrder"/> row in the Work Order Browser (Screenshot 03),
/// surfacing human-readable <see cref="WorkOrder.Number"/> ("WO #000001") alongside status and summary fields.
/// </summary>
public sealed class WorkOrderBrowserRowViewModel
{
    public WorkOrderBrowserRowViewModel(WorkOrder workOrder, string? assetLabel = null, Outcome? outcome = null)
    {
        WorkOrder = workOrder ?? throw new ArgumentNullException(nameof(workOrder));
        Outcome = outcome;

        DisplayNumber = FormatWorkOrderNumber(workOrder);
        AssetDisplay = !string.IsNullOrWhiteSpace(assetLabel)
            ? assetLabel!
            : string.IsNullOrWhiteSpace(workOrder.AssetId) ? "Unassigned" : workOrder.AssetId;
        StatusDisplay = workOrder.Status switch
        {
            WorkOrderStatus.Open => "Open",
            WorkOrderStatus.InProgress => "In Progress",
            WorkOrderStatus.WaitingParts => "Waiting Parts",
            WorkOrderStatus.Closed => "Closed",
            _ => workOrder.Status.ToString(),
        };
        ComplaintDisplay = string.IsNullOrWhiteSpace(workOrder.ComplaintSummary)
            ? "(No complaint summary)"
            : workOrder.ComplaintSummary!;
        RepairDisplay = string.IsNullOrWhiteSpace(workOrder.RepairSummary)
            ? "—"
            : workOrder.RepairSummary!;

        if (outcome is not null)
        {
            OutcomeDisplay = outcome.Result switch
            {
                OutcomeResult.Fixed => outcome.ComebackDetected ? "Fixed (Comeback Confirmed)" : "Fixed",
                OutcomeResult.NotFixed => "Not Fixed",
                OutcomeResult.Unsure => "Unsure",
                _ => outcome.Result.ToString(),
            };
        }
        else
        {
            OutcomeDisplay = workOrder.Status == WorkOrderStatus.Closed ? "Closed" : "Open";
        }
    }

    public WorkOrder WorkOrder { get; }

    public Outcome? Outcome { get; }

    public string Id => WorkOrder.Id;

    public string? Number => WorkOrder.Number;

    public string DisplayNumber { get; }

    public string AssetDisplay { get; }

    public string StatusDisplay { get; }

    public string ComplaintDisplay { get; }

    public string RepairDisplay { get; }

    public string OutcomeDisplay { get; }

    public string OpenedAt => WorkOrder.OpenedAt;

    public string? ClosedAt => WorkOrder.ClosedAt;

    public bool IsClosed => WorkOrder.Status == WorkOrderStatus.Closed;

    public static string FormatWorkOrderNumber(WorkOrder? workOrder)
    {
        if (workOrder is null)
            return "WO #<none>";

        if (!string.IsNullOrWhiteSpace(workOrder.Number))
            return $"WO #{workOrder.Number}";

        var id = workOrder.Id ?? "";
        var shortId = id.Length > 8 ? id[..8] : id;
        return $"WO #{shortId}";
    }
}

