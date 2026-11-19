namespace GseTechPro.Devices.Protocols.J1939;

/// <summary>
/// DM11 — Diagnostic Data Clear/Reset for Active DTCs (PGN 65226 is DM1; DM11 is PGN 65235),
/// defined in <b>SAE J1939-73</b> §5.7.8. A diagnostic tool sends DM11 to an ECU to clear the
/// listed active DTCs; listing no DTCs requests clearing <em>all</em> active DTCs.
///
/// Message payload: two lamp-status bytes (typically 0x00 0x00 for a tool request) followed by
/// zero or more 4-byte DTC entries using the same SPN(19)/FMI(5) layout as DM1 (§5.7.1.7.1), with
/// the Occurrence Count field set to all-ones (0x7F) and Conversion Method set to 1 to signal "clear
/// this SPN/FMI regardless of occurrence count".
///
/// NOTE (clean-room): the exact clear-all byte sequence (empty DTC list vs. a reserved SPN/FMI
/// sentinel) varies between ECU implementations. This encoder produces the standard "two lamp bytes +
/// DTC list" form; the precise clear-all behavior against a specific ECU is verified on hardware, not
/// asserted here.
/// </summary>
public static class J1939Dm11
{
    /// <summary>Byte value used for the OC(7)/CM(1) byte of a DM11 clear entry: OC = 0x7F, CM = 1.</summary>
    private const byte ClearOcCmByte = 0xFF;

    /// <summary>
    /// Encodes a DM11 payload clearing the listed SPN/FMI pairs. An empty list clears all active DTCs.
    /// </summary>
    public static byte[] Encode(IEnumerable<(int Spn, int Fmi)>? dtcs = null)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(0x00); // lamp status byte 1 (tool request: no lamps)
        buffer.WriteByte(0x00); // lamp status byte 2

        if (dtcs is not null)
        {
            foreach (var (spn, fmi) in dtcs)
            {
                var entry = J1939DtcCodec.Encode(spn, fmi, occurrenceCount: 0x7F, conversionMethod: true);
                buffer.Write(entry, 0, entry.Length);
            }
        }

        return buffer.ToArray();
    }
}
