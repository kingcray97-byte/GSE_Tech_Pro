using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.App.Services;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Protocols;
using GseTechPro.Devices.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GseTechPro.App.ViewModels;

public sealed partial class MainShellViewModel : ObservableObject, IAsyncDisposable
{
    private const int PlaybackWindowSize = 250;
    private const double SampleIntervalSeconds = 0.1;

    private readonly DataServices _dataServices;
    private readonly IDiagnosticAdapterFactory _adapterFactory;
    private readonly IScopeDeviceFactory _scopeDeviceFactory;
    private readonly IAiClient _aiClient;
    private readonly AiServiceLauncher _aiLauncher;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly DispatcherQueueTimer? _uiThrottleTimer;
    private readonly DispatcherQueueTimer? _playbackTimer;

    private IDiagnosticAdapter? _activeAdapter;
    private string? _activeHostStreamId;
    private IReadOnlyList<DtcReadout> _lastDtcs = Array.Empty<DtcReadout>();

    // DTC library cache keyed by (Protocol, Code)
    private readonly Dictionary<(ProtocolType Protocol, string Code), DtcLibraryEntry> _dtcLibraryMap = new();

    // Thread-safe coalescing buffer for 10 Hz background stream -> UI thread
    private readonly object _streamLock = new();
    private readonly Dictionary<string, PidValue> _pendingStreamPids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LivePidRowViewModel> _pidMap = new(StringComparer.OrdinalIgnoreCase);
    private long _liveSampleSequence;
    private readonly Stopwatch _sessionStopwatch = new();

    // Recording & trigger state
    private readonly List<RecordedFrameBatch> _preTriggerRing = new();
    private readonly List<RecordedFrameBatch> _pendingDbFrames = new();
    private RecordingSession? _activeRecordingSession;
    private long _recordingFrameSequence;
    private bool _triggerArmed;
    private bool _triggerFired;
    private DateTimeOffset _triggerFiredAtUtc;
    private double? _lastThresholdComparedCanonicalValue;

    // Playback state (screen 17)
    private RecordingFrameWindow? _currentPlaybackWindow;
    private DateTimeOffset? _firstFrameTimestampUtc;

    // Phase 4: GSE Troubleshooting & Manual Readings caches
    private readonly Dictionary<string, SafetyWarning> _safetyWarningsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TestPoint> _testPointsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TroubleshootingTree> _troubleshootingTreesById = new(StringComparer.Ordinal);
    private IReadOnlyList<Reading> _lastWorkOrderReadings = Array.Empty<Reading>();
    private (string SessionId, string StepId, double Value, string Unit)? _lastSavedGuidedReadingKey;
    private bool _suppressTestPointSync;

    public MainShellViewModel(
        DataServices dataServices,
        IDiagnosticAdapterFactory adapterFactory,
        DispatcherQueue? dispatcherQueue = null,
        IAiClient? aiClient = null,
        AiServiceLauncher? aiLauncher = null,
        AiServiceStateService? aiStateService = null,
        IScopeDeviceFactory? scopeDeviceFactory = null)
    {
        _dataServices = dataServices;
        _adapterFactory = adapterFactory;
        _scopeDeviceFactory = scopeDeviceFactory ?? new Scope.ScopeDeviceFactory();
        _dispatcherQueue = dispatcherQueue;
        _aiLauncher = aiLauncher ?? new AiServiceLauncher();
        _aiClient = aiClient ?? new AiServiceClient(
            new AiServiceConfig { BaseUrl = _aiLauncher.BaseUrl },
            _aiLauncher.Token);

        AiState = aiStateService ?? new AiServiceStateService(_aiClient, _aiLauncher, _dispatcherQueue);
        AiState.StateUpdated += OnAiStateUpdated;

        if (_dispatcherQueue is not null)
        {
            _uiThrottleTimer = _dispatcherQueue.CreateTimer();
            _uiThrottleTimer.Interval = TimeSpan.FromMilliseconds(100);
            _uiThrottleTimer.Tick += OnUiThrottleTick;

            _playbackTimer = _dispatcherQueue.CreateTimer();
            _playbackTimer.Interval = TimeSpan.FromMilliseconds(100);
            _playbackTimer.Tick += OnPlaybackTimerTick;
        }

        OpenTabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TabStripVisibility));
        SyncModuleNavSelection(null);
        RebuildWorkOrderDocument();
        OpenTabs.Add(new OpenWorkOrderTab(
            "SIM-SESSION-01",
            "Active Diagnostic Session (Unassigned)",
            "BENCH / SIM",
            "Open"));
        SelectedTab = OpenTabs[0];

        AvailableTechnicians.Add("Default Tech [Unassigned]");
        SelectedTechnician = AvailableTechnicians[0];

        // Seed default in-process simulator options before host discovery
        PopulateDefaultDeviceList();
        InitializeScopeState();
        PopulateDefaultGuidedCatalog();

        WorkOrders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoWorkOrders));
            OnPropertyChanged(nameof(NoWorkOrdersVisibility));
            NotifyActiveWorkOrderChanged();
            RebuildWorkOrderBrowserRows();
        };

        Assets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoAssets));
            OnPropertyChanged(nameof(NoAssetsVisibility));
            SyncReadingsFilterToActiveAssetModel();
            RebuildWorkOrderBrowserRows();
        };

        TryPopulateCatalogIfDatabaseInitialized();

        // Initialize 6 slots for the Custom Display Builder (screen 15)
        for (int i = 1; i <= 6; i++)
        {
            var slot = new CustomDisplaySlotViewModel(i, AvailablePidNames)
            {
                DisplayType = i switch
                {
                    1 => CustomSlotDisplayType.BarGraph,
                    2 => CustomSlotDisplayType.Gauge,
                    3 => CustomSlotDisplayType.AnalogMeter,
                    _ => CustomSlotDisplayType.DigitalMeter,
                },
            };
            CustomDisplaySlots.Add(slot);
        }

    }

    public AiServiceStateService AiState { get; }

    public IAiClient AiClient => _aiClient;

    public AiServiceLauncher AiLauncher => _aiLauncher;

    // =========================================================================
    // Collections & Shell State
    // =========================================================================

    public ObservableCollection<OpenWorkOrderTab> OpenTabs { get; } = new();

    [ObservableProperty]
    private OpenWorkOrderTab? _selectedTab;

    public ObservableCollection<string> AvailableTechnicians { get; } = new();

    [ObservableProperty]
    private string _selectedTechnician = "Default Tech [Unassigned]";

    public IReadOnlyList<ProtocolType> SimulatorProtocols { get; } = new[]
    {
        ProtocolType.J1939,
        ProtocolType.J1708,
        ProtocolType.OBD2,
    };

    [ObservableProperty]
    private ProtocolType _selectedProtocol = ProtocolType.J1939;

    public ObservableCollection<SelectableDeviceItem> AvailableDevices { get; } = new();

    [ObservableProperty]
    private SelectableDeviceItem? _selectedDevice;

    public IReadOnlyList<SidebarItem> SidebarSections { get; } = BuildSidebarSections();

    [ObservableProperty]
    private string _activeTool = "Diagnostics";

    [ObservableProperty]
    private string _activeSectionKey = "Diagnostics";

    [ObservableProperty]
    private string _breadcrumbRoot = "Equipment Selection";

    [ObservableProperty]
    private string _breadcrumbCategory = "Diagnostics";

    [ObservableProperty]
    private string _breadcrumbLeaf = "Equipment Health";

    [ObservableProperty]
    private UnitSystem _currentUnitSystem = UnitSystem.Imperial;

    public string UnitSystemLabel => CurrentUnitSystem == UnitSystem.Imperial
        ? "Units: Imperial (psi, °F, mph)"
        : "Units: Metric (bar, °C, km/h)";

    [ObservableProperty]
    private bool _isSunlightMode;

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isCurrentDataSimulated = true;

    public Visibility SimulatedBannerVisibility =>
        (IsConnected || PlaybackState != PlaybackState.Live) && IsCurrentDataSimulated
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility RealHardwareBannerVisibility =>
        (IsConnected || PlaybackState != PlaybackState.Live) && !IsCurrentDataSimulated
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string ProvenanceStatusBadgeText =>
        IsCurrentDataSimulated ? "SIMULATED DATA" : "REAL HARDWARE";

    [ObservableProperty]
    private string _adapterDeviceName = "No Adapter Selected";

    [ObservableProperty]
    private string _adapterKindBadge = "SIMULATOR";

    [ObservableProperty]
    private string _connectionStatusText = "No Connection";

    [ObservableProperty]
    private string _activeProtocolText = "Protocol: None";

    [ObservableProperty]
    private string _vehicleVinText = "VIN: <not connected>";

    [ObservableProperty]
    private string _milStatusText = "OFF";

    [ObservableProperty]
    private string _shopNameText = "GSE Shop";

    [ObservableProperty]
    private string _statusBannerMessage = "Ready — Select an adapter/protocol and click 'Connect' to inspect Equipment Health & Live Data.";

    public ObservableCollection<DtcRowViewModel> Dtcs { get; } = new();
    public ObservableCollection<LivePidRowViewModel> LivePids { get; } = new();
    public ObservableCollection<LivePidRowViewModel> SnaplistPids { get; } = new();
    public ObservableCollection<string> AvailablePidNames { get; } = new();
    public ObservableCollection<LivePidRowViewModel> FreezeFramePids { get; } = new();
    public ObservableCollection<ReadinessMonitorRowViewModel> ReadinessMonitors { get; } = new();
    public ObservableCollection<EcuInfo> DiscoveredEcus { get; } = new();
    public ObservableCollection<Asset> Assets { get; } = new();
    public ObservableCollection<WorkOrder> WorkOrders { get; } = new();
    public ObservableCollection<DiagnosticLogEntry> DiagnosticLog { get; } = new();

    public XamlRoot? DialogXamlRoot { get; set; }

    [ObservableProperty]
    private string _freezeFrameTimestamp = "Not captured";

    // =========================================================================
    // Live Data Display Modes, Custom Display Builder & Graph Cursors (13–16)
    // =========================================================================

    public IReadOnlyList<LiveDataDisplayMode> LiveDataDisplayModes { get; } = Enum.GetValues<LiveDataDisplayMode>();

    public IReadOnlyList<CustomSlotDisplayType> CustomSlotDisplayTypes { get; } = Enum.GetValues<CustomSlotDisplayType>();

    [ObservableProperty]
    private LiveDataDisplayMode _selectedLiveDataMode = LiveDataDisplayMode.List;

    [ObservableProperty]
    private bool _onlyShowSnaplistPids;

    [ObservableProperty]
    private string _snaplistName = "Standard Snaplist 'All Parameters'";

    public ObservableCollection<CustomDisplaySlotViewModel> CustomDisplaySlots { get; } = new();

    // Line Graph & Dual Cursors (screen 16)
    [ObservableProperty]
    private LivePidRowViewModel? _selectedGraphPid;

    [ObservableProperty]
    private PointCollection? _graphPoints;

    [ObservableProperty]
    private double _cursorAPercent = 25.0;

    [ObservableProperty]
    private double _cursorBPercent = 75.0;

    [ObservableProperty]
    private double _cursorAX = 180.0;

    [ObservableProperty]
    private double _cursorBX = 540.0;

    [ObservableProperty]
    private string _cursorALabel = "Cursor A: Frame 0 | —";

    [ObservableProperty]
    private string _cursorBLabel = "Cursor B: Frame 0 | —";

    [ObservableProperty]
    private string _cursorDeltaLabel = "Cursor Delta: ΔFrame = 0 | Δt = 0.0 s | ΔValue = 0";

    [ObservableProperty]
    private string _graphScaleTopLabel = "100";

    [ObservableProperty]
    private string _graphScaleMidLabel = "50";

    [ObservableProperty]
    private string _graphScaleBottomLabel = "0";

    partial void OnSelectedLiveDataModeChanged(LiveDataDisplayMode value)
    {
        NotifyLiveDataModeVisibilityChanged();
        RefreshGraphGeometry();
    }

    partial void OnSelectedGraphPidChanged(LivePidRowViewModel? value) => RefreshGraphGeometry();
    partial void OnCursorAPercentChanged(double value) => RefreshGraphGeometry();
    partial void OnCursorBPercentChanged(double value) => RefreshGraphGeometry();

    // =========================================================================
    // Recording, Trigger Setup & VCR Playback State (screens 17–18)
    // =========================================================================

    public ObservableCollection<RecordingSession> SavedRecordings { get; } = new();

    [ObservableProperty]
    private RecordingSession? _selectedRecordingSession;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingStatusLabel = "Trigger Ready (Manual)";

    [ObservableProperty]
    private int _recordedFrameCount;

    // Trigger Setup properties (screen 18)
    public IReadOnlyList<TriggerKind> TriggerKinds { get; } = Enum.GetValues<TriggerKind>();
    public IReadOnlyList<ThresholdComparison> ThresholdComparisons { get; } = Enum.GetValues<ThresholdComparison>();

    [ObservableProperty]
    private TriggerKind _triggerKind = TriggerKind.Manual;

    [ObservableProperty]
    private string _triggerDtcCode = "";

    [ObservableProperty]
    private string? _triggerPidName;

    [ObservableProperty]
    private double _triggerThresholdValue = 90.0;

    [ObservableProperty]
    private string _triggerThresholdUnit = UnitConverter.Celsius;

    [ObservableProperty]
    private ThresholdComparison _triggerComparison = ThresholdComparison.Above;

    [ObservableProperty]
    private double _preTriggerSeconds = 2.0;

    [ObservableProperty]
    private double _postTriggerSeconds = 5.0;

    [ObservableProperty]
    private bool _triggerAutoStart = true;

    [ObservableProperty]
    private bool _triggerAutoStop = true;

    // Playback VCR Cursor properties (screen 17)
    public IReadOnlyList<double> PlaybackRates { get; } = new[] { 0.25, 0.5, 1.0, 2.0, 4.0 };

    [ObservableProperty]
    private PlaybackState _playbackState = PlaybackState.Live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackSequenceDouble))]
    private long _playbackSequence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackMaxSequenceDouble))]
    private long _playbackMaxSequence;

    public double PlaybackSequenceDouble => PlaybackSequence;
    public double PlaybackMaxSequenceDouble => Math.Max(1.0, (double)PlaybackMaxSequence);

    [ObservableProperty]
    private double _playbackPositionSeconds;

    [ObservableProperty]
    private double _playbackDurationSeconds;

    [ObservableProperty]
    private double _playbackRate = 1.0;

    [ObservableProperty]
    private string _playbackFrameSummary = "Frame: 0  Time: 00.000 sec (LIVE)";

    [ObservableProperty]
    private string _playbackMetadataSummary = "Live Stream";

    partial void OnPlaybackRateChanged(double value)
    {
        var intervalMs = Math.Clamp(100.0 / Math.Max(0.25, value), 25.0, 400.0);
        if (_playbackTimer is not null)
        {
            _playbackTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        }
    }

    partial void OnSelectedDeviceChanged(SelectableDeviceItem? value)
    {
        if (value is not null && value.Device.Protocols.Count > 0 && !value.Device.Protocols.Contains(SelectedProtocol))
        {
            SelectedProtocol = value.Device.Protocols[0];
        }
    }

    // =========================================================================
    // Visibility Helpers
    // =========================================================================

    public bool HasNoAssets => Assets.Count == 0;
    public bool HasNoWorkOrders => WorkOrders.Count == 0;
    public bool HasNoDtcs => Dtcs.Count == 0;
    public bool HasNoLivePids => LivePids.Count == 0;
    public bool HasNoSavedRecordings => SavedRecordings.Count == 0;

    public bool IsDiagnosticsViewVisible =>
        ActiveSectionKey is "Diagnostics" or "LiveData" or "DTCs" or "FreezeFrames" or "CurrentData" or "Readiness" or "Monitors";

    public bool IsWorkOrdersViewVisible => ActiveSectionKey == "WorkOrders";
    public bool IsAssetsViewVisible => ActiveSectionKey == "Assets";
    public bool IsTechChatViewVisible => ActiveSectionKey == "AiChat";
    public bool IsGuidedViewVisible => ActiveSectionKey == "GuidedTree";
    public bool IsManualSearchViewVisible => ActiveSectionKey == "ManualSearch";
    public bool IsReadingsViewVisible => ActiveSectionKey == "Readings";
    public bool IsRecommendationsViewVisible => ActiveSectionKey == "Recommendations";

    public bool IsToolPlaceholderVisible =>
        !IsDiagnosticsViewVisible &&
        !IsWorkOrdersViewVisible &&
        !IsAssetsViewVisible &&
        !IsTechChatViewVisible &&
        !IsGuidedViewVisible &&
        !IsManualSearchViewVisible &&
        !IsReadingsViewVisible &&
        !IsRecommendationsViewVisible &&
        !IsSettingsViewVisible &&
        !IsScopeViewVisible &&
        !IsCanMonitorViewVisible;

    // Section-filtering inside Diagnostics / Equipment Health (Screenshot 04 shows all; sub-nav focuses or shows all)
    public Visibility DtcSectionVisibility =>
        ActiveSectionKey is "Diagnostics" or "DTCs" or "LiveData" or "CurrentData" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LiveDataSectionVisibility =>
        ActiveSectionKey is "Diagnostics" or "LiveData" or "CurrentData" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FreezeFrameSectionVisibility =>
        ActiveSectionKey is "Diagnostics" or "FreezeFrames" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReadinessSectionVisibility =>
        ActiveSectionKey is "Diagnostics" or "Readiness" or "Monitors" ? Visibility.Visible : Visibility.Collapsed;

    // Live Data Sub-View Visibilities (screens 13–16)
    public Visibility LiveListModeVisibility => SelectedLiveDataMode == LiveDataDisplayMode.List ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SnaplistEditorModeVisibility => SelectedLiveDataMode == LiveDataDisplayMode.SnaplistEditor ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BarGraphModeVisibility => SelectedLiveDataMode == LiveDataDisplayMode.Bar ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DigitalMeterModeVisibility => SelectedLiveDataMode == LiveDataDisplayMode.DigitalMeter ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AnalogGaugeModeVisibility => SelectedLiveDataMode == LiveDataDisplayMode.AnalogGauge ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LineGraphModeVisibility => SelectedLiveDataMode == LiveDataDisplayMode.LineGraph ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CustomDisplayModeVisibility => SelectedLiveDataMode == LiveDataDisplayMode.CustomDisplay ? Visibility.Visible : Visibility.Collapsed;

    // Convenience Slot Properties for XAML x:Bind
    public CustomDisplaySlotViewModel CustomSlot1 => CustomDisplaySlots[0];
    public CustomDisplaySlotViewModel CustomSlot2 => CustomDisplaySlots[1];
    public CustomDisplaySlotViewModel CustomSlot3 => CustomDisplaySlots[2];
    public CustomDisplaySlotViewModel CustomSlot4 => CustomDisplaySlots[3];
    public CustomDisplaySlotViewModel CustomSlot5 => CustomDisplaySlots[4];
    public CustomDisplaySlotViewModel CustomSlot6 => CustomDisplaySlots[5];

    public Visibility DiagnosticsViewVisibility => IsDiagnosticsViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkOrdersViewVisibility => IsWorkOrdersViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AssetsViewVisibility => IsAssetsViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TechChatViewVisibility => IsTechChatViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GuidedViewVisibility => IsGuidedViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManualSearchViewVisibility => IsManualSearchViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadingsViewVisibility => IsReadingsViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecommendationsViewVisibility => IsRecommendationsViewVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolPlaceholderVisibility => IsToolPlaceholderVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoAssetsVisibility => HasNoAssets ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoWorkOrdersVisibility => HasNoWorkOrders ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDtcsVisibility => HasNoDtcs ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoLivePidsVisibility => HasNoLivePids ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDiagnosticLogVisibility => DiagnosticLog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSavedRecordingsVisibility => HasNoSavedRecordings ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private string _placeholderTitle = "";

    [ObservableProperty]
    private string _placeholderSubtitle = "";

    // =========================================================================
    // Initialization & Database Loading
    // =========================================================================

    public async Task InitializeAsync()
    {
        try
        {
            await _dataServices.InitializeAsync();
            await LoadDtcLibraryIndexAsync();
            await ReloadDatabaseRecordsAsync();
            await DiscoverDevicesAsync();
            await DiscoverScopeDevicesAsync();
            AiState.StartPolling(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] Database initialization failed: {ex}");
            StatusBannerMessage = $"DATABASE ERROR — could not initialize the local database: {ex.Message}";
            AppendDiagnosticLog($"Database initialization failed: {ex.Message}");
            await ShowErrorDialogAsync(
                "Database Error",
                "The local database could not be initialized, so records cannot be saved or loaded.\n\n" + ex.Message);
        }
    }

    private async Task LoadDtcLibraryIndexAsync()
    {
        _dtcLibraryMap.Clear();
        _manufacturerSpnMap.Clear();
        await using var connection = await _dataServices.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, protocol, code, spn, fmi, description, system, severity, likely_causes, source_citation, created_at, updated_at
            FROM dtc_library;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var protocolStr = reader.GetString(1);
            if (!DbEnum.TryParse<ProtocolType>(protocolStr, out var protocol))
                continue;

            var code = reader.GetString(2);
            var entry = new DtcLibraryEntry
            {
                Id = reader.GetString(0),
                Protocol = protocol,
                Code = code,
                Spn = await reader.IsDBNullAsync(3) ? null : reader.GetInt32(3),
                Fmi = await reader.IsDBNullAsync(4) ? null : reader.GetInt32(4),
                Description = await reader.IsDBNullAsync(5) ? null : reader.GetString(5),
                System = await reader.IsDBNullAsync(6) ? null : reader.GetString(6),
                Severity = await reader.IsDBNullAsync(7) ? null : reader.GetString(7),
                LikelyCauses = await reader.IsDBNullAsync(8) ? null : reader.GetString(8),
                SourceCitation = await reader.IsDBNullAsync(9) ? null : reader.GetString(9),
                CreatedAt = reader.GetString(10),
                UpdatedAt = reader.GetString(11),
            };
            _dtcLibraryMap[(protocol, code.Trim().ToUpperInvariant())] = entry;
            if (protocol == ProtocolType.Manufacturer && entry.Spn is int spn && DtcCodes.IsProprietarySpn(spn))
                _manufacturerSpnMap[spn] = entry;
        }
    }

    /// <summary>
    /// Manufacturer rows (PCS TCM) that the manual ties to a proprietary J1939 SPN, keyed by SPN.
    /// The guides give no FMI, so a received <c>SPN x FMI y</c> with no exact row is matched by SPN.
    /// </summary>
    private readonly Dictionary<int, DtcLibraryEntry> _manufacturerSpnMap = new();

    /// <summary>
    /// Fallback for a J1939 DTC with a proprietary SPN and no exact library row: the manufacturer
    /// row cited for that SPN (today only the PCS TCM). The caller must flag it as SPN-only, because a
    /// proprietary SPN means something else on another maker's ECU.
    /// </summary>
    public DtcLibraryEntry? FindProprietarySpnEntry(DtcReadout dtc) =>
        dtc.Protocol == ProtocolType.J1939 && dtc.Spn is int spn ? FindManufacturerSpnEntry(spn) : null;

    /// <summary>The manufacturer (PCS TCM) row cited for a proprietary SPN, or null.</summary>
    public DtcLibraryEntry? FindManufacturerSpnEntry(int spn) =>
        DtcCodes.IsProprietarySpn(spn) && _manufacturerSpnMap.TryGetValue(spn, out var entry) ? entry : null;

    /// <summary>Looks up a <see cref="DtcLibraryEntry"/> by exact <c>(protocol, code)</c> match.</summary>
    public DtcLibraryEntry? FindDtcLibraryEntry(ProtocolType protocol, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        _dtcLibraryMap.TryGetValue((protocol, code.Trim().ToUpperInvariant()), out var entry);
        return entry;
    }

    [RelayCommand]
    public async Task ReloadDatabaseRecordsAsync()
    {
        await LoadAiRoutingSettingsAsync();

        var shops = await _dataServices.Shops.GetAllAsync();
        if (shops.Count > 0)
        {
            var shop = shops[0];
            ShopNameText = shop.Name;
            CurrentUnitSystem = shop.DefaultUnitSystem;
            OnPropertyChanged(nameof(UnitSystemLabel));
        }

        var techs = await _dataServices.Technicians.GetAllAsync();
        AvailableTechnicians.Clear();
        if (techs.Count == 0)
        {
            AvailableTechnicians.Add("Default Tech [Unassigned]");
        }
        else
        {
            foreach (var t in techs)
            {
                var role = t.Role?.ToString() ?? "Tech";
                AvailableTechnicians.Add($"{t.DisplayName} [{role}]");
            }
        }
        SelectedTechnician = AvailableTechnicians[0];

        var assets = await _dataServices.Assets.GetAllAsync();
        Assets.Clear();
        foreach (var asset in assets)
            Assets.Add(asset);
        OnPropertyChanged(nameof(HasNoAssets));
        OnPropertyChanged(nameof(NoAssetsVisibility));

        var workOrders = await _dataServices.WorkOrders.GetAllAsync();
        WorkOrders.Clear();
        foreach (var wo in workOrders)
            WorkOrders.Add(wo);
        OnPropertyChanged(nameof(HasNoWorkOrders));
        OnPropertyChanged(nameof(NoWorkOrdersVisibility));

        await ReloadGuidedCatalogFromDatabaseAsync();
        await ReloadScopeCatalogAsync();
        await ReloadManualsCatalogAsync();
        await ReloadRecordingsListAsync();
        await ReloadWorkOrderReadingsAsync();
        await ReloadWorkOrderWaveformsAsync();
        await ReloadWorkOrderOutcomesAndRepairsAsync();
    }

    public async Task ReloadRecordingsListAsync()
    {
        var allRecordings = await _dataServices.Recordings.GetAllAsync();
        SavedRecordings.Clear();
        foreach (var rec in allRecordings.OrderByDescending(r => r.StartedAt))
        {
            SavedRecordings.Add(Data.Repositories.RecordingRepository.ToSession(rec));
        }
        if (SelectedRecordingSession is null && SavedRecordings.Count > 0)
        {
            SelectedRecordingSession = SavedRecordings[0];
        }
        OnPropertyChanged(nameof(HasNoSavedRecordings));
        OnPropertyChanged(nameof(NoSavedRecordingsVisibility));
    }

    // =========================================================================
    // Device Selection & Host32 / In-Process Simulator Connection (Screen 26/28)
    // =========================================================================

    private void PopulateDefaultDeviceList()
    {
        AvailableDevices.Clear();
        var inProcSim = new SelectableDeviceItem(
            new DeviceInfo
            {
                Id = "inproc-simulator",
                Name = "In-Process Simulator (J1939 / J1708 / OBD II)",
                Kind = "simulator",
                Protocols = new[] { ProtocolType.J1939, ProtocolType.J1708, ProtocolType.OBD2 },
                IsSimulated = true,
            },
            isInProcessSimulator: true);

        AvailableDevices.Add(inProcSim);
        SelectedDevice = inProcSim;
    }

    [RelayCommand]
    public async Task DiscoverDevicesAsync()
    {
        var previousId = SelectedDevice?.Id;
        PopulateDefaultDeviceList();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var hostDevices = await _adapterFactory.ListDevicesAsync(cts.Token);
            foreach (var dev in hostDevices)
            {
                AvailableDevices.Add(new SelectableDeviceItem(dev, isInProcessSimulator: false));
            }

            if (previousId is not null)
            {
                var match = AvailableDevices.FirstOrDefault(d => d.Id == previousId);
                if (match is not null)
                    SelectedDevice = match;
            }

            AppendDiagnosticLog($"Device discovery complete: {AvailableDevices.Count} adapter(s) available (including 32-bit host devices).");
        }
        catch (Exception ex)
        {
            // Keep in-process simulator available even if Host32 is not built/reachable
            Debug.WriteLine($"[GSE Tech Pro] Host32 device discovery fallback to in-proc simulator: {ex.Message}");
            AppendDiagnosticLog($"32-bit device host discovery note: {ex.Message} (In-process simulator active).");
        }
    }

    [RelayCommand]
    public async Task ConnectSimulatorAsync()
    {
        await StopPlaybackAndReturnToLiveAsync();
        ClearDiagnosticCollections();
        await DisconnectSimulatorAsync();

        var chosen = SelectedDevice ?? AvailableDevices.FirstOrDefault();
        try
        {
            if (chosen is not null && !chosen.IsInProcessSimulator)
            {
                // Host32 adapter path via ConnectHostAsync + SubscribeLiveDataAsync
                _activeAdapter = await _adapterFactory.ConnectHostAsync(chosen.Device, SelectedProtocol);
                _activeAdapter.LiveDataReceived += OnAdapterLiveDataReceived;
                await _activeAdapter.ConnectAsync();

                if (_activeAdapter is HostDiagnosticAdapter hostAdapter)
                {
                    hostAdapter.Disconnected += OnAdapterDisconnected;
                    _activeHostStreamId = await hostAdapter.SubscribeLiveDataAsync(Array.Empty<string>(), intervalMs: 100);
                }

                AdapterKindBadge = chosen.KindLabel;
            }
            else
            {
                // In-process simulator path
                _activeAdapter = _adapterFactory.CreateSimulator(SelectedProtocol, seed: null);
                _activeAdapter.LiveDataReceived += OnAdapterLiveDataReceived;
                await _activeAdapter.ConnectAsync();
                AdapterKindBadge = "SIMULATOR (IN-PROC)";
            }

            _liveSampleSequence = 0;
            _sessionStopwatch.Restart();
            _uiThrottleTimer?.Start();

            IsConnected = _activeAdapter.IsConnected;
            IsCurrentDataSimulated = _activeAdapter.IsSimulated;
            AdapterDeviceName = _activeAdapter.Name;
            ConnectionStatusText = IsCurrentDataSimulated ? "Connected (SIMULATED)" : "Connected (REAL HARDWARE)";
            ActiveProtocolText = $"Protocol: {SelectedProtocol}{(IsCurrentDataSimulated ? " (SIMULATED)" : "")}";

            NotifyProvenanceChanged();

            var vehicleInfo = await _activeAdapter.GetVehicleInfoAsync();
            VehicleVinText = $"VIN: {vehicleInfo.Vin ?? "<unknown>"}";

            DiscoveredEcus.Clear();
            foreach (var ecu in vehicleInfo.Ecus)
                DiscoveredEcus.Add(ecu);

            await RefreshDiagnosticsAsync();

            StatusBannerMessage = $"Connected to {_activeAdapter.Name}. Streaming 10 Hz {(IsCurrentDataSimulated ? "SIMULATED" : "LIVE")} bus data.";
            AppendDiagnosticLog($"Connected adapter '{_activeAdapter.Name}' on {SelectedProtocol} (Simulated={IsCurrentDataSimulated}).");
        }
        catch (Exception ex)
        {
            await HandleAdapterFaultOrDisconnectAsync($"Adapter connect failed: {ex.Message}");
            throw;
        }
    }

    [RelayCommand]
    public async Task DisconnectSimulatorAsync()
    {
        _uiThrottleTimer?.Stop();

        // If a recording is in progress when the user or host disconnects, finalize it as Interrupted
        if (IsRecording && _activeRecordingSession is not null)
        {
            await StopRecordingInternalAsync(RecordingStatus.Interrupted, "Adapter disconnected mid-capture");
        }

        if (_activeAdapter is not null)
        {
            _activeAdapter.LiveDataReceived -= OnAdapterLiveDataReceived;

            if (_activeAdapter is HostDiagnosticAdapter hostAdapter)
            {
                hostAdapter.Disconnected -= OnAdapterDisconnected;

                if (_activeHostStreamId is not null)
                {
                    try { await hostAdapter.UnsubscribeLiveDataAsync(_activeHostStreamId); }
                    catch { }
                    _activeHostStreamId = null;
                }
                try { await hostAdapter.DisposeAsync(); }
                catch { }
            }
            else
            {
                try { await _activeAdapter.DisconnectAsync(); }
                catch { }
            }

            _activeAdapter = null;
        }

        IsConnected = false;
        AdapterDeviceName = "No Adapter Connected";
        ConnectionStatusText = "No Connection";
        ActiveProtocolText = "Protocol: None";
        VehicleVinText = "VIN: <not connected>";
        MilStatusText = "OFF";
        NotifyProvenanceChanged();

        ClearDiagnosticCollections();
        StatusBannerMessage = "Disconnected from diagnostic adapter.";
    }

    private async Task HandleAdapterFaultOrDisconnectAsync(string reason)
    {
        Debug.WriteLine($"[GSE Tech Pro] Adapter fault/disconnect: {reason}");
        if (IsRecording && _activeRecordingSession is not null)
        {
            await StopRecordingInternalAsync(RecordingStatus.Interrupted, reason);
        }
        StatusBannerMessage = $"Connection interrupted: {reason}";
        AppendDiagnosticLog($"Connection interrupted: {reason}");
    }

    private void OnAdapterDisconnected(object? sender, EventArgs e)
    {
        // The host connection raises this on its reader thread; marshal to the UI thread before
        // touching any view state. If the dispatcher queue is already shutting down the enqueue is
        // dropped, which is acceptable during app teardown.
        if (_dispatcherQueue is not null)
        {
            _dispatcherQueue.TryEnqueue(() => _ = OnHostDisconnectedAsync());
        }
        else
        {
            _ = OnHostDisconnectedAsync();
        }
    }

    private async Task OnHostDisconnectedAsync()
    {
        if (_activeAdapter is null || !IsConnected)
            return;

        const string reason = "Device host connection lost";
        await HandleAdapterFaultOrDisconnectAsync(reason);
        await DisconnectSimulatorAsync();
        StatusBannerMessage = $"Connection interrupted: {reason}";
    }

    private void NotifyProvenanceChanged()
    {
        OnPropertyChanged(nameof(SimulatedBannerVisibility));
        OnPropertyChanged(nameof(RealHardwareBannerVisibility));
        OnPropertyChanged(nameof(ProvenanceStatusBadgeText));
    }

    // =========================================================================
    // Equipment Health: DTC Enrichment, Freeze Frame, Live Data & Readiness (04)
    // =========================================================================

    [RelayCommand]
    public async Task RefreshDiagnosticsAsync()
    {
        if (_activeAdapter is null || !_activeAdapter.IsConnected)
        {
            StatusBannerMessage = "Not connected. Select a device and click 'Connect' to read DTCs and live data.";
            return;
        }

        try
        {
            _lastDtcs = await _activeAdapter.ReadDTCsAsync();
            if (_lastDtcs.Any(d => d.IsSimulated) || _activeAdapter.IsSimulated)
            {
                IsCurrentDataSimulated = true;
                NotifyProvenanceChanged();
            }

            RebuildDtcRows();

            var hasActive = _lastDtcs.Any(d => d.Status == DtcStatus.Active);
            MilStatusText = hasActive ? "ON" : "OFF";

            // Evaluate On-DTC recording trigger if armed
            EvaluateDtcTriggerIfArmed(_lastDtcs);

            var snapshot = await _activeAdapter.ReadLiveDataAsync(Array.Empty<string>());
            if (snapshot.Count > 0)
            {
                ApplyPidBatchOnUiThread(snapshot);
            }

            var ff = await _activeAdapter.ReadFreezeFrameAsync();
            FreezeFramePids.Clear();
            if (ff is not null)
            {
                FreezeFrameTimestamp = ff.CapturedAt;
                foreach (var pid in ff.Pids)
                {
                    FreezeFramePids.Add(new LivePidRowViewModel(
                        pid.Name,
                        pid.Value,
                        pid.Unit,
                        CurrentUnitSystem,
                        ff.CapturedAt,
                        pid.IsSimulated || _activeAdapter.IsSimulated));
                }
            }

            RebuildReadinessMonitors();
        }
        catch (Exception ex)
        {
            await HandleAdapterFaultOrDisconnectAsync(ex.Message);
            throw;
        }
    }

    private void RebuildDtcRows()
    {
        Dtcs.Clear();
        var adapterSim = _activeAdapter?.IsSimulated ?? true;
        foreach (var dtc in _lastDtcs)
        {
            var match = FindDtcLibraryEntry(dtc.Protocol, dtc.Code);
            var spnOnly = false;
            if (match is null && FindProprietarySpnEntry(dtc) is { } proprietary)
            {
                match = proprietary;
                spnOnly = true;
            }
            Dtcs.Add(new DtcRowViewModel(dtc, match, adapterSim, IsDarkTheme, IsSunlightMode, spnOnly));
        }
        OnPropertyChanged(nameof(HasNoDtcs));
        OnPropertyChanged(nameof(NoDtcsVisibility));
    }

    private void RebuildReadinessMonitors()
    {
        ReadinessMonitors.Clear();
        var isSim = _activeAdapter?.IsSimulated ?? true;
        var hasActiveFault = _lastDtcs.Any(d => d.Status == DtcStatus.Active);
        var hasPendingFault = _lastDtcs.Any(d => d.Status == DtcStatus.Pending);
        var primaryEcu = DiscoveredEcus.FirstOrDefault()?.Id ?? (SelectedProtocol == ProtocolType.J1939 ? "SA 0x00" : "ECM");

        if (SelectedProtocol == ProtocolType.OBD2)
        {
            // Continuous & Non-Continuous I/M Readiness Monitors (Screenshots 08, 09)
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Continuous Monitor", "Misfire Monitor", hasActiveFault ? "Fault Active" : "Ready / Complete", !hasActiveFault, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Continuous Monitor", "Fuel System Monitor", hasActiveFault ? "Fault Active" : "Ready / Complete", !hasActiveFault, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Continuous Monitor", "Comprehensive Component Monitor", "Ready / Complete", true, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Noncontinuous Monitor", "Catalyst Monitor", "Ready / Complete", true, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Noncontinuous Monitor", "Evaporative System Monitor", hasPendingFault ? "Not Ready (Pending)" : "Ready / Complete", !hasPendingFault, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Noncontinuous Monitor", "Oxygen Sensor & Heater Monitor", "Ready / Complete", true, isSim, IsDarkTheme, IsSunlightMode));
        }
        else
        {
            // Heavy-Duty J1939 DM1/DM2 / J1708 System Monitors
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "J1939 / HD Lamp Status", $"Malfunction Indicator Lamp (MIL: {MilStatusText})", hasActiveFault ? "Lamp ON (Active DTCs)" : "Lamp OFF (Normal)", !hasActiveFault, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Continuous Monitor", "Fuel Delivery & Injection System", hasActiveFault ? "Active Fault Present" : "Ready / Normal", !hasActiveFault, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Continuous Monitor", "Engine Cooling & Lubrication", hasPendingFault ? "Pending Warning" : "Ready / Normal", !hasPendingFault, isSim, IsDarkTheme, IsSunlightMode));
            ReadinessMonitors.Add(new ReadinessMonitorRowViewModel(primaryEcu, "Network Monitor", $"{SelectedProtocol} Data Link & ECU Heartbeat", "Online / Active", true, isSim, IsDarkTheme, IsSunlightMode));
        }
    }

    // =========================================================================
    // Live Data Stream Coalescing & Snaplist / Custom Display / Graph (13–16)
    // =========================================================================

    private void OnAdapterLiveDataReceived(object? sender, LiveDataUpdateEventArgs e)
    {
        if (PlaybackState != PlaybackState.Live)
            return;

        lock (_streamLock)
        {
            _pendingStreamPids[e.Value.Name] = e.Value;
        }
    }

    private void OnUiThrottleTick(object? sender, object e)
    {
        if (PlaybackState != PlaybackState.Live)
            return;

        List<PidValue>? batch = null;
        lock (_streamLock)
        {
            if (_pendingStreamPids.Count > 0)
            {
                batch = _pendingStreamPids.Values.ToList();
                _pendingStreamPids.Clear();
            }
        }

        if (batch is null || batch.Count == 0)
            return;

        ApplyPidBatchOnUiThread(batch);
    }

    private void ApplyPidBatchOnUiThread(IReadOnlyList<PidValue> batch, long? explicitSeq = null, double? explicitElapsedSec = null)
    {
        var seq = explicitSeq ?? Interlocked.Increment(ref _liveSampleSequence);
        var elapsedSec = explicitElapsedSec ?? _sessionStopwatch.Elapsed.TotalSeconds;
        var adapterSim = _activeAdapter?.IsSimulated ?? true;
        var anySim = adapterSim;

        foreach (var pid in batch)
        {
            var isSim = pid.IsSimulated || adapterSim;
            if (isSim) anySim = true;

            if (_pidMap.TryGetValue(pid.Name, out var existing))
            {
                existing.Update(pid.Value, pid.Unit, CurrentUnitSystem, pid.Timestamp, isSim, seq, elapsedSec);
            }
            else
            {
                var vm = new LivePidRowViewModel(pid.Name, pid.Value, pid.Unit, CurrentUnitSystem, pid.Timestamp, isSim);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(LivePidRowViewModel.IsSelectedInSnaplist))
                        RefreshSnaplistCollection();
                };
                _pidMap[pid.Name] = vm;
                LivePids.Add(vm);
                AvailablePidNames.Add(pid.Name);

                // Auto-bind default custom display slots & graph PID when first discovered
                AssignDefaultCustomSlotIfEmpty(vm);
                if (SelectedGraphPid is null)
                    SelectedGraphPid = vm;
                if (TriggerPidName is null)
                    TriggerPidName = vm.Name;

                OnPropertyChanged(nameof(HasNoLivePids));
                OnPropertyChanged(nameof(NoLivePidsVisibility));
            }
        }

        if (IsCurrentDataSimulated != anySim)
        {
            IsCurrentDataSimulated = anySim;
            NotifyProvenanceChanged();
        }

        RefreshSnaplistCollection();

        if (SelectedLiveDataMode == LiveDataDisplayMode.LineGraph)
        {
            RefreshGraphGeometry();
        }

        if (PlaybackState == PlaybackState.Live)
        {
            PlaybackFrameSummary = $"Frame: {seq}  Time: {elapsedSec:00.000} sec (LIVE)";
            ProcessLiveFrameForRecording(seq, batch);
        }
    }

    private void AssignDefaultCustomSlotIfEmpty(LivePidRowViewModel vm)
    {
        var emptySlot = CustomDisplaySlots.FirstOrDefault(s => s.BoundPid is null);
        if (emptySlot is not null)
        {
            emptySlot.SelectedPidName = vm.Name;
            emptySlot.BoundPid = vm;
        }
    }

    public void RefreshSnaplistCollection()
    {
        SnaplistPids.Clear();
        foreach (var pid in LivePids)
        {
            if (!OnlyShowSnaplistPids || pid.IsSelectedInSnaplist)
            {
                SnaplistPids.Add(pid);
            }
        }
        SnaplistName = OnlyShowSnaplistPids
            ? $"Custom Snaplist ({SnaplistPids.Count} of {LivePids.Count} Parameters)"
            : $"Standard Snaplist 'All Parameters' ({LivePids.Count})";
    }

    [RelayCommand]
    public void SelectAllSnaplistPids()
    {
        foreach (var pid in LivePids)
            pid.IsSelectedInSnaplist = true;
        RefreshSnaplistCollection();
    }

    [RelayCommand]
    public void DeselectAllSnaplistPids()
    {
        foreach (var pid in LivePids)
            pid.IsSelectedInSnaplist = false;
        RefreshSnaplistCollection();
    }

    [RelayCommand]
    public void SaveAndReturnSnaplist()
    {
        OnlyShowSnaplistPids = true;
        RefreshSnaplistCollection();
        SelectedLiveDataMode = LiveDataDisplayMode.List;
        StatusBannerMessage = $"Applied {SnaplistName}.";
    }

    [RelayCommand]
    public void ApplyCustomDisplaySlots()
    {
        foreach (var slot in CustomDisplaySlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.SelectedPidName) &&
                _pidMap.TryGetValue(slot.SelectedPidName, out var pidVm))
            {
                slot.BoundPid = pidVm;
            }
            else
            {
                slot.BoundPid = null;
            }
        }
        SelectedLiveDataMode = LiveDataDisplayMode.CustomDisplay;
        StatusBannerMessage = "Custom 6-slot gauge/bar/meter display updated.";
    }

    [RelayCommand]
    public void ClearCustomDisplaySlots()
    {
        foreach (var slot in CustomDisplaySlots)
        {
            slot.SelectedPidName = null;
            slot.BoundPid = null;
        }
    }

    public void RefreshGraphGeometry()
    {
        var pid = SelectedGraphPid ?? LivePids.FirstOrDefault();
        if (pid is null || pid.History.Count == 0)
        {
            if (_dispatcherQueue is not null)
            {
                GraphPoints = new PointCollection();
            }
            CursorALabel = "Cursor A: no samples";
            CursorBLabel = "Cursor B: no samples";
            CursorDeltaLabel = "Cursor Delta: —";
            return;
        }

        const double canvasWidth = 720.0;
        const double canvasHeight = 220.0;

        var history = pid.History;
        var minVal = pid.RangeMin;
        var maxVal = pid.RangeMax;
        var span = Math.Max(1e-6, maxVal - minVal);

        GraphScaleTopLabel = $"{LivePidRowViewModel.FormatNumber(maxVal, pid.DisplayUnit)} {pid.DisplayUnit}";
        GraphScaleMidLabel = $"{LivePidRowViewModel.FormatNumber((minVal + maxVal) * 0.5, pid.DisplayUnit)} {pid.DisplayUnit}";
        GraphScaleBottomLabel = $"{LivePidRowViewModel.FormatNumber(minVal, pid.DisplayUnit)} {pid.DisplayUnit}";

        var pts = new PointCollection();
        int count = history.Count;
        for (int i = 0; i < count; i++)
        {
            double x = count == 1 ? 0 : (i / (double)(count - 1)) * canvasWidth;
            double norm = Math.Clamp((history[i].DisplayNumericValue - minVal) / span, 0.0, 1.0);
            double y = canvasHeight - (norm * (canvasHeight - 16.0)) - 8.0;
            pts.Add(new Point(x, y));
        }
        GraphPoints = pts;

        // Compute Cursor A & Cursor B sample indices + Delta (Screenshot 16)
        CursorAX = Math.Clamp(CursorAPercent / 100.0, 0.0, 1.0) * canvasWidth;
        CursorBX = Math.Clamp(CursorBPercent / 100.0, 0.0, 1.0) * canvasWidth;

        int idxA = Math.Clamp((int)Math.Round((CursorAPercent / 100.0) * (count - 1)), 0, count - 1);
        int idxB = Math.Clamp((int)Math.Round((CursorBPercent / 100.0) * (count - 1)), 0, count - 1);

        var sampleA = history[idxA];
        var sampleB = history[idxB];

        CursorALabel = $"Cursor A: Frame = {sampleA.FrameSeq}, {pid.Name} = {LivePidRowViewModel.FormatNumber(sampleA.DisplayNumericValue, pid.DisplayUnit)} {pid.DisplayUnit}";
        CursorBLabel = $"Cursor B: Frame = {sampleB.FrameSeq}, {pid.Name} = {LivePidRowViewModel.FormatNumber(sampleB.DisplayNumericValue, pid.DisplayUnit)} {pid.DisplayUnit}";

        long dFrame = sampleB.FrameSeq - sampleA.FrameSeq;
        double dTime = sampleB.TimeSec - sampleA.TimeSec;
        double dVal = sampleB.DisplayNumericValue - sampleA.DisplayNumericValue;
        CursorDeltaLabel = $"Cursor Delta: ΔFrame = {dFrame:+#;-#;0} | Δt = {dTime:+0.00;-0.00;0.00} s | ΔValue = {LivePidRowViewModel.FormatNumber(dVal, pid.DisplayUnit)} {pid.DisplayUnit}";
    }

    // =========================================================================
    // Recording + Trigger Evaluation + VCR Playback (Screens 17–18)
    // =========================================================================

    public TriggerConfig BuildCurrentTriggerConfig() => new()
    {
        Kind = TriggerKind,
        DtcCode = TriggerKind == TriggerKind.OnDtc && !string.IsNullOrWhiteSpace(TriggerDtcCode) ? TriggerDtcCode.Trim() : null,
        PidName = TriggerKind == TriggerKind.OnPidThreshold ? (TriggerPidName ?? LivePids.FirstOrDefault()?.Name) : null,
        ThresholdValue = TriggerKind == TriggerKind.OnPidThreshold ? TriggerThresholdValue : null,
        ThresholdUnit = TriggerKind == TriggerKind.OnPidThreshold ? TriggerThresholdUnit : null,
        Comparison = TriggerComparison,
        PreTriggerSeconds = Math.Max(0, PreTriggerSeconds),
        PostTriggerSeconds = Math.Max(0.5, PostTriggerSeconds),
        AutoStart = TriggerAutoStart,
        AutoStop = TriggerAutoStop,
    };

    [RelayCommand]
    public async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopRecordingInternalAsync(RecordingStatus.Completed, "Stopped by technician");
        }
        else
        {
            await StartRecordingAsync();
        }
    }

    public async Task StartRecordingAsync()
    {
        if (!IsConnected || _activeAdapter is null)
        {
            StatusBannerMessage = "Connect to an adapter before starting a live-data recording.";
            return;
        }

        await StopPlaybackAndReturnToLiveAsync();

        var workOrderId = await EnsureActiveWorkOrderForRecordingAsync();
        var triggerConfig = BuildCurrentTriggerConfig();
        var source = (_activeAdapter.IsSimulated || IsCurrentDataSimulated)
            ? RecordingSource.Simulated
            : RecordingSource.Real;

        var session = new RecordingSession
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = workOrderId,
            AssetId = Assets.FirstOrDefault()?.Id,
            TechnicianId = null,
            Kind = RecordingKind.LiveData,
            Source = source,
            AdapterName = _activeAdapter.Name,
            Protocol = SelectedProtocol,
            PidNames = LivePids.Select(p => p.Name).ToArray(),
            Trigger = triggerConfig,
            StartedAt = Clock.UtcNow,
            Status = RecordingStatus.Recording,
        };

        _activeRecordingSession = await _dataServices.Recordings.CreateSessionAsync(
            session,
            notes: $"Captured by {SelectedTechnician} ({triggerConfig.Kind})");

        _recordingFrameSequence = 0;
        RecordedFrameCount = 0;
        _pendingDbFrames.Clear();
        _lastThresholdComparedCanonicalValue = null;

        if (triggerConfig.Kind == TriggerKind.Manual)
        {
            _triggerArmed = false;
            _triggerFired = true;
            _triggerFiredAtUtc = DateTimeOffset.UtcNow;
            RecordingStatusLabel = $"REC (Manual · {source})";
        }
        else
        {
            _triggerArmed = true;
            _triggerFired = false;
            RecordingStatusLabel = $"ARMED ({triggerConfig.Kind} · Pre {triggerConfig.PreTriggerSeconds:F1}s / Post {triggerConfig.PostTriggerSeconds:F1}s)";
        }

        IsRecording = true;
        StatusBannerMessage = $"Recording session started ({_activeRecordingSession.Source}, Trigger: {triggerConfig.Kind}).";
        AppendDiagnosticLog($"Started recording {_activeRecordingSession.Id} (Source={_activeRecordingSession.Source}, Trigger={triggerConfig.Kind}).");
    }

    private void ProcessLiveFrameForRecording(long liveSeq, IReadOnlyList<PidValue> currentBatch)
    {
        // Build a full snapshot of all known PIDs at this instant so each frame is a complete row
        var snapshotPids = LivePids.Select(p => new PidValue
        {
            Name = p.Name,
            Value = p.EnteredValue,
            Unit = p.EnteredUnit,
            Timestamp = Clock.UtcNow,
            IsSimulated = p.IsSimulated,
        }).ToArray();

        if (snapshotPids.Length == 0)
            return;

        if (!IsRecording || _activeRecordingSession is null)
        {
            // Maintain pre-trigger ring buffer even while idle/armed
            int maxPreFrames = Math.Max(1, (int)Math.Ceiling(PreTriggerSeconds / SampleIntervalSeconds));
            while (_preTriggerRing.Count >= maxPreFrames)
                _preTriggerRing.RemoveAt(0);

            _preTriggerRing.Add(new RecordedFrameBatch
            {
                Sequence = liveSeq,
                CapturedAt = Clock.UtcNow,
                Pids = snapshotPids,
            });
            return;
        }

        var trigger = _activeRecordingSession.Trigger ?? new TriggerConfig();

        if (_triggerArmed && !_triggerFired)
        {
            // Keep pre-trigger ring buffer updated while waiting for trigger
            int maxPreFrames = Math.Max(1, (int)Math.Ceiling(trigger.PreTriggerSeconds / SampleIntervalSeconds));
            while (_preTriggerRing.Count >= maxPreFrames)
                _preTriggerRing.RemoveAt(0);

            _preTriggerRing.Add(new RecordedFrameBatch
            {
                Sequence = 0,
                CapturedAt = Clock.UtcNow,
                Pids = snapshotPids,
            });

            if (EvaluatePidThresholdTrigger(trigger))
            {
                FireRecordingTrigger("PID threshold crossed");
            }
            return;
        }

        // Trigger has fired (or manual mode): append frame with monotonic 0-based sequence
        var frame = new RecordedFrameBatch
        {
            Sequence = _recordingFrameSequence++,
            CapturedAt = Clock.UtcNow,
            Pids = snapshotPids,
        };
        _pendingDbFrames.Add(frame);
        RecordedFrameCount = (int)_recordingFrameSequence;

        // Flush batch every 5 frames (~0.5 s) asynchronously
        if (_pendingDbFrames.Count >= 5)
        {
            _ = FlushPendingRecordingFramesAsync();
        }

        // Check post-trigger auto-stop window
        if (trigger.Kind != TriggerKind.Manual && trigger.AutoStop)
        {
            var elapsedPost = (DateTimeOffset.UtcNow - _triggerFiredAtUtc).TotalSeconds;
            if (elapsedPost >= trigger.PostTriggerSeconds)
            {
                _ = StopRecordingInternalAsync(RecordingStatus.Completed, $"Post-trigger window ({trigger.PostTriggerSeconds:F1}s) elapsed");
            }
        }
    }

    private void EvaluateDtcTriggerIfArmed(IReadOnlyList<DtcReadout> dtcs)
    {
        if (!IsRecording || !_triggerArmed || _triggerFired || _activeRecordingSession?.Trigger is null)
            return;

        var trigger = _activeRecordingSession.Trigger;
        if (trigger.Kind != TriggerKind.OnDtc || dtcs.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(trigger.DtcCode))
        {
            FireRecordingTrigger($"DTC detected ({dtcs[0].Code})");
        }
        else if (dtcs.Any(d => string.Equals(d.Code, trigger.DtcCode.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            FireRecordingTrigger($"Target DTC '{trigger.DtcCode}' detected");
        }
    }

    private bool EvaluatePidThresholdTrigger(TriggerConfig trigger)
    {
        if (trigger.Kind != TriggerKind.OnPidThreshold ||
            string.IsNullOrWhiteSpace(trigger.PidName) ||
            trigger.ThresholdValue is not double rawThreshold)
        {
            return false;
        }

        if (!_pidMap.TryGetValue(trigger.PidName, out var pidVm))
            return false;

        // Canonicalize threshold & PID value when unit is known
        double currentVal = pidVm.CanonicalValue;
        double thresholdVal = rawThreshold;
        if (!string.IsNullOrWhiteSpace(trigger.ThresholdUnit) && UnitConverter.IsKnownUnit(trigger.ThresholdUnit) &&
            UnitConverter.IsKnownUnit(pidVm.EnteredUnit) &&
            string.Equals(UnitConverter.CanonicalUnit(trigger.ThresholdUnit), pidVm.CanonicalUnit, StringComparison.Ordinal))
        {
            thresholdVal = UnitConverter.ToCanonical(rawThreshold, trigger.ThresholdUnit);
        }
        else
        {
            currentVal = pidVm.DisplayNumericValue;
        }

        bool fired = trigger.Comparison switch
        {
            ThresholdComparison.Above => currentVal >= thresholdVal,
            ThresholdComparison.Below => currentVal <= thresholdVal,
            ThresholdComparison.Crosses => _lastThresholdComparedCanonicalValue is double prev &&
                                           ((prev < thresholdVal && currentVal >= thresholdVal) ||
                                            (prev > thresholdVal && currentVal <= thresholdVal)),
            _ => false,
        };

        _lastThresholdComparedCanonicalValue = currentVal;
        return fired;
    }

    private void FireRecordingTrigger(string reason)
    {
        _triggerArmed = false;
        _triggerFired = true;
        _triggerFiredAtUtc = DateTimeOffset.UtcNow;

        // Flush pre-trigger buffered frames first with 0-based monotonic sequence numbers
        foreach (var pre in _preTriggerRing)
        {
            _pendingDbFrames.Add(new RecordedFrameBatch
            {
                Sequence = _recordingFrameSequence++,
                CapturedAt = pre.CapturedAt,
                Pids = pre.Pids,
            });
        }
        _preTriggerRing.Clear();
        RecordedFrameCount = (int)_recordingFrameSequence;
        RecordingStatusLabel = $"TRIGGERED ({reason})";
        StatusBannerMessage = $"Recording trigger fired: {reason}. Capturing post-trigger window…";
        AppendDiagnosticLog($"Recording trigger fired: {reason}.");
    }

    private async Task FlushPendingRecordingFramesAsync()
    {
        if (_activeRecordingSession is null || _pendingDbFrames.Count == 0)
            return;

        var recordingId = _activeRecordingSession.Id;
        var toWrite = _pendingDbFrames.ToArray();
        _pendingDbFrames.Clear();

        try
        {
            await _dataServices.RecordingFrames.AppendAsync(recordingId, toWrite);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] Failed to append recording frames: {ex.Message}");
        }
    }

    private async Task StopRecordingInternalAsync(RecordingStatus finalStatus, string reason)
    {
        if (_activeRecordingSession is null)
            return;

        var session = _activeRecordingSession;
        IsRecording = false;
        _triggerArmed = false;
        _triggerFired = false;

        try
        {
            await FlushPendingRecordingFramesAsync();
            await _dataServices.Recordings.CloseRecordingAsync(session.Id, finalStatus, Clock.UtcNow);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] CloseRecordingAsync error: {ex.Message}");
        }
        finally
        {
            _activeRecordingSession = null;
        }

        RecordingStatusLabel = finalStatus == RecordingStatus.Interrupted
            ? $"INTERRUPTED ({RecordedFrameCount} frames saved)"
            : $"COMPLETED ({RecordedFrameCount} frames saved)";

        AppendDiagnosticLog($"Recording {session.Id} closed as {finalStatus} ({reason}, {RecordedFrameCount} frames).");
        await ReloadRecordingsListAsync();
        SelectedRecordingSession = SavedRecordings.FirstOrDefault(r => r.Id == session.Id) ?? SavedRecordings.FirstOrDefault();
    }

    private async Task<string> EnsureActiveWorkOrderForRecordingAsync()
    {
        var existing = WorkOrders.FirstOrDefault(w => w.Status != WorkOrderStatus.Closed) ?? WorkOrders.FirstOrDefault();
        if (existing is not null)
            return existing.Id;

        var asset = Assets.FirstOrDefault();
        if (asset is null)
        {
            asset = new Asset
            {
                Id = Guid.NewGuid().ToString("D"),
                AssetKind = AssetKind.Equipment,
                AssetNumber = "SIM-ASSET-01",
                Name = "Bench / Diagnostic Session Asset",
                Status = AssetStatus.InService,
            };
            await _dataServices.Assets.AddAsync(asset);
            Assets.Add(asset);
            OnPropertyChanged(nameof(HasNoAssets));
            OnPropertyChanged(nameof(NoAssetsVisibility));
        }

        string assignedNumber = "000001";
        try
        {
            assignedNumber = await _dataServices.WorkOrders.AssignNextNumberAsync();
        }
        catch
        {
            // Keep default if table not yet migrated in isolated mock
        }

        var wo = new WorkOrder
        {
            Id = Guid.NewGuid().ToString("D"),
            Number = assignedNumber,
            AssetId = asset.Id,
            Status = WorkOrderStatus.InProgress,
            OpenedAt = Clock.UtcNow,
            ComplaintSummary = "Live Diagnostic Recording Session",
        };
        await _dataServices.WorkOrders.AddAsync(wo);
        WorkOrders.Add(wo);
        SelectedWorkOrder = wo;
        OnPropertyChanged(nameof(HasNoWorkOrders));
        OnPropertyChanged(nameof(NoWorkOrdersVisibility));
        return wo.Id;
    }

    // ---- VCR Playback Transport (Screen 17) ----

    [RelayCommand]
    public async Task LoadSelectedRecordingForPlaybackAsync()
    {
        if (SelectedRecordingSession is null)
        {
            StatusBannerMessage = "Select a saved recording session to play back.";
            return;
        }

        if (IsRecording)
        {
            await StopRecordingInternalAsync(RecordingStatus.Completed, "Switched to playback");
        }

        _playbackTimer?.Stop();
        var rec = await _dataServices.Recordings.GetSessionByIdAsync(SelectedRecordingSession.Id) ?? SelectedRecordingSession;
        SelectedRecordingSession = rec;

        IsCurrentDataSimulated = rec.Source == RecordingSource.Simulated;
        NotifyProvenanceChanged();

        PlaybackMaxSequence = Math.Max(0, rec.FrameCount - 1);
        PlaybackSequence = 0;
        _firstFrameTimestampUtc = null;
        _currentPlaybackWindow = null;

        var trigDesc = rec.Trigger is null ? "Manual" : $"{rec.Trigger.Kind}";
        PlaybackMetadataSummary = $"Playback: {rec.AdapterName} · {rec.Protocol} · Source: {rec.Source.ToString().ToUpperInvariant()} · Status: {rec.Status} · Trigger: {trigDesc} · {rec.FrameCount} frames";

        await SeekPlaybackToSequenceAsync(0, PlaybackState.Paused);
    }

    [RelayCommand]
    public async Task PlayRecordingAsync()
    {
        if (SelectedRecordingSession is null)
        {
            if (SavedRecordings.Count > 0)
                SelectedRecordingSession = SavedRecordings[0];
            else
                return;
        }

        if (PlaybackState == PlaybackState.Live || _currentPlaybackWindow is null)
        {
            await LoadSelectedRecordingForPlaybackAsync();
        }

        if (PlaybackSequence >= PlaybackMaxSequence && PlaybackMaxSequence > 0)
        {
            await SeekPlaybackToSequenceAsync(0, PlaybackState.Playing);
        }
        else
        {
            PlaybackState = PlaybackState.Playing;
        }

        _playbackTimer?.Start();
    }

    [RelayCommand]
    public void PauseRecording()
    {
        _playbackTimer?.Stop();
        if (PlaybackState == PlaybackState.Playing)
        {
            PlaybackState = PlaybackState.Paused;
        }
    }

    [RelayCommand]
    public async Task StopPlaybackAndReturnToLiveAsync()
    {
        _playbackTimer?.Stop();
        _currentPlaybackWindow = null;
        PlaybackState = PlaybackState.Live;
        PlaybackMetadataSummary = "Live Stream";
        IsCurrentDataSimulated = _activeAdapter?.IsSimulated ?? true;
        NotifyProvenanceChanged();
        await Task.CompletedTask;
    }

    [RelayCommand]
    public Task StepForwardFrameAsync() =>
        SeekPlaybackToSequenceAsync(Math.Min(PlaybackMaxSequence, PlaybackSequence + 1), PlaybackState.Paused);

    [RelayCommand]
    public Task StepBackwardFrameAsync() =>
        SeekPlaybackToSequenceAsync(Math.Max(0, PlaybackSequence - 1), PlaybackState.Paused);

    [RelayCommand]
    public Task JumpToStartFrameAsync() =>
        SeekPlaybackToSequenceAsync(0, PlaybackState.Paused);

    [RelayCommand]
    public Task JumpToEndFrameAsync() =>
        SeekPlaybackToSequenceAsync(PlaybackMaxSequence, PlaybackState.Paused);

    public async Task SeekPlaybackToSequenceAsync(long targetSequence, PlaybackState desiredState)
    {
        if (SelectedRecordingSession is null)
            return;

        targetSequence = Math.Max(0, targetSequence);
        var window = _currentPlaybackWindow;

        bool insideWindow = window is not null &&
                            window.Frames.Count > 0 &&
                            targetSequence >= window.Frames[0].Sequence &&
                            targetSequence <= window.Frames[^1].Sequence;

        if (!insideWindow)
        {
            window = await _dataServices.RecordingFrames.GetWindowAsync(new RecordingSeekRequest
            {
                RecordingId = SelectedRecordingSession.Id,
                StartSequence = targetSequence,
                MaxFrames = PlaybackWindowSize,
            });
            _currentPlaybackWindow = window;
        }

        if (window is null || window.Frames.Count == 0)
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackFrameSummary = "Frame: 0 (Empty recording)";
            return;
        }

        var frame = window.Frames.FirstOrDefault(f => f.Sequence == targetSequence) ?? window.Frames[0];
        PlaybackSequence = frame.Sequence;
        PlaybackState = desiredState;

        if (DateTimeOffset.TryParse(frame.CapturedAt, out var frameTime))
        {
            _firstFrameTimestampUtc ??= frameTime;
            PlaybackPositionSeconds = Math.Max(0, (frameTime - _firstFrameTimestampUtc.Value).TotalSeconds);
        }
        else
        {
            PlaybackPositionSeconds = frame.Sequence * SampleIntervalSeconds;
        }

        PlaybackDurationSeconds = Math.Max(PlaybackPositionSeconds, (PlaybackMaxSequence + 1) * SampleIntervalSeconds);

        var cursor = new PlaybackCursor
        {
            RecordingId = SelectedRecordingSession.Id,
            Sequence = frame.Sequence,
            CapturedAt = frame.CapturedAt,
            PositionSeconds = PlaybackPositionSeconds,
            DurationSeconds = PlaybackDurationSeconds,
            State = desiredState,
            Rate = PlaybackRate,
        };

        PlaybackFrameSummary = $"Frame: {cursor.Sequence}/{PlaybackMaxSequence}  Time: {cursor.PositionSeconds:00.000}s / {cursor.DurationSeconds:00.000}s ({cursor.Rate:0.##}x · {SelectedRecordingSession.Source.ToString().ToUpperInvariant()})";

        ApplyPidBatchOnUiThread(frame.Pids, explicitSeq: frame.Sequence, explicitElapsedSec: cursor.PositionSeconds);
    }

    private async void OnPlaybackTimerTick(object? sender, object e)
    {
        if (PlaybackState != PlaybackState.Playing || SelectedRecordingSession is null)
        {
            _playbackTimer?.Stop();
            return;
        }

        var nextSeq = PlaybackSequence + 1;
        if (nextSeq > PlaybackMaxSequence)
        {
            _playbackTimer?.Stop();
            PlaybackState = PlaybackState.Paused;
            return;
        }

        await SeekPlaybackToSequenceAsync(nextSeq, PlaybackState.Playing);
    }

    // =========================================================================
    // Clear DTCs & Audit Trail (Screenshot 27)
    // =========================================================================

    [RelayCommand]
    public async Task ClearDtcsAsync()
    {
        if (_activeAdapter is null || !_activeAdapter.IsConnected)
            return;

        if (!await ConfirmClearDtcsAsync())
        {
            StatusBannerMessage = "Clear DTCs cancelled — no codes were cleared.";
            return;
        }

        RecordClearDtcsAuditNote();

        await _activeAdapter.ClearDTCsAsync();
        await RefreshDiagnosticsAsync();
        StatusBannerMessage = $"Active and Pending {(IsCurrentDataSimulated ? "SIMULATED " : "")}DTCs cleared (stored history retained).";
    }

    private async Task<bool> ConfirmClearDtcsAsync()
    {
        if (DialogXamlRoot is null)
        {
            StatusBannerMessage = "Cannot show confirmation dialog; DTCs were not cleared.";
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "Clear Diagnostic Trouble Codes & Freeze Frame",
            Content = "This function clears the following information on the connected ECU(s):\n\n" +
                      "• Malfunction Indicator Lamp (MIL)\n" +
                      "• Confirmed / Active and Pending Diagnostic Trouble Codes\n" +
                      "• Freeze Frame snapshot data\n" +
                      "• Status of readiness monitors\n\n" +
                      "Ensure ignition is ON and engine is not running before initiating this function.\n" +
                      "Do you want to perform this clear function?",
            PrimaryButtonText = "Yes, Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = DialogXamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void RecordClearDtcsAuditNote()
    {
        var preClearCodes = string.Join("; ", _lastDtcs.Select(d => $"{d.Code} [{d.Status}]"));
        if (string.IsNullOrWhiteSpace(preClearCodes))
            preClearCodes = "(no codes present)";

        AppendDiagnosticLog(
            $"Cleared DTCs — Technician: {SelectedTechnician} | Adapter: {AdapterDeviceName} | Pre-clear codes: {preClearCodes}");
    }

    private void AppendDiagnosticLog(string message)
    {
        DiagnosticLog.Add(new DiagnosticLogEntry(Clock.UtcNow, message));
        OnPropertyChanged(nameof(NoDiagnosticLogVisibility));
    }

    public void ReportError(string context, Exception ex)
    {
        Debug.WriteLine($"[GSE Tech Pro] {context}: {ex}");
        StatusBannerMessage = $"{context}: {ex.Message}";
        AppendDiagnosticLog($"{context}: {ex.Message}");
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        if (DialogXamlRoot is null)
            return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = DialogXamlRoot,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] Failed to show error dialog: {ex}");
        }
    }

    private void ClearDiagnosticCollections()
    {
        lock (_streamLock)
        {
            _pendingStreamPids.Clear();
        }

        _lastDtcs = Array.Empty<DtcReadout>();
        RebuildDtcRows();

        _pidMap.Clear();
        LivePids.Clear();
        SnaplistPids.Clear();
        AvailablePidNames.Clear();
        OnPropertyChanged(nameof(HasNoLivePids));
        OnPropertyChanged(nameof(NoLivePidsVisibility));

        FreezeFramePids.Clear();
        FreezeFrameTimestamp = "Not captured";

        ReadinessMonitors.Clear();
    }

    [RelayCommand]
    public void ToggleUnitSystem()
    {
        CurrentUnitSystem = CurrentUnitSystem == UnitSystem.Imperial
            ? UnitSystem.Metric
            : UnitSystem.Imperial;

        OnPropertyChanged(nameof(UnitSystemLabel));

        foreach (var row in LivePids)
            row.FormatForUnitSystem(CurrentUnitSystem);

        foreach (var row in FreezeFramePids)
            row.FormatForUnitSystem(CurrentUnitSystem);

        OnPropertyChanged(nameof(GuidedSpecSummaryText));
        OnPropertyChanged(nameof(GuidedReadingPreviewText));
        OnPropertyChanged(nameof(SelectedTestPointSpecDisplay));
        RebuildWorkOrderReadingRows();

        RefreshGraphGeometry();
        StatusBannerMessage = $"Switched shop default display units to {CurrentUnitSystem} (canonical SI storage & per-reading overrides preserved).";
    }

    // =========================================================================
    // Toolbar & Left Navigation Rail Routing
    // =========================================================================

    private static IReadOnlyList<SidebarItem> BuildSidebarSections()
    {
        static SidebarItem Child(string parent, string key, string label, string glyph) =>
            new(key, label, glyph, parent);

        return new[]
        {
            new SidebarItem("Diagnostics", "Diagnostics", "")
            {
                Children = new[]
                {
                    Child("Diagnostics", "Overview", "Equipment Health", ""),
                    Child("Diagnostics", "LiveData", "Live Data", ""),
                    Child("Diagnostics", "DTCs", "Trouble Codes", ""),
                    Child("Diagnostics", "FreezeFrames", "Freeze Frames", ""),
                    Child("Diagnostics", "Readiness", "Readiness", ""),
                    Child("Diagnostics", "Monitors", "Monitors", ""),
                },
            },
            new SidebarItem("Scope", "Scope", "")
            {
                Children = new[]
                {
                    Child("Scope", "Overview", "Scope Capture", ""),
                    Child("Scope", "ScopeChannels", "Channels", ""),
                    Child("Scope", "GuidedTests", "Guided Tests", ""),
                    Child("Scope", "OneTapTests", "One-tap Tests", ""),
                    Child("Scope", "RefWaveforms", "Reference Waveforms", ""),
                },
            },
            new SidebarItem("CanCapture", "CAN Capture", "")
            {
                Children = new[]
                {
                    Child("CanCapture", "CanMonitor", "Bus Monitor", ""),
                    Child("CanCapture", "CanGraph", "Line Graph", ""),
                    Child("CanCapture", "CanSnaplist", "Snaplist", ""),
                },
            },
            new SidebarItem("WorkOrders", "Work Orders", ""),
            new SidebarItem("Assets", "Equipment", ""),
            new SidebarItem("AiTechChat", "AI Tech Chat", "")
            {
                Children = new[]
                {
                    Child("AiTechChat", "AiChat", "Tech Chat", ""),
                    Child("AiTechChat", "Recommendations", "Recommendations", ""),
                    Child("AiTechChat", "GuidedTree", "Troubleshooting", ""),
                    Child("AiTechChat", "Readings", "Manual Readings", ""),
                    Child("AiTechChat", "ManualSearch", "Manual Search", ""),
                },
            },
            new SidebarItem("Camera", "Camera", ""),
        };
    }

    /// <summary>Sidebar section key for the active tool (used to keep the sidebar selection and page command bars in sync).</summary>
    public string CurrentSidebarSectionKey => ActiveTool switch
    {
        "Work Orders" => "WorkOrders",
        "Equipment Registry" => "Assets",
        "Scope" => "Scope",
        "CAN Capture" => "CanCapture",
        "Camera" => "Camera",
        "AI Tech Chat" => "AiTechChat",
        "Settings" => "Settings",
        _ => "Diagnostics",
    };

    public Visibility DeviceCommandBarVisibility =>
        CurrentSidebarSectionKey == "Diagnostics" || (CurrentSidebarSectionKey == "CanCapture" && !IsCanMonitorViewVisible)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility WorkOrdersCommandBarVisibility =>
        CurrentSidebarSectionKey == "WorkOrders" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TabStripVisibility => OpenTabs.Count > 0 && !IsScopeFullScreen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ConnectButtonVisibility => IsConnected ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DisconnectButtonVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonVisibility));
        OnPropertyChanged(nameof(DisconnectButtonVisibility));
    }

    public bool IsSettingsViewVisible => ActiveSectionKey == "Settings";

    public Visibility SettingsViewVisibility => IsSettingsViewVisible ? Visibility.Visible : Visibility.Collapsed;

    partial void OnActiveToolChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentSidebarSectionKey));
        OnPropertyChanged(nameof(DeviceCommandBarVisibility));
        OnPropertyChanged(nameof(WorkOrdersCommandBarVisibility));
        OnPropertyChanged(nameof(CurrentModuleNavItems));
        OnPropertyChanged(nameof(ModuleNavTitle));
        OnPropertyChanged(nameof(ModuleNavGlyph));
        OnPropertyChanged(nameof(ModuleNavVisibility));
        SyncModuleNavSelection(null);
    }

    // ------------------------------------------------------------------
    // Phase 10B: Shop Foreman Pro shell. The menu bar and icon toolbar pick a module; inside a
    // module, a navy left menu lists that module's pages (SFP's "New Scan / Recent Vehicles" rail).
    // The menu reuses SidebarSections, so the toolbar, menus and in-page links share one router.
    // ------------------------------------------------------------------

    private bool _syncingModuleNav;

    private SidebarItem? CurrentSection =>
        SidebarSections.FirstOrDefault(s => s.Key == CurrentSidebarSectionKey);

    /// <summary>Pages of the active module, shown in the navy left menu. Empty for screens without sub-pages.</summary>
    public IReadOnlyList<SidebarItem> CurrentModuleNavItems => CurrentSection?.Children ?? Array.Empty<SidebarItem>();

    public string ModuleNavTitle => CurrentSection?.Label ?? string.Empty;

    public string ModuleNavGlyph => CurrentSection?.Glyph ?? string.Empty;

    public Visibility ModuleNavVisibility => CurrentModuleNavItems.Count > 0 && !IsScopeFullScreen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The highlighted page in the module menu. Setting it from the UI opens that page.</summary>
    [ObservableProperty]
    private SidebarItem? _selectedModuleNavItem;

    partial void OnSelectedModuleNavItemChanged(SidebarItem? value)
    {
        if (_syncingModuleNav || value is null)
            return;
        _ = OpenModuleNavItemAsync(value);
    }

    private async Task OpenModuleNavItemAsync(SidebarItem item)
    {
        try
        {
            await SelectSidebarItemAsync(item);
        }
        catch (Exception ex)
        {
            ReportError("Navigation failed", ex);
        }
    }

    /// <summary>Highlights a module page without navigating (code-driven page changes).</summary>
    private void SyncModuleNavSelection(string? childKey)
    {
        var items = CurrentModuleNavItems;
        var target = (childKey is null ? null : items.FirstOrDefault(c => c.Key == childKey)) ?? items.FirstOrDefault();
        if (ReferenceEquals(SelectedModuleNavItem, target))
            return;
        _syncingModuleNav = true;
        try
        {
            SelectedModuleNavItem = target;
        }
        finally
        {
            _syncingModuleNav = false;
        }
    }

    /// <summary>Underline under the toolbar tile of the active module (x:Bind function binding).</summary>
    public static Visibility ToolbarIndicator(string activeTool, string tool) =>
        string.Equals(activeTool, tool, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Finds the sidebar entry for a page key: the active section's own sub-page first,
    /// then a top-level section, then a sub-page of any other section.
    /// </summary>
    public SidebarItem? FindSidebarItem(string key)
    {
        var current = SidebarSections.FirstOrDefault(s => s.Key == CurrentSidebarSectionKey);
        return current?.Children.FirstOrDefault(c => c.Key == key)
               ?? SidebarSections.FirstOrDefault(s => s.Key == key)
               ?? SidebarSections.SelectMany(s => s.Children).FirstOrDefault(c => c.Key == key);
    }

    /// <summary>
    /// Opens a sidebar entry. A sub-page first switches to its parent section when that
    /// section is not already active, then opens the sub-page.
    /// </summary>
    public async Task SelectSidebarItemAsync(SidebarItem item)
    {
        if (item.ParentKey is null)
        {
            SelectToolbarAction(item.Key);
            return;
        }

        if (CurrentSidebarSectionKey != item.ParentKey)
        {
            SelectToolbarAction(item.ParentKey);
        }

        await SelectNavRailItem(item.Key);
        SyncModuleNavSelection(item.Key);
    }

    [RelayCommand]
    public void SelectToolbarAction(string actionKey)
    {
        switch (actionKey)
        {
            case "Diagnostics":
                ActivateDiagnosticTool("Diagnostics", "Diagnostics", "Equipment Health");
                break;

            case "Settings":
                ActiveTool = "Settings";
                ActiveSectionKey = "Settings";
                BreadcrumbCategory = "Settings";
                BreadcrumbLeaf = "Display, Units & Devices";
                break;

            case "WorkOrders":
            case "Open":
                ActiveTool = "Work Orders";
                ActiveSectionKey = "WorkOrders";
                BreadcrumbRoot = "Shop Foreman";
                BreadcrumbCategory = "Repair Orders";
                BreadcrumbLeaf = "Work Order Browser";
                break;

            case "New":
                var tabNum = OpenTabs.Count + 1;
                var newTab = new OpenWorkOrderTab(
                    $"WO-DRAFT-{tabNum:00}",
                    $"New Work Order #{tabNum:00} (Draft)",
                    "Unassigned Asset",
                    "Draft");
                OpenTabs.Add(newTab);
                SelectedTab = newTab;
                StatusBannerMessage = $"Opened draft work order tab '{newTab.Title}'.";
                break;

            case "Assets":
            case "Home":
                ActiveTool = "Equipment Registry";
                ActiveSectionKey = "Assets";
                BreadcrumbRoot = "Equipment Selection";
                BreadcrumbCategory = "Fleet & GSE Registry";
                BreadcrumbLeaf = "Assets & Equipment Database";
                break;

            case "Search":
                ActiveSectionKey = "WorkOrders";
                BreadcrumbRoot = "Shop Foreman";
                BreadcrumbCategory = "Search";
                BreadcrumbLeaf = "Work Orders & Assets";
                StatusBannerMessage = "Showing Work Order & Asset repository view.";
                break;

            case "Save":
                StatusBannerMessage = "Use the Record button in the VCR bar below to capture live-data frames to SQLite.";
                break;

            case "Print":
                StatusBannerMessage = "Print report preview is scheduled for later phase.";
                break;

            case "Notes":
                ShowPlaceholder("Technician & Work Order Notes", "Structured work order, asset, and frame notes panel.");
                break;

            case "J2534":
                SelectedProtocol = ProtocolType.J1939;
                ActivateDiagnosticTool("J2534", "J2534 Pass-Thru Diagnostics", "Equipment Health");
                break;

            case "HeavyDuty":
                SelectedProtocol = ProtocolType.J1939;
                ActivateDiagnosticTool("Heavy Duty", "Heavy Duty (J1939 / J1708)", "Equipment Health");
                break;

            case "OBDII":
                SelectedProtocol = ProtocolType.OBD2;
                ActivateDiagnosticTool("OBD II", "Global OBD II Diagnostics", "Vehicle Health");
                break;

            case "Scope":
                ActiveTool = "Scope";
                ActiveSectionKey = "Scope";
                BreadcrumbRoot = "Equipment Selection";
                BreadcrumbCategory = "Oscilloscope (4-Channel)";
                BreadcrumbLeaf = "Scope Capture, Guided Tests & Measurements";
                _ = EnsureScopeCatalogAndDevicesLoadedAsync();
                break;

            case "CanCapture":
                ActiveTool = "CAN Capture";
                ActiveSectionKey = "CanMonitor";
                SelectedLiveDataMode = LiveDataDisplayMode.List;
                BreadcrumbRoot = "Equipment Selection";
                BreadcrumbCategory = "CAN / Bus Capture";
                BreadcrumbLeaf = "Bus Monitor (listen-only)";
                RefreshCanDevices();
                StatusBannerMessage = CanStatusText;
                break;

            case "Camera":
                ActiveTool = "Camera";
                ShowPlaceholder("Camera & Pen Annotation", "Rugged tablet photo capture, pen markup, and dataplate OCR (Phase 7).");
                break;

            case "AiTechChat":
                ActiveTool = "AI Tech Chat";
                ActiveSectionKey = "AiChat";
                BreadcrumbRoot = "Equipment Selection";
                BreadcrumbCategory = "AI Tech Chat";
                BreadcrumbLeaf = "Technician Assistant & Citations";
                EnsureWelcomeChatMessage();
                break;
        }

        NotifyViewVisibilityChanged();
    }

    [RelayCommand]
    public async Task SelectNavRailItem(string navKey)
    {
        switch (navKey)
        {
            case "Overview":
            case "Back":
                if (ActiveTool == "Scope")
                {
                    ActiveSectionKey = "Scope";
                    BreadcrumbLeaf = "Scope Capture, Guided Tests & Measurements";
                    break;
                }
                ActiveSectionKey = "Diagnostics";
                SelectedLiveDataMode = LiveDataDisplayMode.List;
                BreadcrumbLeaf = "Equipment Health (All-in-One)";
                break;

            case "Scope":
            case "ScopeChannels":
                ActiveTool = "Scope";
                ActiveSectionKey = "ScopeChannels";
                BreadcrumbCategory = "Oscilloscope (4-Channel)";
                BreadcrumbLeaf = "Channels CH1–CH4, Timebase & Trigger";
                await EnsureScopeCatalogAndDevicesLoadedAsync();
                break;

            case "GuidedTests":
                ActiveTool = "Scope";
                ActiveSectionKey = "GuidedTests";
                BreadcrumbCategory = "Oscilloscope (4-Channel)";
                BreadcrumbLeaf = "Automotive Guided Scope Tests (Safety First)";
                await EnsureScopeCatalogAndDevicesLoadedAsync();
                break;

            case "OneTapTests":
                ActiveTool = "Scope";
                ActiveSectionKey = "OneTapTests";
                BreadcrumbCategory = "Oscilloscope (4-Channel)";
                BreadcrumbLeaf = "One-tap Tests (Battery / Cranking)";
                await EnsureScopeCatalogAndDevicesLoadedAsync();
                break;

            case "RefWaveforms":
                ActiveTool = "Scope";
                ActiveSectionKey = "RefWaveforms";
                BreadcrumbCategory = "Oscilloscope (4-Channel)";
                BreadcrumbLeaf = "Reference Expectations & Saved Waveforms";
                await EnsureScopeCatalogAndDevicesLoadedAsync();
                break;

            case "LiveData":
                ActiveSectionKey = "LiveData";
                SelectedLiveDataMode = LiveDataDisplayMode.List;
                BreadcrumbLeaf = "Live Snaplist & Multi-View";
                break;

            case "EditSnaplist":
                ActiveSectionKey = "LiveData";
                SelectedLiveDataMode = LiveDataDisplayMode.SnaplistEditor;
                BreadcrumbLeaf = "Edit Custom Snaplist";
                break;

            case "CustomDisplay":
                ActiveSectionKey = "LiveData";
                SelectedLiveDataMode = LiveDataDisplayMode.CustomDisplay;
                BreadcrumbLeaf = "Custom Display Builder (Gauges / Bars / Meters)";
                break;

            case "GraphView":
                ActiveSectionKey = "LiveData";
                SelectedLiveDataMode = LiveDataDisplayMode.LineGraph;
                BreadcrumbLeaf = "Line Graph & Dual Cursors";
                RefreshGraphGeometry();
                break;

            case "CurrentData":
                ActiveSectionKey = "CurrentData";
                SelectedLiveDataMode = LiveDataDisplayMode.List;
                BreadcrumbLeaf = "Current Data (Value / Units)";
                break;

            // CAN Capture pages (Phase 10B.3 menu; the bus monitor itself is Phase 10C.2)
            case "CanMonitor":
                ActiveSectionKey = "CanMonitor";
                BreadcrumbLeaf = "Bus Monitor (listen-only)";
                if (CanDevices.Count == 0)
                    RefreshCanDevices();
                break;

            case "CanGraph":
                ActiveSectionKey = "LiveData";
                SelectedLiveDataMode = LiveDataDisplayMode.LineGraph;
                BreadcrumbLeaf = "Line Graph & Dual Cursors";
                RefreshGraphGeometry();
                break;

            case "CanSnaplist":
                ActiveSectionKey = "LiveData";
                SelectedLiveDataMode = LiveDataDisplayMode.SnaplistEditor;
                BreadcrumbLeaf = "Edit Custom Snaplist";
                break;

            case "Refresh":
                await RefreshDiagnosticsAsync();
                break;

            case "DTCs":
                ActiveSectionKey = "DTCs";
                BreadcrumbLeaf = "Diagnostic Trouble Codes (DTC Library Enriched)";
                break;

            case "FreezeFrames":
                ActiveSectionKey = "FreezeFrames";
                BreadcrumbLeaf = "Freeze Frame Snapshot";
                break;

            case "Readiness":
                ActiveSectionKey = "Readiness";
                BreadcrumbLeaf = "I/M Readiness Tests";
                break;

            case "Monitors":
                ActiveSectionKey = "Monitors";
                BreadcrumbLeaf = "On-Board Monitors";
                break;

            case "DataFormat":
                if (ActiveTool == "Scope")
                {
                    ScopeZoomFactor = ScopeZoomFactor >= 16.0 ? 1.0 : ScopeZoomFactor * 2.0;
                    if (Math.Abs(ScopeZoomFactor - 1.0) < 1e-6)
                        ScopePanPercent = 0.0;
                    break;
                }
                CycleLiveDataFormat();
                break;

            case "WorkOrders":
                ActiveSectionKey = "WorkOrders";
                BreadcrumbLeaf = "Work Order Browser";
                break;

            case "Assets":
                ActiveSectionKey = "Assets";
                BreadcrumbLeaf = "Equipment & Vehicle Registry";
                break;

            case "AiChat":
                ActiveTool = "AI Tech Chat";
                ActiveSectionKey = "AiChat";
                BreadcrumbCategory = "AI Tech Chat";
                BreadcrumbLeaf = "Technician Assistant & Citations";
                EnsureWelcomeChatMessage();
                break;

            case "GuidedTree":
                ActiveTool = "AI Tech Chat";
                ActiveSectionKey = "GuidedTree";
                BreadcrumbCategory = "AI Tech Chat";
                BreadcrumbLeaf = "Guided Troubleshooting";
                await ReloadGuidedCatalogFromDatabaseAsync();
                await ReloadWorkOrderReadingsAsync();
                break;

            case "ManualSearch":
                ActiveTool = "AI Tech Chat";
                ActiveSectionKey = "ManualSearch";
                BreadcrumbCategory = "AI Tech Chat";
                BreadcrumbLeaf = "Repair Manuals & Hybrid RAG Search";
                break;

            case "Recommendations":
                ActiveSectionKey = "Recommendations";
                BreadcrumbCategory = "Repair History Learning (Phase 5)";
                BreadcrumbLeaf = "Repair Recommendations & Evidence";
                await LoadRepairRecommendationsAsync();
                break;

            case "Readings":
                ActiveSectionKey = "Readings";
                BreadcrumbCategory = "GSE Troubleshooting (No-ECU)";
                BreadcrumbLeaf = "Manual Readings & Test Points";
                await ReloadGuidedCatalogFromDatabaseAsync();
                await ReloadWorkOrderReadingsAsync();
                break;

            default:
                ActiveSectionKey = "Diagnostics";
                BreadcrumbLeaf = "Equipment Health";
                break;
        }

        NotifyViewVisibilityChanged();
    }

    private void CycleLiveDataFormat()
    {
        ActiveSectionKey = "LiveData";
        SelectedLiveDataMode = SelectedLiveDataMode switch
        {
            LiveDataDisplayMode.List => LiveDataDisplayMode.Bar,
            LiveDataDisplayMode.Bar => LiveDataDisplayMode.DigitalMeter,
            LiveDataDisplayMode.DigitalMeter => LiveDataDisplayMode.AnalogGauge,
            LiveDataDisplayMode.AnalogGauge => LiveDataDisplayMode.LineGraph,
            LiveDataDisplayMode.LineGraph => LiveDataDisplayMode.CustomDisplay,
            LiveDataDisplayMode.CustomDisplay => LiveDataDisplayMode.SnaplistEditor,
            _ => LiveDataDisplayMode.List,
        };
        BreadcrumbLeaf = $"Live Data — {SelectedLiveDataMode}";
    }

    public void UpdateThemeState(bool isDark, bool isSunlight)
    {
        IsDarkTheme = isDark;
        IsSunlightMode = isSunlight;
        RebuildDtcRows();
        RebuildReadinessMonitors();
        RebuildWorkOrderReadingRows();
        RebuildScopeMeasurementRowsForTheme();
        OnPropertyChanged(nameof(ManualReadingVerdictBrush));
        OnPropertyChanged(nameof(GuidedReadingPreviewBrush));
    }

    private void ActivateDiagnosticTool(string toolName, string category, string leaf)
    {
        ActiveTool = toolName;
        ActiveSectionKey = "Diagnostics";
        BreadcrumbRoot = "Equipment Selection";
        BreadcrumbCategory = category;
        BreadcrumbLeaf = leaf;
    }

    private void ShowPlaceholder(string title, string subtitle)
    {
        ActiveSectionKey = "Placeholder";
        BreadcrumbCategory = ActiveTool;
        BreadcrumbLeaf = title;
        PlaceholderTitle = title;
        PlaceholderSubtitle = subtitle;
    }

    private void NotifyViewVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsDiagnosticsViewVisible));
        OnPropertyChanged(nameof(IsWorkOrdersViewVisible));
        OnPropertyChanged(nameof(IsAssetsViewVisible));
        OnPropertyChanged(nameof(IsTechChatViewVisible));
        OnPropertyChanged(nameof(IsGuidedViewVisible));
        OnPropertyChanged(nameof(IsManualSearchViewVisible));
        OnPropertyChanged(nameof(IsReadingsViewVisible));
        OnPropertyChanged(nameof(IsRecommendationsViewVisible));
        OnPropertyChanged(nameof(IsScopeViewVisible));
        OnPropertyChanged(nameof(IsToolPlaceholderVisible));
        OnPropertyChanged(nameof(DiagnosticsViewVisibility));
        OnPropertyChanged(nameof(WorkOrdersViewVisibility));
        OnPropertyChanged(nameof(AssetsViewVisibility));
        OnPropertyChanged(nameof(TechChatViewVisibility));
        OnPropertyChanged(nameof(GuidedViewVisibility));
        OnPropertyChanged(nameof(ManualSearchViewVisibility));
        OnPropertyChanged(nameof(ReadingsViewVisibility));
        OnPropertyChanged(nameof(RecommendationsViewVisibility));
        OnPropertyChanged(nameof(ScopeViewVisibility));
        OnPropertyChanged(nameof(IsCanMonitorViewVisible));
        OnPropertyChanged(nameof(CanMonitorViewVisibility));
        OnPropertyChanged(nameof(DeviceCommandBarVisibility));
        SyncScopePanelToSection();
        SyncScopeFullScreenToSection();
        OnPropertyChanged(nameof(ToolPlaceholderVisibility));
        OnPropertyChanged(nameof(IsSettingsViewVisible));
        OnPropertyChanged(nameof(SettingsViewVisibility));
        OnPropertyChanged(nameof(DtcSectionVisibility));
        OnPropertyChanged(nameof(LiveDataSectionVisibility));
        OnPropertyChanged(nameof(FreezeFrameSectionVisibility));
        OnPropertyChanged(nameof(ReadinessSectionVisibility));
        NotifyLiveDataModeVisibilityChanged();
    }

    private void NotifyLiveDataModeVisibilityChanged()
    {
        OnPropertyChanged(nameof(LiveListModeVisibility));
        OnPropertyChanged(nameof(SnaplistEditorModeVisibility));
        OnPropertyChanged(nameof(BarGraphModeVisibility));
        OnPropertyChanged(nameof(DigitalMeterModeVisibility));
        OnPropertyChanged(nameof(AnalogGaugeModeVisibility));
        OnPropertyChanged(nameof(LineGraphModeVisibility));
        OnPropertyChanged(nameof(CustomDisplayModeVisibility));
    }

    // =========================================================================
    // Phase 3: AI Service State, Tech Chat, Citations, Guided Troubleshooting & Manual RAG
    // =========================================================================

    public string AiStatusIndicatorText => AiState.StatusIndicatorText;

    public string AiStatusBannerText => AiState.StatusBannerText;

    public string AiModelSummaryText => AiState.ModelSummaryText;

    public string AiConfiguredChatModelDisplay =>
        string.IsNullOrWhiteSpace(AiState.ChatModel) ? "Configured by AI Service" : AiState.ChatModel;

    public string AiConfiguredEmbeddingModelDisplay =>
        string.IsNullOrWhiteSpace(AiState.EmbeddingModel) ? "Configured by AI Service" : AiState.EmbeddingModel;

    public string AiTechChatToolTip =>
        !string.IsNullOrWhiteSpace(AiState.ChatModel)
            ? $"Offline AI Tech Chat ({AiState.ChatModel})"
            : "Offline AI Tech Chat";

    public Visibility AiReadyBadgeVisibility =>
        AiState.UiState == AiUiState.Ready ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AiDegradedBadgeVisibility =>
        AiState.UiState is AiUiState.DegradedSearch or AiUiState.WarmingUp ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AiUnavailableBadgeVisibility =>
        AiState.UiState == AiUiState.Unavailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AiChatOfflineBannerVisibility =>
        AiState.UiState != AiUiState.Ready ? Visibility.Visible : Visibility.Collapsed;

    private void OnAiStateUpdated(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AiStatusIndicatorText));
        OnPropertyChanged(nameof(AiStatusBannerText));
        OnPropertyChanged(nameof(AiModelSummaryText));
        OnPropertyChanged(nameof(AiConfiguredChatModelDisplay));
        OnPropertyChanged(nameof(AiConfiguredEmbeddingModelDisplay));
        OnPropertyChanged(nameof(AiTechChatToolTip));
        OnPropertyChanged(nameof(AiReadyBadgeVisibility));
        OnPropertyChanged(nameof(AiDegradedBadgeVisibility));
        OnPropertyChanged(nameof(AiUnavailableBadgeVisibility));
        OnPropertyChanged(nameof(AiChatOfflineBannerVisibility));
        OnPropertyChanged(nameof(AiRouteStatusLines));
    }

    [RelayCommand]
    public async Task RefreshAiStatusAsync()
    {
        await AiState.RefreshOnceAsync();
        StatusBannerMessage = $"{AiState.StatusIndicatorText} — {AiState.StatusBannerText}";
    }

    // ---- Tech Chat Screen ----

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    [ObservableProperty]
    private string _chatInputText = "";

    [ObservableProperty]
    private bool _isChatBusy;

    [ObservableProperty]
    private bool _useChatTools = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CitationActionBannerVisibility))]
    private string _citationActionBanner = "";

    public Visibility CitationActionBannerVisibility =>
        string.IsNullOrWhiteSpace(CitationActionBanner) ? Visibility.Collapsed : Visibility.Visible;

    private void EnsureWelcomeChatMessage()
    {
        if (ChatMessages.Count > 0)
            return;

        ChatMessages.Add(new ChatMessageViewModel(
            AiChatRoles.System,
            "Ask a diagnostic question about the current GSE/fleet asset, active DTCs, hydraulic test ports, or repair history. " +
            "All AI output is a suggestion — the technician makes the final call and must follow manufacturer safety procedures.",
            isSuggestion: false));
    }

    [RelayCommand]
    public async Task SendChatMessageAsync()
    {
        var prompt = ChatInputText?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(prompt) || IsChatBusy)
            return;

        ChatInputText = "";
        await SendChatPromptInternalAsync(prompt);
    }

    [RelayCommand]
    public async Task SendQuickPromptAsync(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || IsChatBusy)
            return;

        await SendChatPromptInternalAsync(prompt.Trim());
    }

    private async Task SendChatPromptInternalAsync(string prompt)
    {
        IsChatBusy = true;
        try
        {
            ChatMessages.Add(new ChatMessageViewModel(
                AiChatRoles.User,
                prompt,
                isSuggestion: false));

            var history = ChatMessages
                .Where(m => m.Role is AiChatRoles.User or AiChatRoles.Assistant)
                .Select(m => new AiChatMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                })
                .ToArray();

            var request = new AiChatRequest
            {
                Messages = history,
                WorkOrderId = WorkOrders.FirstOrDefault()?.Id ?? SelectedTab?.Id,
                AssetId = Assets.FirstOrDefault()?.Id,
                UseTools = UseChatTools,
                Routing = CurrentChatRouting,
            };

            var response = await _aiClient.ChatFullAsync(request);

            ChatMessages.Add(new ChatMessageViewModel(
                AiChatRoles.Assistant,
                response.Text,
                isSuggestion: response.IsSuggestion,
                citations: response.Citations,
                route: response.Route ?? AiRoute.Tablet,
                model: response.Model));

            StatusBannerMessage = response.Citations.Count > 0
                ? $"AI suggestion received ({response.Citations.Count} citation(s))."
                : "AI suggestion received.";
        }
        catch (AiServiceException ex)
        {
            AiState.NotifyChatCallFailure(ex);

            string noticeText = ex.IsAiStarting
                ? "AI is starting… The local model is warming up. Please retry in a moment."
                : ex.IsAiUnavailable
                    ? $"AI unavailable — the local model (llama.cpp) is not running. Manual search remains available. ({ex.Error.Message})"
                    : $"AI service notice ({ex.Code}): {ex.Error.Message}";

            ChatMessages.Add(new ChatMessageViewModel(
                AiChatRoles.System,
                noticeText,
                isSuggestion: false,
                isDegradedNotice: true));

            StatusBannerMessage = noticeText;
        }
        catch (TaskCanceledException ex)
        {
            await AiState.HandleChatCallFailureAsync(ex);

            string timeoutNotice = AiState.UiState == AiUiState.Unavailable
                ? "AI unavailable — check that the local AI service and its model (llama.cpp) are running. Manual search and vehicle diagnostics remain available."
                : $"AI chat request timed out while loading or running the local model on CPU ({AiState.StatusIndicatorText}). Please retry shortly; manual search remains available.";

            ChatMessages.Add(new ChatMessageViewModel(
                AiChatRoles.System,
                timeoutNotice,
                isSuggestion: false,
                isDegradedNotice: true));

            StatusBannerMessage = timeoutNotice;
        }
        catch (HttpRequestException ex)
        {
            AiState.NotifyChatCallFailure(ex);

            const string offlineNotice =
                "AI unavailable — check that the local AI service and its model (llama.cpp) are running. Manual search and vehicle diagnostics remain available.";

            ChatMessages.Add(new ChatMessageViewModel(
                AiChatRoles.System,
                offlineNotice,
                isSuggestion: false,
                isDegradedNotice: true));

            StatusBannerMessage = offlineNotice;
        }
        finally
        {
            IsChatBusy = false;
        }
    }

    [RelayCommand]
    public void ClearChatHistory()
    {
        ChatMessages.Clear();
        CitationActionBanner = "";
        EnsureWelcomeChatMessage();
    }

    /// <summary>
    /// Handles tapping a citation link in Tech Chat:
    /// - <see cref="CitationKind.ManualPage"/>: opens the Manual Viewer to that manual/page if imported locally,
    ///   otherwise displays "Manual, page N" cleanly.
    /// - <see cref="CitationKind.WorkOrder"/>: opens/selects the referenced work order tab and navigates to Work Orders.
    /// - <see cref="CitationKind.Dtc"/>: navigates to the DTCs section or shows the DTC reference.
    /// </summary>
    [RelayCommand]
    public async Task OpenCitationAsync(CitationItemViewModel? citation)
    {
        if (citation is null)
            return;

        switch (citation.Kind)
        {
            case CitationKind.ManualPage:
            {
                var pageNum = citation.PageStart ?? 1;
                Manual? matchedManual = null;

                if (!string.IsNullOrWhiteSpace(citation.ManualId))
                {
                    matchedManual = ImportedManuals.FirstOrDefault(m =>
                        string.Equals(m.Id, citation.ManualId, StringComparison.OrdinalIgnoreCase));

                    if (matchedManual is null)
                    {
                        try
                        {
                            matchedManual = await _dataServices.Manuals.GetByIdAsync(citation.ManualId);
                        }
                        catch
                        {
                            // Database might not be initialized in unit test context
                        }
                    }
                }

                if (matchedManual is not null)
                {
                    string pageExcerpt = "";
                    try
                    {
                        var chunks = await _dataServices.ManualChunks.ListByManualAsync(matchedManual.Id);
                        var pageChunks = chunks
                            .Where(c => c.PageStart <= pageNum && c.PageEnd >= pageNum)
                            .Select(c => c.ChunkText)
                            .ToArray();

                        if (pageChunks.Length > 0)
                        {
                            pageExcerpt = string.Join("\n\n", pageChunks);
                        }
                    }
                    catch
                    {
                    }

                    SelectedManualViewerHeader = $"{matchedManual.Title} — Page {pageNum}";
                    SelectedManualViewerContent = string.IsNullOrWhiteSpace(pageExcerpt)
                        ? $"Manual: {matchedManual.Title}\nPage: {pageNum}\nFile: {matchedManual.FilePath}\nCitation: {citation.Label}"
                        : pageExcerpt;

                    CitationActionBanner = $"Opened {matchedManual.Title}, page {pageNum}.";
                    StatusBannerMessage = CitationActionBanner;
                    ActiveSectionKey = "ManualSearch";
                    BreadcrumbLeaf = $"{matchedManual.Title} (Page {pageNum})";
                    NotifyViewVisibilityChanged();
                }
                else
                {
                    var fallbackDisplay = $"Manual, page {pageNum}";
                    SelectedManualViewerHeader = fallbackDisplay;
                    SelectedManualViewerContent = $"{fallbackDisplay} ({citation.Label})";
                    CitationActionBanner = !string.IsNullOrWhiteSpace(citation.Citation.DisplayText)
                        ? $"{fallbackDisplay} — {citation.Citation.DisplayText}"
                        : fallbackDisplay;
                    StatusBannerMessage = CitationActionBanner;
                }
                break;
            }

            case CitationKind.WorkOrder:
            {
                var woNumber = citation.WorkOrderNumber ?? "Unknown";
                var existingTab = OpenTabs.FirstOrDefault(t =>
                    string.Equals(t.Id, woNumber, StringComparison.OrdinalIgnoreCase) ||
                    t.Title.Contains(woNumber, StringComparison.OrdinalIgnoreCase));

                if (existingTab is null)
                {
                    var matchingWo = WorkOrders.FirstOrDefault(w =>
                        string.Equals(w.Id, woNumber, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(w.Number, woNumber, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(w.Number) && string.Equals(w.Number.TrimStart('0'), woNumber.TrimStart('0'), StringComparison.OrdinalIgnoreCase)));

                    var tabTitle = matchingWo is not null
                        ? WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(matchingWo)
                        : $"WO #{woNumber}";

                    existingTab = new OpenWorkOrderTab(
                        matchingWo?.Id ?? $"WO-{woNumber}",
                        tabTitle,
                        matchingWo?.AssetId ?? "Cited Repair History",
                        matchingWo?.Status.ToString() ?? "Referenced");
                    OpenTabs.Add(existingTab);

                    if (matchingWo is not null)
                    {
                        SelectedWorkOrder = matchingWo;
                    }
                }

                SelectedTab = existingTab;
                CitationActionBanner = $"Opened Work Order #{woNumber} ({citation.Label}).";
                StatusBannerMessage = CitationActionBanner;
                ActiveSectionKey = "WorkOrders";
                BreadcrumbLeaf = $"Work Order #{woNumber}";
                NotifyViewVisibilityChanged();
                break;
            }

            case CitationKind.Dtc:
            {
                CitationActionBanner = $"DTC Citation: {citation.Label}";
                StatusBannerMessage = CitationActionBanner;
                break;
            }
        }
    }

    // =========================================================================
    // Phase 4: Guided Troubleshooting Decision-Tree Walker & Manual Readings
    // =========================================================================

    public IReadOnlyList<string> StandardReadingUnits { get; } = new[]
    {
        UnitConverter.Psi,
        UnitConverter.Bar,
        UnitConverter.KiloPascal,
        UnitConverter.Fahrenheit,
        UnitConverter.Celsius,
        UnitConverter.Volt,
        UnitConverter.Ampere,
        UnitConverter.LiterPerMinute,
        UnitConverter.Pascal,
    };

    public IReadOnlyList<ReadingType> AllReadingTypes { get; } = Enum.GetValues<ReadingType>();

    [ObservableProperty]
    private WorkOrder? _selectedWorkOrder;

    partial void OnSelectedWorkOrderChanged(WorkOrder? value)
    {
        NotifyActiveWorkOrderChanged();
        SyncReadingsFilterToActiveAssetModel();
        _ = ReloadWorkOrderReadingsAsync(value?.Id);
        _ = ReloadWorkOrderWaveformsAsync(value?.Id);
        _ = ReloadWorkOrderOutcomesAndRepairsAsync(value?.Id);
    }

    partial void OnSelectedTabChanged(OpenWorkOrderTab? value)
    {
        NotifyActiveWorkOrderChanged();
    }

    public WorkOrder? ActiveWorkOrder => ResolveActiveWorkOrderInMemory();

    public bool HasActiveWorkOrder => ActiveWorkOrder is not null;

    public Visibility NoActiveWorkOrderBannerVisibility =>
        HasActiveWorkOrder ? Visibility.Collapsed : Visibility.Visible;

    public string ActiveWorkOrderNumberDisplay =>
        WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(ActiveWorkOrder);

    public string ActiveWorkOrderSummaryText => ActiveWorkOrder is WorkOrder wo
        ? $"Active Work Order: {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo)} ({wo.Id}) · Asset: {wo.AssetId} · Status: {wo.Status}"
        : "No open work order — open or create a work order first before recording readings.";

    private void NotifyActiveWorkOrderChanged()
    {
        NotifyAiRoutingChanged();
        OnPropertyChanged(nameof(ActiveWorkOrder));
        OnPropertyChanged(nameof(HasActiveWorkOrder));
        OnPropertyChanged(nameof(NoActiveWorkOrderBannerVisibility));
        OnPropertyChanged(nameof(ActiveWorkOrderNumberDisplay));
        OnPropertyChanged(nameof(ActiveWorkOrderSummaryText));
        NotifyWorkOrderPreviewPropertiesChanged();
    }

    private WorkOrder? ResolveActiveWorkOrderInMemory()
    {
        if (SelectedWorkOrder is not null)
            return SelectedWorkOrder;

        if (SelectedTab is not null)
        {
            var tabMatch = WorkOrders.FirstOrDefault(w =>
                string.Equals(w.Id, SelectedTab.Id, StringComparison.OrdinalIgnoreCase));
            if (tabMatch is not null)
                return tabMatch;
        }

        return WorkOrders.FirstOrDefault(w => w.Status != WorkOrderStatus.Closed)
               ?? WorkOrders.FirstOrDefault();
    }

    /// <summary>
    /// Resolves the active/open <see cref="WorkOrder"/> (checking in-memory <see cref="SelectedWorkOrder"/> /
    /// <see cref="WorkOrders"/> first, then querying <c>_dataServices.WorkOrders</c>). Ensures the work order
    /// and its parent asset exist in SQLite so foreign-key constraints on <c>readings.work_order_id</c> succeed.
    /// Returns <c>null</c> when no work order is open.
    /// </summary>
    public async Task<WorkOrder?> ResolveActiveWorkOrderAsync()
    {
        var active = ResolveActiveWorkOrderInMemory();

        if (active is null)
        {
            try
            {
                var dbWorkOrders = await _dataServices.WorkOrders.GetAllAsync();
                if (dbWorkOrders.Count > 0)
                {
                    WorkOrders.Clear();
                    foreach (var w in dbWorkOrders)
                        WorkOrders.Add(w);

                    active = dbWorkOrders.FirstOrDefault(w => w.Status != WorkOrderStatus.Closed)
                             ?? dbWorkOrders[0];
                    SelectedWorkOrder = active;
                }
            }
            catch
            {
                // Database may not be initialized yet
            }
        }

        if (active is null)
        {
            NotifyActiveWorkOrderChanged();
            return null;
        }

        // Ensure the active work order (and its referenced asset) exist in SQLite when added in-memory by a test
        try
        {
            var existingInDb = await _dataServices.WorkOrders.GetByIdAsync(active.Id);
            if (existingInDb is null)
            {
                var assetId = string.IsNullOrWhiteSpace(active.AssetId) ? "ASSET-DEFAULT-01" : active.AssetId;
                var existingAsset = await _dataServices.Assets.GetByIdAsync(assetId);
                if (existingAsset is null)
                {
                    var modelId = SelectedGuidedEquipmentModel?.Id;
                    if (string.IsNullOrWhiteSpace(modelId))
                        modelId = null;

                    var newAsset = new Asset
                    {
                        Id = assetId,
                        AssetKind = AssetKind.Equipment,
                        AssetNumber = $"GSE-{assetId[..Math.Min(8, assetId.Length)]}",
                        Name = "GSE Diagnostic Asset",
                        ModelId = modelId,
                        Status = AssetStatus.InService,
                    };
                    await _dataServices.Assets.AddAsync(newAsset);
                    if (Assets.All(a => !string.Equals(a.Id, newAsset.Id, StringComparison.OrdinalIgnoreCase)))
                        Assets.Add(newAsset);
                }

                if (string.IsNullOrWhiteSpace(active.Number))
                {
                    try
                    {
                        var assignedNum = await _dataServices.WorkOrders.AssignNextNumberAsync();
                        active = active with { Number = assignedNum };
                    }
                    catch
                    {
                        // Ignore if table not migrated yet
                    }
                }

                await _dataServices.WorkOrders.AddAsync(active);
                ReplaceWorkOrderInMemory(active);
            }
            else if (string.IsNullOrWhiteSpace(active.Number) && !string.IsNullOrWhiteSpace(existingInDb.Number))
            {
                active = active with { Number = existingInDb.Number };
                ReplaceWorkOrderInMemory(active);
            }
        }
        catch
        {
            // Ignore if DB is not initialized yet; caller's AddAsync will surface any DB error if needed
        }

        NotifyActiveWorkOrderChanged();
        return active;
    }

    [RelayCommand]
    public async Task CreateActiveWorkOrderAsync()
    {
        try
        {
            await _dataServices.InitializeAsync();
            if (AllTestPoints.Count == 0 || GuidedEquipmentModels.Count <= 1)
            {
                await ReloadGuidedCatalogFromDatabaseAsync();
            }

            var chosenModelId = !string.IsNullOrWhiteSpace(SelectedReadingsModelFilter?.Id)
                ? SelectedReadingsModelFilter!.Id
                : !string.IsNullOrWhiteSpace(SelectedGuidedEquipmentModel?.Id)
                    ? SelectedGuidedEquipmentModel!.Id
                    : GuidedEquipmentModels.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Id))?.Id;

            var asset = Assets.FirstOrDefault(a =>
                            !string.IsNullOrWhiteSpace(chosenModelId) &&
                            string.Equals(a.ModelId, chosenModelId, StringComparison.Ordinal))
                        ?? Assets.FirstOrDefault();

            if (asset is null)
            {
                var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
                asset = new Asset
                {
                    Id = Guid.NewGuid().ToString("D"),
                    AssetKind = AssetKind.Equipment,
                    AssetNumber = $"GSE-{suffix}",
                    Name = chosenModelId is not null
                        ? $"GSE Asset ({chosenModelId})"
                        : "GSE Ramp Equipment Asset",
                    ModelId = chosenModelId,
                    Status = AssetStatus.InService,
                };
                await _dataServices.Assets.AddAsync(asset);
                Assets.Add(asset);
            }

            var assignedNumber = await _dataServices.WorkOrders.AssignNextNumberAsync();

            var wo = new WorkOrder
            {
                Id = Guid.NewGuid().ToString("D"),
                Number = assignedNumber,
                AssetId = asset.Id,
                Status = WorkOrderStatus.Open,
                OpenedAt = Clock.UtcNow,
                ComplaintSummary = !string.IsNullOrWhiteSpace(GuidedComplaintText)
                    ? GuidedComplaintText.Trim()
                    : "GSE No-ECU Troubleshooting & Manual Readings",
            };

            await _dataServices.WorkOrders.AddAsync(wo);
            WorkOrders.Insert(0, wo);
            SelectedWorkOrder = wo;

            var tab = new OpenWorkOrderTab(
                wo.Id,
                WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo),
                asset.AssetNumber,
                wo.Status.ToString());
            OpenTabs.Add(tab);
            SelectedTab = tab;

            NotifyActiveWorkOrderChanged();
            SyncReadingsFilterToActiveAssetModel();
            await ReloadWorkOrderReadingsAsync(wo.Id);
            await ReloadWorkOrderWaveformsAsync(wo.Id);
            await ReloadWorkOrderOutcomesAndRepairsAsync(wo.Id);

            StatusBannerMessage = $"Opened {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo)} ('{wo.Id}') for asset '{asset.AssetNumber}'.";
            GuidedStatusMessage = StatusBannerMessage;
            ManualReadingStatusMessage = StatusBannerMessage;
        }
        catch (Exception ex)
        {
            ReportError("Could not create work order", ex);
        }
    }

    // ---- Guided Troubleshooting Screen (Phase 4 Decision-Tree Walker) ----

    public ObservableCollection<SelectableEquipmentModelItem> GuidedEquipmentModels { get; } = new();

    [ObservableProperty]
    private SelectableEquipmentModelItem? _selectedGuidedEquipmentModel;

    public ObservableCollection<SelectableSymptomItem> GuidedSymptoms { get; } = new();

    [ObservableProperty]
    private SelectableSymptomItem? _selectedGuidedSymptom;

    [ObservableProperty]
    private string _guidedComplaintText = "";

    [ObservableProperty]
    private bool _isGuidedBusy;

    [ObservableProperty]
    private GuidedStep? _currentGuidedStep;

    public ObservableCollection<string> GuidedSafetyWarnings { get; } = new();

    public ObservableCollection<string> GuidedAvailableUnits { get; } = new();

    [ObservableProperty]
    private double? _guidedReadingValue;

    public double GuidedReadingBoxValue
    {
        get => GuidedReadingValue ?? double.NaN;
        set => GuidedReadingValue = double.IsNaN(value) || double.IsInfinity(value) ? null : value;
    }

    [ObservableProperty]
    private string _guidedReadingUnit = UnitConverter.Psi;

    [ObservableProperty]
    private string _guidedAnswerText = "";

    [ObservableProperty]
    private string _guidedStatusMessage =
        "Select an equipment model and symptom (or enter a free-text complaint) and tap 'Start Guided Session'.";

    partial void OnGuidedReadingValueChanged(double? value)
    {
        OnPropertyChanged(nameof(GuidedReadingBoxValue));
        NotifyGuidedReadingPreviewChanged();
    }
    partial void OnGuidedReadingUnitChanged(string value) => NotifyGuidedReadingPreviewChanged();

    private void NotifyGuidedReadingPreviewChanged()
    {
        OnPropertyChanged(nameof(GuidedReadingPreviewText));
        OnPropertyChanged(nameof(GuidedReadingComputedPassFail));
        OnPropertyChanged(nameof(GuidedReadingPreviewBrush));
    }

    public bool IsCurrentGuidedTreeDraft =>
        CurrentGuidedStep is { Available: true, TreeId: { } treeId } &&
        !string.IsNullOrWhiteSpace(treeId) &&
        _troubleshootingTreesById.TryGetValue(treeId, out var tree) &&
        tree.Status == TreeStatus.Draft;

    public Visibility GuidedDraftTreeBadgeVisibility =>
        IsCurrentGuidedTreeDraft ? Visibility.Visible : Visibility.Collapsed;

    public string GuidedDraftTreeBadgeText => "DRAFT — not yet approved";

    public Visibility HasGuidedStepVisibility =>
        CurrentGuidedStep is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoGuidedStepVisibility =>
        CurrentGuidedStep is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GuidedTreeUnavailableVisibility =>
        CurrentGuidedStep is { Available: false } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GuidedTreeActiveStepVisibility =>
        CurrentGuidedStep is { Available: true } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GuidedSafetyWarningsVisibility =>
        GuidedSafetyWarnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool HasGuidedTerminalResult =>
        CurrentGuidedStep is { Available: true } && !string.IsNullOrWhiteSpace(CurrentGuidedStep.TerminalResult);

    public Visibility GuidedTerminalResultVisibility =>
        HasGuidedTerminalResult ? Visibility.Visible : Visibility.Collapsed;

    public string GuidedTerminalSuggestionLabel =>
        "AI/tree suggestion — technician makes the final call";

    public bool IsGuidedSpecTodo =>
        CurrentGuidedStep is { Available: true } s &&
        ((s.ReadingType is not null && s.SpecMin is null && s.SpecMax is null) ||
         (!string.IsNullOrWhiteSpace(s.SpecSource) && s.SpecSource.TrimStart().StartsWith("TODO", StringComparison.OrdinalIgnoreCase)));

    public Visibility GuidedSpecTodoVisibility =>
        IsGuidedSpecTodo ? Visibility.Visible : Visibility.Collapsed;

    public string GuidedSpecTodoBannerText =>
        "Needs spec from manual (TODO) — No numeric threshold is published yet. Readings are recorded with PassFail = N/A (NotApplicable) without fabricating pass/fail.";

    public Visibility GuidedReadingEntryVisibility =>
        CurrentGuidedStep is { Available: true, ReadingType: not null } ? Visibility.Visible : Visibility.Collapsed;

    public string GuidedStepHeaderText => CurrentGuidedStep switch
    {
        null => "No Active Guided Session",
        { Available: false } s => $"Session {s.SessionId} — No Matching Decision Tree Available",
        { Available: true } s => $"Step #{s.StepOrder} · Tree: {s.TreeId ?? "Active"} · Session: {s.SessionId}",
    };

    public string GuidedInstructionText =>
        CurrentGuidedStep?.Instruction ?? "Start a session above to load the troubleshooting tree.";

    public string GuidedQuestionText =>
        string.IsNullOrWhiteSpace(CurrentGuidedStep?.Question)
            ? "Follow the step instruction above and enter a measurement or pass/fail verdict."
            : CurrentGuidedStep!.Question!;

    /// <summary>
    /// Converts canonical SI <see cref="GuidedStep.SpecMin"/>/<see cref="GuidedStep.SpecMax"/> into the shop's
    /// display units via <see cref="UnitConverter.ToDisplay"/> and the <see cref="ReadingType"/> → canonical-unit
    /// mapping from ADR-009 §2 (e.g. "Expected: 2,300 – 2,500 psi (Citation)").
    /// When there is no numeric spec, shows the citation/source only ("TODO: needs spec from manual").
    /// </summary>
    public string GuidedSpecSummaryText
    {
        get
        {
            if (CurrentGuidedStep is null || !CurrentGuidedStep.Available)
                return "Spec Range: N/A";

            // Inspection / question steps take no measurement; showing the TODO marker there would
            // wrongly suggest a missing spec.
            if (CurrentGuidedStep.ReadingType is null && CurrentGuidedStep.TestPointId is null &&
                CurrentGuidedStep.SpecMin is null && CurrentGuidedStep.SpecMax is null &&
                string.IsNullOrWhiteSpace(CurrentGuidedStep.SpecSource))
                return string.IsNullOrWhiteSpace(CurrentGuidedStep.TerminalResult)
                    ? "No measurement for this step: answer PASS / FAIL."
                    : "No measurement for this step.";

            return TroubleshootingUnitHelper.FormatCanonicalSpecSummary(
                CurrentGuidedStep.ReadingType,
                CurrentGuidedStep.SpecMin,
                CurrentGuidedStep.SpecMax,
                CurrentGuidedStep.SpecSource,
                CurrentUnitSystem);
        }
    }

    public PassFail GuidedReadingComputedPassFail
    {
        get
        {
            if (CurrentGuidedStep is not { Available: true, ReadingType: not null } step)
                return PassFail.NotApplicable;

            var unit = GuidedReadingUnit?.Trim() ?? "";
            if (GuidedReadingValue is not double enteredVal || double.IsNaN(enteredVal) || double.IsInfinity(enteredVal) || !UnitConverter.IsKnownUnit(unit))
                return PassFail.NotApplicable;

            var canonVal = UnitConverter.ToCanonical(enteredVal, unit);
            return TroubleshootingUnitHelper.ComputePassFail(canonVal, step.SpecMin, step.SpecMax);
        }
    }

    public SolidColorBrush? GuidedReadingPreviewBrush =>
        TroubleshootingUnitHelper.CreatePassFailBrush(GuidedReadingComputedPassFail, IsDarkTheme, IsSunlightMode);

    public string GuidedReadingPreviewText
    {
        get
        {
            if (CurrentGuidedStep is not { Available: true, ReadingType: ReadingType rt })
                return "Question / observation step (no numeric reading required).";

            var unit = GuidedReadingUnit?.Trim() ?? "";
            if (GuidedReadingValue is not double enteredVal || double.IsNaN(enteredVal) || double.IsInfinity(enteredVal) || !UnitConverter.IsKnownUnit(unit))
                return "Enter a numeric value and valid unit to compute canonical SI and pass/fail.";

            var canonVal = UnitConverter.ToCanonical(enteredVal, unit);
            var canonUnit = UnitConverter.CanonicalUnit(unit);
            var verdict = TroubleshootingUnitHelper.ComputePassFail(canonVal, CurrentGuidedStep.SpecMin, CurrentGuidedStep.SpecMax);
            var verdictText = verdict switch
            {
                PassFail.Pass => "PASS",
                PassFail.Fail => "FAIL",
                _ => "N/A (TODO: needs spec from manual)",
            };

            return $"Canonical SI: {canonVal:G6} {canonUnit} · Computed Verdict: {verdictText}";
        }
    }

    public string GuidedTerminalResultText =>
        CurrentGuidedStep?.TerminalResult ?? "";

    partial void OnCurrentGuidedStepChanged(GuidedStep? oldValue, GuidedStep? newValue)
    {
        if (oldValue?.StepId != newValue?.StepId || oldValue?.SessionId != newValue?.SessionId)
        {
            GuidedReadingValue = null;
        }

        var value = newValue;
        GuidedSafetyWarnings.Clear();
        if (value?.SafetyWarnings is not null)
        {
            foreach (var warning in value.SafetyWarnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    GuidedSafetyWarnings.Add(warning);
                }
            }
        }

        // Update available units & default unit when the step carries a ReadingType
        if (value?.ReadingType is ReadingType rt &&
            TroubleshootingUnitHelper.TryGetCanonicalUnit(rt) is string expectedCanon)
        {
            var tpUnit = !string.IsNullOrWhiteSpace(value.TestPointId) &&
                         _testPointsById.TryGetValue(value.TestPointId!, out var tp) &&
                         !string.IsNullOrWhiteSpace(tp.ExpectedUnit) &&
                         UnitConverter.IsKnownUnit(tp.ExpectedUnit)
                ? tp.ExpectedUnit!
                : TroubleshootingUnitHelper.GetDefaultDisplayUnit(rt, CurrentUnitSystem);

            RebuildGuidedAvailableUnits(tpUnit);

            var currentUnit = GuidedReadingUnit?.Trim() ?? "";
            if (!UnitConverter.IsKnownUnit(currentUnit) ||
                !string.Equals(UnitConverter.CanonicalUnit(currentUnit), expectedCanon, StringComparison.Ordinal))
            {
                GuidedReadingUnit = tpUnit;
            }
        }
        else if (GuidedAvailableUnits.Count == 0)
        {
            RebuildGuidedAvailableUnits(UnitConverter.Psi);
        }

        OnPropertyChanged(nameof(IsCurrentGuidedTreeDraft));
        OnPropertyChanged(nameof(GuidedDraftTreeBadgeVisibility));
        OnPropertyChanged(nameof(GuidedDraftTreeBadgeText));
        OnPropertyChanged(nameof(HasGuidedStepVisibility));
        OnPropertyChanged(nameof(NoGuidedStepVisibility));
        OnPropertyChanged(nameof(GuidedTreeUnavailableVisibility));
        OnPropertyChanged(nameof(GuidedTreeActiveStepVisibility));
        OnPropertyChanged(nameof(GuidedSafetyWarningsVisibility));
        OnPropertyChanged(nameof(HasGuidedTerminalResult));
        OnPropertyChanged(nameof(GuidedTerminalResultVisibility));
        OnPropertyChanged(nameof(IsGuidedSpecTodo));
        OnPropertyChanged(nameof(GuidedSpecTodoVisibility));
        OnPropertyChanged(nameof(GuidedReadingEntryVisibility));
        OnPropertyChanged(nameof(GuidedStepHeaderText));
        OnPropertyChanged(nameof(GuidedInstructionText));
        OnPropertyChanged(nameof(GuidedQuestionText));
        OnPropertyChanged(nameof(GuidedSpecSummaryText));
        OnPropertyChanged(nameof(GuidedTerminalResultText));
        NotifyGuidedReadingPreviewChanged();
    }

    private void RebuildGuidedAvailableUnits(string preferredUnit)
    {
        GuidedAvailableUnits.Clear();
        if (!string.IsNullOrWhiteSpace(preferredUnit) && UnitConverter.IsKnownUnit(preferredUnit))
        {
            GuidedAvailableUnits.Add(preferredUnit);
        }
        foreach (var u in StandardReadingUnits)
        {
            if (!GuidedAvailableUnits.Contains(u))
                GuidedAvailableUnits.Add(u);
        }
    }

    /// <summary>
    /// Minimal placeholder catalog shown only before <see cref="ReloadGuidedCatalogFromDatabaseAsync"/> runs.
    /// Does not contain stale hardcoded equipment IDs; the seeded SQLite database is authoritative.
    /// </summary>
    private void PopulateDefaultGuidedCatalog()
    {
        GuidedEquipmentModels.Clear();
        GuidedEquipmentModels.Add(new SelectableEquipmentModelItem("", "No equipment models loaded yet"));
        SelectedGuidedEquipmentModel = GuidedEquipmentModels[0];

        GuidedSymptoms.Clear();
        GuidedSymptoms.Add(new SelectableSymptomItem(null, "(Free-text complaint / All symptoms)"));
        SelectedGuidedSymptom = GuidedSymptoms[0];

        ReadingsModelFilters.Clear();
        ReadingsModelFilters.Add(new SelectableEquipmentModelItem("", "(All Equipment Models)"));
        SelectedReadingsModelFilter = ReadingsModelFilters[0];

        RebuildGuidedAvailableUnits(UnitConverter.Psi);
        RebuildManualReadingUnits(UnitConverter.Volt);
        RebuildFilteredTestPoints();
    }

    private void TryPopulateCatalogIfDatabaseInitialized()
    {
        try
        {
            using var conn = _dataServices.Connections.CreateConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'equipment_models';";
            var exists = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
            if (!exists)
                return;
        }
        catch
        {
            return;
        }

        try
        {
            ReloadGuidedCatalogFromDatabaseAsync().GetAwaiter().GetResult();
            ReloadScopeCatalogAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Keep minimal fallback if DB read fails
        }
    }

    public async Task ReloadGuidedCatalogFromDatabaseAsync()
    {
        try
        {
            var preservedTpId = SelectedTestPoint?.Id;
            var preservedFreeEntry = IsFreeEntryReadingMode;

            var models = await _dataServices.EquipmentModels.GetAllAsync();
            if (models.Count > 0)
            {
                var prevGuidedId = SelectedGuidedEquipmentModel?.Id;
                GuidedEquipmentModels.Clear();
                foreach (var m in models)
                {
                    GuidedEquipmentModels.Add(new SelectableEquipmentModelItem(m));
                }

                SelectedGuidedEquipmentModel = GuidedEquipmentModels.FirstOrDefault(x =>
                                                   !string.IsNullOrWhiteSpace(prevGuidedId) &&
                                                   string.Equals(x.Id, prevGuidedId, StringComparison.Ordinal))
                                               ?? GuidedEquipmentModels[0];

                var prevFilterId = SelectedReadingsModelFilter?.Id;
                ReadingsModelFilters.Clear();
                ReadingsModelFilters.Add(new SelectableEquipmentModelItem("", "(All Equipment Models)"));
                foreach (var m in models)
                {
                    ReadingsModelFilters.Add(new SelectableEquipmentModelItem(m));
                }

                SelectedReadingsModelFilter = ReadingsModelFilters.FirstOrDefault(x =>
                                                  !string.IsNullOrWhiteSpace(prevFilterId) &&
                                                  string.Equals(x.Id, prevFilterId, StringComparison.Ordinal))
                                              ?? ReadingsModelFilters[0];
            }

            var symptoms = await _dataServices.Symptoms.GetAllAsync();
            if (symptoms.Count > 0)
            {
                var prevSymptomId = SelectedGuidedSymptom?.Id;
                GuidedSymptoms.Clear();
                GuidedSymptoms.Add(new SelectableSymptomItem(null, "(Free-text complaint / All symptoms)"));
                foreach (var s in symptoms)
                {
                    GuidedSymptoms.Add(new SelectableSymptomItem(s));
                }

                SelectedGuidedSymptom = GuidedSymptoms.FirstOrDefault(x =>
                                            !string.IsNullOrWhiteSpace(prevSymptomId) &&
                                            string.Equals(x.Id, prevSymptomId, StringComparison.Ordinal))
                                        ?? GuidedSymptoms[0];
            }

            var warnings = await _dataServices.SafetyWarnings.GetAllAsync();
            _safetyWarningsById.Clear();
            foreach (var w in warnings)
            {
                _safetyWarningsById[w.Id] = w;
            }

            var testPoints = await _dataServices.TestPoints.GetAllAsync();
            _testPointsById.Clear();
            AllTestPoints.Clear();
            foreach (var tp in testPoints)
            {
                _testPointsById[tp.Id] = tp;
                AllTestPoints.Add(tp);
            }

            var trees = await _dataServices.TroubleshootingTrees.GetAllAsync();
            _troubleshootingTreesById.Clear();
            foreach (var tree in trees)
            {
                _troubleshootingTreesById[tree.Id] = tree;
            }

            OnPropertyChanged(nameof(IsCurrentGuidedTreeDraft));
            OnPropertyChanged(nameof(GuidedDraftTreeBadgeVisibility));
            OnPropertyChanged(nameof(GuidedDraftTreeBadgeText));

            SyncReadingsFilterToActiveAssetModel();
            RebuildFilteredTestPoints(preservedTpId, preservedFreeEntry);
        }
        catch
        {
            // Keep minimal fallback catalog items if DB query fails
        }
    }

    [RelayCommand]
    public async Task StartGuidedTroubleshootingAsync()
    {
        if (IsGuidedBusy)
            return;

        IsGuidedBusy = true;
        try
        {
            if (GuidedEquipmentModels.Count <= 1 && string.IsNullOrWhiteSpace(GuidedEquipmentModels.FirstOrDefault()?.Id))
            {
                await ReloadGuidedCatalogFromDatabaseAsync();
            }

            var modelId = !string.IsNullOrWhiteSpace(SelectedGuidedEquipmentModel?.Id)
                ? SelectedGuidedEquipmentModel!.Id
                : "eqm-tug-660-28";
            var symptomId = SelectedGuidedSymptom?.Id;
            var complaint = string.IsNullOrWhiteSpace(GuidedComplaintText) ? null : GuidedComplaintText.Trim();

            var request = new GuidedStartRequest
            {
                EquipmentModelId = modelId,
                SymptomId = symptomId,
                ComplaintText = complaint,
            };

            _lastSavedGuidedReadingKey = null;
            var step = await _aiClient.StartGuidedAsync(request);
            CurrentGuidedStep = step;

            if (!step.Available)
            {
                GuidedStatusMessage = step.Instruction;
            }
            else if (!string.IsNullOrWhiteSpace(step.TerminalResult))
            {
                GuidedStatusMessage = $"Terminal diagnosis reached (Step #{step.StepOrder}) — {GuidedTerminalSuggestionLabel}.";
            }
            else
            {
                GuidedStatusMessage = $"Guided session '{step.SessionId}' started — Step #{step.StepOrder}.";
            }

            StatusBannerMessage = GuidedStatusMessage;

            // Phase 5 (ADR-010): Auto-populate Repair Recommendations on diagnosis start when a work order is open
            if (ActiveWorkOrder is not null)
            {
                await LoadRepairRecommendationsAsync(complaint);
            }
        }
        catch (AiServiceException ex)
        {
            CurrentGuidedStep = new GuidedStep
            {
                SessionId = "offline",
                Available = false,
                Instruction = $"Guided service error ({ex.Code}): {ex.Error.Message}",
            };
            GuidedStatusMessage = CurrentGuidedStep.Instruction;
            StatusBannerMessage = GuidedStatusMessage;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AiState.NotifyChatCallFailure(ex);
            CurrentGuidedStep = new GuidedStep
            {
                SessionId = "offline-session",
                Available = false,
                Instruction = "No troubleshooting tree is available for this model and symptom yet (AI service unreachable).",
            };
            GuidedStatusMessage = CurrentGuidedStep.Instruction;
            StatusBannerMessage = GuidedStatusMessage;
        }
        finally
        {
            IsGuidedBusy = false;
        }
    }

    [RelayCommand]
    public async Task NextGuidedStepAsync(string? verdictToken)
    {
        if (IsGuidedBusy || CurrentGuidedStep is null)
            return;

        PassFail? explicitVerdict = verdictToken?.Trim().ToLowerInvariant() switch
        {
            "pass" => PassFail.Pass,
            "fail" => PassFail.Fail,
            "na" => PassFail.NotApplicable,
            _ => null,
        };

        IsGuidedBusy = true;
        try
        {
            var step = CurrentGuidedStep;
            PassFail? verdictToSend = explicitVerdict;
            double? readingValueToSend = null;
            string? readingUnitToSend = null;

            bool hasEnteredReading = step.ReadingType is ReadingType stepReadingType &&
                                     stepReadingType != ReadingType.Other &&
                                     GuidedReadingValue is double enteredReadingValue &&
                                     !double.IsNaN(enteredReadingValue) &&
                                     !double.IsInfinity(enteredReadingValue) &&
                                     !string.IsNullOrWhiteSpace(GuidedReadingUnit);

            if (hasEnteredReading && step.ReadingType is ReadingType validReadingType && GuidedReadingValue is double enteredValue)
            {
                var enteredUnit = GuidedReadingUnit.Trim();
                if (!UnitConverter.IsKnownUnit(enteredUnit))
                {
                    GuidedStatusMessage = $"Unknown reading unit '{enteredUnit}'. Choose a valid unit (psi, bar, kPa, °F, °C, V, A, L/min).";
                    StatusBannerMessage = GuidedStatusMessage;
                    return;
                }

                var activeWo = await ResolveActiveWorkOrderAsync();
                if (activeWo is null)
                {
                    GuidedStatusMessage = "Please open a work order first before recording a reading and advancing.";
                    StatusBannerMessage = GuidedStatusMessage;
                    return;
                }

                var valueCanonical = UnitConverter.ToCanonical(enteredValue, enteredUnit);
                var unitCanonical = UnitConverter.CanonicalUnit(enteredUnit);
                var computedPassFail = TroubleshootingUnitHelper.ComputePassFail(valueCanonical, step.SpecMin, step.SpecMax);
                var specUnitCanonical = TroubleshootingUnitHelper.TryGetCanonicalUnit(validReadingType) ?? unitCanonical;
                var specSource = string.IsNullOrWhiteSpace(step.SpecSource)
                    ? TroubleshootingUnitHelper.DefaultTodoSpecSource
                    : step.SpecSource!;

                (string SessionId, string StepId, double Value, string Unit) currentReadingKey =
                    (step.SessionId, step.StepId ?? "", enteredValue, enteredUnit);
                bool alreadySavedSameStepReading = _lastSavedGuidedReadingKey is { } prevKey &&
                                                   string.Equals(prevKey.SessionId, currentReadingKey.SessionId, StringComparison.Ordinal) &&
                                                   string.Equals(prevKey.StepId, currentReadingKey.StepId, StringComparison.Ordinal) &&
                                                   Math.Abs(prevKey.Value - currentReadingKey.Value) < 1e-9 &&
                                                   string.Equals(prevKey.Unit, currentReadingKey.Unit, StringComparison.Ordinal);

                if (!alreadySavedSameStepReading)
                {
                    _testPointsById.TryGetValue(step.TestPointId ?? "", out var matchedTp);

                    var reading = new Reading
                    {
                        WorkOrderId = activeWo.Id,
                        TestPointId = step.TestPointId,
                        ReadingType = validReadingType,
                        System = matchedTp?.System,
                        ChannelComponent = matchedTp?.Name ?? step.TestPointId,
                        ValueCanonical = valueCanonical,
                        UnitCanonical = unitCanonical,
                        EnteredValue = enteredValue,
                        EnteredUnit = enteredUnit,
                        SpecMin = step.SpecMin,
                        SpecMax = step.SpecMax,
                        SpecUnit = specUnitCanonical,
                        SpecSource = specSource,
                        PassFail = computedPassFail,
                        Source = ReadingSource.Manual,
                        IsSimulated = false,
                        EnteredAt = Clock.UtcNow,
                        EnteredBy = SelectedTechnician,
                        Notes = string.IsNullOrWhiteSpace(GuidedAnswerText) ? null : GuidedAnswerText.Trim(),
                    };

                    // Persist the reading BEFORE calling /guided/next (ADR-009 §3)
                    await _dataServices.Readings.AddAsync(reading);
                    _lastSavedGuidedReadingKey = currentReadingKey;
                    await ReloadWorkOrderReadingsAsync(activeWo.Id);
                }

                // Only auto-override the verdict on the non-explicit ("auto" / null) path;
                // an explicit "pass"/"fail"/"na" click always preserves the technician's clicked verdict.
                if (explicitVerdict is null)
                {
                    verdictToSend = computedPassFail;
                }

                readingValueToSend = enteredValue;
                readingUnitToSend = enteredUnit;
            }
            else if (explicitVerdict is null &&
                     step.ReadingType is ReadingType missingReadingType &&
                     missingReadingType != ReadingType.Other)
            {
                GuidedStatusMessage = "Enter a measured reading value before recording, or choose PASS / FAIL / N/A.";
                StatusBannerMessage = GuidedStatusMessage;
                return;
            }

            var request = new GuidedNextRequest
            {
                SessionId = step.SessionId,
                StepId = step.StepId ?? "",
                Verdict = verdictToSend,
                ReadingValue = readingValueToSend,
                ReadingUnit = readingUnitToSend,
                Answer = string.IsNullOrWhiteSpace(GuidedAnswerText) ? null : GuidedAnswerText.Trim(),
            };

            var nextStep = await _aiClient.NextGuidedAsync(request);
            CurrentGuidedStep = nextStep;

            if (!nextStep.Available)
            {
                GuidedStatusMessage = nextStep.Instruction;
            }
            else if (!string.IsNullOrWhiteSpace(nextStep.TerminalResult))
            {
                GuidedStatusMessage = $"Terminal diagnosis reached (Step #{nextStep.StepOrder}) — {GuidedTerminalSuggestionLabel}.";
            }
            else if (string.Equals(nextStep.StepId, step.StepId, StringComparison.Ordinal))
            {
                GuidedStatusMessage = verdictToSend == PassFail.NotApplicable
                    ? $"Reading recorded as N/A on step #{nextStep.StepOrder} (needs spec/tolerance from manual). Select PASS or FAIL to branch to the next step."
                    : $"Remaining on step #{nextStep.StepOrder} (Verdict: {verdictToSend?.ToString() ?? "N/A"}).";
            }
            else
            {
                GuidedStatusMessage = $"Advanced to step #{nextStep.StepOrder} (Verdict: {verdictToSend?.ToString() ?? "N/A"}).";
            }

            StatusBannerMessage = GuidedStatusMessage;
        }
        catch (AiServiceException ex)
        {
            GuidedStatusMessage = $"Guided step error ({ex.Code}): {ex.Error.Message}";
            StatusBannerMessage = GuidedStatusMessage;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AiState.NotifyChatCallFailure(ex);
            GuidedStatusMessage = "AI service is unreachable — cannot advance guided session.";
            StatusBannerMessage = GuidedStatusMessage;
        }
        finally
        {
            IsGuidedBusy = false;
        }
    }

    // ---- Deliverable B: Manual Readings-Entry Screen (No-ECU Equipment) ----

    public ObservableCollection<SelectableEquipmentModelItem> ReadingsModelFilters { get; } = new();

    [ObservableProperty]
    private SelectableEquipmentModelItem? _selectedReadingsModelFilter;

    public ObservableCollection<TestPoint> AllTestPoints { get; } = new();

    public ObservableCollection<SelectableTestPointItem> FilteredTestPoints { get; } = new();

    [ObservableProperty]
    private SelectableTestPointItem? _selectedTestPointItem;

    [ObservableProperty]
    private TestPoint? _selectedTestPoint;

    [ObservableProperty]
    private bool _isFreeEntryReadingMode;

    public ObservableCollection<SafetyWarningItemViewModel> ActiveTestPointSafetyWarnings { get; } = new();

    public ObservableCollection<string> ActiveTestPointSafetyWarningTexts { get; } = new();

    public ObservableCollection<string> ManualReadingAvailableUnits { get; } = new();

    [ObservableProperty]
    private double _manualReadingValue;

    [ObservableProperty]
    private string _manualReadingUnit = UnitConverter.Volt;

    [ObservableProperty]
    private ReadingType _manualReadingType = ReadingType.Voltage;

    [ObservableProperty]
    private string _manualChannelComponent = "";

    [ObservableProperty]
    private string _manualSystemName = "";

    [ObservableProperty]
    private string _manualReadingNotes = "";

    [ObservableProperty]
    private string _manualReadingStatusMessage =
        "Select a test point (or Free-Entry mode), review mandatory safety warnings, enter value + unit, and tap 'Save Reading'.";

    public ObservableCollection<SavedReadingRowViewModel> WorkOrderReadings { get; } = new();

    public bool HasNoWorkOrderReadings => WorkOrderReadings.Count == 0;

    public Visibility NoWorkOrderReadingsVisibility =>
        HasNoWorkOrderReadings ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SelectedTestPointDetailsVisibility =>
        !IsFreeEntryReadingMode && SelectedTestPoint is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FreeEntryFieldsVisibility =>
        IsFreeEntryReadingMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActiveTestPointSafetyWarningsVisibility =>
        ActiveTestPointSafetyWarnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string SelectedTestPointNameDisplay =>
        SelectedTestPoint?.Name ?? "Free-Entry Reading (No Test Point)";

    public string SelectedTestPointLocationDisplay =>
        SelectedTestPoint?.Location ?? "Specify component/channel location below.";

    public string SelectedTestPointReadingTypeDisplay =>
        SelectedTestPoint is not null
            ? TroubleshootingUnitHelper.FormatReadingTypeLabel(SelectedTestPoint.ReadingType)
            : TroubleshootingUnitHelper.FormatReadingTypeLabel(ManualReadingType);

    public string SelectedTestPointExpectedUnitDisplay =>
        !string.IsNullOrWhiteSpace(SelectedTestPoint?.ExpectedUnit)
            ? SelectedTestPoint!.ExpectedUnit!
            : ManualReadingUnit;

    public string SelectedTestPointSpecDisplay
    {
        get
        {
            if (SelectedTestPoint is null)
                return TroubleshootingUnitHelper.DefaultTodoSpecSource;

            var item = SelectedTestPointItem?.TestPoint?.Id == SelectedTestPoint.Id
                ? SelectedTestPointItem
                : new SelectableTestPointItem(SelectedTestPoint);

            if (item.CanonicalSpecMin is null && item.CanonicalSpecMax is null)
                return TroubleshootingUnitHelper.DefaultTodoSpecSource;

            return $"{item.AuthoredSpecRangeText} ({TroubleshootingUnitHelper.FormatCanonicalSpecSummary(SelectedTestPoint.ReadingType, item.CanonicalSpecMin, item.CanonicalSpecMax, SelectedTestPoint.SpecSource, CurrentUnitSystem)})";
        }
    }

    public string SelectedTestPointCitationDisplay =>
        string.IsNullOrWhiteSpace(SelectedTestPoint?.SpecSource)
            ? TroubleshootingUnitHelper.DefaultTodoSpecSource
            : SelectedTestPoint!.SpecSource!;

    public string SelectedTestPointProcedureDisplay =>
        string.IsNullOrWhiteSpace(SelectedTestPoint?.ProcedureRef)
            ? "Follow the applicable OEM maintenance manual test procedure."
            : SelectedTestPoint!.ProcedureRef!;

    public double ManualReadingCanonicalValue
    {
        get
        {
            var unit = ManualReadingUnit?.Trim() ?? "";
            if (double.IsNaN(ManualReadingValue) || double.IsInfinity(ManualReadingValue) || !UnitConverter.IsKnownUnit(unit))
                return double.NaN;

            return UnitConverter.ToCanonical(ManualReadingValue, unit);
        }
    }

    public string ManualReadingCanonicalUnit
    {
        get
        {
            var unit = ManualReadingUnit?.Trim() ?? "";
            return UnitConverter.IsKnownUnit(unit) ? UnitConverter.CanonicalUnit(unit) : "";
        }
    }

    public string ManualReadingCanonicalDisplay
    {
        get
        {
            var unit = ManualReadingUnit?.Trim() ?? "";
            if (double.IsNaN(ManualReadingValue) || double.IsInfinity(ManualReadingValue) || !UnitConverter.IsKnownUnit(unit))
                return "Enter a valid value and unit";

            var canonVal = UnitConverter.ToCanonical(ManualReadingValue, unit);
            var canonUnit = UnitConverter.CanonicalUnit(unit);
            return $"{canonVal:G6} {canonUnit} (entered: {TroubleshootingUnitHelper.FormatSpecNumber(ManualReadingValue, unit)} {unit})";
        }
    }

    public PassFail ManualReadingComputedPassFail
    {
        get
        {
            if (IsFreeEntryReadingMode || SelectedTestPoint is null)
                return PassFail.NotApplicable;

            var unit = ManualReadingUnit?.Trim() ?? "";
            if (double.IsNaN(ManualReadingValue) || double.IsInfinity(ManualReadingValue) || !UnitConverter.IsKnownUnit(unit))
                return PassFail.NotApplicable;

            var item = SelectedTestPointItem?.TestPoint?.Id == SelectedTestPoint.Id
                ? SelectedTestPointItem
                : new SelectableTestPointItem(SelectedTestPoint);

            if (item.CanonicalSpecMin is null && item.CanonicalSpecMax is null)
                return PassFail.NotApplicable;

            var canonUnit = UnitConverter.CanonicalUnit(unit);
            if (!string.IsNullOrWhiteSpace(item.CanonicalSpecUnit) &&
                !string.Equals(canonUnit, item.CanonicalSpecUnit, StringComparison.Ordinal))
            {
                return PassFail.NotApplicable;
            }

            var canonVal = UnitConverter.ToCanonical(ManualReadingValue, unit);
            return TroubleshootingUnitHelper.ComputePassFail(canonVal, item.CanonicalSpecMin, item.CanonicalSpecMax);
        }
    }

    public string ManualReadingVerdictLabel
    {
        get
        {
            if (IsFreeEntryReadingMode || SelectedTestPoint is null)
                return $"N/A — Free-Entry ({TroubleshootingUnitHelper.DefaultTodoSpecSource})";

            var item = SelectedTestPointItem?.TestPoint?.Id == SelectedTestPoint.Id
                ? SelectedTestPointItem
                : new SelectableTestPointItem(SelectedTestPoint);

            if (item.CanonicalSpecMin is null && item.CanonicalSpecMax is null)
                return $"N/A — {TroubleshootingUnitHelper.DefaultTodoSpecSource}";

            return ManualReadingComputedPassFail switch
            {
                PassFail.Pass => "PASS — Within Spec",
                PassFail.Fail => "FAIL — Outside Spec",
                _ => $"N/A — {TroubleshootingUnitHelper.DefaultTodoSpecSource}",
            };
        }
    }

    public SolidColorBrush? ManualReadingVerdictBrush =>
        TroubleshootingUnitHelper.CreatePassFailBrush(ManualReadingComputedPassFail, IsDarkTheme, IsSunlightMode);

    partial void OnSelectedReadingsModelFilterChanged(SelectableEquipmentModelItem? value)
    {
        RebuildFilteredTestPoints();
    }

    partial void OnSelectedTestPointItemChanged(SelectableTestPointItem? value)
    {
        if (_suppressTestPointSync)
            return;

        try
        {
            _suppressTestPointSync = true;
            SelectedTestPoint = value?.TestPoint;
            IsFreeEntryReadingMode = value is null || value.IsFreeEntry;
            ApplySelectedTestPointState(SelectedTestPoint);
        }
        finally
        {
            _suppressTestPointSync = false;
        }
    }

    partial void OnSelectedTestPointChanged(TestPoint? value)
    {
        if (_suppressTestPointSync)
            return;

        try
        {
            _suppressTestPointSync = true;
            IsFreeEntryReadingMode = value is null;
            if (value is null)
            {
                SelectedTestPointItem = FilteredTestPoints.FirstOrDefault(x => x.IsFreeEntry);
            }
            else
            {
                var matchingItem = FilteredTestPoints.FirstOrDefault(x =>
                    x.TestPoint is not null && string.Equals(x.TestPoint.Id, value.Id, StringComparison.Ordinal));
                if (matchingItem is null)
                {
                    matchingItem = new SelectableTestPointItem(value);
                    FilteredTestPoints.Insert(0, matchingItem);
                }
                SelectedTestPointItem = matchingItem;
            }

            ApplySelectedTestPointState(value);
        }
        finally
        {
            _suppressTestPointSync = false;
        }
    }

    partial void OnIsFreeEntryReadingModeChanged(bool value)
    {
        if (_suppressTestPointSync)
            return;

        if (value && SelectedTestPoint is not null)
        {
            SelectedTestPoint = null;
        }
        else if (!value && SelectedTestPoint is null)
        {
            var firstTp = FilteredTestPoints.FirstOrDefault(x => !x.IsFreeEntry)?.TestPoint;
            if (firstTp is not null)
                SelectedTestPoint = firstTp;
        }

        NotifyManualReadingPropertiesChanged();
    }

    partial void OnManualReadingValueChanged(double value) => NotifyManualReadingPropertiesChanged();
    partial void OnManualReadingUnitChanged(string value) => NotifyManualReadingPropertiesChanged();
    partial void OnManualReadingTypeChanged(ReadingType value) => NotifyManualReadingPropertiesChanged();

    [RelayCommand]
    public void EnableFreeEntryReadingMode()
    {
        SelectedTestPoint = null;
    }

    /// <summary>
    /// Filters <see cref="FilteredTestPoints"/> by the specified <paramref name="equipmentModelId"/>
    /// (or shows all models when null/empty).
    /// </summary>
    public void FilterTestPointsByModel(string? equipmentModelId)
    {
        EnsureTestPointsAndWarningsLoadedSyncIfNeeded();

        var match = ReadingsModelFilters.FirstOrDefault(m =>
            string.Equals(m.Id, equipmentModelId ?? "", StringComparison.Ordinal));

        if (match is not null)
        {
            SelectedReadingsModelFilter = match;
        }
        else if (!string.IsNullOrWhiteSpace(equipmentModelId))
        {
            var customFilter = new SelectableEquipmentModelItem(equipmentModelId, equipmentModelId);
            ReadingsModelFilters.Add(customFilter);
            SelectedReadingsModelFilter = customFilter;
        }
        else
        {
            SelectedReadingsModelFilter = ReadingsModelFilters.FirstOrDefault();
        }

        RebuildFilteredTestPoints();
    }

    private void SyncReadingsFilterToActiveAssetModel()
    {
        var activeWo = ResolveActiveWorkOrderInMemory();
        Asset? activeAsset = null;
        if (activeWo is not null && !string.IsNullOrWhiteSpace(activeWo.AssetId))
        {
            activeAsset = Assets.FirstOrDefault(a => string.Equals(a.Id, activeWo.AssetId, StringComparison.OrdinalIgnoreCase));
        }
        activeAsset ??= Assets.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(activeAsset?.ModelId))
        {
            var matchingFilter = ReadingsModelFilters.FirstOrDefault(f =>
                string.Equals(f.Id, activeAsset.ModelId, StringComparison.Ordinal));
            if (matchingFilter is not null)
            {
                SelectedReadingsModelFilter = matchingFilter;
            }
        }
    }

    private void EnsureTestPointsAndWarningsLoadedSyncIfNeeded()
    {
        if (AllTestPoints.Count > 0 && _safetyWarningsById.Count > 0)
            return;

        TryPopulateCatalogIfDatabaseInitialized();
    }

    private void RebuildFilteredTestPoints(string? explicitPreviousTpId = null, bool? explicitWasFreeEntry = null)
    {
        var previousTpId = explicitPreviousTpId ?? SelectedTestPoint?.Id;
        var wasFreeEntry = explicitWasFreeEntry ?? IsFreeEntryReadingMode;
        var modelFilterId = SelectedReadingsModelFilter?.Id;

        FilteredTestPoints.Clear();

        IEnumerable<TestPoint> source = string.IsNullOrWhiteSpace(modelFilterId)
            ? AllTestPoints
            : AllTestPoints.Where(tp => string.Equals(tp.EquipmentModelId, modelFilterId, StringComparison.Ordinal));

        foreach (var tp in source)
        {
            FilteredTestPoints.Add(new SelectableTestPointItem(tp));
        }

        var freeEntryItem = new SelectableTestPointItem(null);
        FilteredTestPoints.Add(freeEntryItem);

        SelectableTestPointItem? nextSelection = null;
        if (wasFreeEntry)
        {
            nextSelection = freeEntryItem;
        }
        else if (!string.IsNullOrWhiteSpace(previousTpId))
        {
            nextSelection = FilteredTestPoints.FirstOrDefault(x =>
                x.TestPoint is not null && string.Equals(x.TestPoint.Id, previousTpId, StringComparison.Ordinal));
        }

        nextSelection ??= FilteredTestPoints.FirstOrDefault(x => !x.IsFreeEntry) ?? freeEntryItem;
        SelectedTestPointItem = nextSelection;
    }

    private void ApplySelectedTestPointState(TestPoint? tp)
    {
        EnsureTestPointsAndWarningsLoadedSyncIfNeeded();

        ActiveTestPointSafetyWarnings.Clear();
        ActiveTestPointSafetyWarningTexts.Clear();

        if (tp is not null)
        {
            var warningIds = TroubleshootingUnitHelper.ParseSafetyWarningIds(tp.SafetyWarningIds);
            var resolved = new List<SafetyWarningItemViewModel>();
            foreach (var wid in warningIds)
            {
                if (_safetyWarningsById.TryGetValue(wid, out var sw))
                {
                    resolved.Add(new SafetyWarningItemViewModel(sw));
                }
            }

            foreach (var item in resolved.OrderBy(r => r.SeverityRank).ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                ActiveTestPointSafetyWarnings.Add(item);
                ActiveTestPointSafetyWarningTexts.Add(item.Text);
            }

            ManualReadingType = tp.ReadingType;
            var defaultUnit = !string.IsNullOrWhiteSpace(tp.ExpectedUnit) && UnitConverter.IsKnownUnit(tp.ExpectedUnit)
                ? tp.ExpectedUnit!
                : TroubleshootingUnitHelper.GetDefaultDisplayUnit(tp.ReadingType, CurrentUnitSystem);

            RebuildManualReadingUnits(defaultUnit);
            ManualReadingUnit = defaultUnit;
        }
        else
        {
            var defaultUnit = TroubleshootingUnitHelper.GetDefaultDisplayUnit(ManualReadingType, CurrentUnitSystem);
            RebuildManualReadingUnits(defaultUnit);
            if (!UnitConverter.IsKnownUnit(ManualReadingUnit))
            {
                ManualReadingUnit = defaultUnit;
            }
        }

        NotifyManualReadingPropertiesChanged();
    }

    private void RebuildManualReadingUnits(string preferredUnit)
    {
        ManualReadingAvailableUnits.Clear();
        if (!string.IsNullOrWhiteSpace(preferredUnit) && UnitConverter.IsKnownUnit(preferredUnit))
        {
            ManualReadingAvailableUnits.Add(preferredUnit);
        }

        foreach (var unit in StandardReadingUnits)
        {
            if (!ManualReadingAvailableUnits.Contains(unit))
                ManualReadingAvailableUnits.Add(unit);
        }
    }

    private void NotifyManualReadingPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedTestPointDetailsVisibility));
        OnPropertyChanged(nameof(FreeEntryFieldsVisibility));
        OnPropertyChanged(nameof(ActiveTestPointSafetyWarningsVisibility));
        OnPropertyChanged(nameof(SelectedTestPointNameDisplay));
        OnPropertyChanged(nameof(SelectedTestPointLocationDisplay));
        OnPropertyChanged(nameof(SelectedTestPointReadingTypeDisplay));
        OnPropertyChanged(nameof(SelectedTestPointExpectedUnitDisplay));
        OnPropertyChanged(nameof(SelectedTestPointSpecDisplay));
        OnPropertyChanged(nameof(SelectedTestPointCitationDisplay));
        OnPropertyChanged(nameof(SelectedTestPointProcedureDisplay));
        OnPropertyChanged(nameof(ManualReadingCanonicalValue));
        OnPropertyChanged(nameof(ManualReadingCanonicalUnit));
        OnPropertyChanged(nameof(ManualReadingCanonicalDisplay));
        OnPropertyChanged(nameof(ManualReadingComputedPassFail));
        OnPropertyChanged(nameof(ManualReadingVerdictLabel));
        OnPropertyChanged(nameof(ManualReadingVerdictBrush));
    }

    [RelayCommand]
    public async Task SaveManualReadingAsync()
    {
        var activeWo = await ResolveActiveWorkOrderAsync();
        if (activeWo is null)
        {
            ManualReadingStatusMessage = "Please open a work order first before saving a manual reading.";
            StatusBannerMessage = ManualReadingStatusMessage;
            return;
        }

        if (double.IsNaN(ManualReadingValue) || double.IsInfinity(ManualReadingValue))
        {
            ManualReadingStatusMessage = "Enter a valid numeric reading value before saving.";
            StatusBannerMessage = ManualReadingStatusMessage;
            return;
        }

        var enteredUnit = ManualReadingUnit?.Trim() ?? "";
        if (!UnitConverter.IsKnownUnit(enteredUnit))
        {
            ManualReadingStatusMessage = $"Unknown unit '{enteredUnit}'. Choose a valid unit (psi, bar, kPa, °F, °C, V, A, L/min).";
            StatusBannerMessage = ManualReadingStatusMessage;
            return;
        }

        var valueCanonical = UnitConverter.ToCanonical(ManualReadingValue, enteredUnit);
        var unitCanonical = UnitConverter.CanonicalUnit(enteredUnit);

        ReadingType readingType;
        string? systemName;
        string? channelComponent;
        double? specMinCanonical = null;
        double? specMaxCanonical = null;
        string? specUnitCanonical;
        string specSource;

        if (!IsFreeEntryReadingMode && SelectedTestPoint is TestPoint tp)
        {
            var expectedTypeCanon = TroubleshootingUnitHelper.TryGetCanonicalUnit(tp.ReadingType);
            if (expectedTypeCanon is not null && !string.Equals(unitCanonical, expectedTypeCanon, StringComparison.Ordinal))
            {
                ManualReadingStatusMessage =
                    $"Unit '{enteredUnit}' ({unitCanonical}) does not match test point reading type '{tp.ReadingType}' ({expectedTypeCanon}).";
                StatusBannerMessage = ManualReadingStatusMessage;
                return;
            }

            var item = new SelectableTestPointItem(tp);
            readingType = tp.ReadingType;
            systemName = tp.System;
            channelComponent = tp.Name;
            specMinCanonical = item.CanonicalSpecMin;
            specMaxCanonical = item.CanonicalSpecMax;
            specUnitCanonical = item.CanonicalSpecUnit ?? expectedTypeCanon ?? unitCanonical;
            specSource = item.SpecSource;
        }
        else
        {
            readingType = ManualReadingType;
            systemName = string.IsNullOrWhiteSpace(ManualSystemName) ? null : ManualSystemName.Trim();
            channelComponent = string.IsNullOrWhiteSpace(ManualChannelComponent)
                ? "Free-Entry Reading"
                : ManualChannelComponent.Trim();
            specUnitCanonical = TroubleshootingUnitHelper.TryGetCanonicalUnit(readingType) ?? unitCanonical;
            specSource = TroubleshootingUnitHelper.DefaultTodoSpecSource;
        }

        var passFail = TroubleshootingUnitHelper.ComputePassFail(valueCanonical, specMinCanonical, specMaxCanonical);

        var reading = new Reading
        {
            WorkOrderId = activeWo.Id,
            TestPointId = (!IsFreeEntryReadingMode && SelectedTestPoint is not null) ? SelectedTestPoint.Id : null,
            ReadingType = readingType,
            System = systemName,
            ChannelComponent = channelComponent,
            ValueCanonical = valueCanonical,
            UnitCanonical = unitCanonical,
            EnteredValue = ManualReadingValue,
            EnteredUnit = enteredUnit,
            SpecMin = specMinCanonical,
            SpecMax = specMaxCanonical,
            SpecUnit = specUnitCanonical,
            SpecSource = specSource,
            PassFail = passFail,
            Source = ReadingSource.Manual,
            IsSimulated = false,
            EnteredAt = Clock.UtcNow,
            EnteredBy = SelectedTechnician,
            Notes = string.IsNullOrWhiteSpace(ManualReadingNotes) ? null : ManualReadingNotes.Trim(),
        };

        await _dataServices.Readings.AddAsync(reading);
        await ReloadWorkOrderReadingsAsync(activeWo.Id);

        var verdictDisplay = passFail switch
        {
            PassFail.Pass => "PASS",
            PassFail.Fail => "FAIL",
            _ => "N/A",
        };
        ManualReadingStatusMessage =
            $"Saved manual reading: {TroubleshootingUnitHelper.FormatSpecNumber(ManualReadingValue, enteredUnit)} {enteredUnit} " +
            $"({valueCanonical:G6} {unitCanonical}) · Verdict: {verdictDisplay} · WO: {activeWo.Id}.";
        StatusBannerMessage = ManualReadingStatusMessage;
    }

    [RelayCommand]
    public async Task ReloadWorkOrderReadingsAsync(string? workOrderId = null)
    {
        try
        {
            var targetWoId = workOrderId ?? ResolveActiveWorkOrderInMemory()?.Id;
            if (string.IsNullOrWhiteSpace(targetWoId))
            {
                _lastWorkOrderReadings = Array.Empty<Reading>();
                RebuildWorkOrderReadingRows();
                return;
            }

            var rows = await _dataServices.Readings.FindAsync("work_order_id", targetWoId);
            _lastWorkOrderReadings = rows
                .OrderByDescending(r => r.EnteredAt, StringComparer.Ordinal)
                .ToArray();
            RebuildWorkOrderReadingRows();
        }
        catch
        {
            // Database may not be initialized in unit test context
        }
    }

    private void RebuildWorkOrderReadingRows()
    {
        WorkOrderReadings.Clear();
        foreach (var r in _lastWorkOrderReadings)
        {
            string? tpName = null;
            if (!string.IsNullOrWhiteSpace(r.TestPointId) &&
                _testPointsById.TryGetValue(r.TestPointId!, out var tp))
            {
                tpName = tp.Name;
            }

            WorkOrderReadings.Add(new SavedReadingRowViewModel(
                r,
                tpName,
                CurrentUnitSystem,
                IsDarkTheme,
                IsSunlightMode));
        }

        OnPropertyChanged(nameof(HasNoWorkOrderReadings));
        OnPropertyChanged(nameof(NoWorkOrderReadingsVisibility));
    }

    // ---- Manual RAG Search & Citation Viewer ----

    public ObservableCollection<Manual> ImportedManuals { get; } = new();

    public ObservableCollection<ManualSearchResultViewModel> ManualSearchResults { get; } = new();

    [ObservableProperty]
    private string _manualSearchQuery = "";

    [ObservableProperty]
    private int _manualSearchTopK = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualSearchFallbackBadgeVisibility))]
    private bool _isManualSearchFallback;

    public Visibility ManualSearchFallbackBadgeVisibility =>
        IsManualSearchFallback ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoManualSearchResultsVisibility =>
        ManualSearchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private string _manualSearchStatusText =
        "Search repair manuals (hybrid BM25 + vector RRF when embedding model is online; deterministic BM25 fallback when offline).";

    [ObservableProperty]
    private string _selectedManualViewerHeader = "Manual Citation & Page Viewer";

    [ObservableProperty]
    private string _selectedManualViewerContent =
        "Tap any citation link in Tech Chat or select a manual search result to inspect the cited manual page.";

    private async Task ReloadManualsCatalogAsync()
    {
        try
        {
            var manuals = await _dataServices.Manuals.GetAllAsync();
            ImportedManuals.Clear();
            foreach (var m in manuals.Where(x => x.DeletedAt is null))
            {
                ImportedManuals.Add(m);
            }
        }
        catch
        {
        }
    }

    [RelayCommand]
    public async Task SearchManualsAsync()
    {
        var query = ManualSearchQuery?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            ManualSearchStatusText = "Enter a search query to search imported repair manuals.";
            return;
        }

        try
        {
            var response = await _aiClient.SearchManualsFullAsync(new SearchManualsRequest
            {
                Query = query,
                TopK = Math.Clamp(ManualSearchTopK, 1, 50),
                IncludeText = true,
            });

            IsManualSearchFallback = response.Fallback;
            ManualSearchResults.Clear();
            foreach (var r in response.Results)
            {
                ManualSearchResults.Add(new ManualSearchResultViewModel(r, response.Fallback));
            }
            OnPropertyChanged(nameof(NoManualSearchResultsVisibility));

            ManualSearchStatusText = response.Fallback
                ? $"Found {response.Results.Count} result(s) (Text-only BM25 fallback — embedding model offline)."
                : $"Found {response.Results.Count} hybrid RAG result(s).";

            if (ManualSearchResults.Count > 0)
            {
                SelectManualSearchResult(ManualSearchResults[0]);
            }
        }
        catch (AiServiceException ex)
        {
            ManualSearchStatusText = $"Manual search error ({ex.Code}): {ex.Error.Message}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AiState.NotifyChatCallFailure(ex);
            ManualSearchStatusText = "AI service is not reachable — start the local AI service to run manual search.";
        }
    }

    [RelayCommand]
    public void SelectManualSearchResult(ManualSearchResultViewModel? hit)
    {
        if (hit is null)
            return;

        SelectedManualViewerHeader = $"{hit.Citation} (Section: {hit.Section})";
        SelectedManualViewerContent = hit.Text;
    }

    // =========================================================================
    // Phase 5: Repair-History Learning — Recommendations, Outcomes, Comebacks & WO Numbering (ADR-010)
    // =========================================================================

    private readonly Dictionary<string, Outcome> _outcomesByWorkOrderId = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<WorkOrderBrowserRowViewModel> WorkOrderBrowserRows { get; } = new();

    [ObservableProperty]
    private WorkOrderBrowserRowViewModel? _selectedWorkOrderBrowserRow;

    partial void OnSelectedWorkOrderBrowserRowChanged(WorkOrderBrowserRowViewModel? value)
    {
        if (value is not null && !ReferenceEquals(SelectedWorkOrder, value.WorkOrder))
        {
            SelectedWorkOrder = value.WorkOrder;
        }
    }

    public ObservableCollection<RepairCandidateItemViewModel> RecommendationCandidates { get; } = new();

    public ObservableCollection<RecommendationTierGroupViewModel> RecommendationTierGroups { get; } = new();

    [ObservableProperty]
    private string _recommendationQueryText = "";

    [ObservableProperty]
    private int _recommendationTopK = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecommendationFallbackBadgeVisibility))]
    private bool _isRecommendationFallback;

    public Visibility RecommendationFallbackBadgeVisibility =>
        IsRecommendationFallback ? Visibility.Visible : Visibility.Collapsed;

    public string RecommendationFallbackBadgeText =>
        "text-only ranking (no embedding model)";

    public string RecommendationSuggestionBadgeText =>
        "AI SUGGESTION — Technician makes the final call";

    [ObservableProperty]
    private bool _isRecommendationsBusy;

    [ObservableProperty]
    private string _recommendationStatusMessage =
        "Search past repair history to surface similar failures and the repairs that fixed them (grouped by Model, Same family, and All).";

    public bool HasNoRecommendations => RecommendationCandidates.Count == 0;

    public Visibility NoRecommendationsVisibility =>
        HasNoRecommendations ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasRecommendationsVisibility =>
        !HasNoRecommendations ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<Repair> WorkOrderRepairs { get; } = new();

    public bool HasNoWorkOrderRepairs => WorkOrderRepairs.Count == 0;

    public Visibility NoWorkOrderRepairsVisibility =>
        HasNoWorkOrderRepairs ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<OutcomeResult> AllOutcomeResults { get; } = new[]
    {
        OutcomeResult.Fixed,
        OutcomeResult.NotFixed,
        OutcomeResult.Unsure,
    };

    [ObservableProperty]
    private OutcomeResult? _selectedCloseOutcomeResult = OutcomeResult.Fixed;

    [ObservableProperty]
    private string _closeOutcomeReason = "";

    [ObservableProperty]
    private string _workOrderCloseStatusMessage =
        "Select an outcome (Fixed / Not Fixed / Unsure) and optional reason to close the active work order.";

    public ObservableCollection<Comeback> DetectedComebacksForLastClose { get; } = new();

    /// <summary>
    /// Optional callback for headless tests to respond to comeback confirmation prompts without a live ContentDialog.
    /// Return <c>true</c> to confirm, <c>false</c> to dismiss.
    /// </summary>
    public Func<Data.Services.ComebackMatch, WorkOrder?, Task<bool>>? ComebackPromptHandler { get; set; }

    /// <summary>
    /// Optional callback for headless tests to respond to outcome prompts when closing a work order without an explicit outcome.
    /// </summary>
    public Func<WorkOrder, Task<(OutcomeResult Result, string? Reason)?>>? OutcomePromptHandler { get; set; }

    // ---- Work Order Browser Preview Pane Properties (Screenshot 03) ----

    public string PreviewRepairOrderLine
    {
        get
        {
            var wo = ActiveWorkOrder;
            if (wo is null)
                return "No work order selected";

            var num = WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo);
            var closedText = string.IsNullOrWhiteSpace(wo.ClosedAt) ? "" : $" · Closed: {wo.ClosedAt}";
            return $"{num} · Status: {wo.Status} · Opened: {wo.OpenedAt}{closedText}";
        }
    }

    public string PreviewVehicleAssetLine
    {
        get
        {
            var wo = ActiveWorkOrder;
            if (wo is null)
                return "—";

            var asset = Assets.FirstOrDefault(a => string.Equals(a.Id, wo.AssetId, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                return wo.AssetId;

            var modelText = string.IsNullOrWhiteSpace(asset.ModelId) ? "" : $" · Model: {asset.ModelId}";
            return $"{asset.AssetNumber} — {asset.Name ?? "Equipment"}{modelText}";
        }
    }

    public string PreviewSymptomsLine
    {
        get
        {
            var wo = ActiveWorkOrder;
            if (wo is null)
                return "—";

            if (!string.IsNullOrWhiteSpace(wo.SymptomsSummary))
                return wo.SymptomsSummary!;
            if (!string.IsNullOrWhiteSpace(wo.ComplaintSummary))
                return wo.ComplaintSummary!;
            return "(No symptoms recorded)";
        }
    }

    public string PreviewServiceNotesLine
    {
        get
        {
            var wo = ActiveWorkOrder;
            if (wo is null)
                return "—";

            if (!string.IsNullOrWhiteSpace(wo.RepairSummary))
                return wo.RepairSummary!;
            if (WorkOrderRepairs.Count > 0)
                return string.Join("; ", WorkOrderRepairs.Select(r => r.Description));
            return "(No repair/service notes recorded yet)";
        }
    }

    public string PreviewOutcomeLine
    {
        get
        {
            var wo = ActiveWorkOrder;
            if (wo is null)
                return "—";

            if (_outcomesByWorkOrderId.TryGetValue(wo.Id, out var outcome))
            {
                var comebackTag = outcome.ComebackDetected ? " [COMEBACK CONFIRMED]" : "";
                var reasonTag = string.IsNullOrWhiteSpace(outcome.Reason) ? "" : $" — {outcome.Reason}";
                return $"{outcome.Result}{comebackTag}{reasonTag} (Closed by {outcome.ClosedBy ?? "Tech"} at {outcome.ClosedAt})";
            }

            return wo.Status == WorkOrderStatus.Closed ? "Closed" : "Open — Not yet closed";
        }
    }

    public string PreviewTechnicianLine => SelectedTechnician;

    public string PreviewShopInfoLine => ShopNameText;

    private void NotifyWorkOrderPreviewPropertiesChanged()
    {
        OnPropertyChanged(nameof(PreviewRepairOrderLine));
        OnPropertyChanged(nameof(PreviewVehicleAssetLine));
        OnPropertyChanged(nameof(PreviewSymptomsLine));
        OnPropertyChanged(nameof(PreviewServiceNotesLine));
        OnPropertyChanged(nameof(PreviewOutcomeLine));
        OnPropertyChanged(nameof(PreviewTechnicianLine));
        OnPropertyChanged(nameof(PreviewShopInfoLine));
        RebuildWorkOrderDocument();
    }

    private void ReplaceWorkOrderInMemory(WorkOrder updated)
    {
        for (int i = 0; i < WorkOrders.Count; i++)
        {
            if (string.Equals(WorkOrders[i].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                WorkOrders[i] = updated;
                break;
            }
        }

        if (SelectedWorkOrder is not null &&
            string.Equals(SelectedWorkOrder.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
        {
            SelectedWorkOrder = updated;
        }

        for (int i = 0; i < OpenTabs.Count; i++)
        {
            if (string.Equals(OpenTabs[i].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                var wasSelected = ReferenceEquals(SelectedTab, OpenTabs[i]);
                var updatedTab = new OpenWorkOrderTab(
                    updated.Id,
                    WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(updated),
                    OpenTabs[i].AssetNumber,
                    updated.Status.ToString());
                OpenTabs[i] = updatedTab;
                if (wasSelected)
                {
                    SelectedTab = updatedTab;
                }
                break;
            }
        }

        RebuildWorkOrderBrowserRows();
        NotifyActiveWorkOrderChanged();
    }

    public void RebuildWorkOrderBrowserRows()
    {
        var previousSelectedId = SelectedWorkOrder?.Id ?? SelectedWorkOrderBrowserRow?.Id;
        WorkOrderBrowserRows.Clear();

        foreach (var wo in WorkOrders)
        {
            var asset = Assets.FirstOrDefault(a => string.Equals(a.Id, wo.AssetId, StringComparison.OrdinalIgnoreCase));
            var assetLabel = asset is not null ? $"{asset.AssetNumber} ({asset.Name})" : wo.AssetId;
            _outcomesByWorkOrderId.TryGetValue(wo.Id, out var outcome);
            WorkOrderBrowserRows.Add(new WorkOrderBrowserRowViewModel(wo, assetLabel, outcome));
        }

        if (!string.IsNullOrWhiteSpace(previousSelectedId))
        {
            SelectedWorkOrderBrowserRow = WorkOrderBrowserRows.FirstOrDefault(r =>
                string.Equals(r.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task ReloadWorkOrderOutcomesAndRepairsAsync(string? workOrderId = null)
    {
        try
        {
            var allOutcomes = await _dataServices.Outcomes.GetAllAsync();
            _outcomesByWorkOrderId.Clear();
            foreach (var o in allOutcomes)
            {
                _outcomesByWorkOrderId[o.WorkOrderId] = o;
            }

            var targetWoId = workOrderId ?? ResolveActiveWorkOrderInMemory()?.Id;
            WorkOrderRepairs.Clear();
            if (!string.IsNullOrWhiteSpace(targetWoId))
            {
                var repairs = await _dataServices.Repairs.FindAsync("work_order_id", targetWoId);
                foreach (var r in repairs.Where(x => x.DeletedAt is null).OrderByDescending(x => x.PerformedAt, StringComparer.Ordinal))
                {
                    WorkOrderRepairs.Add(r);
                }
            }

            OnPropertyChanged(nameof(HasNoWorkOrderRepairs));
            OnPropertyChanged(nameof(NoWorkOrderRepairsVisibility));
            RebuildWorkOrderBrowserRows();
            NotifyWorkOrderPreviewPropertiesChanged();
        }
        catch
        {
            // Ignore when DB is not initialized yet
        }
    }

    /// <summary>
    /// Calls <see cref="IAiClient.SearchRepairHistoryAsync"/> for the active work order,
    /// preserves the exact candidate order returned by the service (never re-sorts),
    /// groups candidates by <see cref="MatchTier"/> (Model / Family / All), and records a
    /// <c>recommendations</c> row (<c>Presented = true</c>, <c>Accepted = false</c>) for each shown candidate.
    /// </summary>
    [RelayCommand]
    public async Task LoadRepairRecommendationsAsync(string? explicitQueryText = null)
    {
        if (IsRecommendationsBusy)
            return;

        IsRecommendationsBusy = true;
        try
        {
            var activeWo = await ResolveActiveWorkOrderAsync();
            if (activeWo is null)
            {
                RecommendationCandidates.Clear();
                RecommendationTierGroups.Clear();
                OnPropertyChanged(nameof(HasNoRecommendations));
                OnPropertyChanged(nameof(NoRecommendationsVisibility));
                OnPropertyChanged(nameof(HasRecommendationsVisibility));
                RecommendationStatusMessage = "Please open or create a work order first to load repair recommendations.";
                StatusBannerMessage = RecommendationStatusMessage;
                return;
            }

            var queryText = explicitQueryText
                            ?? (string.IsNullOrWhiteSpace(RecommendationQueryText) ? null : RecommendationQueryText.Trim());

            var request = new RepairHistorySearchRequest
            {
                WorkOrderId = activeWo.Id,
                QueryText = queryText,
                TopK = Math.Clamp(RecommendationTopK, 1, 50),
            };

            var response = await _aiClient.SearchRepairHistoryAsync(request);
            IsRecommendationFallback = response.Fallback;

            RecommendationCandidates.Clear();
            RecommendationTierGroups.Clear();

            var now = Clock.UtcNow;
            for (int rank = 0; rank < response.Candidates.Count; rank++)
            {
                var candidate = response.Candidates[rank];

                // Verify candidate_repair_id exists in local repairs table to satisfy FK if foreign keys are enforced
                string? validCandidateRepairId = string.IsNullOrWhiteSpace(candidate.CandidateRepairId)
                    ? null
                    : candidate.CandidateRepairId;

                if (validCandidateRepairId is not null)
                {
                    try
                    {
                        var existingRepair = await _dataServices.Repairs.GetByIdAsync(validCandidateRepairId);
                        if (existingRepair is null)
                        {
                            validCandidateRepairId = null;
                        }
                    }
                    catch
                    {
                        validCandidateRepairId = null;
                    }
                }

                string? persistedRecId = null;
                try
                {
                    var recRow = new Recommendation
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        WorkOrderId = activeWo.Id,
                        CandidateRepairId = validCandidateRepairId,
                        SimilarityScore = candidate.SimilarityScore,
                        SuccessRate = candidate.SuccessRate,
                        EvidenceJson = candidate.Evidence ?? "",
                        Rank = rank,
                        Presented = true,
                        Accepted = false,
                        GeneratedAt = now,
                        CreatedAt = now,
                    };
                    await _dataServices.Recommendations.AddAsync(recRow);
                    persistedRecId = recRow.Id;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GSE Tech Pro] Could not persist presented recommendation row: {ex.Message}");
                }

                var itemVm = new RepairCandidateItemViewModel(
                    candidate,
                    rank: rank,
                    isFallback: response.Fallback,
                    recommendationRowId: persistedRecId,
                    isAccepted: false);

                RecommendationCandidates.Add(itemVm);
            }

            // Build MatchTier groups in exact returned order without re-sorting candidates
            BuildTierGroupsPreservingOrder(RecommendationCandidates);

            OnPropertyChanged(nameof(HasNoRecommendations));
            OnPropertyChanged(nameof(NoRecommendationsVisibility));
            OnPropertyChanged(nameof(HasRecommendationsVisibility));

            if (RecommendationCandidates.Count == 0)
            {
                RecommendationStatusMessage = "No similar past repairs found";
            }
            else if (response.Fallback)
            {
                RecommendationStatusMessage =
                    $"Loaded {RecommendationCandidates.Count} repair recommendation(s) for {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(activeWo)} — text-only ranking (no embedding model).";
            }
            else
            {
                RecommendationStatusMessage =
                    $"Loaded {RecommendationCandidates.Count} hybrid repair recommendation(s) for {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(activeWo)}.";
            }

            StatusBannerMessage = RecommendationStatusMessage;
        }
        catch (AiServiceException ex)
        {
            RecommendationCandidates.Clear();
            RecommendationTierGroups.Clear();
            OnPropertyChanged(nameof(HasNoRecommendations));
            OnPropertyChanged(nameof(NoRecommendationsVisibility));
            OnPropertyChanged(nameof(HasRecommendationsVisibility));
            RecommendationStatusMessage = $"Repair history search error ({ex.Code}): {ex.Error.Message}";
            StatusBannerMessage = RecommendationStatusMessage;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AiState.NotifyChatCallFailure(ex);
            RecommendationCandidates.Clear();
            RecommendationTierGroups.Clear();
            OnPropertyChanged(nameof(HasNoRecommendations));
            OnPropertyChanged(nameof(NoRecommendationsVisibility));
            OnPropertyChanged(nameof(HasRecommendationsVisibility));
            RecommendationStatusMessage = "AI service is unreachable — start the local AI service to search repair history.";
            StatusBannerMessage = RecommendationStatusMessage;
        }
        finally
        {
            IsRecommendationsBusy = false;
        }
    }

    private void BuildTierGroupsPreservingOrder(IReadOnlyList<RepairCandidateItemViewModel> orderedItems)
    {
        RecommendationTierGroups.Clear();
        if (orderedItems.Count == 0)
            return;

        // Preserve exact candidate sequence and tier order as returned by the recommender
        var seenTiers = new List<MatchTier>();
        var itemsByTier = new Dictionary<MatchTier, List<RepairCandidateItemViewModel>>();

        foreach (var item in orderedItems)
        {
            if (!itemsByTier.TryGetValue(item.MatchTier, out var bucket))
            {
                bucket = new List<RepairCandidateItemViewModel>();
                itemsByTier[item.MatchTier] = bucket;
                seenTiers.Add(item.MatchTier);
            }
            bucket.Add(item);
        }

        foreach (var tier in seenTiers)
        {
            RecommendationTierGroups.Add(new RecommendationTierGroupViewModel(tier, itemsByTier[tier]));
        }
    }

    /// <summary>
    /// Marks the chosen <see cref="RepairCandidateItemViewModel"/> as accepted in <c>recommendations</c>
    /// (<c>Presented = true</c>, <c>Accepted = true</c>) and links the repair to the active work order
    /// by inserting a <see cref="Repair"/> row and updating <see cref="WorkOrder.RepairSummary"/>.
    /// </summary>
    [RelayCommand]
    public async Task AcceptRecommendationAsync(RepairCandidateItemViewModel? item)
    {
        if (item is null)
            return;

        var activeWo = await ResolveActiveWorkOrderAsync();
        if (activeWo is null)
        {
            RecommendationStatusMessage = "Please open a work order first before accepting a repair recommendation.";
            StatusBannerMessage = RecommendationStatusMessage;
            return;
        }

        var now = Clock.UtcNow;

        // 1. Write / update recommendations row with Presented = true, Accepted = true
        try
        {
            bool updatedExisting = false;
            if (!string.IsNullOrWhiteSpace(item.RecommendationRowId))
            {
                var existing = await _dataServices.Recommendations.GetByIdAsync(item.RecommendationRowId!);
                if (existing is not null)
                {
                    var acceptedRow = existing with
                    {
                        Presented = true,
                        Accepted = true,
                    };
                    updatedExisting = await _dataServices.Recommendations.UpdateAsync(acceptedRow);
                }
            }

            if (!updatedExisting)
            {
                string? validCandidateRepairId = string.IsNullOrWhiteSpace(item.CandidateRepairId)
                    ? null
                    : item.CandidateRepairId;
                if (validCandidateRepairId is not null)
                {
                    var existingRep = await _dataServices.Repairs.GetByIdAsync(validCandidateRepairId);
                    if (existingRep is null)
                        validCandidateRepairId = null;
                }

                var newRec = new Recommendation
                {
                    Id = Guid.NewGuid().ToString("D"),
                    WorkOrderId = activeWo.Id,
                    CandidateRepairId = validCandidateRepairId,
                    SimilarityScore = item.SimilarityScore,
                    SuccessRate = item.SuccessRate,
                    EvidenceJson = item.Evidence ?? "",
                    Rank = item.Rank,
                    Presented = true,
                    Accepted = true,
                    GeneratedAt = now,
                    CreatedAt = now,
                };
                await _dataServices.Recommendations.AddAsync(newRec);
                item.RecommendationRowId = newRec.Id;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] AcceptRecommendationAsync recommendation write error: {ex.Message}");
        }

        // 2. Link the chosen repair to the work order (insert a repairs row + update WO RepairSummary)
        var repairRow = new Repair
        {
            Id = Guid.NewGuid().ToString("D"),
            WorkOrderId = activeWo.Id,
            Description = item.RepairDescription,
            System = item.System,
            LaborHours = item.AverageLaborHours,
            PerformedAt = now,
            PerformedBy = SelectedTechnician,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _dataServices.Repairs.AddAsync(repairRow);

        var updatedWo = activeWo with
        {
            Status = activeWo.Status == WorkOrderStatus.Open ? WorkOrderStatus.InProgress : activeWo.Status,
            RepairSummary = item.RepairDescription,
            LaborHours = item.AverageLaborHours ?? activeWo.LaborHours,
            UpdatedAt = now,
        };
        await _dataServices.WorkOrders.UpdateAsync(updatedWo);
        ReplaceWorkOrderInMemory(updatedWo);

        item.IsAccepted = true;
        await ReloadWorkOrderOutcomesAndRepairsAsync(updatedWo.Id);

        RecommendationStatusMessage =
            $"Accepted recommendation #{item.Rank + 1} ('{item.RepairDescription}') and recorded repair on {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(updatedWo)}.";
        StatusBannerMessage = RecommendationStatusMessage;
    }

    /// <summary>
    /// Closes the active (or specified) work order:
    /// 1. Assigns <see cref="WorkOrder.Number"/> via <c>AssignNextNumberAsync</c> if the work order lacks one.
    /// 2. Requires/records an <see cref="Outcome"/> (<c>fixed</c> / <c>not_fixed</c> / <c>unsure</c> + optional reason).
    ///    When called without an explicit outcome, prompts via dialog/<see cref="OutcomePromptHandler"/> or defaults to
    ///    <see cref="OutcomeResult.Unsure"/> with a reason.
    /// 3. Runs <c>ComebackDetection.DetectAsync</c>:
    ///    - Interactive mode: persists each unconfirmed prompt via <c>RecordDetectedAsync</c> and asks the tech to confirm/dismiss
    ///      ("This looks like a repeat of WO #X — same &lt;system/symptom/DTC&gt;. Was the earlier repair a failure?"), calling
    ///      <c>MarkConfirmedAsync</c> on confirm.
    ///    - Bypass-prompts mode (<paramref name="bypassComebackPrompts"/> = true): calls <c>ConfirmAsync</c> directly when
    ///      <paramref name="confirmWhenBypassingPrompts"/> is true.
    /// </summary>
    public async Task<Outcome?> CloseWorkOrderAsync(
        OutcomeResult? explicitResult = null,
        string? explicitReason = null,
        string? workOrderId = null,
        bool bypassComebackPrompts = false,
        bool confirmWhenBypassingPrompts = true)
    {
        WorkOrder? targetWo = null;
        if (!string.IsNullOrWhiteSpace(workOrderId))
        {
            targetWo = WorkOrders.FirstOrDefault(w => string.Equals(w.Id, workOrderId, StringComparison.OrdinalIgnoreCase))
                       ?? await _dataServices.WorkOrders.GetByIdAsync(workOrderId!);
        }
        targetWo ??= await ResolveActiveWorkOrderAsync();

        if (targetWo is null)
        {
            WorkOrderCloseStatusMessage = "No open work order to close.";
            StatusBannerMessage = WorkOrderCloseStatusMessage;
            return null;
        }

        // Resolve outcome result & reason (prompt if missing, or default to Unsure with reason)
        OutcomeResult chosenResult;
        string? chosenReason = string.IsNullOrWhiteSpace(explicitReason) ? null : explicitReason.Trim();

        if (explicitResult.HasValue)
        {
            chosenResult = explicitResult.Value;
        }
        else if (OutcomePromptHandler is not null)
        {
            var prompted = await OutcomePromptHandler(targetWo);
            if (prompted is null)
            {
                WorkOrderCloseStatusMessage = $"Close cancelled for {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(targetWo)}.";
                StatusBannerMessage = WorkOrderCloseStatusMessage;
                return null;
            }
            chosenResult = prompted.Value.Result;
            chosenReason = string.IsNullOrWhiteSpace(prompted.Value.Reason) ? chosenReason : prompted.Value.Reason!.Trim();
        }
        else if (DialogXamlRoot is not null)
        {
            var dialogOutcome = await ShowOutcomeEntryDialogAsync(targetWo);
            if (dialogOutcome is null)
            {
                WorkOrderCloseStatusMessage = $"Close cancelled for {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(targetWo)}.";
                StatusBannerMessage = WorkOrderCloseStatusMessage;
                return null;
            }
            chosenResult = dialogOutcome.Value.Result;
            chosenReason = dialogOutcome.Value.Reason;
        }
        else
        {
            // Headless default when no explicit outcome was supplied: Unsure with a reason
            chosenResult = OutcomeResult.Unsure;
            chosenReason ??= "Closed without explicit outcome selection; defaulted to unsure.";
        }

        var closedAt = Clock.UtcNow;

        // 1. Ensure WO has a human-readable Number before saving
        var assignedNumber = targetWo.Number;
        if (string.IsNullOrWhiteSpace(assignedNumber))
        {
            assignedNumber = await _dataServices.WorkOrders.AssignNextNumberAsync();
        }

        var closedWo = targetWo with
        {
            Number = assignedNumber,
            Status = WorkOrderStatus.Closed,
            ClosedAt = closedAt,
            UpdatedAt = closedAt,
        };

        var existingInDb = await _dataServices.WorkOrders.GetByIdAsync(closedWo.Id);
        if (existingInDb is null)
        {
            await _dataServices.WorkOrders.AddAsync(closedWo);
        }
        else
        {
            await _dataServices.WorkOrders.UpdateAsync(closedWo);
        }

        ReplaceWorkOrderInMemory(closedWo);

        // 2. Persist the Outcome row in outcomes table
        var existingOutcomes = await _dataServices.Outcomes.FindAsync("work_order_id", closedWo.Id);
        Outcome persistedOutcome;
        if (existingOutcomes.Count > 0)
        {
            persistedOutcome = existingOutcomes[0] with
            {
                Result = chosenResult,
                Reason = chosenReason,
                ClosedBy = SelectedTechnician,
                ClosedAt = closedAt,
                UpdatedAt = closedAt,
            };
            await _dataServices.Outcomes.UpdateAsync(persistedOutcome);
        }
        else
        {
            persistedOutcome = new Outcome
            {
                Id = Guid.NewGuid().ToString("D"),
                WorkOrderId = closedWo.Id,
                Result = chosenResult,
                Reason = chosenReason,
                ClosedBy = SelectedTechnician,
                ClosedAt = closedAt,
                ComebackDetected = false,
                CreatedAt = closedAt,
                UpdatedAt = closedAt,
            };
            await _dataServices.Outcomes.AddAsync(persistedOutcome);
        }

        // 3. Comeback detection & confirmation (ADR-010 §6)
        DetectedComebacksForLastClose.Clear();
        int confirmedComebackCount = 0;

        try
        {
            var matches = await _dataServices.ComebackDetection.DetectAsync(closedWo.Id);
            foreach (var match in matches)
            {
                var priorWo = WorkOrders.FirstOrDefault(w => string.Equals(w.Id, match.OriginalWorkOrderId, StringComparison.OrdinalIgnoreCase))
                              ?? await _dataServices.WorkOrders.GetByIdAsync(match.OriginalWorkOrderId);

                if (bypassComebackPrompts)
                {
                    if (confirmWhenBypassingPrompts)
                    {
                        var directConfirmed = await _dataServices.ComebackDetection.ConfirmAsync(
                            match.OriginalWorkOrderId,
                            match.ComebackWorkOrderId,
                            match.WindowDays,
                            match.WindowHours,
                            match.MatchReason);
                        DetectedComebacksForLastClose.Add(directConfirmed);
                        confirmedComebackCount++;
                    }
                    else
                    {
                        var recorded = await _dataServices.ComebackDetection.RecordDetectedAsync(match);
                        DetectedComebacksForLastClose.Add(recorded);
                    }
                }
                else
                {
                    // Persist unconfirmed comeback prompt first
                    var pendingComeback = await _dataServices.ComebackDetection.RecordDetectedAsync(match);
                    bool confirmed = await PromptComebackConfirmationAsync(match, priorWo);

                    if (confirmed)
                    {
                        await _dataServices.ComebackDetection.MarkConfirmedAsync(pendingComeback.Id);
                        pendingComeback = pendingComeback with { Confirmed = true };
                        confirmedComebackCount++;
                    }

                    DetectedComebacksForLastClose.Add(pendingComeback);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSE Tech Pro] Comeback detection error on close: {ex.Message}");
        }

        await ReloadWorkOrderOutcomesAndRepairsAsync(closedWo.Id);

        var comebackSummary = DetectedComebacksForLastClose.Count == 0
            ? "No repeat-failure comebacks detected."
            : $"Comebacks detected: {DetectedComebacksForLastClose.Count} ({confirmedComebackCount} confirmed).";

        WorkOrderCloseStatusMessage =
            $"Closed {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(closedWo)} with outcome '{chosenResult}'{(string.IsNullOrWhiteSpace(chosenReason) ? "" : $" ({chosenReason})")}. {comebackSummary}";
        StatusBannerMessage = WorkOrderCloseStatusMessage;

        return persistedOutcome;
    }

    [RelayCommand]
    public async Task CloseActiveWorkOrderFromPanelAsync()
    {
        var resultToUse = SelectedCloseOutcomeResult;
        var reasonToUse = string.IsNullOrWhiteSpace(CloseOutcomeReason) ? null : CloseOutcomeReason.Trim();
        await CloseWorkOrderAsync(resultToUse, reasonToUse);
    }

    public static string BuildComebackPromptMessage(Data.Services.ComebackMatch match, WorkOrder? originalWo)
    {
        var woLabel = originalWo is not null
            ? WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(originalWo)
            : $"WO #{(match.OriginalWorkOrderId.Length > 8 ? match.OriginalWorkOrderId[..8] : match.OriginalWorkOrderId)}";

        var reason = string.IsNullOrWhiteSpace(match.MatchReason)
            ? "system/symptom/DTC"
            : match.MatchReason.Trim();

        return $"This looks like a repeat of {woLabel} — same {reason} Was the earlier repair a failure?";
    }

    private async Task<bool> PromptComebackConfirmationAsync(Data.Services.ComebackMatch match, WorkOrder? originalWo)
    {
        if (ComebackPromptHandler is not null)
        {
            return await ComebackPromptHandler(match, originalWo);
        }

        if (DialogXamlRoot is null)
        {
            return false;
        }

        var promptText = BuildComebackPromptMessage(match, originalWo);
        var dialog = new ContentDialog
        {
            Title = "Possible Comeback / Repeat Failure Detected",
            Content = promptText,
            PrimaryButtonText = "Confirm Comeback (Earlier Repair Failed)",
            CloseButtonText = "Dismiss / Skip",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = DialogXamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<(OutcomeResult Result, string? Reason)?> ShowOutcomeEntryDialogAsync(WorkOrder workOrder)
    {
        if (DialogXamlRoot is null)
            return null;

        var outcomeCombo = new ComboBox
        {
            Header = "Repair Outcome (Required)",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = AllOutcomeResults,
            SelectedItem = SelectedCloseOutcomeResult ?? OutcomeResult.Unsure,
        };

        var reasonBox = new TextBox
        {
            Header = "Outcome Reason / Notes (Optional)",
            PlaceholderText = "e.g. Relief valve replaced and pressure verified at 2,400 psi; or awaiting follow-up test",
            MinHeight = 44,
            Text = CloseOutcomeReason ?? "",
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 440,
            Children =
            {
                new TextBlock
                {
                    Text = $"Record the repair outcome before closing {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(workOrder)}:",
                    TextWrapping = TextWrapping.Wrap,
                },
                outcomeCombo,
                reasonBox,
            },
        };

        var dialog = new ContentDialog
        {
            Title = $"Close {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(workOrder)} — Record Outcome",
            Content = panel,
            PrimaryButtonText = "Close Work Order",
            SecondaryButtonText = "Default to Unsure",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = DialogXamlRoot,
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult == ContentDialogResult.Primary)
        {
            var picked = outcomeCombo.SelectedItem is OutcomeResult r ? r : OutcomeResult.Unsure;
            var reason = string.IsNullOrWhiteSpace(reasonBox.Text) ? null : reasonBox.Text.Trim();
            return (picked, reason);
        }

        if (dialogResult == ContentDialogResult.Secondary)
        {
            var reason = string.IsNullOrWhiteSpace(reasonBox.Text)
                ? "Closed as unsure by technician."
                : reasonBox.Text.Trim();
            return (OutcomeResult.Unsure, reason);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _uiThrottleTimer?.Stop();
        _playbackTimer?.Stop();
        AiState.StateUpdated -= OnAiStateUpdated;
        await AiState.DisposeAsync();
        if (_aiClient is IDisposable disposableClient)
        {
            disposableClient.Dispose();
        }
        await DisconnectScopeDeviceAsync();
        await DisposeCanMonitorAsync();
        await DisconnectSimulatorAsync();
        await _adapterFactory.DisposeAsync();
    }
}
