using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Devices.Discovery;
using GseTechPro.Devices.Ipc;
using GseTechPro.Devices.Protocols;
using GseTechPro.Devices.Protocols.J1708;
using GseTechPro.Devices.Protocols.J1939;

namespace GseTechPro.Devices.Host32.Rp1210;

/// <summary>
/// <see cref="IDiagnosticAdapter"/> backed by a vendor RP1210 API DLL (loaded only in the x86 host).
/// Raw messages returned by the driver are decoded by the pure <c>GseTechPro.Devices.Protocols</c>
/// decoders. The exact message byte framing and connect-status bitfield are vendor-specific and
/// confirmed on hardware (Nexiq USB-Link / Noregon DLA+), not asserted here.
/// </summary>
public sealed class Rp1210DiagnosticAdapter : IDiagnosticAdapter
{
    /// <summary>J1939 source address used by the tool.</summary>
    private const byte ToolSourceAddress = 0xF9;

    /// <summary>J1708 message priority prefix for the tool's requests ("all other messages", SAE J1708 Table 1).</summary>
    private const byte J1708RequestPriority = 7;

    /// <summary>J1708/J1587 engine #1 MID (the target of diagnostic-data requests).</summary>
    private const byte J1708EngineMid = 0x80;

    private readonly Rp1210Api _api;
    private readonly Rp1210VendorDescriptor _descriptor;
    private readonly ProtocolType _protocol;
    private readonly J1939TransportProtocol _transport = new();

    private short _clientId;
    private bool _connected;
    private bool _released;

    private static readonly ProtocolType[] Supported = { ProtocolType.J1939, ProtocolType.J1708, ProtocolType.OBD2 };

    internal Rp1210DiagnosticAdapter(Rp1210Api api, Rp1210VendorDescriptor descriptor, ProtocolType protocol)
    {
        _api = api;
        _descriptor = descriptor;
        _protocol = protocol;
    }

    public string Name => _descriptor.DeviceName;

    public bool IsConnected => _connected;

    public bool IsSimulated => false;

    public IReadOnlyList<ProtocolType> SupportedProtocols => Supported;

    // Streaming is not implemented for real RP1210 hardware in this round; the event exists to satisfy
    // IDiagnosticAdapter and will be raised once a live-data push path is added.
#pragma warning disable CS0067
    public event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived;
#pragma warning restore CS0067

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connected)
            return Task.CompletedTask;

        // The numeric device id comes from the vendor's own INI (e.g. Nexiq.ini); device 1 is the
        // conventional first device and is confirmed at hardware time.
        _clientId = _api.ClientConnect(deviceId: 1, protocol: Rp1210ProtocolString(_protocol), connectStatus: 0, bufferSize: 4096);
        _transport.Reset();
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        if (!_connected)
            return;
        _connected = false;
        try { _api.ClientDisconnect(_clientId); } catch { }
        _transport.Reset();
        Release();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Release()
    {
        if (_released)
            return;
        _released = true;
        _api.Dispose(); // unloads the vendor DLL (adapter is single-use: connect → disconnect)
    }

    public async Task<IReadOnlyList<DtcReadout>> ReadDTCsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return _protocol switch
        {
            ProtocolType.J1708 => await ReadJ1708DTCsAsync(cancellationToken).ConfigureAwait(false),
            ProtocolType.J1939 => await ReadJ1939DTCsAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new HostIpcException(HostErrorCodes.Unsupported, $"DTC read is not implemented for '{_protocol}' over RP1210."),
        };
    }

    public async Task ClearDTCsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        switch (_protocol)
        {
            case ProtocolType.J1708:
                // J1587 clear: request PID 194 (0xC2) with the clear command (0x01) to MID 128 (engine #1).
                _api.SendMessage(_clientId, J1708Request(0x01));
                break;
            case ProtocolType.J1939:
                await SendJ1939Async(J1939Pgn.Dm11, J1939Dm11.Encode(), cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new HostIpcException(HostErrorCodes.Unsupported, $"DTC clear is not implemented for '{_protocol}' over RP1210.");
        }
    }

    public async Task<IReadOnlyList<PidValue>> ReadLiveDataAsync(IReadOnlyList<string> pidNames, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_protocol != ProtocolType.J1939)
            return Array.Empty<PidValue>();

        var pgns = new[] { J1939Pgn.ElectronicEngineController1, J1939Pgn.EngineTemperature1, J1939Pgn.EngineFluidLevelPressure1 };
        var values = new List<PidValue>();
        foreach (var pgn in pgns)
        {
            await SendPgnRequestAsync(pgn, cancellationToken).ConfigureAwait(false);
            foreach (var frame in await ReadJ1939FramesAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false))
            {
                if (J1939Identifier.Parse(frame.Id).Pgn != pgn)
                    continue;
                foreach (var reading in J1939LiveData.Extract(pgn, frame.Data))
                    values.Add(new PidValue { Name = reading.Name, Value = reading.Value, Unit = reading.Unit, Timestamp = Clock.UtcNow, IsSimulated = false });
            }
        }
        return values;
    }

    public Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return Task.FromResult<FreezeFrameData?>(null); // RP1210 freeze-frame capture is not in this round
    }

    public Task<VehicleInfo> GetVehicleInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return Task.FromResult(new VehicleInfo { Vin = null, Protocol = _protocol, Ecus = Array.Empty<EcuInfo>() });
    }

    private async Task<IReadOnlyList<DtcReadout>> ReadJ1708DTCsAsync(CancellationToken cancellationToken)
    {
        // J1587 §3.8.3: request diagnostic data from MID 128 (engine #1) via PID 194 (0xC2), command 0x00 = all.
        _api.SendMessage(_clientId, J1708Request(0x00));

        var results = new List<DtcReadout>();
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1000);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var message = _api.ReadMessage(_clientId, 1024, blockOnRead: false);
            // RP1210_ReadMessage prepends a 4-byte timestamp: [ts(4)][MID][PID][data].
            var body = Rp1210Framing.StripTimestamp(message);
            if (body.Length < 3)
                continue;
            results.AddRange(J1587Decoder.DecodeDtcReport(body[0], body[1], body[2..]));
        }
        return results;
    }

    /// <summary>Builds a J1708 request message with the 1-byte priority prefix RP1210 requires.</summary>
    private static byte[] J1708Request(byte command) =>
        new byte[] { J1708RequestPriority, J1708EngineMid, J1587Decoder.PidDiagnosticData, command };

    private async Task<IReadOnlyList<DtcReadout>> ReadJ1939DTCsAsync(CancellationToken cancellationToken)
    {
        var results = new List<DtcReadout>();
        foreach (var pgn in new[] { J1939Pgn.Dm1, J1939Pgn.Dm2 })
        {
            await SendPgnRequestAsync(pgn, cancellationToken).ConfigureAwait(false);
            foreach (var reassembled in await ReadJ1939ForAsync(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false))
            {
                if (reassembled.Pgn != pgn)
                    continue;
                results.AddRange(J1939DtcCodec.ToReadouts(
                    J1939DtcCodec.DecodeDmMessage(reassembled.Data, isDm2: pgn == J1939Pgn.Dm2)));
            }
        }
        return results;
    }

    private Task SendPgnRequestAsync(uint pgn, CancellationToken cancellationToken)
    {
        var id = J1939PgnRequest.BuildId(destinationAddress: 0xFF, sourceAddress: ToolSourceAddress);
        var data = J1939PgnRequest.Encode(pgn);
        WriteCanFrame(id, data);
        return Task.CompletedTask;
    }

    private Task SendJ1939Async(uint pgn, byte[] payload, CancellationToken cancellationToken)
    {
        var id = J1939Identifier.Build(priority: 6, edp: 0, dp: 0, pf: (pgn >> 8) & 0xFF, ps: 0xFF, sa: ToolSourceAddress);
        WriteCanFrame(id, payload);
        return Task.CompletedTask;
    }

    private void WriteCanFrame(uint id, ReadOnlySpan<byte> data)
    {
        // RP1210 CAN framing (TMC RP1210C §10.3): [frame-type = 29-bit][id BE(4)][data].
        _api.SendMessage(_clientId, Rp1210Framing.BuildCan(id, data));
    }

    private async Task<IReadOnlyList<J1939Reassembled>> ReadJ1939ForAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var completed = new List<J1939Reassembled>();
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var message = _api.ReadMessage(_clientId, 1024, blockOnRead: false);
            if (!Rp1210Framing.TryParseCan(Rp1210Framing.StripTimestamp(message), out var id, out var data))
                continue;
            var reassembled = _transport.Feed(id, data);
            if (reassembled is { } complete)
                completed.Add(complete);
        }
        return completed;
    }

    private async Task<IReadOnlyList<RawFrame>> ReadJ1939FramesAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var frames = new List<RawFrame>();
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var message = _api.ReadMessage(_clientId, 1024, blockOnRead: false);
            if (!Rp1210Framing.TryParseCan(Rp1210Framing.StripTimestamp(message), out var id, out var data))
                continue;
            frames.Add(new RawFrame(id, data.ToArray()));
        }
        return frames;
    }

    private static string Rp1210ProtocolString(ProtocolType protocol) => protocol switch
    {
        // Prefer the "CAN" framing (TMC RP1210C §10.3) so the raw 29-bit CAN id feeds J1939TransportProtocol
        // for BAM/TP reassembly, rather than the "J1939" [PGN][How][SA][DA] framing.
        ProtocolType.J1939 => "CAN",
        ProtocolType.J1708 => "J1708",
        ProtocolType.OBD2 => "ISO15765",
        _ => throw new HostIpcException(HostErrorCodes.Unsupported, $"No RP1210 protocol string for '{protocol}'."),
    };

    private void EnsureConnected()
    {
        if (!_connected)
            throw new InvalidOperationException("Not connected to an RP1210 device.");
    }

    private readonly record struct RawFrame(uint Id, byte[] Data);
}
