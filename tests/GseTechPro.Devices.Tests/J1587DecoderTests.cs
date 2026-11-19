using GseTechPro.Core.Enums;
using GseTechPro.Devices.Protocols;
using GseTechPro.Devices.Protocols.J1708;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// Unit tests for the J1587 (over J1708) decoder. Test bytes follow the J1587 FEB2002 message
/// structure (§3.8.3 Diagnostic Data) and the dtc_library code keys (data/dtc/README.md §1).
/// </summary>
public class J1587DecoderTests
{
    [Fact]
    public void DecodeDtcEntry_ExtractsIdentifierFmiOccurrence()
    {
        // 2-byte entry: PID 94 (0x5E), FMI 3 with occurrence count 0 → byte 1 = 0x03.
        var dtc = J1587Decoder.DecodeDtcEntry(new byte[] { 0x5E, 0x03 });

        Assert.Equal(94, dtc.Identifier);
        Assert.Equal(3, dtc.Fmi);
        Assert.Equal(0, dtc.OccurrenceCount);
    }

    [Fact]
    public void DecodeDtcEntry_ExtractsOccurrenceCount()
    {
        // occurrence count 2 (bits 7-6 = 10) + FMI 5 → byte 1 = 0b10_000101 = 0x85.
        var dtc = J1587Decoder.DecodeDtcEntry(new byte[] { 0x01, 0x85 });

        Assert.Equal(1, dtc.Identifier);
        Assert.Equal(5, dtc.Fmi);
        Assert.Equal(2, dtc.OccurrenceCount);
    }

    [Fact]
    public void DecodeDtcReport_PidMarker_ProducesCanonicalPidCode()
    {
        // Message: MID 128 (0x80), marker 194 (0xC2, PID), DTC entry [PID 94, FMI 3].
        var readouts = J1587Decoder.DecodeDtcReport(128, J1587Decoder.PidDiagnosticData, new byte[] { 0x5E, 0x03 });

        var readout = Assert.Single(readouts);
        Assert.Equal("MID 128 PID 94 FMI 3", readout.Code);
        Assert.Equal(ProtocolType.J1708, readout.Protocol);
        Assert.Equal(3, readout.Fmi);
        Assert.Null(readout.Spn); // a J1587 PID is not a J1939 SPN
        Assert.Equal(DtcStatus.Active, readout.Status);
    }

    [Fact]
    public void DecodeDtcReport_SidMarker_ProducesCanonicalSidCode()
    {
        // MID 128 (engine), marker 195 (0xC3, SID), entry [SID 1 (injector cyl #1), FMI 5].
        var readouts = J1587Decoder.DecodeDtcReport(128, J1587Decoder.PidDiagnosticDataSid, new byte[] { 0x01, 0x05 });

        var readout = Assert.Single(readouts);
        Assert.Equal("MID 128 SID 1 FMI 5", readout.Code);
    }

    [Fact]
    public void DecodeDtcReport_MultipleEntries()
    {
        var readouts = J1587Decoder.DecodeDtcReport(
            128, J1587Decoder.PidDiagnosticData, new byte[] { 0x5E, 0x03, 0x6E, 0x00 });

        Assert.Equal(2, readouts.Count);
        Assert.Equal("MID 128 PID 94 FMI 3", readouts[0].Code);
        Assert.Equal("MID 128 PID 110 FMI 0", readouts[1].Code); // PID 110 (0x6E), FMI 0
    }

    [Fact]
    public void DecodeDtcReport_RejectsUnknownMarker()
    {
        Assert.Throws<ArgumentException>(() => J1587Decoder.DecodeDtcReport(128, 0x00, new byte[] { 0x5E, 0x03 }));
    }

    [Fact]
    public void ExtractPidData_EngineSpeed()
    {
        // PID 190 (Engine Speed): 2 bytes, 0.25 rpm/bit. 1500 rpm → 6000 → 0x1770 (LE: 70 17).
        var value = J1587Decoder.ExtractPidData(190, new byte[] { 0x70, 0x17 });

        Assert.True(value.Known);
        Assert.Equal("Engine Speed", value.Name);
        Assert.Equal(1500.0, value.Value);
        Assert.Equal("rpm", value.Unit);
    }

    [Fact]
    public void ExtractPidData_UnknownPid_ReturnsRaw()
    {
        var value = J1587Decoder.ExtractPidData(200, new byte[] { 0x2A });

        Assert.False(value.Known);
        Assert.Equal(200, value.Pid);
        Assert.Equal(42.0, value.Value);
    }
}
