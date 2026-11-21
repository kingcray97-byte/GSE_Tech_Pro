using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GseTechPro.App.ViewModels;

public sealed partial class MainShellViewModel
{
    private IScopeDevice? _activeScopeDevice;
    private CancellationTokenSource? _scopeCaptureCts;
    private long _scopeRenderGeneration;
    private bool _suppressScopeOptionSync;
    private bool _suppressChannelConfigEvents;
    private bool _suppressGuidedTestSelectionSync;
    private readonly Dictionary<string, GuidedTest> _guidedTestsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReferenceWaveform> _referenceWaveformsById = new(StringComparer.Ordinal);

    public IScopeDeviceFactory ScopeDeviceFactory => _scopeDeviceFactory;

    public IScopeDevice? ActiveScopeDevice => _activeScopeDevice;

    // =========================================================================
    // Scope View Visibility & Section Focus
    // =========================================================================

    public bool IsScopeViewVisible =>
        ActiveSectionKey is "Scope" or "ScopeChannels" or "GuidedTests" or "RefWaveforms";

    public Visibility ScopeViewVisibility =>
        IsScopeViewVisible ? Visibility.Visible : Visibility.Collapsed;

    // =========================================================================
    // 1. Scope Device Picker & Connection State
    // =========================================================================

    public ObservableCollection<SelectableScopeDeviceItem> AvailableScopeDevices { get; } = new();

    [ObservableProperty]
    private SelectableScopeDeviceItem? _selectedScopeDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeSimulatedBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeRealBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeProvenanceBadgeText))]
    private bool _isScopeSimulated = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeConnectButtonText))]
    private bool _isScopeConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScopeCaptureIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScopeBufferPreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScopeBufferNextCommand))]
    private bool _isScopeCapturing;

    public bool IsScopeCaptureIdle => !IsScopeCapturing;

    [ObservableProperty]
    private string _scopeDeviceName = SimulatedScopeDevice.DefaultDeviceName;

    [ObservableProperty]
    private string _scopeConnectionStatusText = "Disconnected (Simulator Ready)";

    [ObservableProperty]
    private string _scopeStatusMessage =
        "Select a guided test (or configure CH1–CH4 manually), review mandatory safety warnings, and tap 'Capture Waveform'.";

    public string ScopeConnectButtonText =>
        IsScopeConnected ? "Reconnect Scope" : "Open & Connect Scope";

    public string ScopeProvenanceBadgeText =>
        IsScopeSimulated ? "SIMULATED" : "REAL HARDWARE";

    public Visibility ScopeSimulatedBadgeVisibility =>
        IsScopeSimulated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeRealBadgeVisibility =>
        !IsScopeSimulated ? Visibility.Visible : Visibility.Collapsed;

    // =========================================================================
    // 2. Channel Setup Bar (CH1–CH4)
    // =========================================================================

    public ObservableCollection<ScopeChannelSetupViewModel> ScopeChannels { get; } = new();

    public ScopeChannelSetupViewModel ScopeChannel1 => ScopeChannels[0];
    public ScopeChannelSetupViewModel ScopeChannel2 => ScopeChannels[1];
    public ScopeChannelSetupViewModel ScopeChannel3 => ScopeChannels[2];
    public ScopeChannelSetupViewModel ScopeChannel4 => ScopeChannels[3];

    public Visibility ScopeChannel1TraceVisibility =>
        ScopeChannels.Count > 0 && ScopeChannels[0].IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeChannel2TraceVisibility =>
        ScopeChannels.Count > 1 && ScopeChannels[1].IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeChannel3TraceVisibility =>
        ScopeChannels.Count > 2 && ScopeChannels[2].IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScopeChannel4TraceVisibility =>
        ScopeChannels.Count > 3 && ScopeChannels[3].IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    // =========================================================================
    // 3. Timebase, Sample Rate, Trigger & Simulator Signal Preset
    // =========================================================================

    public IReadOnlyList<SelectableTimebaseOption> ScopeTimebaseOptions { get; } =
        SelectableTimebaseOption.StandardOptions;

    [ObservableProperty]
    private SelectableTimebaseOption? _selectedScopeTimebaseOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeTimebaseDisplay))]
    [NotifyPropertyChangedFor(nameof(ScopeTotalWindowDisplay))]
    private double _scopeTimebaseSecondsPerDivision = 0.005; // 5 ms/div default

    public IReadOnlyList<SelectableSampleRateOption> ScopeSampleRateOptions { get; } =
        SelectableSampleRateOption.StandardOptions;

    [ObservableProperty]
    private SelectableSampleRateOption? _selectedScopeSampleRateOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeSampleRateDisplay))]
    private int _scopeSampleRateHz = 1_000_000; // 1 MS/s default

    public IReadOnlyList<ScopeTriggerMode> ScopeTriggerModes { get; } = Enum.GetValues<ScopeTriggerMode>();

    public IReadOnlyList<int> ScopeTriggerChannels { get; } = [1, 2, 3, 4];

    [ObservableProperty]
    private ScopeTriggerMode _scopeTriggerMode = ScopeTriggerMode.Rising;

    [ObservableProperty]
    private int _scopeTriggerChannel = 1;

    [ObservableProperty]
    private double _scopeTriggerLevelVolts = 2.0;

    public IReadOnlyList<SimulatedSignalPreset> SimulatorSignalPresets { get; } = SimulatedScopePresets.All;

    [ObservableProperty]
    private SimulatedSignalPreset _selectedSimulatorPreset = SimulatedSignalPreset.CrankSensor;

    public string ScopeTimebaseDisplay =>
        SavedWaveformRowViewModel.FormatTimebase(ScopeTimebaseSecondsPerDivision);

    public string ScopeTotalWindowDisplay =>
        $"Screen Window (10 div): {WaveformRenderEngine.FormatTimeValue(ScopeTimebaseSecondsPerDivision * 10.0)}";

    public string ScopeSampleRateDisplay =>
        SavedWaveformRowViewModel.FormatSampleRate(ScopeSampleRateHz);

    partial void OnSelectedScopeTimebaseOptionChanged(SelectableTimebaseOption? value)
    {
        if (_suppressScopeOptionSync || value is null)
            return;

        try
        {
            _suppressScopeOptionSync = true;
            ScopeTimebaseSecondsPerDivision = value.SecondsPerDivision;
        }
        finally
        {
            _suppressScopeOptionSync = false;
        }
    }

    partial void OnScopeTimebaseSecondsPerDivisionChanged(double value)
    {
        RebuildScopeAxisLabels();
        if (_suppressScopeOptionSync)
            return;

        try
        {
            _suppressScopeOptionSync = true;
            var match = ScopeTimebaseOptions.FirstOrDefault(o => Math.Abs(o.SecondsPerDivision - value) < 1e-9);
            if (match is not null)
                SelectedScopeTimebaseOption = match;
        }
        finally
        {
            _suppressScopeOptionSync = false;
        }
    }

    partial void OnSelectedScopeSampleRateOptionChanged(SelectableSampleRateOption? value)
    {
        if (_suppressScopeOptionSync || value is null)
            return;

        try
        {
            _suppressScopeOptionSync = true;
            ScopeSampleRateHz = value.SampleRateHz;
        }
        finally
        {
            _suppressScopeOptionSync = false;
        }
    }

    partial void OnScopeSampleRateHzChanged(int value)
    {
        if (_suppressScopeOptionSync)
            return;

        try
        {
            _suppressScopeOptionSync = true;
            var match = ScopeSampleRateOptions.FirstOrDefault(o => o.SampleRateHz == value);
            if (match is not null)
                SelectedScopeSampleRateOption = match;
        }
        finally
        {
            _suppressScopeOptionSync = false;
        }
    }

    // =========================================================================
    // 4. Waveform Render, Cursors/Rulers, Zoom/Pan & Reference Overlay
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopeCapture))]
    [NotifyPropertyChangedFor(nameof(HasScopeCaptureVisibility))]
    [NotifyPropertyChangedFor(nameof(NoScopeCaptureVisibility))]
    private ScopeCapture? _lastScopeCapture;

    public bool HasScopeCapture => LastScopeCapture is not null;

    public Visibility HasScopeCaptureVisibility =>
        HasScopeCapture ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoScopeCaptureVisibility =>
        !HasScopeCapture ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferenceScopeCapture))]
    [NotifyPropertyChangedFor(nameof(ScopeReferenceOverlayVisibility))]
    private ScopeCapture? _referenceScopeCapture;

    public bool HasReferenceScopeCapture => ReferenceScopeCapture is not null;

    public Visibility ScopeReferenceOverlayVisibility =>
        HasReferenceScopeCapture ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private string _scopeReferenceOverlayStatusText =
        "Reference overlay: no reference .wfm file loaded.";

    // Off-UI-thread computed point arrays (headless-test safe, no XAML requirement)
    public IReadOnlyList<Point> ScopeChannel1RawPoints { get; private set; } = Array.Empty<Point>();
    public IReadOnlyList<Point> ScopeChannel2RawPoints { get; private set; } = Array.Empty<Point>();
    public IReadOnlyList<Point> ScopeChannel3RawPoints { get; private set; } = Array.Empty<Point>();
    public IReadOnlyList<Point> ScopeChannel4RawPoints { get; private set; } = Array.Empty<Point>();
    public IReadOnlyList<Point> ScopeReferenceOverlayRawPoints { get; private set; } = Array.Empty<Point>();

    // UI-bound Polyline PointCollections
    [ObservableProperty]
    private PointCollection? _scopeChannel1Points;

    [ObservableProperty]
    private PointCollection? _scopeChannel2Points;

    [ObservableProperty]
    private PointCollection? _scopeChannel3Points;

    [ObservableProperty]
    private PointCollection? _scopeChannel4Points;

    [ObservableProperty]
    private PointCollection? _scopeReferenceOverlayPoints;

    [ObservableProperty]
    private string _scopeScaleTopLabel = "+5.0 V";

    [ObservableProperty]
    private string _scopeScaleMidLabel = "0.0 V";

    [ObservableProperty]
    private string _scopeScaleBottomLabel = "-5.0 V";

    // Zoom & Pan
    public IReadOnlyList<double> ScopeZoomFactors { get; } = [1.0, 2.0, 4.0, 8.0, 16.0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeZoomLabel))]
    private double _scopeZoomFactor = 1.0;

    [ObservableProperty]
    private double _scopePanPercent;

    public string ScopeZoomLabel =>
        string.Create(CultureInfo.InvariantCulture, $"Zoom: {ScopeZoomFactor:0.#}x (Pan {ScopePanPercent:F0}%)");

    // Cursors / Rulers (Cursor A & Cursor B)
    [ObservableProperty]
    private double _scopeCursorAPercent = 25.0;

    [ObservableProperty]
    private double _scopeCursorBPercent = 75.0;

    [ObservableProperty]
    private double _scopeCursorAX = 250.0;

    [ObservableProperty]
    private double _scopeCursorBX = 750.0;

    [ObservableProperty]
    private string _scopeCursorALabel = "Cursor A: t = 0.000 ms | —";

    [ObservableProperty]
    private string _scopeCursorBLabel = "Cursor B: t = 0.000 ms | —";

    [ObservableProperty]
    private string _scopeCursorDeltaLabel = "Rulers: Δt = 0.000 ms | 1/Δt = — Hz | ΔV = 0.000 V";

    partial void OnScopeZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ScopeZoomLabel));
        _ = RefreshScopeWaveformRenderAsync();
    }

    partial void OnScopePanPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ScopeZoomLabel));
        _ = RefreshScopeWaveformRenderAsync();
    }

    partial void OnScopeCursorAPercentChanged(double value) => UpdateScopeCursorReadouts();
    partial void OnScopeCursorBPercentChanged(double value) => UpdateScopeCursorReadouts();

    [RelayCommand]
    public void ScopeZoomIn()
    {
        ScopeZoomFactor = Math.Min(16.0, ScopeZoomFactor * 2.0);
    }

    [RelayCommand]
    public void ScopeZoomOut()
    {
        ScopeZoomFactor = Math.Max(1.0, ScopeZoomFactor * 0.5);
        if (Math.Abs(ScopeZoomFactor - 1.0) < 1e-6)
            ScopePanPercent = 0.0;
    }

    [RelayCommand]
    public void ScopeResetZoom()
    {
        ScopeZoomFactor = 1.0;
        ScopePanPercent = 0.0;
    }

    // =========================================================================
    // 5. Guided Tests Runner & Safety Warnings First (ADR-011 §7)
    // =========================================================================

    public ObservableCollection<SelectableGuidedTestItem> GuidedScopeTests { get; } = new();

    [ObservableProperty]
    private SelectableGuidedTestItem? _selectedGuidedTestItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGuidedTest))]
    [NotifyPropertyChangedFor(nameof(SelectedGuidedTestVisibility))]
    [NotifyPropertyChangedFor(nameof(SelectedGuidedTestNameDisplay))]
    [NotifyPropertyChangedFor(nameof(SelectedGuidedTestDescriptionDisplay))]
    [NotifyPropertyChangedFor(nameof(SelectedGuidedTestProcedureDisplay))]
    private GuidedTest? _selectedGuidedTest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveReferenceCitationDisplay))]
    [NotifyPropertyChangedFor(nameof(ActiveReferenceNameDisplay))]
    private ReferenceWaveform? _activeReferenceWaveform;

    [ObservableProperty]
    private ReferenceExpectations? _activeReferenceExpectations;

    public ObservableCollection<SafetyWarningItemViewModel> ScopeSafetyWarnings { get; } = new();

    public ObservableCollection<string> ScopeSafetyWarningTexts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeSafetyWarningsVisibility))]
    private bool _hasScopeSafetyWarnings;

    public Visibility ScopeSafetyWarningsVisibility =>
        HasScopeSafetyWarnings ? Visibility.Visible : Visibility.Collapsed;

    public bool HasSelectedGuidedTest => SelectedGuidedTest is not null;

    public Visibility SelectedGuidedTestVisibility =>
        HasSelectedGuidedTest ? Visibility.Visible : Visibility.Collapsed;

    public string SelectedGuidedTestNameDisplay =>
        SelectedGuidedTest?.Name ?? "Manual Oscilloscope Setup (No Guided Test Selected)";

    public string SelectedGuidedTestDescriptionDisplay =>
        !string.IsNullOrWhiteSpace(SelectedGuidedTest?.Description)
            ? SelectedGuidedTest!.Description!
            : "Choose one of the 6 automotive guided scope tests above to load its safety warnings, channel setup, trigger recipe, and reference expectations.";

    public string SelectedGuidedTestProcedureDisplay =>
        !string.IsNullOrWhiteSpace(SelectedGuidedTest?.Procedure)
            ? SelectedGuidedTest!.Procedure!
            : "Follow OEM maintenance manual safety and probing procedures.";

    public string ActiveReferenceNameDisplay =>
        ActiveReferenceWaveform?.Name ?? "No Reference Selected";

    public string ActiveReferenceCitationDisplay =>
        !string.IsNullOrWhiteSpace(ActiveReferenceWaveform?.Citation)
            ? ActiveReferenceWaveform!.Citation!
            : TroubleshootingUnitHelper.DefaultTodoSpecSource;

    partial void OnSelectedGuidedTestItemChanged(SelectableGuidedTestItem? value)
    {
        _scopeAutoSetupPinnedPreset = null;
        // Keep the library browser's highlight in step with the picked test.
        SelectedScopeGuidedBrowserTest = value is null
            ? null
            : ScopeGuidedBrowserTests.FirstOrDefault(t => t.Id == value.Id);
        if (_suppressGuidedTestSelectionSync)
            return;

        _ = ApplySelectedGuidedTestAsync(value?.GuidedTest, value?.ReferenceWaveform);
    }

    // =========================================================================
    // 6. Measurements Panel & Reference Comparison Verdicts (ADR-011 §4/§5)
    // =========================================================================

    public ObservableCollection<ScopeMeasurementRowViewModel> ScopeMeasurements { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeOverallVerdict))]
    [NotifyPropertyChangedFor(nameof(ScopeOverallVerdictCode))]
    [NotifyPropertyChangedFor(nameof(ScopeOverallVerdictDisplay))]
    [NotifyPropertyChangedFor(nameof(ScopeOverallVerdictBrush))]
    private ScopeComparisonResult? _lastScopeComparisonResult;

    public ScopeVerdict ScopeOverallVerdict =>
        LastScopeComparisonResult?.Overall ?? ScopeVerdict.Na;

    public string ScopeOverallVerdictCode => ScopeOverallVerdict switch
    {
        ScopeVerdict.Pass => "pass",
        ScopeVerdict.Fail => "fail",
        _ => "na",
    };

    public string ScopeOverallVerdictDisplay
    {
        get
        {
            if (LastScopeComparisonResult is null)
                return "OVERALL VERDICT: N/A (No capture yet)";

            bool anyTodo = ScopeMeasurements.Any(m => m.IsTodoExpectation);
            return LastScopeComparisonResult.Overall switch
            {
                ScopeVerdict.Pass => "OVERALL VERDICT: PASS — Within Reference Tolerance",
                ScopeVerdict.Fail => "OVERALL VERDICT: FAIL — Outside Reference Tolerance",
                _ => anyTodo
                    ? $"OVERALL VERDICT: N/A ({WaveformComparer.TodoReason})"
                    : "OVERALL VERDICT: N/A (No reference expectation or un-computable signal)",
            };
        }
    }

    public SolidColorBrush? ScopeOverallVerdictBrush =>
        ScopeMeasurementRowViewModel.CreateSafeBrush(
            ScopeMeasurementRowViewModel.GetVerdictColor(ScopeOverallVerdict, IsDarkTheme, IsSunlightMode));

    public Visibility NoScopeMeasurementsVisibility =>
        ScopeMeasurements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // =========================================================================
    // 7. Saved Waveform Library (Work Order Persistence — ADR-011 §6)
    // =========================================================================

    public ObservableCollection<SavedWaveformRowViewModel> WorkOrderWaveforms { get; } = new();

    [ObservableProperty]
    private SavedWaveformRowViewModel? _selectedSavedWaveform;

    public bool HasNoWorkOrderWaveforms => WorkOrderWaveforms.Count == 0;

    public Visibility NoWorkOrderWaveformsVisibility =>
        HasNoWorkOrderWaveforms ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSelectedSavedWaveformChanged(SavedWaveformRowViewModel? value)
    {
        if (value is not null)
        {
            _ = LoadSavedWaveformAsync(value);
        }
    }

    // =========================================================================
    // Initialization & Catalog Loading
    // =========================================================================

    private void InitializeScopeState()
    {
        ScopeChannels.Clear();
        for (int ch = 1; ch <= 4; ch++)
        {
            var vm = new ScopeChannelSetupViewModel(ch, isEnabled: ch == 1);
            vm.ConfigurationChanged += OnScopeChannelConfigurationChanged;
            vm.PropertyChanged += OnScopeChannelLabelPropertyChanged;
            ScopeChannels.Add(vm);
        }

        _suppressScopeOptionSync = true;
        try
        {
            SelectedScopeTimebaseOption = ScopeTimebaseOptions.FirstOrDefault(o => Math.Abs(o.SecondsPerDivision - 0.005) < 1e-9)
                                          ?? ScopeTimebaseOptions[0];
            ScopeTimebaseSecondsPerDivision = SelectedScopeTimebaseOption.SecondsPerDivision;

            SelectedScopeSampleRateOption = ScopeSampleRateOptions.FirstOrDefault(o => o.SampleRateHz == 1_000_000)
                                            ?? ScopeSampleRateOptions[0];
            ScopeSampleRateHz = SelectedScopeSampleRateOption.SampleRateHz;
        }
        finally
        {
            _suppressScopeOptionSync = false;
        }

        // Seed default simulator entry so device picker is always populated immediately
        var simItem = new SelectableScopeDeviceItem(SimulatedScopeDeviceFactory.SimulatorDeviceInfo);
        AvailableScopeDevices.Add(simItem);
        SelectedScopeDevice = simItem;
    }

    private void OnScopeChannelConfigurationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ScopeChannel1TraceVisibility));
        OnPropertyChanged(nameof(ScopeChannel2TraceVisibility));
        OnPropertyChanged(nameof(ScopeChannel3TraceVisibility));
        OnPropertyChanged(nameof(ScopeChannel4TraceVisibility));
        RebuildScopeAxisLabels();

        if (!_suppressChannelConfigEvents && LastScopeCapture is not null && !IsScopeCapturing)
        {
            _ = RefreshScopeWaveformRenderAsync();
        }
    }

    public async Task EnsureScopeCatalogAndDevicesLoadedAsync()
    {
        if (AvailableScopeDevices.Count <= 1)
        {
            await DiscoverScopeDevicesAsync();
        }

        if (GuidedScopeTests.Count == 0)
        {
            await ReloadScopeCatalogAsync();
        }

        await ReloadWorkOrderWaveformsAsync();
    }

    [RelayCommand]
    public async Task DiscoverScopeDevicesAsync()
    {
        var previousId = SelectedScopeDevice?.Id;

        try
        {
            var discovered = await _scopeDeviceFactory.DiscoverAsync();
            AvailableScopeDevices.Clear();

            foreach (var info in discovered)
            {
                AvailableScopeDevices.Add(new SelectableScopeDeviceItem(info));
            }

            if (AvailableScopeDevices.Count == 0)
            {
                AvailableScopeDevices.Add(new SelectableScopeDeviceItem(SimulatedScopeDeviceFactory.SimulatorDeviceInfo));
            }

            SelectedScopeDevice = AvailableScopeDevices.FirstOrDefault(d =>
                                      !string.IsNullOrWhiteSpace(previousId) &&
                                      string.Equals(d.Id, previousId, StringComparison.OrdinalIgnoreCase))
                                  ?? AvailableScopeDevices.FirstOrDefault(d => d.IsSimulated)
                                  ?? AvailableScopeDevices[0];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] Scope discovery fallback to simulator: {ex.Message}");
            if (AvailableScopeDevices.Count == 0)
            {
                var simItem = new SelectableScopeDeviceItem(SimulatedScopeDeviceFactory.SimulatorDeviceInfo);
                AvailableScopeDevices.Add(simItem);
                SelectedScopeDevice = simItem;
            }
        }
    }

    [RelayCommand]
    public async Task OpenSelectedScopeDeviceAsync()
    {
        var chosen = SelectedScopeDevice?.Device ?? SimulatedScopeDeviceFactory.SimulatorDeviceInfo;
        await OpenScopeDeviceAsync(chosen);
    }

    public async Task<IScopeDevice> OpenScopeDeviceAsync(ScopeDeviceInfo? deviceInfo = null)
    {
        await DisconnectScopeDeviceAsync();

        var targetInfo = deviceInfo
                         ?? SelectedScopeDevice?.Device
                         ?? SimulatedScopeDeviceFactory.SimulatorDeviceInfo;

        var device = await _scopeDeviceFactory.OpenAsync(targetInfo);
        await device.ConnectAsync();
        _activeScopeDevice = device;

        IsScopeConnected = device.IsConnected;
        IsScopeSimulated = device.IsSimulated;
        ScopeDeviceName = device.Name;
        ScopeConnectionStatusText = device.IsSimulated
            ? $"Connected: {device.Name} (SIMULATED)"
            : $"Connected: {device.Name} (REAL HARDWARE)";

        // Apply current channel setup to the opened device
        foreach (var chVm in ScopeChannels.Where(c => c.IsEnabled))
        {
            await device.ConfigureChannelAsync(chVm.Channel, chVm.ToChannelConfig());
        }

        // If a guided test is already selected and this is the simulator, configure its matching preset
        if (device is SimulatedScopeDevice simDevice && SelectedGuidedTest is not null)
        {
            if (!simDevice.TryConfigurePreset(SelectedGuidedTest.Name))
            {
                simDevice.TryConfigurePreset(SelectedGuidedTest.Id);
            }

            foreach (var chVm in ScopeChannels.Where(c => c.IsEnabled))
            {
                await device.ConfigureChannelAsync(chVm.Channel, chVm.ToChannelConfig());
            }
        }

        ScopeStatusMessage = $"Scope connected: {device.Name} ({(device.IsSimulated ? "SIMULATED" : "REAL")}).";
        StatusBannerMessage = ScopeStatusMessage;
        return device;
    }

    [RelayCommand]
    public async Task DisconnectScopeDeviceAsync()
    {
        if (_scopeCaptureCts is not null)
        {
            try { _scopeCaptureCts.Cancel(); }
            catch { }
            _scopeCaptureCts.Dispose();
            _scopeCaptureCts = null;
        }

        if (_activeScopeDevice is not null)
        {
            try
            {
                await _activeScopeDevice.DisconnectAsync();
            }
            catch
            {
            }
            _activeScopeDevice = null;
        }

        IsScopeConnected = false;
        IsScopeCapturing = false;
        ScopeConnectionStatusText = "Disconnected";
    }

    public async Task ReloadScopeCatalogAsync()
    {
        try
        {
            if (_safetyWarningsById.Count == 0)
            {
                var warnings = await _dataServices.SafetyWarnings.GetAllAsync();
                foreach (var w in warnings)
                {
                    _safetyWarningsById[w.Id] = w;
                }
            }

            var refWaveforms = await _dataServices.ReferenceWaveforms.GetAllAsync();
            _referenceWaveformsById.Clear();
            foreach (var rw in refWaveforms)
            {
                _referenceWaveformsById[rw.Id] = rw;
            }

            var tests = await _dataServices.GuidedTests.GetAllAsync();
            _guidedTestsById.Clear();

            var previousTestId = SelectedGuidedTest?.Id;
            _suppressGuidedTestSelectionSync = true;
            try
            {
                GuidedScopeTests.Clear();
                foreach (var gt in tests)
                {
                    _guidedTestsById[gt.Id] = gt;
                    ReferenceWaveform? linkedRef = null;
                    if (!string.IsNullOrWhiteSpace(gt.ReferenceWaveformId))
                    {
                        _referenceWaveformsById.TryGetValue(gt.ReferenceWaveformId!, out linkedRef);
                    }
                    GuidedScopeTests.Add(new SelectableGuidedTestItem(gt, linkedRef));
                }

                if (!string.IsNullOrWhiteSpace(previousTestId))
                {
                    SelectedGuidedTestItem = GuidedScopeTests.FirstOrDefault(x =>
                        string.Equals(x.Id, previousTestId, StringComparison.Ordinal));
                }
            }
            finally
            {
                _suppressGuidedTestSelectionSync = false;
            }

            await RebuildScopeGuidedLibraryAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] ReloadScopeCatalogAsync notice: {ex.Message}");
        }
    }

    // =========================================================================
    // Guided Test Selection: Safety Warnings FIRST, then Apply scope_setup_json
    // =========================================================================

    /// <summary>
    /// Selects a guided scope test by its ID or name (e.g. <c>"gt-crank-sensor"</c> or <c>"Crank Sensor (CKP) Signal"</c>),
    /// resolves and displays its safety warnings FIRST, applies its <c>scope_setup_json</c> capture request,
    /// configures the simulator preset when the simulator is open, and loads the linked reference expectations.
    /// </summary>
    public async Task<GuidedTest?> SelectGuidedScopeTestAsync(string testIdOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testIdOrName);

        if (GuidedScopeTests.Count == 0)
        {
            await ReloadScopeCatalogAsync();
        }

        var item = GuidedScopeTests.FirstOrDefault(t =>
                       string.Equals(t.Id, testIdOrName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(t.Name, testIdOrName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                       t.Name.Contains(testIdOrName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            _suppressGuidedTestSelectionSync = true;
            try
            {
                SelectedGuidedTestItem = item;
            }
            finally
            {
                _suppressGuidedTestSelectionSync = false;
            }

            await ApplySelectedGuidedTestAsync(item.GuidedTest, item.ReferenceWaveform);
            return item.GuidedTest;
        }

        var fromDb = await _dataServices.GuidedTests.GetByIdWithReferenceAsync(testIdOrName.Trim());
        if (fromDb is not null)
        {
            await ApplySelectedGuidedTestAsync(fromDb.GuidedTest, fromDb.ReferenceWaveform);
            return fromDb.GuidedTest;
        }

        return null;
    }

    private async Task ApplySelectedGuidedTestAsync(GuidedTest? test, ReferenceWaveform? preloadedReference = null)
    {
        if (test is null)
        {
            SelectedGuidedTest = null;
            ActiveReferenceWaveform = null;
            ActiveReferenceExpectations = null;
            ScopeSafetyWarnings.Clear();
            ScopeSafetyWarningTexts.Clear();
            HasScopeSafetyWarnings = false;
            ResetScopeSafetyAcknowledgement();
            ReferenceScopeCapture = null;
            ScopeReferenceOverlayStatusText = "Reference overlay: no guided test selected.";
            return;
        }

        // Ensure safety warnings catalog is loaded from SQLite before resolving safety_warning_ids
        if (_safetyWarningsById.Count == 0)
        {
            try
            {
                var warnings = await _dataServices.SafetyWarnings.GetAllAsync();
                foreach (var w in warnings)
                {
                    _safetyWarningsById[w.Id] = w;
                }
            }
            catch
            {
            }
        }

        // 1. Resolve and display safety warnings FIRST (ADR-011 §7 / AGENTS.md Rule 6)
        ScopeSafetyWarnings.Clear();
        ScopeSafetyWarningTexts.Clear();

        var warningIds = TroubleshootingUnitHelper.ParseSafetyWarningIds(test.SafetyWarningIds);
        var resolvedWarnings = new List<SafetyWarningItemViewModel>();
        foreach (var wid in warningIds)
        {
            if (_safetyWarningsById.TryGetValue(wid, out var sw))
            {
                resolvedWarnings.Add(new SafetyWarningItemViewModel(sw));
            }
        }

        foreach (var item in resolvedWarnings.OrderBy(r => r.SeverityRank).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            ScopeSafetyWarnings.Add(item);
            ScopeSafetyWarningTexts.Add(item.Text);
        }

        ResetScopeSafetyAcknowledgement();
        HasScopeSafetyWarnings = ScopeSafetyWarnings.Count > 0;

        // 2. Resolve linked ReferenceWaveform & expectations
        ReferenceWaveform? reference = preloadedReference;
        if (reference is null && !string.IsNullOrWhiteSpace(test.ReferenceWaveformId))
        {
            if (!_referenceWaveformsById.TryGetValue(test.ReferenceWaveformId!, out reference))
            {
                try
                {
                    var withRef = await _dataServices.GuidedTests.GetByIdWithReferenceAsync(test.Id);
                    reference = withRef?.ReferenceWaveform;
                    if (reference is not null)
                    {
                        _referenceWaveformsById[reference.Id] = reference;
                    }
                }
                catch
                {
                }
            }
        }

        SelectedGuidedTest = test;
        ActiveReferenceWaveform = reference;
        ActiveReferenceExpectations = ScopeJson.DeserializeReferenceExpectations(reference?.ExpectedJson);

        // 3. Apply scope_setup_json (channels, timebase, sample rate, trigger) to UI & open device
        var captureSetup = ScopeJson.DeserializeCaptureRequest(test.ScopeSetupJson);
        if (captureSetup is not null)
        {
            ApplyCaptureRequestToUi(captureSetup);
        }

        // 4. Configure simulator preset if open (or sync SelectedSimulatorPreset)
        if (SimulatedScopePresets.TryResolvePreset(test.Name, out var matchedPreset) ||
            SimulatedScopePresets.TryResolvePreset(test.Id, out matchedPreset))
        {
            SelectedSimulatorPreset = matchedPreset;
        }
        else if (captureSetup is not null &&
                 SimulatedScopePresets.TryMatchFromCaptureRequest(captureSetup) is SimulatedSignalPreset inferredPreset)
        {
            SelectedSimulatorPreset = inferredPreset;
        }

        if (_activeScopeDevice is { IsConnected: true })
        {
            if (_activeScopeDevice is SimulatedScopeDevice simDevice)
            {
                if (!simDevice.TryConfigurePreset(test.Name))
                {
                    simDevice.TryConfigurePreset(test.Id);
                }
            }

            if (captureSetup is not null)
            {
                foreach (var chConfig in captureSetup.Channels)
                {
                    await _activeScopeDevice.ConfigureChannelAsync(chConfig.Channel, chConfig);
                }
            }
        }

        // 5. Load optional reference .wfm overlay when reference.DataPath is non-empty
        await LoadReferenceOverlayIfPresentAsync(reference);

        ScopeStatusMessage =
            $"Guided test '{test.Name}' loaded: {ScopeSafetyWarnings.Count} mandatory safety warning(s) displayed first, scope setup applied.";
        StatusBannerMessage = ScopeStatusMessage;
    }

    private async Task LoadReferenceOverlayIfPresentAsync(ReferenceWaveform? reference)
    {
        ReferenceScopeCapture = null;
        ScopeReferenceOverlayRawPoints = Array.Empty<Point>();
        ScopeReferenceOverlayPoints = null;

        if (reference is null || string.IsNullOrWhiteSpace(reference.DataPath))
        {
            ScopeReferenceOverlayStatusText = reference is null
                ? "Reference overlay: no reference waveform linked."
                : $"Reference '{reference.Name}': numeric expectations loaded ({ActiveReferenceCitationDisplay}); no reference .wfm overlay file.";
            return;
        }

        var fullPath = ResolveScopeDataFilePath(reference.DataPath);
        if (!File.Exists(fullPath))
        {
            ScopeReferenceOverlayStatusText =
                $"Reference '{reference.Name}': overlay path '{reference.DataPath}' not found on disk; numeric expectations active.";
            return;
        }

        try
        {
            var refCapture = await Task.Run(() => WaveformFile.ReadAsync(fullPath));
            ReferenceScopeCapture = refCapture;
            ScopeReferenceOverlayStatusText =
                $"Reference overlay active: '{reference.Name}' ({refCapture.SampleCount:N0} samples @ {SavedWaveformRowViewModel.FormatSampleRate(refCapture.SampleRateHz)}).";
            await RefreshScopeWaveformRenderAsync();
        }
        catch (Exception ex)
        {
            ScopeReferenceOverlayStatusText = $"Reference overlay load error: {ex.Message}";
        }
    }

    // =========================================================================
    // Capture Request Building & Applying
    // =========================================================================

    public void ApplyCaptureRequestToUi(ScopeCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _suppressChannelConfigEvents = true;
        try
        {
            var byChannel = request.Channels.ToDictionary(c => c.Channel, c => c);
            foreach (var chVm in ScopeChannels)
            {
                if (byChannel.TryGetValue(chVm.Channel, out var cfg))
                {
                    chVm.ApplyConfig(cfg, enable: true);
                }
                else
                {
                    chVm.IsEnabled = false;
                }
            }
        }
        finally
        {
            _suppressChannelConfigEvents = false;
        }

        if (request.TimebaseSecondsPerDivision > 0.0 && double.IsFinite(request.TimebaseSecondsPerDivision))
        {
            ScopeTimebaseSecondsPerDivision = request.TimebaseSecondsPerDivision;
        }

        if (request.SampleRateHz >= 1)
        {
            ScopeSampleRateHz = request.SampleRateHz;
        }

        ScopeTriggerMode = request.TriggerMode;
        if (request.TriggerChannel >= 1 && request.TriggerChannel <= 4)
        {
            ScopeTriggerChannel = request.TriggerChannel;
        }
        ScopeTriggerLevelVolts = request.TriggerLevelVolts;
    }

    public ScopeCaptureRequest BuildScopeCaptureRequest()
    {
        var enabledConfigs = ScopeChannels
            .Where(c => c.IsEnabled)
            .Select(c => c.ToChannelConfig())
            .ToList();

        if (enabledConfigs.Count == 0 && ScopeChannels.Count > 0)
        {
            ScopeChannels[0].IsEnabled = true;
            enabledConfigs.Add(ScopeChannels[0].ToChannelConfig());
        }

        int trigCh = ScopeTriggerChannel;
        if (ScopeTriggerMode != ScopeTriggerMode.None &&
            enabledConfigs.All(c => c.Channel != trigCh))
        {
            trigCh = enabledConfigs[0].Channel;
        }

        return new ScopeCaptureRequest
        {
            Channels = enabledConfigs,
            SampleRateHz = Math.Max(1, ScopeSampleRateHz),
            TimebaseSecondsPerDivision = ScopeTimebaseSecondsPerDivision > 0.0 ? ScopeTimebaseSecondsPerDivision : 0.005,
            TriggerMode = ScopeTriggerMode,
            TriggerChannel = trigCh,
            TriggerLevelVolts = ScopeTriggerLevelVolts,
        };
    }

    // =========================================================================
    // Capture → WaveformMath → WaveformComparer → Off-UI-Thread Render
    // =========================================================================

    [RelayCommand]
    public async Task StartScopeCaptureAsync()
    {
        if (IsScopeCapturing)
            return;
        if (IsScopeCaptureBlockedBySafety())
            return;

        // Ensure a scope device is open and connected before arming the capture token
        if (_activeScopeDevice is null || !_activeScopeDevice.IsConnected)
        {
            await OpenSelectedScopeDeviceAsync();
        }

        _scopeCaptureCts?.Cancel();
        _scopeCaptureCts?.Dispose();
        _scopeCaptureCts = new CancellationTokenSource();
        var ct = _scopeCaptureCts.Token;

        IsScopeCapturing = true;
        try
        {
            var device = _activeScopeDevice!;
            var request = BuildScopeCaptureRequest();

            ConfigureSimulatorPresetFor(device, request);

            // Configure enabled channels on the device
            foreach (var chConfig in request.Channels)
            {
                await device.ConfigureChannelAsync(chConfig.Channel, chConfig, ct);
            }

            ScopeStatusMessage = $"Capturing {request.Channels.Count} channel(s) @ {SavedWaveformRowViewModel.FormatSampleRate(request.SampleRateHz)} ({SavedWaveformRowViewModel.FormatTimebase(request.TimebaseSecondsPerDivision)})…";

            // Arm and run capture + measurement math + comparison on background task so UI thread never blocks
            var (capture, rawMeasurements, comparisonResult) = await Task.Run(async () =>
            {
                await device.StartCaptureAsync(request, ct).ConfigureAwait(false);
                var cap = await device.ReadCaptureAsync(ct).ConfigureAwait(false);

                var allMeasurements = new List<ScopeMeasurement>(request.Channels.Count * 7);
                foreach (var chConfig in request.Channels.OrderBy(c => c.Channel))
                {
                    if (cap.Channels.TryGetValue(chConfig.Channel, out var samples))
                    {
                        var chMeas = WaveformMath.ComputeMeasurements(
                            samples,
                            cap.SampleRateHz,
                            chConfig,
                            chConfig.Channel,
                            isSimulated: cap.IsSimulated || device.IsSimulated);
                        allMeasurements.AddRange(chMeas);
                    }
                }

                var compared = WaveformComparer.CompareMeasurements(
                    allMeasurements,
                    ActiveReferenceExpectations,
                    ActiveReferenceWaveform?.Id);

                return (cap, allMeasurements, compared);
            }, ct);

            IsScopeSimulated = capture.IsSimulated || device.IsSimulated;
            LastScopeCapture = capture;
            PushScopeBuffer(capture, comparisonResult, IsScopeSimulated);

            ApplyScopeComparisonResult(comparisonResult, ActiveReferenceExpectations);
            await RefreshScopeWaveformRenderAsync();

            ScopeStatusMessage =
                $"Captured {capture.SampleCount:N0} samples/ch @ {SavedWaveformRowViewModel.FormatSampleRate(capture.SampleRateHz)} · {ScopeOverallVerdictDisplay}.";
            StatusBannerMessage = ScopeStatusMessage;
        }
        catch (OperationCanceledException)
        {
            ScopeStatusMessage = "Scope capture stopped.";
            StatusBannerMessage = ScopeStatusMessage;
        }
        catch (Exception ex)
        {
            ReportError("Scope capture failed", ex);
            ScopeStatusMessage = $"Scope capture failed: {ex.Message}";
        }
        finally
        {
            IsScopeCapturing = false;
        }
    }

    /// <summary>On the simulator, picks the signal for the technician's setup (guided test, inferred, or chosen preset).</summary>
    private void ConfigureSimulatorPresetFor(IScopeDevice device, ScopeCaptureRequest request)
    {
        if (device is not SimulatedScopeDevice simDevice)
            return;

        if (SelectedGuidedTest is not null)
        {
            if (!simDevice.TryConfigurePreset(SelectedGuidedTest.Name))
                simDevice.TryConfigurePreset(SelectedGuidedTest.Id);
        }
        else if (_scopeAutoSetupPinnedPreset is SimulatedSignalPreset pinned)
        {
            // Auto setup changed the settings for this signal; keep simulating it rather than re-inferring.
            simDevice.ConfigurePreset(pinned);
        }
        else if (SimulatedScopePresets.TryMatchFromCaptureRequest(request) is SimulatedSignalPreset inferred)
        {
            simDevice.ConfigurePreset(inferred);
        }
        else
        {
            simDevice.ConfigurePreset(SelectedSimulatorPreset);
        }
    }

    [RelayCommand]
    public async Task StopScopeCaptureAsync()
    {
        CancelScopeRepeat();
        if (_scopeCaptureCts is not null)
        {
            try { _scopeCaptureCts.Cancel(); }
            catch { }
        }

        if (_activeScopeDevice is not null)
        {
            try
            {
                await _activeScopeDevice.StopCaptureAsync();
            }
            catch
            {
            }
        }

        IsScopeCapturing = false;
        ScopeStatusMessage = "Scope capture stopped.";
    }

    /// <summary>
    /// End-to-end guided test runner helper: selects the specified (or currently selected) guided test,
    /// displays its safety warnings first, applies <c>scope_setup_json</c>, runs the capture,
    /// computes measurements, and compares against the reference expectations.
    /// </summary>
    [RelayCommand]
    public async Task RunGuidedScopeTestAsync(string? testIdOrName = null)
    {
        if (!string.IsNullOrWhiteSpace(testIdOrName))
        {
            await SelectGuidedScopeTestAsync(testIdOrName);
        }
        else if (SelectedGuidedTest is null && GuidedScopeTests.Count > 0)
        {
            SelectedGuidedTestItem = GuidedScopeTests[0];
            await ApplySelectedGuidedTestAsync(GuidedScopeTests[0].GuidedTest, GuidedScopeTests[0].ReferenceWaveform);
        }

        await StartScopeCaptureAsync();
    }

    private void ApplyScopeComparisonResult(
        ScopeComparisonResult comparisonResult,
        ReferenceExpectations? expectations)
    {
        LastScopeComparisonResult = comparisonResult;
        ScopeMeasurements.Clear();

        var todoSet = new HashSet<(int Channel, MeasurementKind Kind)>();
        if (expectations?.Measurements is not null)
        {
            foreach (var exp in expectations.Measurements)
            {
                if (exp.IsTodo)
                {
                    todoSet.Add((exp.Channel, exp.Kind));
                }
            }
        }

        foreach (var m in comparisonResult.Measurements)
        {
            bool isTodo = todoSet.Contains((m.Channel, m.Kind));
            ScopeMeasurements.Add(new ScopeMeasurementRowViewModel(
                m,
                IsDarkTheme,
                IsSunlightMode,
                isTodoExpectation: isTodo));
        }

        OnPropertyChanged(nameof(NoScopeMeasurementsVisibility));
        OnPropertyChanged(nameof(ScopeOverallVerdict));
        OnPropertyChanged(nameof(ScopeOverallVerdictCode));
        OnPropertyChanged(nameof(ScopeOverallVerdictDisplay));
        OnPropertyChanged(nameof(ScopeOverallVerdictBrush));
    }

    private void RebuildScopeMeasurementRowsForTheme()
    {
        if (LastScopeComparisonResult is null)
            return;

        ApplyScopeComparisonResult(LastScopeComparisonResult, ActiveReferenceExpectations);
    }

    /// <summary>
    /// Decimates the capture's per-channel samples to screen width on a background worker thread
    /// and marshals only the final <see cref="Point"/> arrays to the UI thread.
    /// </summary>
    public async Task RefreshScopeWaveformRenderAsync()
    {
        long gen = Interlocked.Increment(ref _scopeRenderGeneration);
        var capture = LastScopeCapture;
        var refCapture = ReferenceScopeCapture;
        if (capture is null)
        {
            if (gen == Interlocked.Read(ref _scopeRenderGeneration))
            {
                ScopeChannel1RawPoints = Array.Empty<Point>();
                ScopeChannel2RawPoints = Array.Empty<Point>();
                ScopeChannel3RawPoints = Array.Empty<Point>();
                ScopeChannel4RawPoints = Array.Empty<Point>();
                ScopeReferenceOverlayRawPoints = Array.Empty<Point>();
                ApplyPointCollectionsOnUiThread();
                UpdateScopeCursorReadouts();
                RebuildScopeAxisLabels();
            }
            return;
        }

        double zoom = ScopeZoomFactor;
        double panNorm = Math.Clamp(ScopePanPercent / 100.0, 0.0, 1.0);

        var chConfigs = ScopeChannels.ToDictionary(
            c => c.Channel,
            c => (c.IsEnabled, Range: Math.Max(0.1, c.VoltageRangeVolts)));

        // Compute primary vertical scale labels from the first enabled channel
        var primaryCh = ScopeChannels.FirstOrDefault(c => c.IsEnabled) ?? ScopeChannels[0];
        double primaryRange = Math.Max(0.1, primaryCh.VoltageRangeVolts);
        ScopeScaleTopLabel = string.Create(CultureInfo.InvariantCulture, $"+{primaryRange:G4} V ({primaryCh.ChannelLabel})");
        ScopeScaleMidLabel = "0.0 V";
        ScopeScaleBottomLabel = string.Create(CultureInfo.InvariantCulture, $"-{primaryRange:G4} V ({primaryCh.ChannelLabel})");

        // Off-UI-thread decimation worker
        var (pts1, pts2, pts3, pts4, refPts) = await Task.Run(() =>
        {
            Point[] DecimateForChannel(int ch)
            {
                if (!chConfigs.TryGetValue(ch, out var cfg) || !cfg.IsEnabled)
                    return Array.Empty<Point>();
                if (!capture.Channels.TryGetValue(ch, out var samples))
                    return Array.Empty<Point>();

                return WaveformRenderEngine.DecimateChannelSamples(
                    samples,
                    cfg.Range,
                    WaveformRenderEngine.DefaultCanvasWidth,
                    WaveformRenderEngine.DefaultCanvasHeight,
                    zoom,
                    panNorm);
            }

            Point[] p1 = DecimateForChannel(1);
            Point[] p2 = DecimateForChannel(2);
            Point[] p3 = DecimateForChannel(3);
            Point[] p4 = DecimateForChannel(4);

            Point[] pRef = Array.Empty<Point>();
            if (refCapture is not null && refCapture.Channels.Count > 0)
            {
                var refChEntry = refCapture.Channels.OrderBy(k => k.Key).First();
                double refRange = chConfigs.TryGetValue(refChEntry.Key, out var rc) ? rc.Range : primaryRange;
                pRef = WaveformRenderEngine.DecimateChannelSamples(
                    refChEntry.Value,
                    refRange,
                    WaveformRenderEngine.DefaultCanvasWidth,
                    WaveformRenderEngine.DefaultCanvasHeight,
                    zoom,
                    panNorm);
            }

            return (p1, p2, p3, p4, pRef);
        });

        if (gen != Interlocked.Read(ref _scopeRenderGeneration))
            return;

        ScopeChannel1RawPoints = pts1;
        ScopeChannel2RawPoints = pts2;
        ScopeChannel3RawPoints = pts3;
        ScopeChannel4RawPoints = pts4;
        ScopeReferenceOverlayRawPoints = refPts;
        _scopeReferenceOverlayChannel = refCapture is not null && refCapture.Channels.Count > 0
            ? refCapture.Channels.Keys.Min()
            : null;

        ApplyPointCollectionsOnUiThread();
        RebuildScopeAxisLabels();
        UpdateScopeCursorReadouts();
        await RefreshScopeMathAsync();
        await RefreshScopeMaskAsync();
        await RefreshScopeDeepMeasureAsync();
        await RefreshScopeSerialAsync();
    }

    private void ApplyPointCollectionsOnUiThread()
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
            }
            catch
            {
                // Headless xUnit test runner without a WinUI XAML apartment: RawPoints are still populated
            }
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ApplyNow);
        }
        else
        {
            ApplyNow();
        }
    }

    private static PointCollection ToPointCollection(IReadOnlyList<Point> points)
    {
        var collection = new PointCollection();
        for (int i = 0; i < points.Count; i++)
        {
            collection.Add(points[i]);
        }
        return collection;
    }

    private void UpdateScopeCursorReadouts()
    {
        const double width = WaveformRenderEngine.DefaultCanvasWidth;
        ScopeCursorAX = Math.Clamp(ScopeCursorAPercent / 100.0, 0.0, 1.0) * width;
        ScopeCursorBX = Math.Clamp(ScopeCursorBPercent / 100.0, 0.0, 1.0) * width;

        var capture = LastScopeCapture;
        if (capture is null || capture.SampleCount <= 0 || capture.SampleRateHz <= 0.0)
        {
            ScopeCursorALabel = "Cursor A: no capture";
            ScopeCursorBLabel = "Cursor B: no capture";
            ScopeCursorDeltaLabel = "Rulers: Δt = — | 1/Δt = — | ΔV = —";
            return;
        }

        double zoom = ScopeZoomFactor;
        double panNorm = Math.Clamp(ScopePanPercent / 100.0, 0.0, 1.0);

        var (idxA, tA) = WaveformRenderEngine.MapCursorToSample(
            capture.SampleCount, capture.SampleRateHz, ScopeCursorAPercent, zoom, panNorm);
        var (idxB, tB) = WaveformRenderEngine.MapCursorToSample(
            capture.SampleCount, capture.SampleRateHz, ScopeCursorBPercent, zoom, panNorm);

        var primaryChannel = ScopeChannels.FirstOrDefault(c => c.IsEnabled && capture.Channels.ContainsKey(c.Channel))
                             ?? ScopeChannels.FirstOrDefault(c => capture.Channels.ContainsKey(c.Channel));

        if (primaryChannel is null || !capture.Channels.TryGetValue(primaryChannel.Channel, out var samples) || samples.Length == 0)
        {
            ScopeCursorALabel = $"Cursor A: t = {WaveformRenderEngine.FormatTimeValue(tA)} (Sample #{idxA})";
            ScopeCursorBLabel = $"Cursor B: t = {WaveformRenderEngine.FormatTimeValue(tB)} (Sample #{idxB})";
            ScopeCursorDeltaLabel = $"Rulers: Δt = {WaveformRenderEngine.FormatTimeValue(tB - tA)}";
            return;
        }

        double vA = samples[Math.Clamp(idxA, 0, samples.Length - 1)];
        double vB = samples[Math.Clamp(idxB, 0, samples.Length - 1)];
        double dV = vB - vA;
        double dt = tB - tA;
        string freqText = Math.Abs(dt) > 1e-12
            ? string.Create(CultureInfo.InvariantCulture, $"{1.0 / Math.Abs(dt):F1} Hz")
            : "— Hz";

        string clampA = primaryChannel.CurrentScaleAmpsPerVolt > 0.0
            ? string.Create(CultureInfo.InvariantCulture, $" ({vA * primaryChannel.CurrentScaleAmpsPerVolt:F2} A)")
            : "";
        string clampB = primaryChannel.CurrentScaleAmpsPerVolt > 0.0
            ? string.Create(CultureInfo.InvariantCulture, $" ({vB * primaryChannel.CurrentScaleAmpsPerVolt:F2} A)")
            : "";

        ScopeCursorALabel = string.Create(
            CultureInfo.InvariantCulture,
            $"Cursor A ({primaryChannel.ChannelLabel}): t = {WaveformRenderEngine.FormatTimeValue(tA)} | V = {vA:F3} V{clampA}");
        ScopeCursorBLabel = string.Create(
            CultureInfo.InvariantCulture,
            $"Cursor B ({primaryChannel.ChannelLabel}): t = {WaveformRenderEngine.FormatTimeValue(tB)} | V = {vB:F3} V{clampB}");
        ScopeCursorDeltaLabel = string.Create(
            CultureInfo.InvariantCulture,
            $"Rulers ({primaryChannel.ChannelLabel}): Δt = {WaveformRenderEngine.FormatTimeValue(dt)} | 1/|Δt| = {freqText} | ΔV = {dV:+0.000;-0.000;0.000} V");
    }

    // =========================================================================
    // Save Capture to Work Order & Load Saved Waveforms (.wfm — ADR-011 §6)
    // =========================================================================

    public string GetScopeAppDataDirectory()
    {
        var dbPath = _dataServices.Connections.DatabasePath;
        if (!string.IsNullOrWhiteSpace(dbPath) &&
            !string.Equals(dbPath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GseTechPro");
    }

    public string ResolveScopeDataFilePath(string relativeOrFullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOrFullPath);
        if (Path.IsPathRooted(relativeOrFullPath))
            return relativeOrFullPath;

        var normalized = relativeOrFullPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(GetScopeAppDataDirectory(), normalized);
    }

    [RelayCommand]
    public async Task<Waveform?> SaveScopeCaptureToWorkOrderAsync()
    {
        if (LastScopeCapture is null)
        {
            ScopeStatusMessage = "Run a scope capture first before saving to the work order.";
            StatusBannerMessage = ScopeStatusMessage;
            return null;
        }

        var activeWo = await ResolveActiveWorkOrderAsync();
        string workOrderId = activeWo?.Id ?? await EnsureActiveWorkOrderForRecordingAsync();

        var capture = LastScopeCapture;
        var waveformId = Guid.NewGuid().ToString("D");
        var relativeDataPath = $"waveforms/{waveformId}.wfm";
        var fullDiskPath = ResolveScopeDataFilePath(relativeDataPath);

        // 1. Write binary .wfm sample file via WaveformFile
        await WaveformFile.WriteAsync(fullDiskPath, capture);

        // 2. Serialize channel_config_json & measurements_json (including comparison verdicts) via ScopeJson
        var enabledConfigs = ScopeChannels
            .Where(c => c.IsEnabled || capture.Channels.ContainsKey(c.Channel))
            .Select(c => c.ToChannelConfig())
            .ToArray();

        var measurementsToSave = LastScopeComparisonResult?.Measurements
                                 ?? ScopeMeasurements.Select(m => m.Measurement).ToArray();

        var deviceKind = _activeScopeDevice?.Kind
                         ?? (capture.IsSimulated || IsScopeSimulated
                             ? ScopeDeviceKind.Simulator
                             : ScopeDeviceKind.PicoScope);

        var waveform = new Waveform
        {
            Id = waveformId,
            WorkOrderId = workOrderId,
            ScopeDevice = deviceKind,
            ChannelConfigJson = ScopeJson.SerializeChannelConfigs(enabledConfigs),
            Timebase = capture.TimebaseSecondsPerDivision,
            SampleRate = (int)Math.Round(capture.SampleRateHz),
            SampleCount = capture.SampleCount,
            DataPath = relativeDataPath,
            GuidedTestId = SelectedGuidedTest?.Id,
            ReferenceWaveformId = ActiveReferenceWaveform?.Id ?? SelectedGuidedTest?.ReferenceWaveformId,
            MeasurementsJson = ScopeJson.SerializeMeasurements(measurementsToSave),
            Notes = CaptureNotes.Normalize(ScopeNotes),
            CreatedAt = Clock.UtcNow,
        };

        await _dataServices.Waveforms.InsertAsync(waveform);
        await ReloadWorkOrderWaveformsAsync(workOrderId);

        ScopeStatusMessage =
            $"Saved waveform '{waveform.Id}' ({relativeDataPath}, device='{DbEnum.ToCode(waveform.ScopeDevice)}') to work order '{workOrderId}'.";
        StatusBannerMessage = ScopeStatusMessage;
        return waveform;
    }

    [RelayCommand]
    public async Task ReloadWorkOrderWaveformsAsync(string? explicitWorkOrderId = null)
    {
        try
        {
            var woId = explicitWorkOrderId ?? ResolveActiveWorkOrderInMemory()?.Id;
            WorkOrderWaveforms.Clear();

            if (string.IsNullOrWhiteSpace(woId))
            {
                OnPropertyChanged(nameof(HasNoWorkOrderWaveforms));
                OnPropertyChanged(nameof(NoWorkOrderWaveformsVisibility));
                return;
            }

            var rows = await _dataServices.Waveforms.GetByWorkOrderAsync(woId);
            foreach (var row in rows.Where(w => w.DeletedAt is null).OrderByDescending(w => w.CreatedAt, StringComparer.Ordinal))
            {
                string? testName = null;
                if (!string.IsNullOrWhiteSpace(row.GuidedTestId) &&
                    _guidedTestsById.TryGetValue(row.GuidedTestId!, out var gt))
                {
                    testName = gt.Name;
                }
                WorkOrderWaveforms.Add(new SavedWaveformRowViewModel(row, testName));
            }

            OnPropertyChanged(nameof(HasNoWorkOrderWaveforms));
            OnPropertyChanged(nameof(NoWorkOrderWaveformsVisibility));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] ReloadWorkOrderWaveformsAsync notice: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task LoadSavedWaveformAsync(SavedWaveformRowViewModel? item = null)
    {
        var target = item ?? SelectedSavedWaveform;
        if (target is null)
            return;

        try
        {
            var waveform = target.Waveform;
            var fullPath = ResolveScopeDataFilePath(waveform.DataPath);
            if (!File.Exists(fullPath))
            {
                ScopeStatusMessage = $"Saved waveform file '{waveform.DataPath}' was not found on disk.";
                StatusBannerMessage = ScopeStatusMessage;
                return;
            }

            bool isSim = waveform.ScopeDevice == ScopeDeviceKind.Simulator;
            var loadedCapture = await Task.Run(() => WaveformFile.ReadAsync(fullPath));
            loadedCapture = loadedCapture with
            {
                IsSimulated = isSim,
                CapturedAt = waveform.CreatedAt,
            };

            // A saved capture brings its own notes (or none).
            ScopeNotes = waveform.Notes ?? "";

            // Restore channel setups
            var savedChannels = ScopeJson.DeserializeChannelConfigs(waveform.ChannelConfigJson);
            if (savedChannels.Count > 0)
            {
                _suppressChannelConfigEvents = true;
                try
                {
                    var byCh = savedChannels.ToDictionary(c => c.Channel, c => c);
                    foreach (var chVm in ScopeChannels)
                    {
                        if (byCh.TryGetValue(chVm.Channel, out var cfg))
                        {
                            chVm.ApplyConfig(cfg, enable: true);
                        }
                        else
                        {
                            chVm.IsEnabled = false;
                        }
                    }
                }
                finally
                {
                    _suppressChannelConfigEvents = false;
                }
            }

            if (waveform.Timebase is double tb && tb > 0.0)
                ScopeTimebaseSecondsPerDivision = tb;
            if (waveform.SampleRate is int sr && sr >= 1)
                ScopeSampleRateHz = sr;

            // Restore measurements & comparison verdict
            var savedMeasurements = ScopeJson.DeserializeMeasurements(waveform.MeasurementsJson);
            ReferenceExpectations? expectations = null;
            if (!string.IsNullOrWhiteSpace(waveform.ReferenceWaveformId) &&
                _referenceWaveformsById.TryGetValue(waveform.ReferenceWaveformId!, out var refWf))
            {
                ActiveReferenceWaveform = refWf;
                expectations = ScopeJson.DeserializeReferenceExpectations(refWf.ExpectedJson);
                ActiveReferenceExpectations = expectations;
            }

            ScopeComparisonResult comparison;
            if (savedMeasurements.Count > 0)
            {
                bool anyFail = savedMeasurements.Any(m => m.Verdict == ScopeVerdict.Fail);
                bool anyPass = savedMeasurements.Any(m => m.Verdict == ScopeVerdict.Pass);
                comparison = new ScopeComparisonResult
                {
                    ReferenceWaveformId = waveform.ReferenceWaveformId,
                    Measurements = savedMeasurements,
                    Overall = anyFail ? ScopeVerdict.Fail : anyPass ? ScopeVerdict.Pass : ScopeVerdict.Na,
                };
            }
            else
            {
                var computed = new List<ScopeMeasurement>();
                foreach (var chCfg in savedChannels)
                {
                    if (loadedCapture.Channels.TryGetValue(chCfg.Channel, out var samples))
                    {
                        computed.AddRange(WaveformMath.ComputeMeasurements(
                            samples,
                            loadedCapture.SampleRateHz,
                            chCfg,
                            chCfg.Channel,
                            isSimulated: isSim));
                    }
                }
                comparison = WaveformComparer.CompareMeasurements(computed, expectations, waveform.ReferenceWaveformId);
            }

            IsScopeSimulated = isSim;
            LastScopeCapture = loadedCapture;
            ApplyScopeComparisonResult(comparison, expectations);
            await RefreshScopeWaveformRenderAsync();

            ScopeStatusMessage =
                $"Loaded saved waveform '{waveform.Id}' ({target.GuidedTestName}, {loadedCapture.SampleCount:N0} samples, device='{target.ScopeDeviceCode}').";
            StatusBannerMessage = ScopeStatusMessage;
        }
        catch (Exception ex)
        {
            ReportError("Load saved waveform failed", ex);
        }
    }
}
