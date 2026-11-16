using System.Buffers.Binary;
using System.Text;

namespace GseTechPro.Devices.Ipc;

/// <summary>
/// Length-prefixed framing for the named pipe (ARCHITECTURE.md §1.1): a 4-byte little-endian
/// length prefix followed by a UTF-8 JSON payload. Control messages are JSON; Phase 2 adds raw
/// byte-array streaming for CAN/scope capture.
/// </summary>
public static class PipeMessage
{
    /// <summary>Sanity cap to reject corrupt/hostile frame lengths before allocating.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024; // 16 MiB

    /// <summary>Writes a UTF-8 JSON payload framed with a little-endian 4-byte length prefix.</summary>
    public static async Task WriteAsync(Stream stream, string payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payload);

        var bytes = Encoding.UTF8.GetBytes(payload);
        var lengthPrefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, bytes.Length);

        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one length-prefixed UTF-8 JSON payload.</summary>
    /// <exception cref="EndOfStreamException">The pipe closed before a full frame arrived.</exception>
    /// <exception cref="InvalidDataException">The length prefix was invalid.</exception>
    public static async Task<string> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lengthPrefix = new byte[sizeof(int)];
        await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (length < 0 || length > MaxMessageBytes)
            throw new InvalidDataException($"Invalid frame length {length}.");

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

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
