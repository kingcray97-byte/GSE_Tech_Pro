namespace GseTechPro.Devices.Protocols;

/// <summary>
/// RP1210 message framing (<b>TMC RP1210C §10.3/§10.4</b>), hardware-free and unit-testable.
///
/// <c>RP1210_ReadMessage</c> always prepends a <b>4-byte big-endian timestamp</b> to the message body
/// (for CAN, J1939 and J1708). The body framing then depends on the connected protocol:
/// <list type="bullet">
/// <item><b>CAN</b>: <c>[FrameType(1: 0 = 11-bit, 1 = 29-bit)][CAN_ID BE(4)][Data…]</c>.</item>
/// <item><b>J1939</b>: <c>[PGN(3, LE)][How/Priority(1)][SA(1)][DA(1)][Data…]</c>.</item>
/// <item><b>J1708</b>: <c>[MID][PID][Data…]</c>.</item>
/// </list>
/// The adapters connect using the <c>"CAN"</c> protocol string (preferred), so the raw 29-bit CAN id
/// arrives in the <c>[type][id BE(4)][data]</c> form and feeds <c>J1939TransportProtocol</c> directly.
/// </summary>
public static class Rp1210Framing
{
    /// <summary>Number of bytes of the big-endian timestamp <c>ReadMessage</c> prepends.</summary>
    public const int TimestampByteCount = 4;

    /// <summary>CAN frame-type byte: 11-bit standard identifier.</summary>
    public const byte FrameTypeStandardCan = 0x00;

    /// <summary>CAN frame-type byte: 29-bit extended identifier (J1939).</summary>
    public const byte FrameTypeExtendedCan = 0x01;

    /// <summary>Strips the 4-byte timestamp prefix; returns the message body (empty if too short).</summary>
    public static ReadOnlySpan<byte> StripTimestamp(ReadOnlySpan<byte> message) =>
        message.Length < TimestampByteCount ? ReadOnlySpan<byte>.Empty : message[TimestampByteCount..];

    /// <summary>
    /// Parses a CAN message body <c>[frame-type][id BE(4)][data]</c> into its 29-bit id and payload.
    /// Returns false when the body is shorter than the id field.
    /// </summary>
    public static bool TryParseCan(ReadOnlySpan<byte> body, out uint id, out ReadOnlySpan<byte> data)
    {
        id = 0;
        data = ReadOnlySpan<byte>.Empty;
        if (body.Length < 1 + CanFraming.IdByteCount)
            return false;

        // Frame-type byte selects the id width; only 29-bit is used here (J1939).
        if (body[0] != FrameTypeExtendedCan)
            return false;

        id = CanFraming.ReadId(body.Slice(1, CanFraming.IdByteCount));
        data = body[(1 + CanFraming.IdByteCount)..];
        return true;
    }

    /// <summary>Builds a CAN message body <c>[frame-type = 29-bit][id BE(4)][data]</c>.</summary>
    public static byte[] BuildCan(uint id, ReadOnlySpan<byte> data)
    {
        var body = new byte[1 + CanFraming.IdByteCount + data.Length];
        body[0] = FrameTypeExtendedCan;
        CanFraming.WriteId(id, body.AsSpan(1, CanFraming.IdByteCount));
        data.CopyTo(body.AsSpan(1 + CanFraming.IdByteCount));
        return body;
    }
}
