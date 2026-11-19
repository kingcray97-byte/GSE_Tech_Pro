using System.Buffers.Binary;

namespace GseTechPro.Devices.Protocols;

/// <summary>
/// Big-endian CAN identifier framing shared by the J2534 and RP1210 adapters.
///
/// Per <b>SAE J2534-1 (04.04) §6.4.2 / §7.2.3.1</b>, for CAN (<c>0x05</c>) and ISO15765 (<c>0x06</c>)
/// channels the 11/29-bit CAN id is stored <b>big-endian in <c>Data[0..3]</c></b>, the payload follows
/// in <c>Data[4..DataSize-1]</c>, and <c>DataSize = 4 + payloadLength</c>. TMC RP1210C §10.3 uses the
/// same big-endian 4-byte id field inside its CAN message body, so one codec serves both vendors.
/// </summary>
public static class CanFraming
{
    /// <summary>Number of bytes a CAN id occupies at the head of a frame (<c>Data[0..3]</c>).</summary>
    public const int IdByteCount = 4;

    /// <summary>Writes a CAN id big-endian into the first four bytes of <paramref name="destination"/>.</summary>
    public static void WriteId(uint id, Span<byte> destination)
    {
        if (destination.Length < IdByteCount)
            throw new ArgumentException($"A CAN id requires {IdByteCount} bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt32BigEndian(destination, id);
    }

    /// <summary>Reads a big-endian CAN id from the first four bytes of <paramref name="source"/>.</summary>
    public static uint ReadId(ReadOnlySpan<byte> source)
    {
        if (source.Length < IdByteCount)
            throw new ArgumentException($"A CAN id requires {IdByteCount} bytes.", nameof(source));
        return BinaryPrimitives.ReadUInt32BigEndian(source);
    }

    /// <summary>Builds a CAN frame <c>[id BE(4)][payload]</c> (the J2534 <c>Data</c> layout).</summary>
    public static byte[] Encode(uint id, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[IdByteCount + payload.Length];
        WriteId(id, frame);
        payload.CopyTo(frame.AsSpan(IdByteCount));
        return frame;
    }
}
