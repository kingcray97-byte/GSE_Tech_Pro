using System.Buffers.Binary;
using System.Text;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices.Ipc;

/// <summary>
/// Kind-tagged framing for the app↔32-bit device-host named pipe (ARCHITECTURE.md §1.1.1, ADR-007):
/// a 1-byte frame-kind tag, then a 4-byte little-endian length prefix, then the payload
/// (<see cref="HostProtocol.MaxFrameBytes"/> cap). Kinds: <see cref="HostFrameKind.Json"/> (control),
/// <see cref="HostFrameKind.Binary"/> (raw CAN capture, Phase 7), <see cref="HostFrameKind.Scope"/>
/// (reserved, Phase 6).
///
/// This supersedes the Phase-1 <see cref="PipeMessage"/> JSON-only framing by adding the kind byte;
/// <see cref="PipeMessage"/> is kept for its existing callers/tests and still writes the older
/// length-prefix-only layout.
/// </summary>
public readonly record struct PipeFrame(byte Kind, byte[] Payload)
{
    /// <summary>Reads one complete frame (kind + length + payload) from the stream.</summary>
    /// <exception cref="EndOfStreamException">The pipe closed before a full frame arrived.</exception>
    /// <exception cref="InvalidDataException">The length prefix was invalid.</exception>
    public static Task<PipeFrame> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadCoreAsync(stream, ct);
    }

    private static async Task<PipeFrame> ReadCoreAsync(Stream stream, CancellationToken ct)
    {
        var kindByte = new byte[1];
        await ReadExactAsync(stream, kindByte, ct).ConfigureAwait(false);

        var lengthPrefix = new byte[sizeof(int)];
        await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (length < 0 || length > HostProtocol.MaxFrameBytes)
            throw new InvalidDataException($"Invalid frame length {length}.");

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        return new PipeFrame(kindByte[0], payload);
    }

    /// <summary>Writes a UTF-8 JSON frame (kind <see cref="HostFrameKind.Json"/>).</summary>
    public static Task WriteJsonAsync(Stream stream, string json, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(json);
        return WriteAsync(stream, HostFrameKind.Json, Encoding.UTF8.GetBytes(json), ct);
    }

    /// <summary>Writes a raw binary frame (kind <see cref="HostFrameKind.Binary"/>).</summary>
    public static Task WriteBinaryAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return WriteAsync(stream, HostFrameKind.Binary, payload, ct);
    }

    /// <summary>Writes a frame with an explicit kind tag and payload.</summary>
    public static async Task WriteAsync(Stream stream, byte kind, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > HostProtocol.MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload exceeds {HostProtocol.MaxFrameBytes} bytes.");

        var lengthPrefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payload.Length);

        await stream.WriteAsync(new byte[] { kind }, ct).ConfigureAwait(false);
        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Decodes a JSON payload to a string (throws if the payload is not valid UTF-8).</summary>
    public string GetJson() => Encoding.UTF8.GetString(Payload);

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Pipe closed before the full frame was read.");
            offset += read;
        }
    }
}
