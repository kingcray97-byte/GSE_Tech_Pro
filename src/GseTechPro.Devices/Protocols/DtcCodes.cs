namespace GseTechPro.Devices.Protocols;

/// <summary>
/// Canonical DTC code strings. These MUST match the <c>dtc_library</c> keys exactly
/// (see <c>data/dtc/README.md</c> §1 "Canonical code keys"):
/// <list type="bullet">
/// <item>J1939: <c>SPN &lt;decimal&gt; FMI &lt;decimal&gt;</c>, e.g. <c>SPN 110 FMI 3</c></item>
/// <item>J1587: <c>MID &lt;decimal&gt; PID|SID &lt;decimal&gt; FMI &lt;decimal&gt;</c>, e.g. <c>MID 128 PID 100 FMI 1</c></item>
/// <item>Manufacturer (PCS TCM): <c>PCS &lt;decimal&gt;</c>, the Delphi/GM 2-digit code, e.g. <c>PCS 72</c></item>
/// <item>OBD II: uppercase five-char generic DTC, e.g. <c>P0300</c> (formatted by
/// <see cref="Protocols.Obd2.Obd2DtcCodec"/>).</item>
/// </list>
/// Both the protocol decoders and the simulator use these helpers so real and simulated data share
/// one code format; a change to the format is made here, not in either caller.
/// </summary>
public static class DtcCodes
{
    /// <summary>J1939 canonical code: <c>SPN &lt;spn&gt; FMI &lt;fmi&gt;</c> (decimal, no leading zeros).</summary>
    public static string J1939(int spn, int fmi) => $"SPN {spn} FMI {fmi}";

    /// <summary>J1587 canonical code for a parameter (PID) DTC: <c>MID &lt;mid&gt; PID &lt;pid&gt; FMI &lt;fmi&gt;</c>.</summary>
    public static string J1587Pid(int mid, int pid, int fmi) => $"MID {mid} PID {pid} FMI {fmi}";

    /// <summary>J1587 canonical code for a subsystem (SID) DTC: <c>MID &lt;mid&gt; SID &lt;sid&gt; FMI &lt;fmi&gt;</c>.</summary>
    public static string J1587Sid(int mid, int sid, int fmi) => $"MID {mid} SID {sid} FMI {fmi}";

    /// <summary>PCS TCM canonical code: <c>PCS &lt;code&gt;</c> (the guide's Delphi/GM 2-digit code).</summary>
    public static string Pcs(int code) => $"PCS {code}";

    /// <summary>First SPN of the J1939-71 range assigned for proprietary (manufacturer) use.</summary>
    public const int J1939ProprietarySpnFirst = 520192;

    /// <summary>Last SPN of the J1939-71 proprietary range (the 19-bit maximum).</summary>
    public const int J1939ProprietarySpnLast = 524287;

    /// <summary>
    /// True for SPNs in the proprietary range. Their meaning depends on who made the ECU, so a
    /// match against a manufacturer table is only valid when the sender is that manufacturer's ECU.
    /// </summary>
    public static bool IsProprietarySpn(int spn) => spn is >= J1939ProprietarySpnFirst and <= J1939ProprietarySpnLast;
}
