using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using GseTechPro.Data;
using GseTechPro.Devices.Abstractions;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GseTechPro.App.ViewModels;

public sealed record NavRailItem(string Key, string Label, string Glyph);

public sealed record OpenWorkOrderTab(string Id, string Title, string AssetNumber, string StatusText);

public sealed record DiagnosticLogEntry(string Timestamp, string Message);

public sealed partial class LivePidRowViewModel : ObservableObject
{
    public LivePidRowViewModel(string name, double rawValue, string rawUnit, UnitSystem unitSystem, string timestamp)
    {
        Name = name;
        Update(rawValue, rawUnit, unitSystem, timestamp);
    }

    public string Name { get; }

    /// <summary>The value exactly as reported by the adapter (NOT yet canonicalized).</summary>
    public double RawCanonicalValue { get; private set; }

    /// <summary>The unit exactly as reported by the adapter (may be a display unit, e.g. "kPa").</summary>
    public string RawCanonicalUnit { get; private set; } = "";

    [ObservableProperty]
    private string _displayValue = "";

    [ObservableProperty]
    private string _displayUnit = "";

    [ObservableProperty]
    private string _timestamp = "";

    [ObservableProperty]
    private string _canonicalSourceDisplay = "";

    public void Update(double rawValue, string rawUnit, UnitSystem unitSystem, string timestamp)
    {
        RawCanonicalValue = rawValue;
        RawCanonicalUnit = rawUnit;
        Timestamp = timestamp;
        CanonicalSourceDisplay = $"{rawValue:G6} {rawUnit}";
        FormatForUnitSystem(unitSystem);
    }

    public void FormatForUnitSystem(UnitSystem unitSystem)
    {
        if (UnitConverter.IsKnownUnit(RawCanonicalUnit))
        {
            // Adapters may report display units (e.g. J1939 "100 kPa"). Convert the reported
            // value to canonical SI before display so a kPa-reporting adapter is not rendered
            // ~1000x wrong (kPa -> Pa). Idempotent because RawCanonicalValue/Unit are the raw
            // reported values and are never mutated by this method.
            var reading = new ReadingValue
            {
                ValueCanonical = UnitConverter.ToCanonical(RawCanonicalValue, RawCanonicalUnit),
                UnitCanonical = UnitConverter.CanonicalUnit(RawCanonicalUnit),
            };
            var (convertedValue, convertedUnit) = reading.ToDisplay(unitSystem);
            DisplayValue = FormatNumber(convertedValue, convertedUnit);
            DisplayUnit = convertedUnit;
        }
        else if (string.Equals(RawCanonicalUnit, "m/s", StringComparison.OrdinalIgnoreCase))
        {
            // Speed reported by simulator in SI m/s: convert for display to mph (Imperial) or km/h (Metric)
            if (unitSystem == UnitSystem.Imperial)
            {
                DisplayValue = (RawCanonicalValue * 2.23693629).ToString("F1");
                DisplayUnit = "mph";
            }
            else
            {
                DisplayValue = (RawCanonicalValue * 3.6).ToString("F1");
                DisplayUnit = "km/h";
            }
        }
        else
        {
            DisplayValue = RawCanonicalValue.ToString("G6");
            DisplayUnit = RawCanonicalUnit;
        }
    }

    private static string FormatNumber(double value, string unit) => unit switch
    {
        UnitConverter.Psi => value.ToString("F1"),
        UnitConverter.Bar => value.ToString("F2"),
        UnitConverter.Fahrenheit or UnitConverter.Celsius => value.ToString("F1"),
        UnitConverter.Volt or UnitConverter.Ampere => value.ToString("F2"),
        UnitConverter.LiterPerMinute => value.ToString("F3"),
        UnitConverter.Hour => value.ToString("F2"),
        _ => value.ToString("G6"),
    };
}

public sealed class DtcRowViewModel
{
    public DtcRowViewModel(DtcReadout readout, bool isDarkTheme, bool isSunlightMode)
    {
        Readout = readout;
        HasFreezeFrame = readout.Status == DtcStatus.Active ? "FF" : "";
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
    public string Code => Readout.Code;
    public string HasFreezeFrame { get; }
    public string Description => Readout.Description ?? "";
    public string Protocol => Readout.Protocol.ToString();
    public string Severity => Readout.Severity ?? "—";
    public string StatusLabel { get; }
    public SolidColorBrush RowBrush { get; }
}

public sealed partial class MainShellViewModel : ObservableObject
{
    private readonly DataServices _dataServices;
    private readonly IDiagnosticAdapterFactory _adapterFactory;
    private readonly DispatcherQueue _dispatcherQueue;
    private IDiagnosticAdapter? _activeAdapter;
    private IReadOnlyList<DtcReadout> _lastDtcs = Array.Empty<DtcReadout>();
    private readonly Dictionary<string, LivePidRowViewModel> _pidMap = new(StringComparer.OrdinalIgnoreCase);

    public MainShellViewModel(
        DataServices dataServices,
        IDiagnosticAdapterFactory adapterFactory,
        DispatcherQueue dispatcherQueue)
    {
        _dataServices = dataServices;
        _adapterFactory = adapterFactory;
        _dispatcherQueue = dispatcherQueue;

        OpenTabs.Add(new OpenWorkOrderTab(
            "SIM-SESSION-01",
            "Active Diagnostic Session (Unassigned)",
            "BENCH / SIM",
            "Open"));
        SelectedTab = OpenTabs[0];

        AvailableTechnicians.Add("Default Tech [Unassigned]");
        SelectedTechnician = AvailableTechnicians[0];

        RebuildNavRailForTool("OBD II");
    }

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

    public ObservableCollection<NavRailItem> NavRailItems { get; } = new();

    [ObservableProperty]
    private string _activeTool = "Heavy Duty";

    [ObservableProperty]
    private string _activeSectionKey = "Diagnostics";

    [ObservableProperty]
    private string _breadcrumbRoot = "Equipment Selection";

    [ObservableProperty]
    private string _breadcrumbCategory = "Main Menu";

    [ObservableProperty]
    private string _breadcrumbLeaf = "Equipment Health & Live Data";

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
    private string _adapterDeviceName = "No Adapter Selected";

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
    private string _statusBannerMessage = "Ready — Select a protocol and click 'Connect Simulator' to stream SIMULATED bus data.";

    public ObservableCollection<DtcRowViewModel> Dtcs { get; } = new();
    public ObservableCollection<LivePidRowViewModel> LivePids { get; } = new();
    public ObservableCollection<LivePidRowViewModel> FreezeFramePids { get; } = new();
    public ObservableCollection<EcuInfo> DiscoveredEcus { get; } = new();
    public ObservableCollection<Asset> Assets { get; } = new();
    public ObservableCollection<WorkOrder> WorkOrders { get; } = new();
    public ObservableCollection<DiagnosticLogEntry> DiagnosticLog { get; } = new();

    /// <summary>
    /// The UI <see cref="XamlRoot"/> used to host confirmation and error dialogs. Set by
    /// <c>MainWindow</c> once the root visual is loaded. Actions that require confirmation
    /// (e.g. Clear DTCs) refuse to proceed while this is null, so no caller can bypass them.
    /// </summary>
    public XamlRoot? DialogXamlRoot { get; set; }

    [ObservableProperty]
    private string _freezeFrameTimestamp = "Not captured";

    public bool HasNoAssets => Assets.Count == 0;
    public bool HasNoWorkOrders => WorkOrders.Count == 0;
    public bool HasNoDtcs => Dtcs.Count == 0;
    public bool HasNoLivePids => LivePids.Count == 0;

    public bool IsDiagnosticsViewVisible =>
        ActiveSectionKey is "Diagnostics" or "LiveData" or "DTCs" or "FreezeFrames" or "CurrentData" or "Readiness" or "Monitors";

    public bool IsWorkOrdersViewVisible => ActiveSectionKey == "WorkOrders";
    public bool IsAssetsViewVisible => ActiveSectionKey == "Assets";
    public bool IsToolPlaceholderVisible => !IsDiagnosticsViewVisible && !IsWorkOrdersViewVisible && !IsAssetsViewVisible;

    public Visibility DiagnosticsViewVisibility =>
        IsDiagnosticsViewVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WorkOrdersViewVisibility =>
        IsWorkOrdersViewVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AssetsViewVisibility =>
        IsAssetsViewVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ToolPlaceholderVisibility =>
        IsToolPlaceholderVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoAssetsVisibility =>
        HasNoAssets ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoWorkOrdersVisibility =>
        HasNoWorkOrders ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoDtcsVisibility =>
        HasNoDtcs ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoLivePidsVisibility =>
        HasNoLivePids ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoDiagnosticLogVisibility =>
        DiagnosticLog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private string _placeholderTitle = "";

    [ObservableProperty]
    private string _placeholderSubtitle = "";

    public async Task InitializeAsync()
    {
        try
        {
            await _dataServices.InitializeAsync();
            await ReloadDatabaseRecordsAsync();
        }
        catch (Exception ex)
        {
            // Surface a DB-init failure loudly instead of continuing with an unusable
            // (offline-first) store. Banner + log line are always visible; the modal is
            // shown only once a XamlRoot is available.
            Debug.WriteLine($"[GSE Tech Pro] Database initialization failed: {ex}");
            StatusBannerMessage = $"DATABASE ERROR — could not initialize the local database: {ex.Message}";
            AppendDiagnosticLog($"Database initialization failed: {ex.Message}");
            await ShowErrorDialogAsync(
                "Database Error",
                "The local database could not be initialized, so records cannot be saved or loaded.\n\n" + ex.Message);
        }
    }

    [RelayCommand]
    public async Task ReloadDatabaseRecordsAsync()
    {
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
    }

    [RelayCommand]
    public async Task ConnectSimulatorAsync()
    {
        // Drop any rows from a previous protocol before establishing the new connection.
        // (DisconnectSimulatorAsync below clears again — kept here for defense in depth.)
        ClearDiagnosticCollections();
        await DisconnectSimulatorAsync();

        _activeAdapter = _adapterFactory.CreateSimulator(SelectedProtocol, seed: null);
        _activeAdapter.LiveDataReceived += OnAdapterLiveDataReceived;

        await _activeAdapter.ConnectAsync();

        IsConnected = _activeAdapter.IsConnected;
        AdapterDeviceName = _activeAdapter.Name;
        ConnectionStatusText = "Connected (SIMULATED)";
        ActiveProtocolText = $"Protocol: {SelectedProtocol} (SIMULATED)";

        var vehicleInfo = await _activeAdapter.GetVehicleInfoAsync();
        VehicleVinText = $"VIN: {vehicleInfo.Vin ?? "<unknown>"}";

        DiscoveredEcus.Clear();
        foreach (var ecu in vehicleInfo.Ecus)
            DiscoveredEcus.Add(ecu);

        await RefreshDiagnosticsAsync();

        StatusBannerMessage = $"Connected to {_activeAdapter.Name}. Streaming 10 Hz SIMULATED live data and trouble codes.";
    }

    [RelayCommand]
    public async Task DisconnectSimulatorAsync()
    {
        if (_activeAdapter is not null)
        {
            _activeAdapter.LiveDataReceived -= OnAdapterLiveDataReceived;
            await _activeAdapter.DisconnectAsync();
            _activeAdapter = null;
        }

        IsConnected = false;
        AdapterDeviceName = "No Adapter Connected";
        ConnectionStatusText = "No Connection";
        ActiveProtocolText = "Protocol: None";
        VehicleVinText = "VIN: <not connected>";
        MilStatusText = "OFF";

        // Drop the previous protocol's frozen live-data, DTC, and freeze-frame rows.
        ClearDiagnosticCollections();

        StatusBannerMessage = "Disconnected from simulator.";
    }

    [RelayCommand]
    public async Task RefreshDiagnosticsAsync()
    {
        if (_activeAdapter is null || !_activeAdapter.IsConnected)
        {
            StatusBannerMessage = "Not connected. Click 'Connect Simulator' to read DTCs and live data.";
            return;
        }

        _lastDtcs = await _activeAdapter.ReadDTCsAsync();
        RebuildDtcRows();

        var hasActive = _lastDtcs.Any(d => d.Status == DtcStatus.Active);
        MilStatusText = hasActive ? "ON" : "OFF";

        var snapshot = await _activeAdapter.ReadLiveDataAsync(Array.Empty<string>());
        foreach (var pid in snapshot)
        {
            UpsertLivePid(pid);
        }

        var ff = await _activeAdapter.ReadFreezeFrameAsync();
        FreezeFramePids.Clear();
        if (ff is not null)
        {
            FreezeFrameTimestamp = ff.CapturedAt;
            foreach (var pid in ff.Pids)
            {
                FreezeFramePids.Add(new LivePidRowViewModel(
                    pid.Name, pid.Value, pid.Unit, CurrentUnitSystem, ff.CapturedAt));
            }
        }
    }

    [RelayCommand]
    public async Task ClearDtcsAsync()
    {
        if (_activeAdapter is null || !_activeAdapter.IsConnected)
            return;

        // Safety (blocking): require explicit confirmation before clearing codes. The
        // confirmation lives here in the view model so no caller (toolbar button, menu
        // item, nav rail, or future keyboard shortcut) can clear DTCs without the
        // technician acknowledging that codes cannot be recovered.
        if (!await ConfirmClearDtcsAsync())
        {
            StatusBannerMessage = "Clear DTCs cancelled — no codes were cleared.";
            return;
        }

        // Record an audit note BEFORE clearing (technician, timestamp, adapter, pre-clear list).
        RecordClearDtcsAuditNote();

        await _activeAdapter.ClearDTCsAsync();
        await RefreshDiagnosticsAsync();
        StatusBannerMessage = "Active and Pending SIMULATED DTCs cleared (stored history retained).";
    }

    private async Task<bool> ConfirmClearDtcsAsync()
    {
        // No UI root yet means we cannot show the confirmation — never clear without it.
        if (DialogXamlRoot is null)
        {
            StatusBannerMessage = "Cannot show confirmation dialog; DTCs were not cleared.";
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "Clear DTCs",
            Content = "Clear active and pending codes on this connection? Stored history is retained. Codes cannot be recovered.",
            PrimaryButtonText = "Clear",
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

        // Phase 4: persist audit note to the work-order notes table.
    }

    private void AppendDiagnosticLog(string message)
    {
        DiagnosticLog.Add(new DiagnosticLogEntry(Clock.UtcNow, message));
        OnPropertyChanged(nameof(NoDiagnosticLogVisibility));
    }

    /// <summary>Logs, banners, and (when possible) dialogs an unexpected failure.</summary>
    public void ReportError(string context, Exception ex)
    {
        Debug.WriteLine($"[GSE Tech Pro] {context}: {ex}");
        StatusBannerMessage = $"{context}: {ex.Message}";
        AppendDiagnosticLog($"{context}: {ex.Message}");
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        // Banner + log line are already set by the caller; the modal is best-effort.
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
        _lastDtcs = Array.Empty<DtcReadout>();
        RebuildDtcRows();

        _pidMap.Clear();
        LivePids.Clear();
        OnPropertyChanged(nameof(HasNoLivePids));
        OnPropertyChanged(nameof(NoLivePidsVisibility));

        FreezeFramePids.Clear();
        FreezeFrameTimestamp = "Not captured";
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

        StatusBannerMessage = $"Switched display units to {CurrentUnitSystem} (canonical SI storage preserved).";
    }

    [RelayCommand]
    public void SelectToolbarAction(string actionKey)
    {
        switch (actionKey)
        {
            case "WorkOrders":
            case "Open":
                ActiveTool = "Work Orders";
                ActiveSectionKey = "WorkOrders";
                BreadcrumbRoot = "Shop Foreman";
                BreadcrumbCategory = "Repair Orders";
                BreadcrumbLeaf = "Work Order Browser";
                RebuildNavRailForTool("WorkOrders");
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
                RebuildNavRailForTool("Assets");
                break;

            case "Search":
                ActiveSectionKey = "WorkOrders";
                BreadcrumbRoot = "Shop Foreman";
                BreadcrumbCategory = "Search";
                BreadcrumbLeaf = "Work Orders & Assets";
                StatusBannerMessage = "Showing Work Order & Asset repository view.";
                break;

            case "Save":
                StatusBannerMessage = "Session snapshot ready (persistence to work order record activates in Phase 2).";
                break;

            case "Print":
                StatusBannerMessage = "Print report preview is scheduled for later phase.";
                break;

            case "Notes":
                ShowPlaceholder("Technician & Work Order Notes", "Structured work order, asset, and frame notes panel (Phase 2).");
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
                RebuildNavRailForTool("Scope");
                ShowPlaceholder("Oscilloscope (PicoScope / Hantek Workflow)", "4-channel automotive scope & guided tests screen (Phase 4 — owned by scope agent).");
                break;

            case "CanCapture":
                ActiveTool = "CAN Capture";
                RebuildNavRailForTool("CanCapture");
                ShowPlaceholder("CAN / J1939 / J1708 Bus Capture", "Streaming frame monitor and trigger recording bar (Phase 2).");
                break;

            case "Camera":
                ActiveTool = "Camera";
                RebuildNavRailForTool("Camera");
                ShowPlaceholder("Camera & Pen Annotation", "Rugged tablet photo capture, pen markup, and dataplate OCR (Phase 3).");
                break;

            case "AiTechChat":
                ActiveTool = "AI Tech Chat";
                RebuildNavRailForTool("AiTechChat");
                ShowPlaceholder("Local AI Tech Chat (Ollama lfm3-omni)", "Offline RAG manual lookup, guided troubleshooting trees, and repair history recommendations (Phase 3).");
                break;
        }

        NotifyViewVisibilityChanged();
    }

    [RelayCommand]
    public async Task SelectNavRailItem(string navKey)
    {
        switch (navKey)
        {
            case "Back":
                ActiveSectionKey = "Assets";
                BreadcrumbLeaf = "Equipment Selection";
                break;

            case "LiveData":
            case "CurrentData":
                ActiveSectionKey = "LiveData";
                BreadcrumbLeaf = "Live Data (Value / Units)";
                break;

            case "Refresh":
                await RefreshDiagnosticsAsync();
                break;

            case "DTCs":
                ActiveSectionKey = "DTCs";
                BreadcrumbLeaf = "Diagnostic Trouble Codes (DTCs)";
                break;

            case "FreezeFrames":
                ActiveSectionKey = "FreezeFrames";
                BreadcrumbLeaf = "Freeze Frame Snapshot";
                break;

            case "Readiness":
                ActiveSectionKey = "Readiness";
                BreadcrumbLeaf = "Readiness & System Status";
                break;

            case "Monitors":
                ActiveSectionKey = "Monitors";
                BreadcrumbLeaf = "On-Board Monitors";
                break;

            case "DataFormat":
                ToggleUnitSystem();
                break;

            case "WorkOrders":
                ActiveSectionKey = "WorkOrders";
                BreadcrumbLeaf = "Work Order Browser";
                break;

            case "Assets":
                ActiveSectionKey = "Assets";
                BreadcrumbLeaf = "Equipment & Vehicle Registry";
                break;

            default:
                ActiveSectionKey = "Diagnostics";
                BreadcrumbLeaf = "Equipment Health";
                break;
        }

        NotifyViewVisibilityChanged();
    }

    public void UpdateThemeState(bool isDark, bool isSunlight)
    {
        IsDarkTheme = isDark;
        IsSunlightMode = isSunlight;
        RebuildDtcRows();
    }

    private void ActivateDiagnosticTool(string toolName, string category, string leaf)
    {
        ActiveTool = toolName;
        ActiveSectionKey = "Diagnostics";
        BreadcrumbRoot = "Equipment Selection";
        BreadcrumbCategory = category;
        BreadcrumbLeaf = leaf;
        RebuildNavRailForTool(toolName);
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
        OnPropertyChanged(nameof(IsToolPlaceholderVisible));
        OnPropertyChanged(nameof(DiagnosticsViewVisibility));
        OnPropertyChanged(nameof(WorkOrdersViewVisibility));
        OnPropertyChanged(nameof(AssetsViewVisibility));
        OnPropertyChanged(nameof(ToolPlaceholderVisibility));
    }

    private void RebuildNavRailForTool(string toolKey)
    {
        NavRailItems.Clear();

        if (toolKey is "WorkOrders")
        {
            NavRailItems.Add(new NavRailItem("Back", "Back", "\uE72B"));
            NavRailItems.Add(new NavRailItem("WorkOrders", "Open ROs", "\uE8A5"));
            NavRailItems.Add(new NavRailItem("Assets", "Equipment List", "\uE7EC"));
            NavRailItems.Add(new NavRailItem("Refresh", "Refresh DB", "\uE72C"));
            return;
        }

        if (toolKey is "Assets")
        {
            NavRailItems.Add(new NavRailItem("Assets", "Equipment List", "\uE7EC"));
            NavRailItems.Add(new NavRailItem("WorkOrders", "Work Orders", "\uE8A5"));
            NavRailItems.Add(new NavRailItem("Diagnostics", "Scan / Health", "\uE9D9"));
            NavRailItems.Add(new NavRailItem("Refresh", "Refresh DB", "\uE72C"));
            return;
        }

        if (toolKey is "Scope")
        {
            NavRailItems.Add(new NavRailItem("Back", "Back", "\uE72B"));
            NavRailItems.Add(new NavRailItem("ScopeChannels", "Channels A–D", "\uE9D9"));
            NavRailItems.Add(new NavRailItem("GuidedTests", "Guided Tests", "\uE890"));
            NavRailItems.Add(new NavRailItem("RefWaveforms", "Ref Waveforms", "\uE8B7"));
            NavRailItems.Add(new NavRailItem("DataFormat", "Data Format", "\uE8AB"));
            return;
        }

        if (toolKey is "AiTechChat")
        {
            NavRailItems.Add(new NavRailItem("Back", "Back", "\uE72B"));
            NavRailItems.Add(new NavRailItem("AiChat", "Tech Chat", "\uE8BD"));
            NavRailItems.Add(new NavRailItem("GuidedTree", "Guided Tree", "\uE890"));
            NavRailItems.Add(new NavRailItem("ManualSearch", "Manual RAG", "\uE82D"));
            return;
        }

        // Default Shop Foreman Pro vertical toolbar order (ARCHITECTURE.md §5.1, Screenshot 04):
        // Back, Live Data, Refresh, DTCs, Freeze Frames, Current Data, Readiness, Monitors, Data Format
        NavRailItems.Add(new NavRailItem("Back", "Back", "\uE72B"));
        NavRailItems.Add(new NavRailItem("LiveData", "Live Data", "\uE9D9"));
        NavRailItems.Add(new NavRailItem("Refresh", "Refresh", "\uE72C"));
        NavRailItems.Add(new NavRailItem("DTCs", "DTCs", "\uE7BA"));
        NavRailItems.Add(new NavRailItem("FreezeFrames", "Freeze Frames", "\uE8B8"));
        NavRailItems.Add(new NavRailItem("CurrentData", "Current Data", "\uE8A5"));
        NavRailItems.Add(new NavRailItem("Readiness", "Readiness", "\uE73E"));
        NavRailItems.Add(new NavRailItem("Monitors", "Monitors", "\uE7F4"));
        NavRailItems.Add(new NavRailItem("DataFormat", "Data Format", "\uE8AB"));
    }

    private void RebuildDtcRows()
    {
        Dtcs.Clear();
        foreach (var dtc in _lastDtcs)
        {
            Dtcs.Add(new DtcRowViewModel(dtc, IsDarkTheme, IsSunlightMode));
        }
        OnPropertyChanged(nameof(HasNoDtcs));
        OnPropertyChanged(nameof(NoDtcsVisibility));
    }

    private void OnAdapterLiveDataReceived(object? sender, LiveDataUpdateEventArgs e)
    {
        var pid = e.Value;
        _dispatcherQueue.TryEnqueue(() => UpsertLivePid(pid));
    }

    private void UpsertLivePid(PidValue pid)
    {
        if (_pidMap.TryGetValue(pid.Name, out var existing))
        {
            existing.Update(pid.Value, pid.Unit, CurrentUnitSystem, pid.Timestamp);
        }
        else
        {
            var vm = new LivePidRowViewModel(pid.Name, pid.Value, pid.Unit, CurrentUnitSystem, pid.Timestamp);
            _pidMap[pid.Name] = vm;
            LivePids.Add(vm);
            OnPropertyChanged(nameof(HasNoLivePids));
            OnPropertyChanged(nameof(NoLivePidsVisibility));
        }
    }
}
