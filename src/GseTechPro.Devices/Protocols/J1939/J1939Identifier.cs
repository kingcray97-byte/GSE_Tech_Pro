namespace GseTechPro.Devices.Protocols.J1939;

/// <summary>
/// Parsed form of a J1939 29-bit CAN identifier, plus the known Parameter Group Numbers (PGNs)
/// used by this decoder. Bit layout per <b>SAE J1939-21</b> §5.3.1 ("Message Format"):
///
/// <code>
///  28 27 26 | 25 | 24 | 23 ... 16 | 15 ... 8 | 7 ... 0
///  Priority |EDP | DP |   PF      |    PS    |   SA
///  (3 bits) |1bit|1bit| (8 bits)  | (8 bits) | (8 bits)
/// </code>
///
/// <list type="bullet">
/// <item><b>Priority</b> (P): 3 bits, 0 (highest) – 7 (lowest).</item>
/// <item><b>Extended Data Page</b> (EDP): 1 bit. Historically the "Reserved" bit; redefined as EDP.</item>
/// <item><b>Data Page</b> (DP): 1 bit.</item>
/// <item><b>PDU Format</b> (PF): 8 bits. PF &lt; 240 ⇒ PDU1 (peer-to-peer), PF ≥ 240 ⇒ PDU2 (broadcast).</item>
/// <item><b>PDU Specific</b> (PS): 8 bits. In PDU1 it is the destination address; in PDU2 it is the
/// Group Extension (low byte of the PGN).</item>
/// <item><b>Source Address</b> (SA): 8 bits.</item>
/// </list>
///
/// PGN (18 bits, J1939-21 §5.2.1): <c>EDP&lt;&lt;17 | DP&lt;&lt;16 | PF&lt;&lt;8 | PS</c>, where the PS term is
/// zeroed for PDU1 (the destination address is not part of the PGN).
/// </summary>
public readonly struct J1939Identifier
{
    /// <summary>The raw 29-bit identifier as received/transmitted.</summary>
    public uint Raw { get; }

    /// <summary>Priority, 0–7.</summary>
    public uint Priority { get; }

    /// <summary>Extended Data Page, 0 or 1.</summary>
    public uint Edp { get; }

    /// <summary>Data Page, 0 or 1.</summary>
    public uint Dp { get; }

    /// <summary>PDU Format, 0–255.</summary>
    public uint Pf { get; }

    /// <summary>PDU Specific, 0–255 (destination address for PDU1, group extension for PDU2).</summary>
    public uint Ps { get; }

    /// <summary>Source Address, 0–255.</summary>
    public uint Sa { get; }

    /// <summary>The 18-bit Parameter Group Number.</summary>
    public uint Pgn { get; }

    private J1939Identifier(uint raw, uint priority, uint edp, uint dp, uint pf, uint ps, uint sa, uint pgn)
    {
        Raw = raw;
        Priority = priority;
        Edp = edp;
        Dp = dp;
        Pf = pf;
        Ps = ps;
        Sa = sa;
        Pgn = pgn;
    }

    /// <summary>True when this is a PDU2 (broadcast) message (PF ≥ 240).</summary>
    public bool IsPdu2 => Pf >= 240;

    /// <summary>Computes the 18-bit PGN from its component fields (PS ignored for PDU1).</summary>
    public static uint ComputePgn(uint edp, uint dp, uint pf, uint ps) =>
        (edp << 17) | (dp << 16) | (pf << 8) | (pf < 240 ? 0u : (ps & 0xFFu));

    /// <summary>Parses a raw 29-bit identifier.</summary>
    public static J1939Identifier Parse(uint raw) => new(
        raw,
        (raw >> 26) & 0x7,
        (raw >> 25) & 0x1,
        (raw >> 24) & 0x1,
        (raw >> 16) & 0xFF,
        (raw >> 8) & 0xFF,
        raw & 0xFF,
        ComputePgn((raw >> 25) & 0x1, (raw >> 24) & 0x1, (raw >> 16) & 0xFF, (raw >> 8) & 0xFF));

    /// <summary>Builds a raw 29-bit identifier from its component fields (all masked to their widths).</summary>
    public static uint Build(uint priority, uint edp, uint dp, uint pf, uint ps, uint sa) =>
        ((priority & 0x7) << 26) |
        ((edp & 0x1) << 25) |
        ((dp & 0x1) << 24) |
        ((pf & 0xFF) << 16) |
        ((ps & 0xFF) << 8) |
        (sa & 0xFF);
}

/// <summary>
/// Known J1939 Parameter Group Numbers (all 18-bit values; EDP=DP=0). Decimal + hex cross-checked
/// against SAE J1939-71 (message definitions) and SAE J1939-73 (diagnostics).
/// </summary>
public static class J1939Pgn
{
    /// <summary>Address Claimed (J1939-81).</summary>
    public const uint AddressClaimed = 60928; // 0xEE00

    /// <summary>Request (J1939-21 §5.5.2.1): ask an ECU to transmit a PGN.</summary>
    public const uint Request = 59904; // 0xEA00

    /// <summary>Acknowledgement (J1939-21 §5.5.3): positive/negative ack of a request.</summary>
    public const uint Acknowledgement = 59392; // 0xE800

    /// <summary>Transport Protocol — Connection Management (J1939-21 §5.10).</summary>
    public const uint TransportConnectionManagement = 60416; // 0xEC00

    /// <summary>Transport Protocol — Data Transfer (J1939-21 §5.10).</summary>
    public const uint TransportDataTransfer = 60160; // 0xEB00

    /// <summary>Electronic Engine Controller 1 (EEC1).</summary>
    public const uint ElectronicEngineController1 = 61444; // 0xF004

    /// <summary>Electronic Engine Controller 2 (EEC2).</summary>
    public const uint ElectronicEngineController2 = 61443; // 0xF003

    /// <summary>Engine Temperature 1 (ET1).</summary>
    public const uint EngineTemperature1 = 65262; // 0xFEEE

    /// <summary>Engine Fluid Level/Pressure 1 (EFL/P1).</summary>
    public const uint EngineFluidLevelPressure1 = 65263; // 0xFEEF

    /// <summary>Cruise Control / Vehicle Speed (CCVS).</summary>
    public const uint CruiseControlVehicleSpeed = 65265; // 0xFEF1

    /// <summary>Engine Hours, Revolutions (HOURS).</summary>
    public const uint EngineHoursRevolutions = 65253; // 0xFEE5

    /// <summary>Ambient Conditions.</summary>
    public const uint AmbientConditions = 65269; // 0xFEF5

    /// <summary>Vehicle Electrical Power.</summary>
    public const uint VehicleElectricalPower = 65271; // 0xFEF7

    /// <summary>Inlet/Exhaust Conditions.</summary>
    public const uint InletExhaustConditions = 65270; // 0xFEF6

    /// <summary>Fuel Economy (LFE).</summary>
    public const uint FuelEconomy = 65266; // 0xFEF2

    /// <summary>DM1 — Active Diagnostic Trouble Codes (J1939-73 §5.7.1).</summary>
    public const uint Dm1 = 65226; // 0xFECA

    /// <summary>DM2 — Previously Active Diagnostic Trouble Codes (J1939-73 §5.7.2).</summary>
    public const uint Dm2 = 65227; // 0xFECB

    /// <summary>DM3 — Diagnostic Data Clear/Reset for Previously Active DTCs (J1939-73 §5.7.3).</summary>
    public const uint Dm3 = 65228; // 0xFECC

    /// <summary>DM11 — Diagnostic Data Clear/Reset for Active DTCs (J1939-73 §5.7.8).</summary>
    public const uint Dm11 = 65235; // 0xFED3
}
