using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Protocols.J1939;

/// <summary>
/// A single J1939 diagnostic trouble code decoded from a DM1/DM2 message.
/// The 32-bit (4-byte) on-wire layout is defined in <b>SAE J1939-73</b> §5.7.1.7.1 (DTC):
///
/// <code>
///  MSB                                                          LSB
///  bit 31 ... bit 13 | bit 12 ... 8 | bit 7 ... 1 | bit 0
///        SPN (19)    |   FMI (5)   |   OC (7)   | CM (1)
/// </code>
///
/// Transmitted little-endian (D[0] is first on the wire):
/// <code>
///   SPN = ((D[1] >> 5) &amp; 0x07) | (D[2] &lt;&lt; 3) | (D[3] &lt;&lt; 11)
///   FMI = D[1] &amp; 0x1F
///   OC  = (D[0] >> 1) &amp; 0x7F
///   CM  = D[0] &amp; 0x01
/// </code>
/// </summary>
public readonly record struct J1939Dtc(int Spn, int Fmi, int OccurrenceCount, bool ConversionMethod);

/// <summary>
/// Lamp status carried in the first two bytes of DM1/DM2 (SAE J1939-73 §5.7.1.5).
/// Byte 1 bits (each 2-bit field: 00 = off, 01 = on, 10/11 = not available/reserved):
/// 7-6 MIL, 5-4 red stop lamp, 3-2 amber warning lamp, 1-0 protect lamp.
/// </summary>
public readonly record struct J1939LampStatus(
    bool MalfunctionIndicatorLamp,
    bool RedStopLamp,
    bool AmberWarningLamp,
    bool ProtectLamp)
{
    /// <summary>True when any of the four lamps is on.</summary>
    public bool AnyLampOn => MalfunctionIndicatorLamp || RedStopLamp || AmberWarningLamp || ProtectLamp;

    /// <summary>Decodes the two lamp-status bytes (only byte 1 is interpreted; byte 2 carries the flash lamps).</summary>
    public static J1939LampStatus Decode(byte lampByte1) => new(
        MalfunctionIndicatorLamp: (lampByte1 & 0xC0) == 0x40,
        RedStopLamp: (lampByte1 & 0x30) == 0x10,
        AmberWarningLamp: (lampByte1 & 0x0C) == 0x04,
        ProtectLamp: (lampByte1 & 0x03) == 0x01);
}

/// <summary>A decoded DM1 (active) or DM2 (previously active/stored) message payload.</summary>
public readonly record struct J1939DmMessage(
    bool IsPreviouslyActive,
    J1939LampStatus Lamps,
    IReadOnlyList<J1939Dtc> Dtcs);

/// <summary>Codec for the J1939 DTC 4-byte representation and DM1/DM2 message payloads.</summary>
public static class J1939DtcCodec
{
    /// <summary>Decodes a 4-byte DTC (little-endian) per J1939-73 §5.7.1.7.1.</summary>
    public static J1939Dtc Decode(ReadOnlySpan<byte> dtc)
    {
        if (dtc.Length < 4)
            throw new ArgumentException("A J1939 DTC is exactly 4 bytes.", nameof(dtc));

        var spn = ((dtc[1] >> 5) & 0x07) | (dtc[2] << 3) | (dtc[3] << 11);
        var fmi = dtc[1] & 0x1F;
        var oc = (dtc[0] >> 1) & 0x7F;
        var cm = (dtc[0] & 0x01) != 0;
        return new J1939Dtc(spn, fmi, oc, cm);
    }

    /// <summary>Encodes an SPN/FMI pair to the 4-byte DTC representation (little-endian).</summary>
    public static byte[] Encode(int spn, int fmi, int occurrenceCount = 1, bool conversionMethod = false)
    {
        if (spn is < 0 or > 0x7FFFF)
            throw new ArgumentOutOfRangeException(nameof(spn), spn, "SPN is a 19-bit value (0–524287).");
        if (fmi is < 0 or > 0x1F)
            throw new ArgumentOutOfRangeException(nameof(fmi), fmi, "FMI is a 5-bit value (0–31).");

        var value = ((uint)spn << 13) |
                    ((uint)(fmi & 0x1F) << 8) |
                    ((uint)(occurrenceCount & 0x7F) << 1) |
                    (conversionMethod ? 1u : 0u);
        return new[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF),
        };
    }

    /// <summary>
    /// Decodes a DM1 (PGN 65226) or DM2 (PGN 65227) payload: two lamp-status bytes followed by
    /// zero or more 4-byte DTCs. <paramref name="isDm2"/> selects the previously-active (stored)
    /// interpretation.
    /// </summary>
    public static J1939DmMessage DecodeDmMessage(ReadOnlySpan<byte> payload, bool isDm2)
    {
        if (payload.Length < 2)
            return new J1939DmMessage(isDm2, new J1939LampStatus(false, false, false, false), Array.Empty<J1939Dtc>());

        var lamps = J1939LampStatus.Decode(payload[0]);
        var count = (payload.Length - 2) / 4;
        var dtcs = new J1939Dtc[count];
        for (var i = 0; i < count; i++)
            dtcs[i] = Decode(payload.Slice(2 + i * 4, 4));

        return new J1939DmMessage(isDm2, lamps, dtcs);
    }

    /// <summary>Maps a decoded DM1/DM2 message to <see cref="DtcReadout"/> records.</summary>
    public static IReadOnlyList<DtcReadout> ToReadouts(J1939DmMessage message)
    {
        var status = message.IsPreviouslyActive ? DtcStatus.Stored : DtcStatus.Active;
        var result = new DtcReadout[message.Dtcs.Count];
        for (var i = 0; i < message.Dtcs.Count; i++)
        {
            var dtc = message.Dtcs[i];
            result[i] = new DtcReadout
            {
                Code = DtcCodes.J1939(dtc.Spn, dtc.Fmi),
                Protocol = ProtocolType.J1939,
                Spn = dtc.Spn,
                Fmi = dtc.Fmi,
                Status = status,
                // Description/Severity left null: the dtc_library lookup (data layer) owns that;
                // we never invent a description for an SPN/FMI pair we have not decoded.
                IsSimulated = false,
            };
        }
        return result;
    }
}
