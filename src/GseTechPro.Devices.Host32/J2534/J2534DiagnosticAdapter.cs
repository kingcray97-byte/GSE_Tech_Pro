using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Devices.Discovery;
using GseTechPro.Devices.Ipc;
using GseTechPro.Devices.Protocols;
using GseTechPro.Devices.Protocols.IsoTp;
using GseTechPro.Devices.Protocols.J1939;
using GseTechPro.Devices.Protocols.Obd2;
using GseTechPro.Devices.Protocols.Uds;

namespace GseTechPro.Devices.Host32.J2534;

/// <summary>
/// <see cref="IDiagnosticAdapter"/> backed by a vendor J2534 pass-thru DLL (loaded only in the x86
/// host). Raw frames returned by the driver are decoded by the pure <c>GseTechPro.Devices.Protocols</c>
/// decoders. The bus request/response byte flows follow the published standards (J1939-21/-73,
/// J1979/ISO 15031-5, ISO 15765-2, ISO 14229-1); the exact on-the-wire sequence against a specific
/// vehicle is verified on hardware (RLink X3), not asserted here.
/// </summary>
public sealed class J2534DiagnosticAdapter : IDiagnosticAdapter
{
    /// <summary>J1939 source address used by the tool ("Off-Board Diagnostics #1", J1939-81).</summary>
    private const byte ToolSourceAddress = 0xF9;

    private readonly J2534PassThru _passThru;
    private readonly J2534DeviceDescriptor _descriptor;
    private readonly ProtocolType _protocol;
    private readonly J1939TransportProtocol _transport = new();

    private uint _deviceId;
    private uint _channelId;
    private bool _connected;
    private bool _released;

    private static readonly ProtocolType[] Supported = { ProtocolType.J1939, ProtocolType.J1708, ProtocolType.OBD2, ProtocolType.UDS };

    internal J2534DiagnosticAdapter(J2534PassThru passThru, J2534DeviceDescriptor descriptor, ProtocolType protocol)
    {
        _passThru = passThru;
        _descriptor = descriptor;
        _protocol = protocol;
    }

    public string Name => _descriptor.Name;

    public bool IsConnected => _connected;

    public bool IsSimulated => false;

    public IReadOnlyList<ProtocolType> SupportedProtocols => Supported;

    // Streaming is not implemented for real J2534 hardware in this round; the event exists to satisfy
    // IDiagnosticAdapter and will be raised once a live-data push path is added.
#pragma warning disable CS0067
    public event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived;
#pragma warning restore CS0067

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connected)
            return Task.CompletedTask;

        var (protocolId, flags, baudrate) = MapProtocol(_protocol);
        _deviceId = _passThru.OpenDevice();
        try
        {
            _channelId = _passThru.Connect(_deviceId, protocolId, flags, baudrate);
        }
        catch
        {
            _passThru.CloseDevice(_deviceId);
            throw;
        }

        try
        {
            _passThru.StartPassFilter(_channelId);
        }
        catch (J2534Exception)
        {
            // Filtering is best-effort; some devices reject an empty pass filter. Continue without it.
        }

        _transport.Reset();
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        if (!_connected)
            return;
        _connected = false;

        try { _passThru.Disconnect(_channelId); } catch { }
        try { _passThru.CloseDevice(_deviceId); } catch { }
        _transport.Reset();
        Release();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Release()
    {
        if (_released)
            return;
        _released = true;
        _passThru.Dispose(); // unloads the vendor DLL (adapter is single-use: connect → disconnect)
    }

    public async Task<IReadOnlyList<DtcReadout>> ReadDTCsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return _protocol switch
        {
            ProtocolType.J1939 => await ReadJ1939DTCsAsync(cancellationToken).ConfigureAwait(false),
            ProtocolType.OBD2 => await ReadObd2DTCsAsync(cancellationToken).ConfigureAwait(false),
            ProtocolType.UDS => await ReadUdsDTCsAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new HostIpcException(HostErrorCodes.Unsupported, $"DTC read is not implemented for '{_protocol}'."),
        };
    }

    public async Task ClearDTCsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        switch (_protocol)
        {
            case ProtocolType.J1939:
                await SendJ1939Async(J1939Pgn.Dm11, J1939Dm11.Encode(), cancellationToken).ConfigureAwait(false);
                break;
            case ProtocolType.OBD2:
                await SendAndDrainAsync(Obd2Decoder.BuildRequest(Obd2Decoder.Mode04), cancellationToken).ConfigureAwait(false);
                break;
            case ProtocolType.UDS:
                await SendAndDrainAsync(UdsDecoder.EncodeClearDiagnosticInformation(), cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new HostIpcException(HostErrorCodes.Unsupported, $"DTC clear is not implemented for '{_protocol}'.");
        }
    }

    public async Task<IReadOnlyList<PidValue>> ReadLiveDataAsync(IReadOnlyList<string> pidNames, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return _protocol switch
        {
            ProtocolType.J1939 => await ReadJ1939LiveDataAsync(cancellationToken).ConfigureAwait(false),
            ProtocolType.OBD2 => await ReadObd2LiveDataAsync(cancellationToken).ConfigureAwait(false),
            _ => Array.Empty<PidValue>(),
        };
    }

    public async Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_protocol != ProtocolType.OBD2)
            return null;

        // Mode $02 returns the freeze frame with the same PID scaling as mode $01 (ISO 15031-5).
        var values = await ReadObd2PidsAsync(Obd2Decoder.Mode02, DefaultObd2Pids, cancellationToken).ConfigureAwait(false);
        return new FreezeFrameData { CapturedAt = Clock.UtcNow, Pids = values };
    }

    public async Task<VehicleInfo> GetVehicleInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_protocol == ProtocolType.OBD2)
        {
            var response = await SendAndReceiveAsync(Obd2Decoder.BuildRequest(Obd2Decoder.Mode09, 0x02), cancellationToken).ConfigureAwait(false);
            return new VehicleInfo { Vin = Obd2Decoder.DecodeVin(response), Protocol = _protocol, Ecus = Array.Empty<EcuInfo>() };
        }

        // J1939 VIN is obtained via the Vehicle Identification message (PGN 65260) on request; that is
        // hardware-verified later. Return an empty identity rather than fabricating one.
        return new VehicleInfo { Vin = null, Protocol = _protocol, Ecus = Array.Empty<EcuInfo>() };
    }

    // ---- J1939 ----

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

    private async Task<IReadOnlyList<PidValue>> ReadJ1939LiveDataAsync(CancellationToken cancellationToken)
    {
        var pgns = new[]
        {
            J1939Pgn.ElectronicEngineController1,
            J1939Pgn.ElectronicEngineController2,
            J1939Pgn.EngineTemperature1,
            J1939Pgn.EngineFluidLevelPressure1,
            J1939Pgn.CruiseControlVehicleSpeed,
        };

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

    private Task SendPgnRequestAsync(uint pgn, CancellationToken cancellationToken)
    {
        var id = J1939PgnRequest.BuildId(destinationAddress: 0xFF, sourceAddress: ToolSourceAddress);
        var data = J1939PgnRequest.Encode(pgn);
        WriteCanFrame(id, data);
        return Task.CompletedTask;
    }

    private Task SendJ1939Async(uint pgn, byte[] payload, CancellationToken cancellationToken)
    {
        // DM11 (and DM3) are PDU2 messages; the destination is broadcast (0xFF).
        var id = J1939Identifier.Build(priority: 6, edp: 0, dp: 0, pf: (pgn >> 8) & 0xFF, ps: 0xFF, sa: ToolSourceAddress);
        WriteCanFrame(id, payload);
        return Task.CompletedTask;
    }

    private void WriteCanFrame(uint id, ReadOnlySpan<byte> data)
    {
        var message = PassThruMsg.Create();
        message.ProtocolID = J2534Protocol.Can;
        message.TxFlags = J2534TxFlags.Can29BitId;
        // SAE J2534-1 §6.4.2/§7.2.3.1: the 29-bit CAN id is big-endian in Data[0..3], payload in
        // Data[4..], DataSize = 4 + payloadLength.
        message.DataSize = (uint)(CanFraming.IdByteCount + data.Length);
        CanFraming.WriteId(id, message.Data.AsSpan(0, CanFraming.IdByteCount));
        data.CopyTo(message.Data.AsSpan(CanFraming.IdByteCount));
        _passThru.WriteMessages(_channelId, new[] { message }, 1000);
    }

    private async Task<IReadOnlyList<J1939Reassembled>> ReadJ1939ForAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var completed = new List<J1939Reassembled>();
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var messages = _passThru.ReadMessages(_channelId, 16, 100);
            if (messages.Count == 0)
                continue;
            foreach (var message in messages)
            {
                if (!TryReadCanFrame(message, out var id, out var data))
                    continue;
                var reassembled = _transport.Feed(id, data);
                if (reassembled is { } complete)
                    completed.Add(complete);
            }
        }
        return completed;
    }

    private async Task<IReadOnlyList<RawFrame>> ReadJ1939FramesAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var frames = new List<RawFrame>();
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var messages = _passThru.ReadMessages(_channelId, 16, 100);
            if (messages.Count == 0)
                continue;
            foreach (var message in messages)
            {
                if (!TryReadCanFrame(message, out var id, out var data))
                    continue;
                frames.Add(new RawFrame(id, data.ToArray()));
            }
        }
        return frames;
    }

    /// <summary>Extracts the big-endian CAN id and payload from a received <see cref="PassThruMsg"/>.</summary>
    private static bool TryReadCanFrame(PassThruMsg message, out uint id, out ReadOnlySpan<byte> data)
    {
        id = 0;
        data = default;
        if (message.DataSize < CanFraming.IdByteCount)
            return false;
        id = CanFraming.ReadId(message.Data.AsSpan(0, CanFraming.IdByteCount));
        data = message.Data.AsSpan(CanFraming.IdByteCount, (int)message.DataSize - CanFraming.IdByteCount);
        return true;
    }

    // ---- OBD-II ----

    private async Task<IReadOnlyList<DtcReadout>> ReadObd2DTCsAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(Obd2Decoder.BuildRequest(Obd2Decoder.Mode03), cancellationToken).ConfigureAwait(false);
        return Obd2Decoder.DecodeMode03Response(response);
    }

    private async Task<IReadOnlyList<PidValue>> ReadObd2LiveDataAsync(CancellationToken cancellationToken)
    {
        var values = await ReadObd2PidsAsync(Obd2Decoder.Mode01, DefaultObd2Pids, cancellationToken).ConfigureAwait(false);
        return values;
    }

    private static readonly byte[] DefaultObd2Pids = { 0x0C, 0x05, 0x0D, 0x0B, 0x11, 0x42 };

    private async Task<IReadOnlyList<PidValue>> ReadObd2PidsAsync(byte mode, byte[] pids, CancellationToken cancellationToken)
    {
        var values = new List<PidValue>();
        foreach (var pid in pids)
        {
            var response = await SendAndReceiveAsync(Obd2Decoder.BuildRequest(mode, pid), cancellationToken).ConfigureAwait(false);
            var reading = Obd2Decoder.DecodePidResponse(response);
            if (reading.Known)
                values.Add(new PidValue { Name = reading.Name, Value = reading.Value, Unit = reading.Unit, Timestamp = Clock.UtcNow, IsSimulated = false });
        }
        return values;
    }

    // ---- UDS ----

    private async Task<IReadOnlyList<DtcReadout>> ReadUdsDTCsAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(UdsDecoder.EncodeReadDtcInformation(), cancellationToken).ConfigureAwait(false);
        return UdsDecoder.DecodeReadDtcInformation(response);
    }

    // ---- ISO 15765 (manual) over raw CAN ----

    private async Task<byte[]> SendAndReceiveAsync(byte[] request, CancellationToken cancellationToken)
    {
        var txFrames = IsoTpEncoder.Encode(request);
        foreach (var frame in txFrames)
            WriteCanFrameIsoTp(RequestCanId, frame);

        var decoder = new IsoTpDecoder();
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var messages = _passThru.ReadMessages(_channelId, 8, 100);
            foreach (var message in messages)
            {
                if (!TryReadCanFrame(message, out var id, out var payload))
                    continue;
                if (id != ResponseCanId)
                    continue;
                var decoded = decoder.Feed(payload);
                if (decoded is not null)
                    return decoded;
            }
        }
        return Array.Empty<byte>();
    }

    private async Task SendAndDrainAsync(byte[] request, CancellationToken cancellationToken)
    {
        _ = await SendAndReceiveAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private const uint RequestCanId = 0x7E0;  // OBD-II functional/physical request id (11-bit)
    private const uint ResponseCanId = 0x7E8; // OBD-II primary ECU response id (11-bit)

    private void WriteCanFrameIsoTp(uint canId, ReadOnlySpan<byte> data)
    {
        var message = PassThruMsg.Create();
        message.ProtocolID = J2534Protocol.Can;
        message.TxFlags = 0; // 11-bit id (no CAN_29BIT_ID flag)
        // SAE J2534-1 §6.4.2/§7.2.3.1: the CAN id is big-endian in Data[0..3], payload in Data[4..].
        message.DataSize = (uint)(CanFraming.IdByteCount + data.Length);
        CanFraming.WriteId(canId, message.Data.AsSpan(0, CanFraming.IdByteCount));
        data.CopyTo(message.Data.AsSpan(CanFraming.IdByteCount));
        _passThru.WriteMessages(_channelId, new[] { message }, 1000);
    }

    private static (uint ProtocolId, uint Flags, uint Baudrate) MapProtocol(ProtocolType protocol) => protocol switch
    {
        // PassThruConnect's Flags parameter (Table 9) carries CAN_29BIT_ID for a 29-bit J1939 channel;
        // the per-message TxFlags/RxStatus also carry CAN_29BIT_ID (bit #8) on each message.
        ProtocolType.J1939 => (J2534Protocol.Can, J2534TxFlags.Can29BitId, 250_000),
        ProtocolType.OBD2 => (J2534Protocol.Can, 0, 500_000),
        ProtocolType.UDS => (J2534Protocol.Can, 0, 500_000),
        ProtocolType.J1708 => (J2534Protocol.SciAEngine, 0, 9_600),
        _ => throw new HostIpcException(HostErrorCodes.Unsupported, $"No J2534 protocol mapping for '{protocol}'."),
    };

    private void EnsureConnected()
    {
        if (!_connected)
            throw new InvalidOperationException("Not connected to a J2534 device.");
    }

    private readonly record struct RawFrame(uint Id, byte[] Data);
}
