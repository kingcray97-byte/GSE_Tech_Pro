using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Protocols.Obd2;

/// <summary>One decoded OBD-II PID (live-data) value.</summary>
public readonly record struct Obd2PidValue(string Name, double Value, string Unit, byte Pid, bool Known)
{
    public static Obd2PidValue Raw(byte pid, double rawValue) => new($"PID 0x{pid:X2} (raw)", rawValue, "raw", pid, Known: false);
}

/// <summary>
/// Codec for the OBD-II 2-byte DTC representation, per <b>SAE J2012 / ISO 15031-6</b> (DTC format).
/// The five-character code <c>T D1 D2 D3 D4</c> is packed as:
/// <code>
///   byte1 = (type &lt;&lt; 6) | (D1 &lt;&lt; 4) | D2     // type: 0=P 1=C 2=B 3=U; D1 in 0–3
///   byte2 = (D3 &lt;&lt; 4) | D4
/// </code>
/// Example: P0230 → 0x02 0x30.
/// </summary>
public static class Obd2DtcCodec
{
    private static readonly char[] TypeChars = { 'P', 'C', 'B', 'U' };
    private const string Hex = "0123456789ABCDEF";

    /// <summary>Decodes a 2-byte DTC to its uppercase five-character code (e.g. "P0230").</summary>
    public static string Decode(byte b1, byte b2)
    {
        var type = TypeChars[(b1 >> 6) & 0x03];
        var d1 = (char)('0' + ((b1 >> 4) & 0x03));
        var d2 = Hex[b1 & 0x0F];
        var d3 = Hex[(b2 >> 4) & 0x0F];
        var d4 = Hex[b2 & 0x0F];
        return $"{type}{d1}{d2}{d3}{d4}";
    }

    /// <summary>Encodes a five-character code (e.g. "P0230") to its 2-byte DTC representation.</summary>
    public static (byte B1, byte B2) Encode(string code)
    {
        if (code is null || code.Length != 5)
            throw new ArgumentException("An OBD-II DTC is exactly 5 characters (e.g. P0230).", nameof(code));

        var type = TypeChars.IndexOf(char.ToUpperInvariant(code[0]));
        if (type < 0)
            throw new ArgumentException($"Unknown OBD-II DTC type '{code[0]}'.", nameof(code));

        var d1 = Hex.IndexOf(char.ToUpperInvariant(code[1]));
        var d2 = Hex.IndexOf(char.ToUpperInvariant(code[2]));
        var d3 = Hex.IndexOf(char.ToUpperInvariant(code[3]));
        var d4 = Hex.IndexOf(char.ToUpperInvariant(code[4]));
        if (d1 < 0 || d1 > 3 || d2 < 0 || d3 < 0 || d4 < 0)
            throw new ArgumentException($"Invalid OBD-II DTC characters in '{code}'.", nameof(code));

        return ((byte)((type << 6) | (d1 << 4) | d2), (byte)((d3 << 4) | d4));
    }
}

/// <summary>
/// OBD-II (SAE J1979 / ISO 15031-5) request/response decoding: mode $01 live data, mode $02 freeze
/// frame (same PID scaling), mode $03 DTC read, mode $04 clear, and mode $09 VIN. PID byte→value
/// scalings are from SAE J1979 Annex B / ISO 15031-5 (Standard PIDs).
/// </summary>
public static class Obd2Decoder
{
    public const byte Mode01 = 0x01; // current powertrain diagnostic data
    public const byte Mode02 = 0x02; // freeze frame data
    public const byte Mode03 = 0x03; // emission-related DTCs
    public const byte Mode04 = 0x04; // clear/erase DTCs
    public const byte Mode09 = 0x09; // vehicle information

    /// <summary>The response service byte for a request service (request + 0x40).</summary>
    public static byte ResponseService(byte mode) => (byte)(mode + 0x40);

    /// <summary>Builds a mode request frame: <c>[mode, pid]</c>.</summary>
    public static byte[] BuildRequest(byte mode, byte pid) => new[] { mode, pid };

    /// <summary>Builds a mode $03 (read DTCs) or mode $04 (clear) request frame.</summary>
    public static byte[] BuildRequest(byte mode) => new[] { mode };

    /// <summary>
    /// Decodes a mode $03 response (<c>0x43 [count] [DTC 2-byte pairs…]</c>) into DtcReadouts.
    /// </summary>
    public static IReadOnlyList<DtcReadout> DecodeMode03Response(ReadOnlySpan<byte> response)
    {
        if (response.Length < 2 || response[0] != ResponseService(Mode03))
            return Array.Empty<DtcReadout>();

        var count = response[1];
        var available = (response.Length - 2) / 2;
        var dtcs = Math.Min(count, available);

        var result = new DtcReadout[dtcs];
        for (var i = 0; i < dtcs; i++)
        {
            var code = Obd2DtcCodec.Decode(response[2 + i * 2], response[3 + i * 2]);
            result[i] = new DtcReadout
            {
                Code = code,
                Protocol = ProtocolType.OBD2,
                Spn = null,
                Fmi = null,
                Status = DtcStatus.Active, // mode $03 reports confirmed (active) DTCs
                IsSimulated = false,
            };
        }
        return result;
    }

    /// <summary>
    /// Decodes a mode $01/$02 PID response body (<c>[0x41|0x42, pid, A..D]</c>) to a scaled value.
    /// </summary>
    public static Obd2PidValue DecodePidResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 2)
            return Obd2PidValue.Raw(0, 0);
        var pid = response[1];
        return DecodePid(pid, response.Slice(2));
    }

    /// <summary>Scales a PID's data bytes (A..D) per SAE J1979 Annex B. Unknown PID → raw.</summary>
    public static Obd2PidValue DecodePid(byte pid, ReadOnlySpan<byte> data)
    {
        double value;
        switch (pid)
        {
            case 0x04: // Calculated Engine Load — (A × 100) / 255 %
                if (data.Length >= 1)
                    return new Obd2PidValue("Calculated Engine Load", data[0] * 100.0 / 255.0, "%", pid, Known: true);
                break;

            case 0x05: // Engine Coolant Temperature — A − 40 °C
                if (data.Length >= 1)
                    return new Obd2PidValue("Engine Coolant Temperature", data[0] - 40.0, "°C", pid, Known: true);
                break;

            case 0x0A: // Fuel Pressure — A × 3 kPa
                if (data.Length >= 1)
                    return new Obd2PidValue("Fuel Pressure", data[0] * 3.0, "kPa", pid, Known: true);
                break;

            case 0x0B: // Intake Manifold Absolute Pressure — A kPa
                if (data.Length >= 1)
                    return new Obd2PidValue("Intake Manifold Absolute Pressure", data[0], "kPa", pid, Known: true);
                break;

            case 0x0C: // Engine RPM — ((A × 256) + B) / 4
                if (data.Length >= 2)
                    return new Obd2PidValue("Engine RPM", ((data[0] * 256.0) + data[1]) / 4.0, "rpm", pid, Known: true);
                break;

            case 0x0D: // Vehicle Speed — A km/h
                if (data.Length >= 1)
                    return new Obd2PidValue("Vehicle Speed", data[0], "km/h", pid, Known: true);
                break;

            case 0x0E: // Timing Advance — (A / 2) − 64 degrees
                if (data.Length >= 1)
                    return new Obd2PidValue("Timing Advance", data[0] / 2.0 - 64.0, "°", pid, Known: true);
                break;

            case 0x0F: // Intake Air Temperature — A − 40 °C
                if (data.Length >= 1)
                    return new Obd2PidValue("Intake Air Temperature", data[0] - 40.0, "°C", pid, Known: true);
                break;

            case 0x10: // Mass Air Flow — ((A × 256) + B) / 100 g/s
                if (data.Length >= 2)
                    return new Obd2PidValue("Mass Air Flow", ((data[0] * 256.0) + data[1]) / 100.0, "g/s", pid, Known: true);
                break;

            case 0x11: // Throttle Position — (A × 100) / 255 %
                if (data.Length >= 1)
                    return new Obd2PidValue("Throttle Position", data[0] * 100.0 / 255.0, "%", pid, Known: true);
                break;

            case 0x21: // Distance Traveled with MIL On — (A × 256) + B km
                if (data.Length >= 2)
                    return new Obd2PidValue("Distance Traveled with MIL On", (data[0] * 256.0) + data[1], "km", pid, Known: true);
                break;

            case 0x2F: // Fuel Level Input — (A × 100) / 255 %
                if (data.Length >= 1)
                    return new Obd2PidValue("Fuel Level Input", data[0] * 100.0 / 255.0, "%", pid, Known: true);
                break;

            case 0x42: // Control Module Voltage — ((A × 256) + B) / 1000 V
                if (data.Length >= 2)
                    return new Obd2PidValue("Control Module Voltage", ((data[0] * 256.0) + data[1]) / 1000.0, "V", pid, Known: true);
                break;

            case 0x46: // Ambient Air Temperature — A − 40 °C
                if (data.Length >= 1)
                    return new Obd2PidValue("Ambient Air Temperature", data[0] - 40.0, "°C", pid, Known: true);
                break;

            case 0x5C: // Engine Oil Temperature — A − 40 °C
                if (data.Length >= 1)
                    return new Obd2PidValue("Engine Oil Temperature", data[0] - 40.0, "°C", pid, Known: true);
                break;
        }

        value = data.Length >= 1 ? data[0] : 0.0;
        return Obd2PidValue.Raw(pid, value);
    }

    /// <summary>
    /// Decodes a mode $09 VIN response (<c>0x49 0x02 0x01 [count] [17 ASCII]</c>) into the VIN string,
    /// or null when the response is not a VIN report.
    /// </summary>
    public static string? DecodeVin(ReadOnlySpan<byte> response)
    {
        // [0x49][0x02][0x01][count][VIN bytes…]
        if (response.Length < 4 || response[0] != ResponseService(Mode09) || response[1] != 0x02)
            return null;

        var count = response[3];
        if (count == 0)
            return null;

        var available = response.Length - 4;
        var length = Math.Min(count, available);
        return System.Text.Encoding.ASCII.GetString(response.Slice(4, length));
    }
}
