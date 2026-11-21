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
    public const string OneTapRelativeCompressionId = "relative-compression";
    public const string OneTapCylinderBalanceId = "cylinder-balance";

    /// <summary>The same warnings as the Starter Current guided test: a crank is the same hazard.</summary>
    public static IReadOnlyList<string> OneTapBatteryCrankingSafetyWarningIds { get; } =
    [
        "sw-lockout-tagout", "sw-high-voltage", "sw-moving-machinery", "sw-wheel-chocks",
        "sw-aircraft-proximity", "sw-fuel-lpg", "sw-hot-surface",
    ];

    private string? _scopeOneTapSafetyTestName;
    private Task? _scopeOneTapSafetyLoad;

    /// <summary>On the simulator, the signal for the one-tap test that set up the scope (both use 500 ms/div, 10 kS/s).</summary>
    private SimulatedSignalPreset? _scopeOneTapSimPreset;

    public IReadOnlyList<OneTapTestItem> ScopeOneTapTests { get; } =
    [
        new(OneTapBatteryCrankingId, "Battery / cranking",
            "Battery voltage on channel A, and starter-cable current on channel B when you have a current clamp. Tap Start, then crank the engine within about 1 s: the capture is 5 s long.",
            IsAvailable: true),
        new(OneTapRelativeCompressionId, "Relative compression",
            "Compares compression between cylinders from the starter current (current clamp on channel B; without one, the battery voltage dips on channel A). Keep the engine from starting as its manual says, tap Start, then crank for the whole 5 s capture, staying within the starter's cranking time limit. Results are % of the strongest cylinder; confirm a low one with a compression gauge.",
            IsAvailable: true),
        new(OneTapCylinderBalanceId, "Cylinder balance",
            "Compares how much each cylinder speeds up the crankshaft, with the engine running at a steady idle. Crank position sensor signal on channel A, connected as the engine manual says. Enter the crank wheel's tooth count (counting missing teeth) and missing teeth from the sensor or wheel spec. Tap Start: the capture is 2 s.",
            IsAvailable: true),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeOneTapDescription))]
    [NotifyPropertyChangedFor(nameof(ScopeOneTapBatteryInputsVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeOneTapCompressionInputsVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeOneTapBalanceInputsVisibility))]
    private OneTapTestItem? _selectedScopeOneTapTest;

    public string ScopeOneTapDescription => SelectedScopeOneTapTest?.Description ?? "Pick a test.";

    public Visibility ScopeOneTapBatteryInputsVisibility =>
        SelectedScopeOneTapTest?.Id == OneTapBatteryCrankingId ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeOneTapCompressionInputsVisibility =>
        SelectedScopeOneTapTest?.Id == OneTapRelativeCompressionId ? Visibility.Visible : Visibility.Collapsed;

    // ---- Relative compression inputs (cylinders and engine cycle required, no defaults) ----

    [ObservableProperty]
    private string _scopeCompressionCylindersText = "";

    public const string ScopeCompressionCyclePick = "Pick...";

    public IReadOnlyList<string> ScopeCompressionEngineCycles { get; } = [ScopeCompressionCyclePick, "4-stroke", "2-stroke"];

    [ObservableProperty]
    private string _scopeCompressionEngineCycle = ScopeCompressionCyclePick;

    [ObservableProperty]
    private string _scopeCompressionMaxSpreadText = "";

    [ObservableProperty]
    private string _scopeCompressionMaxSpreadSource = "";

    public Visibility ScopeOneTapBalanceInputsVisibility =>
        SelectedScopeOneTapTest?.Id == OneTapCylinderBalanceId ? Visibility.Visible : Visibility.Collapsed;

    // ---- Cylinder balance inputs (cylinders and engine cycle are shared with relative compression) ----

    /// <summary>Tooth positions on the crank wheel, counting missing teeth (60 for 60-2). Required, no default.</summary>
    [ObservableProperty]
    private string _scopeBalanceWheelTeethText = "";

    /// <summary>Missing teeth in the reference gap (2 for 60-2, 0 for none). Required, no default.</summary>
    [ObservableProperty]
    private string _scopeBalanceMissingTeethText = "";

    [ObservableProperty]
    private string _scopeBalanceMaxSpreadText = "";

    [ObservableProperty]
    private string _scopeBalanceMaxSpreadSource = "";

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

        if (!TryOptionalPositive(ScopeCrankingClampText, "Current clamp scale (A/V)", out double? clamp, out string? error) ||
            (test.Id == OneTapBatteryCrankingId && !TryReadCrankingInputs(out _, out _, out error)) ||
            (test.Id == OneTapRelativeCompressionId && !TryReadCompressionInputs(out _, out error)) ||
            (test.Id == OneTapCylinderBalanceId && !TryReadBalanceInputs(out _, out error)))
        {
            ScopeOneTapStatusText = error!;
            return;
        }

        if (test.Id == OneTapCylinderBalanceId)
        {
            ApplyCaptureRequestToUi(new ScopeCaptureRequest
            {
                Channels =
                [
                    new ScopeChannelConfig
                    {
                        Channel = 1, Coupling = ScopeCoupling.Dc, ProbeMultiplier = 1.0,
                        VoltageRangeVolts = 20.0, // Hall and inductive sensors; the over-range row says when to go higher
                        Label = "Crank sensor",
                    },
                ],
                TimebaseSecondsPerDivision = 0.2, // 2 s: about a dozen engine cycles at idle
                SampleRateHz = 100_000,           // fine tooth timing
                TriggerMode = ScopeTriggerMode.None,
                TriggerChannel = 1,
            });
            _scopeAutoSetupPinnedPreset = null;
            _scopeOneTapSimPreset = SimulatedSignalPreset.CylinderBalance;
            await CaptureAndAnalyseOneTapAsync("Capturing 2 s: keep the engine at a steady idle.");
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
        _scopeOneTapSimPreset = test.Id == OneTapRelativeCompressionId
            ? SimulatedSignalPreset.RelativeCompression
            : SimulatedSignalPreset.BatteryCranking;

        await CaptureAndAnalyseOneTapAsync(test.Id == OneTapRelativeCompressionId
            ? "Capturing 5 s: crank the engine now, without letting it start."
            : "Capturing 5 s: crank the engine now.");
    }

    private async Task CaptureAndAnalyseOneTapAsync(string capturingText)
    {
        var before = LastScopeCapture;
        ScopeOneTapStatusText = capturingText;
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
            ScopeOneTapStatusText = "The capture has no channel A.";
            return;
        }
        var test = SelectedScopeOneTapTest ?? ScopeOneTapTests[0];
        CrankingTestInputs? inputs = null;
        RelativeCompressionInputs? compression = null;
        CylinderBalanceInputs? balance = null;
        string? error = null;
        bool ok = test.Id switch
        {
            OneTapRelativeCompressionId => TryReadCompressionInputs(out compression, out error),
            OneTapCylinderBalanceId => TryReadBalanceInputs(out balance, out error),
            _ => TryReadCrankingInputs(out inputs, out _, out error),
        };
        if (!ok)
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

        var (success, message, rows) = await Task.Run(() =>
        {
            if (balance is not null)
            {
                var cb = CylinderBalanceTest.Analyse(volts, rate, balance, simulated, vRange);
                return (cb.Success, cb.Message, cb.Rows);
            }
            double[]? amps = clampVolts?.Select(v => v * scale).ToArray();
            if (compression is not null)
            {
                var rc = RelativeCompressionTest.Analyse(volts, amps, rate, compression, simulated);
                return (rc.Success, rc.Message, rc.Rows);
            }
            var cr = CrankingTest.Analyse(volts, amps, rate, inputs!, simulated, vRange, aRange);
            return (cr.Success, cr.Message, cr.Rows);
        });

        ScopeOneTapResults.Clear();
        foreach (var row in rows)
            ScopeOneTapResults.Add(new OneTapResultRowViewModel(row, IsDarkTheme, IsSunlightMode));
        OnPropertyChanged(nameof(ScopeOneTapNoResultsVisibility));

        ScopeOneTapStatusText = (success ? message : "Could not analyse: " + message)
            + (simulated ? " (simulated)" : "");
        StatusBannerMessage = ScopeOneTapStatusText;
    }

    /// <summary>Reads the cylinder balance inputs: engine and crank wheel facts are required, the spread limit optional.</summary>
    private bool TryReadBalanceInputs(out CylinderBalanceInputs? inputs, out string? error)
    {
        inputs = null;
        if (!TryReadEngine(out int cylinders, out int strokes, out error))
            return false;
        if (!int.TryParse(ScopeBalanceWheelTeethText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int teeth) ||
            teeth is < 4 or > 720)
        {
            error = "Enter the crank wheel's tooth count, counting missing teeth (e.g. 60 for 60-2), from the sensor or wheel spec.";
            return false;
        }
        if (!int.TryParse(ScopeBalanceMissingTeethText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int missing) ||
            missing < 0 || missing >= teeth / 2)
        {
            error = "Enter the number of missing teeth in the gap (e.g. 2 for 60-2, 0 when there is no gap).";
            return false;
        }
        if (!TryReadSpread(ScopeBalanceMaxSpreadText, out var spread, out error))
            return false;
        inputs = new CylinderBalanceInputs
        {
            Cylinders = cylinders,
            StrokesPerCycle = strokes,
            WheelTeeth = teeth,
            MissingTeeth = missing,
            MaximumSpreadPercent = spread,
            MaximumSpreadSource = string.IsNullOrWhiteSpace(ScopeBalanceMaxSpreadSource) ? null : ScopeBalanceMaxSpreadSource.Trim(),
        };
        return true;
    }

    /// <summary>Cylinders and engine cycle, shared by relative compression and cylinder balance (same engine).</summary>
    private bool TryReadEngine(out int cylinders, out int strokes, out string? error)
    {
        error = null;
        strokes = 0;
        if (!int.TryParse(ScopeCompressionCylindersText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cylinders) ||
            cylinders is < 1 or > 16)
        {
            error = "Enter the number of cylinders (1–16) from the engine.";
            return false;
        }
        strokes = ScopeCompressionEngineCycle switch { "4-stroke" => 4, "2-stroke" => 2, _ => 0 };
        if (strokes == 0)
        {
            error = "Pick 4-stroke or 2-stroke for the engine.";
            return false;
        }
        return true;
    }

    private static bool TryReadSpread(string? text, out double? spread, out string? error)
    {
        if (!TryOptionalPositive(text, "Maximum spread (%)", out spread, out error))
            return false;
        if (spread > 100.0)
        {
            error = "Maximum spread must be 100 % or less, or blank.";
            return false;
        }
        return true;
    }

    /// <summary>Reads the relative compression inputs: cylinders and engine cycle are required (from the engine).</summary>
    private bool TryReadCompressionInputs(out RelativeCompressionInputs? inputs, out string? error)
    {
        inputs = null;
        if (!TryReadEngine(out int cylinders, out int strokes, out error) ||
            !TryReadSpread(ScopeCompressionMaxSpreadText, out var spread, out error))
            return false;
        inputs = new RelativeCompressionInputs
        {
            Cylinders = cylinders,
            StrokesPerCycle = strokes,
            MaximumSpreadPercent = spread,
            MaximumSpreadSource = string.IsNullOrWhiteSpace(ScopeCompressionMaxSpreadSource) ? null : ScopeCompressionMaxSpreadSource.Trim(),
        };
        return true;
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
