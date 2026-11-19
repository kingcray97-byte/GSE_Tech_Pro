namespace GseTechPro.Devices.Protocols.J1939;

/// <summary>
/// The J1939 NAME: the 64-bit identifier a controller broadcasts in the Address Claimed message
/// (PGN 60928, <b>SAE J1939-81</b> §4.2.1). Field order from MSB (bit 63) to LSB (bit 0):
///
/// <code>
///  63 | 62...60 | 59...56 | 55...49 | 48 | 47...40 | 39...35 | 34...32 | 31...21 | 20...0
///  AAC| Industry| Vehicle | Vehicle | Rsd| Function| Function| ECU     | Manufact| Identity
///  1  | Group 3 | Sys Inst| System 7| 1  | 8       | Inst 5  | Inst 3  | Code 11 | Number 21
/// </code>
/// </summary>
public readonly record struct J1939Name(
    bool ArbitraryAddressCapable,
    int IndustryGroup,
    int VehicleSystemInstance,
    int VehicleSystem,
    int Function,
    int FunctionInstance,
    int EcuInstance,
    int ManufacturerCode,
    int IdentityNumber)
{
    /// <summary>The raw 64-bit NAME value.</summary>
    public ulong Raw { get; init; }

    /// <summary>Decodes a 64-bit NAME from its 8 little-endian message bytes.</summary>
    public static J1939Name Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new ArgumentException("A J1939 NAME is exactly 8 bytes.", nameof(data));

        ulong raw = 0;
        for (var i = 0; i < 8; i++)
            raw |= (ulong)data[i] << (8 * i);

        return new J1939Name(
            ArbitraryAddressCapable: (raw & (1UL << 63)) != 0,
            IndustryGroup: (int)((raw >> 60) & 0x7),
            VehicleSystemInstance: (int)((raw >> 56) & 0xF),
            VehicleSystem: (int)((raw >> 49) & 0x7F),
            Function: (int)((raw >> 40) & 0xFF),
            FunctionInstance: (int)((raw >> 35) & 0x1F),
            EcuInstance: (int)((raw >> 32) & 0x7),
            ManufacturerCode: (int)((raw >> 21) & 0x7FF),
            IdentityNumber: (int)(raw & 0x1FFFFF))
        {
            Raw = raw,
        };
    }

    /// <summary>Encodes the NAME to its 8 little-endian message bytes (for address-claim transmission).</summary>
    public byte[] Encode()
    {
        ulong raw = ArbitraryAddressCapable ? 1UL << 63 : 0UL;
        raw |= (ulong)(IndustryGroup & 0x7) << 60;
        raw |= (ulong)(VehicleSystemInstance & 0xF) << 56;
        raw |= (ulong)(VehicleSystem & 0x7F) << 49;
        raw |= (ulong)(Function & 0xFF) << 40;
        raw |= (ulong)(FunctionInstance & 0x1F) << 35;
        raw |= (ulong)(EcuInstance & 0x7) << 32;
        raw |= (ulong)(ManufacturerCode & 0x7FF) << 21;
        raw |= (ulong)(uint)(IdentityNumber & 0x1FFFFF);

        return new[]
        {
            (byte)(raw & 0xFF),
            (byte)((raw >> 8) & 0xFF),
            (byte)((raw >> 16) & 0xFF),
            (byte)((raw >> 24) & 0xFF),
            (byte)((raw >> 32) & 0xFF),
            (byte)((raw >> 40) & 0xFF),
            (byte)((raw >> 48) & 0xFF),
            (byte)((raw >> 56) & 0xFF),
        };
    }
}

/// <summary>
/// The J1939 Request message (PGN 59904, <b>SAE J1939-21</b> §5.5.2.1). A tool sends it to request a
/// PGN from a specific destination address; the 3 data bytes carry the requested PGN little-endian.
/// </summary>
public static class J1939PgnRequest
{
    /// <summary>Encodes the 3-byte request payload for a PGN.</summary>
    public static byte[] Encode(uint pgn) => new[]
    {
        (byte)(pgn & 0xFF),
        (byte)((pgn >> 8) & 0xFF),
        (byte)((pgn >> 16) & 0xFF),
    };

    /// <summary>Builds the 29-bit Request identifier (PDU1: PS = destination address).</summary>
    public static uint BuildId(byte destinationAddress, byte sourceAddress, byte priority = 6) =>
        J1939Identifier.Build(priority, 0, 0, (uint)(J1939Pgn.Request >> 8) & 0xFF, destinationAddress, sourceAddress);

    /// <summary>Decodes the requested PGN from a 3-byte request payload.</summary>
    public static uint Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
            throw new ArgumentException("A J1939 Request payload is exactly 3 bytes.", nameof(payload));
        return (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16));
    }
}
