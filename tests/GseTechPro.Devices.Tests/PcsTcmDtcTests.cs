using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Can;
using GseTechPro.Devices.Protocols;
using GseTechPro.Devices.Protocols.J1939;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>Phase 10C.3: DM1 faults on the bus monitor and the PCS TCM proprietary SPN plumbing.</summary>
public sealed class PcsTcmDtcTests
{
    [Theory]
    [InlineData(520191, false)]
    [InlineData(520192, true)]
    [InlineData(522742, true)]
    [InlineData(524287, true)]
    [InlineData(110, false)]
    public void ProprietarySpnRange_FollowsJ1939_71(int spn, bool expected) =>
        Assert.Equal(expected, DtcCodes.IsProprietarySpn(spn));

    [Fact]
    public void PcsCode_IsCanonical() => Assert.Equal("PCS 72", DtcCodes.Pcs(72));

    [Fact]
    public void DiagnosticMessage_DropsNoFaultAndPaddingPlaceholders()
    {
        // Lamps, then SPN 0 / FMI 0 ("no active DTC") — the J1939-73 empty DM1.
        var empty = new byte[] { 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF };
        Assert.Empty(CanBusEntry.DecodeDiagnosticMessage(empty, isDm2: false).Dtcs);

        var payload = new byte[10];
        payload[0] = 0x04;
        payload[1] = 0xFF;
        J1939DtcCodec.Encode(522742, 31, 2).CopyTo(payload, 2);
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.CopyTo(payload, 6);
        var dm = CanBusEntry.DecodeDiagnosticMessage(payload, isDm2: false);

        var dtc = Assert.Single(dm.Dtcs);
        Assert.Equal(522742, dtc.Spn);
        Assert.Equal(31, dtc.Fmi);
        Assert.Equal(2, dtc.OccurrenceCount);
        Assert.True(dm.Lamps.AmberWarningLamp);
        Assert.False(dm.IsPreviouslyActive);
    }

    [Fact]
    public void SimulatedBus_TransmissionDm1_ShowsOnTheBusTable()
    {
        var table = new CanBusTable();
        foreach (var frame in SimulatedCanBusMonitor.FramesBetween(0, 1.1))
            table.Apply(frame);

        var dm1 = Assert.Single(table.Entries, e => e.Pgn == J1939Pgn.Dm1);
        Assert.Equal((byte)0x03, dm1.SourceAddress);
        var dtc = Assert.Single(dm1.DiagnosticMessage!.Value.Dtcs);
        Assert.Equal(SimulatedCanBusMonitor.SimulatedPcsSpn, dtc.Spn);
        Assert.True(DtcCodes.IsProprietarySpn(dtc.Spn));

        Assert.True(CanBusTable.Matches(dm1, "SPN 522742"));
        Assert.True(CanBusTable.Matches(dm1, "DM1"));
        Assert.Null(table.Entries.First(e => e.Pgn == J1939Pgn.ElectronicEngineController1).DiagnosticMessage);
    }
}
