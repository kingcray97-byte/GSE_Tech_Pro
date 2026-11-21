using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace GseTechPro.App.ViewModels;

/// <summary>A test in the one-tap test rail. Tests that are not built yet are listed but cannot be started.</summary>
public sealed record OneTapTestItem(string Id, string Name, string Description, bool IsAvailable, string? ComingNote = null)
{
    public string DisplayLabel => IsAvailable ? Name : $"{Name} ({ComingNote})";
    public override string ToString() => DisplayLabel;
}

/// <summary>One Parameter / Value / Comment row of a one-tap test result.</summary>
public sealed class OneTapResultRowViewModel
{
    public OneTapResultRowViewModel(OneTapResultRow row, bool isDarkTheme, bool isSunlightMode)
    {
        Parameter = row.Parameter;
        Value = row.Value;
        Comment = row.Comment;
        Verdict = row.Verdict;
        VerdictText = row.Verdict switch
        {
            OneTapVerdict.Pass => "PASS",
            OneTapVerdict.Fail => "FAIL",
            _ => "",
        };
        var scopeVerdict = row.Verdict switch
        {
            OneTapVerdict.Pass => ScopeVerdict.Pass,
            OneTapVerdict.Fail => ScopeVerdict.Fail,
            _ => ScopeVerdict.Na,
        };
        VerdictBrush = ScopeMeasurementRowViewModel.CreateSafeBrush(
            ScopeMeasurementRowViewModel.GetVerdictColor(scopeVerdict, isDarkTheme, isSunlightMode));
    }

    public string Parameter { get; }
    public string Value { get; }
    public string Comment { get; }
    public OneTapVerdict Verdict { get; }
    public string VerdictText { get; }
    public SolidColorBrush? VerdictBrush { get; }
    public Visibility VerdictVisibility => Verdict == OneTapVerdict.None ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// PicoDiagnostics-style one-tap tests on the Scope screen (Phase 10D.3): a rail of tests, the
/// trace on top, a Parameter / Value / Comment result table, and Start / Analyse with the test's
/// inputs. 10D.3a is the battery / cranking test; relative compression and cylinder balance follow.
/// Safety warnings are shown first and must be read before Start (rule 6). Verdicts only from a
/// technician-entered limit with its source (rule 5).
/// </summary>
public sealed partial class MainShellViewModel
{
    public const string OneTapBatteryCrankingId = "battery-cranking";

    /// <summary>The same warnings as the Starter Current guided test: a crank is the same hazard.</summary>
    public static IReadOnlyList<string> OneTapBatteryCrankingSafetyWarningIds { get; } =
    [
        "sw-lockout-tagout", "sw-high-voltage", "sw-moving-machinery", "sw-wheel-chocks",
        "sw-aircraft-proximity", "sw-fuel-lpg", "sw-hot-surface",
    ];

    private string? _scopeOneTapSafetyTestName;
    private Task? _scopeOneTapSafetyLoad;

    public IReadOnlyList<OneTapTestItem> ScopeOneTapTests { get; } =
    [
        new(OneTapBatteryCrankingId, "Battery / cranking",
            "Battery voltage on channel A, and starter-cable current on channel B when you have a current clamp. Tap Start, then crank the engine within about 1 s: the capture is 5 s long.",
            IsAvailable: true),
        new("relative-compression", "Relative compression",
            "Cranking current per cylinder.", IsAvailable: false, ComingNote: "coming in 10D.3b"),
        new("cylinder-balance", "Cylinder balance",
            "Engine speed change per cylinder.", IsAvailable: false, ComingNote: "coming in 10D.3c"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeOneTapDescription))]
    [NotifyPropertyChangedFor(nameof(ScopeOneTapBatteryInputsVisibility))]
    private OneTapTestItem? _selectedScopeOneTapTest;

    public string ScopeOneTapDescription => SelectedScopeOneTapTest?.Description ?? "Pick a test.";

    public Visibility ScopeOneTapBatteryInputsVisibility =>
        SelectedScopeOneTapTest?.Id == OneTapBatteryCrankingId ? Visibility.Visible : Visibility.Collapsed;

    // ---- Battery / cranking inputs (all optional except the system voltage) ----

    public IReadOnlyList<string> ScopeCrankingSystemVoltages { get; } = ["12 V", "24 V"];

    /// <summary>Nominal system voltage; picks the channel A range (±20 V for 12 V, ±50 V for 24 V).</summary>
    [ObservableProperty]
    private string _scopeCrankingSystemVoltage = "12 V";

    /// <summary>Current clamp output scale in A/V, from the clamp's own setting (1 mV/A = 1000 A/V). Blank = no clamp.</summary>
    [ObservableProperty]
    private string _scopeCrankingClampText = "";

    [ObservableProperty]
    private string _scopeCrankingRatedCcaText = "";

    public IReadOnlyList<string> ScopeCrankingRatingStandards { get; } = ["SAE CCA", "EN", "DIN", "IEC", "Other"];

    [ObservableProperty]
    private string _scopeCrankingRatingStandard = "SAE CCA";

    [ObservableProperty]
    private string _scopeCrankingMinimumVoltsText = "";

    [ObservableProperty]
    private string _scopeCrankingMinimumSource = "";

    [ObservableProperty]
    private string _scopeCrankingTemperatureText = "";

    public IReadOnlyList<string> ScopeCrankingBatteryTypes { get; } =
        ["Not recorded", "Flooded lead-acid", "AGM", "Gel", "EFB", "Lithium", "Other"];

    [ObservableProperty]
    private string _scopeCrankingBatteryType = "Not recorded";

    // ---- Results ----

    public ObservableCollection<OneTapResultRowViewModel> ScopeOneTapResults { get; } = new();

    [ObservableProperty]
    private string _scopeOneTapStatusText = "Pick a test, read its safety warnings, enter what you know, then tap Start.";

    public Visibility ScopeOneTapNoResultsVisibility =>
        ScopeOneTapResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSelectedScopeOneTapTestChanged(OneTapTestItem? value)
    {
        if (value is null)
            return;
        if (!value.IsAvailable)
        {
            ScopeOneTapStatusText = $"{value.Name} is not built yet ({value.ComingNote}).";
            return;
        }
        _scopeOneTapSafetyLoad = LoadOneTapSafetyWarningsAsync(value);
    }

    /// <summary>Shows the test's safety warnings in full (the guided test, if any, is put aside).</summary>
    private async Task LoadOneTapSafetyWarningsAsync(OneTapTestItem test)
    {
        if (SelectedGuidedTestItem is not null || SelectedGuidedTest is not null)
        {
            _suppressGuidedTestSelectionSync = true;
            try { SelectedGuidedTestItem = null; }
            finally { _suppressGuidedTestSelectionSync = false; }
        }
        await ApplySelectedGuidedTestAsync(null);

        if (_safetyWarningsById.Count == 0)
        {
            try
            {
                foreach (var w in await _dataServices.SafetyWarnings.GetAllAsync())
                    _safetyWarningsById[w.Id] = w;
            }
            catch (Exception ex)
            {
                ReportError("Could not load safety warnings", ex);
            }
        }

        ScopeSafetyWarnings.Clear();
        ScopeSafetyWarningTexts.Clear();
        var items = OneTapBatteryCrankingSafetyWarningIds
            .Where(_safetyWarningsById.ContainsKey)
            .Select(id => new SafetyWarningItemViewModel(_safetyWarningsById[id]))
            .OrderBy(r => r.SeverityRank).ThenBy(r => r.Id, StringComparer.Ordinal);
        foreach (var item in items)
        {
            ScopeSafetyWarnings.Add(item);
            ScopeSafetyWarningTexts.Add(item.Text);
        }

        _scopeOneTapSafetyTestName = test.Name;
        ResetScopeSafetyAcknowledgement();
        HasScopeSafetyWarnings = ScopeSafetyWarnings.Count > 0;
        ScopeOneTapStatusText = HasScopeSafetyWarnings
            ? $"{test.Name}: read the {ScopeSafetyWarnings.Count} safety warnings above, then tap 'I have read these warnings'."
            : $"{test.Name}: the safety warnings could not be loaded, so the test cannot start.";
    }

    /// <summary>True when the one-tap test's own warnings are on screen (not a guided test's, not none).</summary>
    private bool AreOneTapWarningsShownFor(OneTapTestItem test) =>
        SelectedGuidedTest is null && HasScopeSafetyWarnings && _scopeOneTapSafetyTestName == test.Name;

    /// <summary>Sets up the scope for the selected test, captures, then analyses.</summary>
    [RelayCommand]
    public async Task StartScopeOneTapTestAsync()
    {
        var test = SelectedScopeOneTapTest ?? ScopeOneTapTests[0];
        if (!test.IsAvailable)
        {
            ScopeOneTapStatusText = $"{test.Name} is not built yet ({test.ComingNote}).";
            return;
        }
        SelectedScopeOneTapTest ??= test;
        if (_scopeOneTapSafetyLoad is { } loading)
            await loading;

        if (!AreOneTapWarningsShownFor(test))
            await LoadOneTapSafetyWarningsAsync(test);
        if (!HasScopeSafetyWarnings)
            return; // never start without the warnings on screen (rule 6)
        if (IsScopeCaptureBlockedBySafety())
        {
            ScopeOneTapStatusText = ScopeSafetyBlockedMessage;
            return;
        }

        if (!TryReadCrankingInputs(out _, out double? clamp, out string? error))
        {
            ScopeOneTapStatusText = error!;
            return;
        }

        var channels = new List<ScopeChannelConfig>
        {
            new()
            {
                Channel = 1, Coupling = ScopeCoupling.Dc, ProbeMultiplier = 1.0,
                VoltageRangeVolts = ScopeCrankingSystemVoltage == "24 V" ? 50.0 : 20.0,
                Label = "Battery voltage",
            },
        };
        if (clamp is double ampsPerVolt)
        {
            channels.Add(new()
            {
                Channel = 2, Coupling = ScopeCoupling.Dc, ProbeMultiplier = 1.0,
                VoltageRangeVolts = 2.0, // clamp output; the over-range row says when a stronger range is needed
                CurrentScaleAmpsPerVolt = ampsPerVolt,
                Label = "Starter cable current",
            });
        }

        ApplyCaptureRequestToUi(new ScopeCaptureRequest
        {
            Channels = channels,
            TimebaseSecondsPerDivision = 0.5, // 5 s: rest, crank and start
            SampleRateHz = 10_000,
            TriggerMode = ScopeTriggerMode.None,
            TriggerChannel = 1,
        });
        _scopeAutoSetupPinnedPreset = null;

        var before = LastScopeCapture;
        ScopeOneTapStatusText = "Capturing 5 s: crank the engine now.";
        await StartScopeCaptureAsync();
        if (ReferenceEquals(before, LastScopeCapture) || LastScopeCapture is null)
        {
            ScopeOneTapStatusText = $"No capture: {ScopeStatusMessage}";
            return;
        }

        await AnalyseScopeOneTapTestAsync();
    }

    /// <summary>Analyses the capture on screen (a new one, a buffered one, or a saved one).</summary>
    [RelayCommand]
    public async Task AnalyseScopeOneTapTestAsync()
    {
        var capture = LastScopeCapture;
        if (capture is null)
        {
            ScopeOneTapStatusText = "Nothing to analyse yet: tap Start.";
            return;
        }
        if (!capture.Channels.TryGetValue(1, out var volts))
        {
            ScopeOneTapStatusText = "The capture has no channel A (battery voltage).";
            return;
        }
        if (!TryReadCrankingInputs(out var inputs, out _, out string? error))
        {
            ScopeOneTapStatusText = error!;
            return;
        }

        var chA = ScopeChannels.FirstOrDefault(c => c.Channel == 1);
        var chB = ScopeChannels.FirstOrDefault(c => c.Channel == 2);
        double? vRange = chA is null ? null : chA.VoltageRangeVolts * chA.ProbeMultiplier;
        double[]? clampVolts = null;
        double scale = 0.0;
        if (chB is { CurrentScaleAmpsPerVolt: > 0.0 } && capture.Channels.TryGetValue(2, out var b))
        {
            clampVolts = b;
            scale = chB.CurrentScaleAmpsPerVolt;
        }
        double? aRange = clampVolts is null ? null : chB!.VoltageRangeVolts * chB.ProbeMultiplier * scale;
        bool simulated = capture.IsSimulated;
        double rate = capture.SampleRateHz;

        var result = await Task.Run(() =>
        {
            double[]? amps = clampVolts?.Select(v => v * scale).ToArray();
            return CrankingTest.Analyse(volts, amps, rate, inputs!, simulated, vRange, aRange);
        });

        ScopeOneTapResults.Clear();
        foreach (var row in result.Rows)
            ScopeOneTapResults.Add(new OneTapResultRowViewModel(row, IsDarkTheme, IsSunlightMode));
        OnPropertyChanged(nameof(ScopeOneTapNoResultsVisibility));

        ScopeOneTapStatusText = (result.Success ? result.Message : "Could not analyse: " + result.Message)
            + (simulated ? " (simulated)" : "");
        StatusBannerMessage = ScopeOneTapStatusText;
    }

    /// <summary>Reads the battery / cranking inputs. Blank = not entered. Bad numbers give a plain message.</summary>
    private bool TryReadCrankingInputs(out CrankingTestInputs? inputs, out double? clampAmpsPerVolt, out string? error)
    {
        inputs = null;
        clampAmpsPerVolt = null;
        error = null;

        if (!TryOptionalPositive(ScopeCrankingClampText, "Current clamp scale (A/V)", out clampAmpsPerVolt, out error) ||
            !TryOptionalPositive(ScopeCrankingRatedCcaText, "Battery rating (A)", out var cca, out error) ||
            !TryOptionalPositive(ScopeCrankingMinimumVoltsText, "Minimum cranking voltage", out var minV, out error))
            return false;

        double? tempC = null;
        if (!string.IsNullOrWhiteSpace(ScopeCrankingTemperatureText))
        {
            if (!TryParseNumber(ScopeCrankingTemperatureText, out double t) || t < -60 || t > 90)
            {
                error = "Battery temperature must be a number in °C (−60 to 90), or blank.";
                return false;
            }
            tempC = t;
        }

        inputs = new CrankingTestInputs
        {
            RatedCca = cca,
            RatingStandard = ScopeCrankingRatingStandard,
            MinimumCrankingVolts = minV,
            MinimumCrankingSource = string.IsNullOrWhiteSpace(ScopeCrankingMinimumSource) ? null : ScopeCrankingMinimumSource.Trim(),
            BatteryTemperatureC = tempC,
            BatteryType = ScopeCrankingBatteryType == "Not recorded" ? null : ScopeCrankingBatteryType,
        };
        return true;
    }

    private static bool TryOptionalPositive(string? text, string name, out double? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!TryParseNumber(text, out double v) || !(v > 0.0) || !double.IsFinite(v))
        {
            error = $"{name} must be a positive number, or blank.";
            return false;
        }
        value = v;
        return true;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
