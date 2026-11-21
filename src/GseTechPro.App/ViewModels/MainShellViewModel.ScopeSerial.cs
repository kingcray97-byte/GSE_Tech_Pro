using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Can;
using GseTechPro.Devices.Protocols.J1939;
using GseTechPro.Scope;

namespace GseTechPro.App.ViewModels;

/// <summary>One decoded CAN frame row in the serial decoding table (display strings only).</summary>
public sealed record ScopeSerialFrameRowViewModel(
    int Number,
    string Time,
    string Identifier,
    string J1939,
    string Dlc,
    string Data,
    string Decoded,
    string Status,
    bool IsError)
{
    /// <summary>Errors and missing ACKs stand out in bold (not by colour alone, for sunlight and colour-blind use).</summary>
    public Windows.UI.Text.FontWeight StatusWeight => IsError ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    public Microsoft.UI.Xaml.Visibility DecodedVisibility =>
        string.IsNullOrEmpty(Decoded) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}

/// <summary>
/// Phase 10D.1 — serial decoding on the scope screen, CAN first: frames decoded from the trace on screen (CAN-H,
/// CAN-L, or CAN-H − CAN-L on two channels) at the bit rate the technician picks, each 29-bit identifier labelled with
/// its J1939 PGN, source address and priority and, for PGNs our J1939 code knows, the SAE J1939-71 values — the same
/// decoder the CAN bus monitor uses. Errors (stuff, CRC, form, missing ACK) are listed, not hidden: that is often
/// the fault being looked for. Computed off the UI thread only while switched on.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Picker entry for the wire being read.</summary>
    public sealed record CanSourceOption(CanSignalSource Source, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>Picker entry for the bit rate.</summary>
    public sealed record CanBitRateOption(int BitRate, string Display)
    {
        public override string ToString() => Display;
    }

    public IReadOnlyList<CanSourceOption> ScopeSerialSources { get; } = new[]
    {
        new CanSourceOption(CanSignalSource.Differential, "CAN-H − CAN-L (two channels)"),
        new CanSourceOption(CanSignalSource.CanHigh, "CAN-H only"),
        new CanSourceOption(CanSignalSource.CanLow, "CAN-L only"),
    };

    public IReadOnlyList<CanBitRateOption> ScopeSerialBitRates { get; } = CanDecodeSettings.StandardBitRates
        .Select(r => new CanBitRateOption(r, r switch
        {
            250_000 => "250 kbit/s (J1939-11/-15)",
            500_000 => "500 kbit/s (J1939-14)",
            _ => string.Create(CultureInfo.InvariantCulture, $"{r / 1000} kbit/s"),
        }))
        .ToArray();

    public IReadOnlyList<int> ScopeSerialChannels { get; } = new[] { 1, 2, 3, 4 };

    [ObservableProperty]
    private bool _isScopeSerialEnabled;

    [ObservableProperty]
    private CanSourceOption? _selectedScopeSerialSource;

    /// <summary>Bit rate of the bus; null until the technician picks it (it is a property of the network, not of the trace).</summary>
    [ObservableProperty]
    private CanBitRateOption? _selectedScopeSerialBitRate;

    /// <summary>CAN-H channel (or the only channel for CAN-H / CAN-L only).</summary>
    [ObservableProperty]
    private int _scopeSerialChannel = 1;

    /// <summary>CAN-L channel for the differential source.</summary>
    [ObservableProperty]
    private int _scopeSerialSecondChannel = 2;

    [ObservableProperty]
    private bool _scopeSerialErrorsOnly;

    [ObservableProperty]
    private string _scopeSerialStatusText = "Serial decoding off.";

    [ObservableProperty]
    private IReadOnlyList<ScopeSerialFrameRowViewModel> _scopeSerialRows = Array.Empty<ScopeSerialFrameRowViewModel>();

    public CanDecodeResult? ScopeSerialResult { get; private set; }

    public Microsoft.UI.Xaml.Visibility ScopeSerialSecondChannelVisibility =>
        (SelectedScopeSerialSource?.Source ?? CanSignalSource.Differential) == CanSignalSource.Differential
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string ScopeSerialChannelHeader => (SelectedScopeSerialSource?.Source ?? CanSignalSource.Differential) switch
    {
        CanSignalSource.CanLow => "CAN-L channel (1 = A … 4 = D)",
        _ => "CAN-H channel (1 = A … 4 = D)",
    };

    partial void OnIsScopeSerialEnabledChanged(bool value) => _ = RefreshScopeSerialAsync();
    partial void OnSelectedScopeSerialSourceChanged(CanSourceOption? value)
    {
        OnPropertyChanged(nameof(ScopeSerialSecondChannelVisibility));
        OnPropertyChanged(nameof(ScopeSerialChannelHeader));
        _ = RefreshScopeSerialAsync();
    }
    partial void OnSelectedScopeSerialBitRateChanged(CanBitRateOption? value)
    {
        SetUpScopeForCanCommand.NotifyCanExecuteChanged();
        _ = RefreshScopeSerialAsync();
    }
    partial void OnScopeSerialChannelChanged(int value) => _ = RefreshScopeSerialAsync();
    partial void OnScopeSerialSecondChannelChanged(int value) => _ = RefreshScopeSerialAsync();
    partial void OnScopeSerialErrorsOnlyChanged(bool value) => ApplyScopeSerialRows();

    private bool CanSetUpScopeForCan() => SelectedScopeSerialBitRate is not null;

    /// <summary>
    /// Sets the scope up to decode the chosen bit rate: the CAN channel(s) on, DC, ±5 V, no trigger, at least 10 samples
    /// per bit, and a timebase that holds several full frames. Scope settings only; it does not touch the bus.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSetUpScopeForCan))]
    public void SetUpScopeForCan()
    {
        if (SelectedScopeSerialBitRate is not { } rate)
            return;
        var source = SelectedScopeSerialSource?.Source ?? CanSignalSource.Differential;
        SelectedScopeSerialSource ??= ScopeSerialSources[0];
        var wanted = source == CanSignalSource.Differential
            ? new[] { ScopeSerialChannel, ScopeSerialSecondChannel }
            : new[] { ScopeSerialChannel };
        foreach (var tile in ScopeChannels)
        {
            if (!wanted.Contains(tile.Channel)) continue;
            tile.IsEnabled = true;
            tile.VoltageRangeVolts = 5.0;
        }

        // Slowest standard rate giving ≥ 10 samples per bit, then the shortest standard timebase whose 10 divisions hold
        // at least 8 of the longest frames (about 135 bits each), within 1 M samples.
        int minRate = rate.BitRate * 10;
        int sampleRate = ScopeSampleRateOptions.Select(o => o.SampleRateHz).Where(r => r >= minRate).DefaultIfEmpty(0).Min();
        if (sampleRate == 0)
        {
            ScopeSerialStatusText = $"This scope's fastest sample rate is too slow for {rate.Display}.";
            return;
        }
        double window = 8 * 135.0 / rate.BitRate;
        double timebase = ScopeTimebaseOptions.Select(o => o.SecondsPerDivision)
            .Where(tb => tb * 10 >= window && tb * 10 * sampleRate <= 1_000_000)
            .DefaultIfEmpty(ScopeTimebaseOptions.Select(o => o.SecondsPerDivision).Where(tb => tb * 10 * sampleRate <= 1_000_000).DefaultIfEmpty(ScopeTimebaseOptions[0].SecondsPerDivision).Max())
            .Min();

        ScopeSampleRateHz = sampleRate;
        ScopeTimebaseSecondsPerDivision = timebase;
        ScopeTriggerMode = ScopeTriggerMode.None;
        IsScopeSerialEnabled = true;
        ScopeSerialStatusText = string.Create(CultureInfo.InvariantCulture,
            $"Scope set for {rate.Display}: {sampleRate / 1e6:0.#} MS/s, {timebase * 1e6:0} µs/div, no trigger. Press Run.");
    }

    /// <summary>Decodes the capture on screen (off the UI thread).</summary>
    public async Task RefreshScopeSerialAsync()
    {
        SelectedScopeSerialSource ??= ScopeSerialSources[0];
        var capture = LastScopeCapture;
        if (!IsScopeSerialEnabled)
        {
            ScopeSerialResult = null;
            ScopeSerialStatusText = "Serial decoding off.";
            ApplyScopeSerialRows();
            return;
        }
        if (SelectedScopeSerialBitRate is not { } rate)
        {
            ScopeSerialResult = null;
            ScopeSerialStatusText = "Choose the bus bit rate (from the vehicle or network documentation).";
            ApplyScopeSerialRows();
            return;
        }
        if (capture is null)
        {
            ScopeSerialResult = null;
            ScopeSerialStatusText = "Serial decoding on: press Run to decode.";
            ApplyScopeSerialRows();
            return;
        }

        var settings = new CanDecodeSettings(ScopeSerialChannel, SelectedScopeSerialSource.Source, rate.BitRate, ScopeSerialSecondChannel);
        var result = await Task.Run(() => CanSerialDecoder.Decode(capture, settings));
        if (!ReferenceEquals(capture, LastScopeCapture) || !IsScopeSerialEnabled
            || settings != new CanDecodeSettings(ScopeSerialChannel, SelectedScopeSerialSource.Source,
                SelectedScopeSerialBitRate?.BitRate ?? 0, ScopeSerialSecondChannel))
            return; // superseded while decoding

        ScopeSerialResult = result;
        string provenance = result.IsSimulated ? " (simulated)" : "";
        if (!result.IsOk)
        {
            ScopeSerialStatusText = result.Error + provenance;
        }
        else
        {
            int errors = result.ErrorFrameCount;
            int noAck = result.Frames.Count(f => f.IsOk && !f.Acknowledged);
            ScopeSerialStatusText = $"{result.Frames.Count} frames · {errors} with errors"
                + (noAck > 0 ? $" · {noAck} not acknowledged" : "")
                + (result.Frames.Count >= CanSerialDecoder.MaxFrames ? " (first frames only; use a shorter timebase)" : "")
                + provenance;
        }
        ApplyScopeSerialRows();
    }

    private void ApplyScopeSerialRows()
    {
        var result = ScopeSerialResult;
        IReadOnlyList<ScopeSerialFrameRowViewModel> rows = Array.Empty<ScopeSerialFrameRowViewModel>();
        if (result is { IsOk: true })
        {
            rows = result.Frames
                .Where(f => !ScopeSerialErrorsOnly || !f.IsOk || !f.Acknowledged)
                .Select(ToSerialRow)
                .ToArray();
        }

        void ApplyNow() => ScopeSerialRows = rows;
        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            _dispatcherQueue.TryEnqueue(ApplyNow);
        else
            ApplyNow();
    }

    public static ScopeSerialFrameRowViewModel ToSerialRow(ScopeCanFrame f)
    {
        var ci = CultureInfo.InvariantCulture;
        string j1939 = "";
        string decoded = "";
        if (f.IsExtended)
        {
            var id = J1939Identifier.Parse(f.Identifier);
            string name = J1939PgnNames.Label(id.Pgn) is { } label ? $"{label} " : "";
            j1939 = string.Create(ci, $"{name}PGN {id.Pgn} · SA {id.Sa:X2} · P{id.Priority}");
            if (f.IsOk && !f.IsRemote && J1939LiveData.IsKnownPgn(id.Pgn))
            {
                decoded = string.Join("  ·  ", J1939LiveData.Extract(id.Pgn, f.Data)
                    .Where(v => v.Known)
                    .Select(v => string.Create(ci, $"{v.Name} {v.Value:0.##} {v.Unit}")));
            }
        }

        string status = f.Error ?? (f.Acknowledged ? "OK" : "OK · no ACK");
        return new ScopeSerialFrameRowViewModel(
            f.Number,
            FormatDeep(f.StartSeconds, CycleMeasureKind.Period),
            f.IdentifierHex,
            j1939,
            f.IsRemote ? $"{f.Dlc} (remote)" : f.Dlc.ToString(ci),
            f.IsOk ? (f.IsRemote ? "" : f.DataHex) : "",
            decoded,
            status,
            !f.IsOk || !f.Acknowledged);
    }
}
