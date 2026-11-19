using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Protocols.J1708;

/// <summary>
/// A single J1587 diagnostic trouble code entry (2 bytes) from a J1708 message.
/// Layout per <b>SAE J1587 FEB2002</b> §3.8.3 (Diagnostic Data):
/// <code>
///   entry[0] = PID (parameter) or SID (subsystem) identifier
///   entry[1] = bits 7-6 Occurrence Count, bits 5-0 Failure Mode Identifier (FMI)
/// </code>
/// Only FMI 0–14 are defined in the cited edition (15 reserved); higher values are carried through
/// numerically and will simply not match a library row (never substituted with an invented meaning).
/// </summary>
public readonly record struct J1587Dtc(int Identifier, int Fmi, int OccurrenceCount);

/// <summary>One decoded J1587 PID live-data value.</summary>
public readonly record struct J1587LiveDataValue(string Name, double Value, string Unit, int Pid, bool Known)
{
    public static J1587LiveDataValue Raw(int pid, double rawValue) => new($"PID {pid} (raw)", rawValue, "raw", pid, Known: false);
}

/// <summary>
/// Decoder for J1587 messages running over the J1708 physical/data link.
///
/// Message framing (<b>SAE J1708</b>): each message is <c>[MID][PID][data…]</c>, where MID identifies
/// the transmitting module and PID the parameter/subsystem being reported. A diagnostic-trouble-code
/// report is <c>[MID][0xC2 (component) or 0xC3 (subsystem)][2-byte DTC entries…]</c>.
///
/// The PID-vs-SID keyword in a code string is determined by the report marker PID (194/0xC2 = PID,
/// 195/0xC3 = SID), not guessed from the numeric identifier; the identifier→name table is content
/// owned by <c>data/dtc</c>, not this decoder.
/// </summary>
public static class J1587Decoder
{
    /// <summary>PID 194 (0xC2) — Diagnostic Data report marker for component (PID) DTCs.</summary>
    public const byte PidDiagnosticData = 194;

    /// <summary>PID 195 (0xC3) — Diagnostic Data report marker for subsystem (SID) DTCs.</summary>
    public const byte PidDiagnosticDataSid = 195;

    /// <summary>Decodes one 2-byte DTC entry into identifier + FMI + occurrence count.</summary>
    public static J1587Dtc DecodeDtcEntry(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 2)
            throw new ArgumentException("A J1587 DTC entry is exactly 2 bytes.", nameof(entry));
        return new J1587Dtc(entry[0], entry[1] & 0x3F, entry[1] >> 6);
    }

    /// <summary>
    /// Decodes a complete DTC report message <c>[MID][marker][entries…]</c> into <see cref="DtcReadout"/>s.
    /// <paramref name="marker"/> is <see cref="PidDiagnosticData"/> or <see cref="PidDiagnosticDataSid"/>.
    /// </summary>
    public static IReadOnlyList<DtcReadout> DecodeDtcReport(int mid, byte marker, ReadOnlySpan<byte> entries)
    {
        var isSid = marker == PidDiagnosticDataSid;
        if (marker != PidDiagnosticData && !isSid)
            throw new ArgumentException($"Marker 0x{marker:X2} is not a J1587 diagnostic-data marker.", nameof(marker));

        var count = entries.Length / 2;
        var result = new DtcReadout[count];
        for (var i = 0; i < count; i++)
        {
            var dtc = DecodeDtcEntry(entries.Slice(i * 2, 2));
            result[i] = new DtcReadout
            {
                Code = isSid
                    ? DtcCodes.J1587Sid(mid, dtc.Identifier, dtc.Fmi)
                    : DtcCodes.J1587Pid(mid, dtc.Identifier, dtc.Fmi),
                Protocol = ProtocolType.J1708,
                Spn = null, // a J1587 PID/SID is NOT a J1939 SPN (data/dtc/README.md §2)
                Fmi = dtc.Fmi,
                Status = DtcStatus.Active, // the base J1587 read reports currently-active faults
                IsSimulated = false,
            };
        }
        return result;
    }

    /// <summary>
    /// Extracts a value for a known J1587 PID. Definitions are from SAE J1587 FEB2002 Table 2; only a
    /// conservative subset is decoded — anything else is returned raw (no invented scaling).
    /// </summary>
    public static J1587LiveDataValue ExtractPidData(int pid, ReadOnlySpan<byte> data)
    {
        switch (pid)
        {
            case 190: // Engine Speed — 2 bytes, 0.25 rpm/bit
                if (data.Length >= 2)
                    return new J1587LiveDataValue("Engine Speed", (data[0] | (data[1] << 8)) * 0.25, "rpm", pid, Known: true);
                break;

            case 110: // Engine Coolant Temperature — 1 byte, 1 °C/bit, offset -40 °C
                if (data.Length >= 1)
                    return new J1587LiveDataValue("Engine Coolant Temperature", data[0] - 40.0, "°C", pid, Known: true);
                break;

            case 168: // Electrical Potential (battery) — 2 bytes, 0.05 V/bit
                if (data.Length >= 2)
                    return new J1587LiveDataValue("Battery Potential (Voltage)", (data[0] | (data[1] << 8)) * 0.05, "V", pid, Known: true);
                break;
        }

        var raw = data.Length >= 1 ? (double)data[0] : 0.0;
        return J1587LiveDataValue.Raw(pid, raw);
    }
}
