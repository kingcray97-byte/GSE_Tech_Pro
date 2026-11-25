using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Devices.Can;
using GseTechPro.Devices.Protocols;
using GseTechPro.Devices.Protocols.J1939;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10C.2: CAN Capture &gt; Bus Monitor. Listen-only bus monitor (ID / DLC / data / count / period, changed
/// bytes), text filter, J1939 decode, record, replay a saved capture, export <c>.asc</c> (Vector ASCII) and CSV, and
/// save a capture to the open work order. Frames are pulled off the monitor's channel in batches (every 100 ms on
/// the UI thread), so a busy bus never floods the UI.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Frames applied per batch; the rest wait for the next tick (the monitor's buffer drops the oldest).</summary>
    public const int CanFramesPerBatch = 5_000;

    /// <summary>Recording stops itself at this many frames (about 20 MB in memory).</summary>
    public const int MaxCanRecordingFrames = 200_000;

    private readonly CanBusTable _canTable = new();
    private readonly Dictionary<(uint, bool), CanBusRowViewModel> _canRowsByKey = new();
    private readonly List<CanFrame> _canRecording = new();
    private readonly Stopwatch _canRateClock = new();
    private CanBusMonitorFactory? _canFactory;
    private ICanBusMonitor? _canMonitor;
    private DispatcherQueueTimer? _canPumpTimer;
    private DateTimeOffset _canRecordingStartedAt;
    private long _canFramesAtLastRate;

    /// <summary>Test seam: swap the factory (fake ECOM discovery).</summary>
    public CanBusMonitorFactory CanFactory
    {
        get => _canFactory ??= new CanBusMonitorFactory();
        set => _canFactory = value;
    }

    public ObservableCollection<CanMonitorDevice> CanDevices { get; } = new();

    public IReadOnlyList<int> CanBitrates => CanMonitorOptions.StandardBitrates;

    /// <summary>Rows that match the filter, sorted by id.</summary>
    public ObservableCollection<CanBusRowViewModel> CanRows { get; } = new();

    public ObservableCollection<string> CanBusErrors { get; } = new();

    /// <summary>Saved <c>.asc</c> captures in the captures folder, newest first (for replay).</summary>
    public ObservableCollection<string> SavedCanCaptures { get; } = new();

    [ObservableProperty]
    private CanMonitorDevice? _selectedCanDevice;

    [ObservableProperty]
    private int _selectedCanBitrate = 250_000;

    [ObservableProperty]
    private string _canFilterText = "";

    [ObservableProperty]
    private bool _isCanMonitoring;

    [ObservableProperty]
    private bool _isCanRecording;

    [ObservableProperty]
    private string _canStatusText = "Not monitoring. Pick an adapter and bit rate, then press Start.";

    [ObservableProperty]
    private string? _selectedSavedCanCapture;

    [ObservableProperty]
    private long _canTotalFrames;

    [ObservableProperty]
    private long _canDroppedFrames;

    [ObservableProperty]
    private double _canFramesPerSecond;

    [ObservableProperty]
    private int _canRecordedFrameCount;

    [ObservableProperty]
    private string _canMonitorName = "";

    [ObservableProperty]
    private bool _isCanSimulated;

    public bool IsCanMonitorViewVisible => ActiveSectionKey == "CanMonitor";

    public Visibility CanMonitorViewVisibility => IsCanMonitorViewVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CanSimulatedBadgeVisibility => IsCanMonitoring && IsCanSimulated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CanRealBadgeVisibility => IsCanMonitoring && !IsCanSimulated ? Visibility.Visible : Visibility.Collapsed;

    public string CanCountersText => string.Create(CultureInfo.InvariantCulture,
        $"{CanTotalFrames:N0} frames · {CanFramesPerSecond:0} frames/s · {CanRows.Count} of {_canTable.Count} ids · {CanDroppedFrames:N0} dropped");

    public string CanRecordButtonText => IsCanRecording ? $"Stop recording ({CanRecordedFrameCount:N0})" : "Record";

    public string CanSelectedDeviceNote => SelectedCanDevice?.UnavailableReason ?? (SelectedCanDevice?.IsSimulated == true
        ? "Simulated bus: frames are demo data and are marked SIMULATED."
        : "Listen-only: the adapter never sends on the bus.");

    // ---------------------------------------------------------------------------------------------
    // Devices
    // ---------------------------------------------------------------------------------------------

    [RelayCommand]
    public void RefreshCanDevices()
    {
        var keep = SelectedCanDevice;
        CanDevices.Clear();
        foreach (var device in CanFactory.Discover())
            CanDevices.Add(device);
        SelectedCanDevice = CanDevices.FirstOrDefault(d => keep is not null && d.Kind == keep.Kind && d.SerialNumber == keep.SerialNumber)
            ?? CanDevices.FirstOrDefault(d => d.Kind == CanBusMonitorFactory.EcomKind && d.UnavailableReason is null)
            ?? CanDevices.FirstOrDefault();
        RefreshSavedCanCaptures();
    }

    /// <summary>On the CAN pages the shell status line follows the bus monitor's status.</summary>
    partial void OnCanStatusTextChanged(string value)
    {
        if (CurrentSidebarSectionKey == "CanCapture")
            StatusBannerMessage = value;
    }

    partial void OnSelectedCanDeviceChanged(CanMonitorDevice? value) =>
        OnPropertyChanged(nameof(CanSelectedDeviceNote));

    // ---------------------------------------------------------------------------------------------
    // Start / stop / replay
    // ---------------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task StartCanMonitorAsync()
    {
        if (CanDevices.Count == 0)
            RefreshCanDevices();
        var device = SelectedCanDevice;
        if (device is null)
        {
            CanStatusText = "No CAN adapter selected.";
            return;
        }
        if (device.UnavailableReason is { } reason)
        {
            CanStatusText = reason;
            return;
        }

        ICanBusMonitor monitor;
        try
        {
            monitor = CanFactory.Create(device);
        }
        catch (Exception ex)
        {
            CanStatusText = ex.Message;
            return;
        }

        await StartCanSourceAsync(monitor, $"Monitoring {monitor.Name} at {SelectedCanBitrate / 1000} kbit/s (listen-only).");
    }

    [RelayCommand]
    public async Task StartCanReplayAsync()
    {
        var name = SelectedSavedCanCapture;
        if (string.IsNullOrWhiteSpace(name))
        {
            CanStatusText = "Pick a saved capture to replay.";
            return;
        }

        var path = Path.Combine(GetCanCaptureDirectory(), name);
        AscReadResult log;
        try
        {
            log = await Task.Run(() =>
            {
                using var reader = new StreamReader(path);
                return AscCanLog.Read(reader);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CanStatusText = $"Could not open {name}: {ex.Message}";
            return;
        }

        if (log.Frames.Count == 0)
        {
            CanStatusText = $"{name} has no CAN frames to replay.";
            return;
        }

        var replay = new ReplayCanBusMonitor(log.Frames, name, log.IsSimulated);
        await StartCanSourceAsync(replay, $"Replaying {name} ({log.Frames.Count:N0} frames).");
    }

    [RelayCommand]
    public async Task StopCanMonitorAsync()
    {
        _canPumpTimer?.Stop();
        var monitor = _canMonitor;
        _canMonitor = null;
        if (monitor is not null)
        {
            monitor.BusError -= OnCanBusError;
            await monitor.StopAsync();
            ProcessPendingCanFrames(monitor);
            await monitor.DisposeAsync();
            CanStatusText = $"Stopped. {CanTotalFrames:N0} frames from {_canTable.Count} ids.";
        }
        if (IsCanRecording)
            IsCanRecording = false;
        IsCanMonitoring = false;
        _canRateClock.Reset();
    }

    private async Task StartCanSourceAsync(ICanBusMonitor monitor, string status)
    {
        await StopCanMonitorAsync();
        ClearCanTable();
        try
        {
            await monitor.StartAsync(new CanMonitorOptions { Bitrate = SelectedCanBitrate });
        }
        catch (Exception ex)
        {
            await monitor.DisposeAsync();
            CanStatusText = ex.Message;
            return;
        }

        monitor.BusError += OnCanBusError;
        _canMonitor = monitor;
        CanMonitorName = monitor.Name;
        IsCanSimulated = monitor.IsSimulated;
        IsCanMonitoring = true;
        CanStatusText = status;
        _canFramesAtLastRate = 0;
        _canRateClock.Restart();

        if (_dispatcherQueue is not null)
        {
            _canPumpTimer ??= CreateCanPumpTimer(_dispatcherQueue);
            _canPumpTimer.Start();
        }
    }

    private DispatcherQueueTimer CreateCanPumpTimer(DispatcherQueue queue)
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) => ProcessPendingCanFrames();
        return timer;
    }

    private void OnCanBusError(object? sender, CanBusErrorEvent e)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{e.TimestampSeconds:0.000} s  {e.Message}");
        if (_dispatcherQueue is null)
            AddCanBusError(line);
        else
            _dispatcherQueue.TryEnqueue(() => AddCanBusError(line));
    }

    private void AddCanBusError(string line)
    {
        CanBusErrors.Insert(0, line);
        while (CanBusErrors.Count > 20)
            CanBusErrors.RemoveAt(CanBusErrors.Count - 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Frame pump (UI thread)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Applies up to <see cref="CanFramesPerBatch"/> waiting frames to the table. Returns how many.</summary>
    public int ProcessPendingCanFrames() => _canMonitor is { } monitor ? ProcessPendingCanFrames(monitor) : 0;

    private int ProcessPendingCanFrames(ICanBusMonitor monitor)
    {
        var applied = 0;
        var touched = new HashSet<CanBusRowViewModel>();
        while (applied < CanFramesPerBatch && monitor.Frames.TryRead(out var frame))
        {
            var (entry, isNew) = _canTable.Apply(frame);
            var key = (entry.Id, entry.IsExtended);
            if (isNew)
            {
                var row = new CanBusRowViewModel(entry, FindManufacturerSpnEntry);
                _canRowsByKey[key] = row;
                if (CanBusTable.Matches(entry, CanFilterText))
                    InsertCanRowSorted(row);
            }
            touched.Add(_canRowsByKey[key]);

            if (IsCanRecording)
            {
                _canRecording.Add(frame);
                if (_canRecording.Count >= MaxCanRecordingFrames)
                {
                    IsCanRecording = false;
                    CanStatusText = $"Recording stopped at {MaxCanRecordingFrames:N0} frames. Save or export it now.";
                }
            }
            applied++;
        }

        foreach (var row in touched)
            row.Refresh();

        CanTotalFrames = _canTable.TotalFrames;
        CanDroppedFrames = monitor.DroppedFrameCount;
        CanRecordedFrameCount = _canRecording.Count;

        var elapsed = _canRateClock.Elapsed.TotalSeconds;
        if (elapsed >= 1.0)
        {
            CanFramesPerSecond = (CanTotalFrames - _canFramesAtLastRate) / elapsed;
            _canFramesAtLastRate = CanTotalFrames;
            _canRateClock.Restart();
        }

        if (monitor is ReplayCanBusMonitor { IsFinished: true } && applied == 0 && IsCanMonitoring)
            CanStatusText = $"Replay finished: {CanTotalFrames:N0} frames.";

        OnPropertyChanged(nameof(CanCountersText));
        return applied;
    }

    private void InsertCanRowSorted(CanBusRowViewModel row)
    {
        var i = 0;
        while (i < CanRows.Count && CanRows[i].SortKey.CompareTo(row.SortKey) < 0)
            i++;
        CanRows.Insert(i, row);
    }

    partial void OnCanFilterTextChanged(string value)
    {
        CanRows.Clear();
        foreach (var row in _canRowsByKey.Values.Where(r => CanBusTable.Matches(r.Entry, value)).OrderBy(r => r.SortKey))
            CanRows.Add(row);
        OnPropertyChanged(nameof(CanCountersText));
    }

    [RelayCommand]
    public void ClearCanTable()
    {
        _canTable.Clear();
        _canRowsByKey.Clear();
        CanRows.Clear();
        CanTotalFrames = 0;
        CanFramesPerSecond = 0;
        _canFramesAtLastRate = 0;
        OnPropertyChanged(nameof(CanCountersText));
    }

    // ---------------------------------------------------------------------------------------------
    // Record / export / save to work order
    // ---------------------------------------------------------------------------------------------

    [RelayCommand]
    public void ToggleCanRecording()
    {
        if (IsCanRecording)
        {
            IsCanRecording = false;
            CanStatusText = $"Recorded {_canRecording.Count:N0} frames. Save it to the work order or export it.";
            return;
        }

        if (!IsCanMonitoring)
        {
            CanStatusText = "Start the monitor first, then press Record.";
            return;
        }

        _canRecording.Clear();
        _canRecordingStartedAt = DateTimeOffset.Now;
        CanRecordedFrameCount = 0;
        IsCanRecording = true;
        CanStatusText = "Recording…";
    }

    partial void OnIsCanRecordingChanged(bool value) => OnPropertyChanged(nameof(CanRecordButtonText));

    partial void OnCanRecordedFrameCountChanged(int value) => OnPropertyChanged(nameof(CanRecordButtonText));

    partial void OnIsCanMonitoringChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSimulatedBadgeVisibility));
        OnPropertyChanged(nameof(CanRealBadgeVisibility));
    }

    partial void OnIsCanSimulatedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSimulatedBadgeVisibility));
        OnPropertyChanged(nameof(CanRealBadgeVisibility));
    }

    /// <summary>True when every recorded frame is simulated (the export and attachment say so).</summary>
    private bool IsCanRecordingSimulated => _canRecording.Count > 0 && _canRecording.All(f => f.IsSimulated);

    /// <summary>Writes the recording to the captures folder as <c>.asc</c> or <c>.csv</c>. Returns the full path, or null.</summary>
    [RelayCommand]
    public async Task<string?> ExportCanCaptureAsync(string? format)
    {
        var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        if (_canRecording.Count == 0)
        {
            CanStatusText = "Nothing recorded yet. Start the monitor and press Record.";
            return null;
        }

        var frames = _canRecording.ToArray();
        var simulated = IsCanRecordingSimulated;
        var stamp = _canRecordingStartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"can-{stamp}{(simulated ? "-SIM" : "")}.{(csv ? "csv" : "asc")}";
        var path = Path.Combine(GetCanCaptureDirectory(), fileName);
        var started = _canRecordingStartedAt;
        var title = CanMonitorName;

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(path, append: false);
            if (csv)
                CsvCanLog.Write(writer, frames);
            else
                AscCanLog.Write(writer, frames, started, simulated, title);
        });

        CanStatusText = $"Exported {frames.Length:N0} frames to {path}";
        RefreshSavedCanCaptures();
        return path;
    }

    /// <summary>Saves the recording as <c>.asc</c> and attaches it to the open work order.</summary>
    [RelayCommand]
    public async Task<Attachment?> SaveCanCaptureToWorkOrderAsync()
    {
        if (ActiveWorkOrder is not WorkOrder wo)
        {
            CanStatusText = "Open a work order first. The capture is saved to the open work order.";
            return null;
        }
        if (_canRecording.Count == 0)
        {
            CanStatusText = "Nothing recorded yet. Start the monitor and press Record.";
            return null;
        }

        var path = await ExportCanCaptureAsync("asc");
        if (path is null)
            return null;

        var simulated = IsCanRecordingSimulated;
        var info = new FileInfo(path);
        var attachment = new Attachment
        {
            Scope = NoteScope.WorkOrder,
            TargetId = wo.Id,
            Kind = AttachmentKind.Recording,
            FilePath = Path.GetRelativePath(GetScopeAppDataDirectory(), path),
            MimeType = "text/plain",
            SizeBytes = info.Exists ? info.Length : null,
            TakenAt = _canRecordingStartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            Caption = string.Create(CultureInfo.InvariantCulture,
                $"{(simulated ? "SIMULATED " : "")}CAN capture: {_canRecording.Count:N0} frames, {SelectedCanBitrate / 1000} kbit/s, {CanMonitorName}"),
        };

        try
        {
            await _dataServices.Attachments.AddAsync(attachment);
        }
        catch (Exception ex)
        {
            CanStatusText = $"Saved the file but could not attach it to the work order: {ex.Message}";
            return null;
        }

        CanStatusText = $"Saved to {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo)}: {attachment.Caption}";
        return attachment;
    }

    public string GetCanCaptureDirectory() => Path.Combine(GetScopeAppDataDirectory(), "captures", "can");

    public void RefreshSavedCanCaptures()
    {
        var keep = SelectedSavedCanCapture;
        SavedCanCaptures.Clear();
        var dir = GetCanCaptureDirectory();
        if (Directory.Exists(dir))
        {
            foreach (var file in new DirectoryInfo(dir).GetFiles("*.asc").OrderByDescending(f => f.LastWriteTimeUtc))
                SavedCanCaptures.Add(file.Name);
        }
        SelectedSavedCanCapture = keep is not null && SavedCanCaptures.Contains(keep) ? keep : SavedCanCaptures.FirstOrDefault();
    }

    // ---------------------------------------------------------------------------------------------
    // Module frame strip (CAN pages report the CAN monitor)
    // ---------------------------------------------------------------------------------------------

    private string CanModuleConnectionText => !IsCanMonitoring
        ? "No CAN Connection"
        : IsCanSimulated
            ? $"CAN monitoring (SIMULATED) · {CanMonitorName}"
            : $"CAN monitoring (REAL HARDWARE, listen-only) · {CanMonitorName}";

    private async Task DisposeCanMonitorAsync() => await StopCanMonitorAsync();
}

/// <summary>One bus monitor row (one CAN id).</summary>
public sealed class CanBusRowViewModel : ObservableObject
{
    private readonly Func<int, DtcLibraryEntry?>? _proprietarySpnLookup;

    public CanBusRowViewModel(CanBusEntry entry, Func<int, DtcLibraryEntry?>? proprietarySpnLookup = null)
    {
        Entry = entry;
        _proprietarySpnLookup = proprietarySpnLookup;
    }

    public CanBusEntry Entry { get; }

    /// <summary>11-bit ids sort before 29-bit, then by id.</summary>
    public (int, uint) SortKey => (Entry.IsExtended ? 1 : 0, Entry.Id);

    public string IdText => Entry.IdText;

    public string FrameKind => Entry.IsExtended ? "29-bit" : "11-bit";

    public string PgnText => Entry.Pgn is { } pgn
        ? string.Create(CultureInfo.InvariantCulture, $"{pgn} ({pgn:X4}){(Entry.PgnLabel is { } l ? " " + l : "")}")
        : "—";

    public string SourceText => Entry.SourceAddress is { } sa ? $"SA {sa:X2}" : "—";

    public string DlcText => Entry.IsRemote ? "R" : Entry.Data.Length.ToString(CultureInfo.InvariantCulture);

    public string DataText => Entry.DataText;

    /// <summary>Data bytes, with a "*" after each byte that changed in the latest frame.</summary>
    public string DataWithChangesText => Entry.IsRemote
        ? "(remote)"
        : string.Join(' ', Entry.Data.Select((b, i) =>
            b.ToString("X2", CultureInfo.InvariantCulture) + (((Entry.ChangedMask >> i) & 1) != 0 ? "*" : "")));

    public string CountText => Entry.Count.ToString("N0", CultureInfo.InvariantCulture);

    public string PeriodText => Entry.PeriodSeconds is { } p
        ? (p * 1000).ToString(p < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + " ms"
        : "—";

    public string DecodedText => Entry.DiagnosticMessage is { } dm
        ? DiagnosticText(dm)
        : Entry.Decoded.Count == 0
            ? ""
            : string.Join("  ·  ", Entry.Decoded.Select(v =>
                string.Create(CultureInfo.InvariantCulture, $"{v.Name} {v.Value:0.##} {v.Unit}")));

    /// <summary>
    /// DM1/DM2 text: lamps, then each DTC as SPN/FMI/OC. A proprietary SPN that the PCS guides list is
    /// named with its PCS code and flagged as valid only for a PCS TCM (proprietary SPNs are maker-defined).
    /// </summary>
    private string DiagnosticText(J1939DmMessage dm)
    {
        var lamps = new List<string>(4);
        if (dm.Lamps.MalfunctionIndicatorLamp) lamps.Add("MIL");
        if (dm.Lamps.RedStopLamp) lamps.Add("Red stop");
        if (dm.Lamps.AmberWarningLamp) lamps.Add("Amber");
        if (dm.Lamps.ProtectLamp) lamps.Add("Protect");
        var head = (dm.IsPreviouslyActive ? "Stored" : "Active") + " faults: " + dm.Dtcs.Count.ToString(CultureInfo.InvariantCulture)
            + (lamps.Count > 0 ? " · lamps " + string.Join("/", lamps) : "");
        var parts = new List<string> { head };
        foreach (var d in dm.Dtcs)
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"SPN {d.Spn} FMI {d.Fmi} OC {d.OccurrenceCount}");
            if (DtcCodes.IsProprietarySpn(d.Spn) && _proprietarySpnLookup?.Invoke(d.Spn) is { } match)
                text += $" = {match.Code} {StripPcsPrefix(match.Description)} (if PCS TCM)";
            parts.Add(text);
        }
        return string.Join("  ·  ", parts);
    }

    private static string StripPcsPrefix(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return "";
        var dash = description.IndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 ? description[(dash + 3)..] : description;
    }

    public Visibility SimulatedBadgeVisibility => Entry.IsSimulated ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
