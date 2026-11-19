using GseTechPro.Core.Enums;
using GseTechPro.Devices.Protocols;
using GseTechPro.Devices.Protocols.J1939;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// Unit tests for the J1939 decoders. The test byte sequences are derived directly from the published
/// bit layouts (not invented plausible frames): each test documents the layout citation and shows the
/// arithmetic that produces the expected bytes.
/// </summary>
public class J1939DecoderTests
{
    // ---- 29-bit identifier / PGN (SAE J1939-21 §5.3.1, §5.2.1) ----

    [Fact]
    public void Identifier_Parse_ExtractsFieldsAndPgn()
    {
        // EEC1 (PGN 61444, PF=0xF0, PS=0x04) broadcast from engine SA 0x00 at priority 6.
        // raw = (6<<26)|(0xF0<<16)|(0x04<<8)|0x00
        var id = J1939Identifier.Parse(0x18F00400);

        Assert.Equal(6u, id.Priority);
        Assert.Equal(0u, id.Edp);
        Assert.Equal(0u, id.Dp);
        Assert.Equal(0xF0u, id.Pf);
        Assert.Equal(0x04u, id.Ps);
        Assert.Equal(0x00u, id.Sa);
        Assert.Equal(J1939Pgn.ElectronicEngineController1, id.Pgn);
        Assert.True(id.IsPdu2);
    }

    [Fact]
    public void Identifier_Pdu1_PgnIgnoresDestinationAddress()
    {
        // PDU1: Request (PGN 59904, PF=0xEA) addressed to destination 0x00 (engine #1).
        var id = J1939Identifier.Parse(0x18EA00F9); // priority 6, PF 0xEA, PS 0x00 (dest), SA 0xF9

        Assert.False(id.IsPdu2);
        Assert.Equal(J1939Pgn.Request, id.Pgn);
        Assert.Equal(0x00u, id.Ps); // PS is the destination address, not part of the PGN
    }

    [Fact]
    public void Identifier_RoundTrips()
    {
        var raw = J1939Identifier.Build(priority: 3, edp: 0, dp: 0, pf: 0xFE, ps: 0xCA, sa: 0x00);
        Assert.Equal(0x0CFECA00u, raw);

        var id = J1939Identifier.Parse(raw);
        Assert.Equal(3u, id.Priority);
        Assert.Equal(J1939Pgn.Dm1, id.Pgn);
    }

    // ---- DTC 4-byte codec (SAE J1939-73 §5.7.1.7.1) ----

    [Fact]
    public void Dtc_Encode_MatchesDerivedBytes()
    {
        // SPN 110, FMI 0, OC 1, CM 0.
        // value = (110 << 13) | (0 << 8) | (1 << 1) | 0 = 0xDC002 → little-endian [02 C0 0D 00].
        var bytes = J1939DtcCodec.Encode(110, 0, occurrenceCount: 1, conversionMethod: false);

        Assert.Equal(new byte[] { 0x02, 0xC0, 0x0D, 0x00 }, bytes);
    }

    [Fact]
    public void Dtc_Decode_MatchesDerivedBytes()
    {
        // SPN 639, FMI 9, OC 1, CM 0.
        // value = (639 << 13) | (9 << 8) | (1 << 1) | 0 = 0x4FE902 → little-endian [02 E9 4F 00].
        var dtc = J1939DtcCodec.Decode(new byte[] { 0x02, 0xE9, 0x4F, 0x00 });

        Assert.Equal(639, dtc.Spn);
        Assert.Equal(9, dtc.Fmi);
        Assert.Equal(1, dtc.OccurrenceCount);
        Assert.False(dtc.ConversionMethod);
    }

    [Theory]
    [InlineData(110, 3)]
    [InlineData(94, 4)]
    [InlineData(639, 9)]
    [InlineData(175, 1)]
    [InlineData(524287, 31)] // max SPN + max FMI
    public void Dtc_RoundTrips(int spn, int fmi)
    {
        var decoded = J1939DtcCodec.Decode(J1939DtcCodec.Encode(spn, fmi));
        Assert.Equal(spn, decoded.Spn);
        Assert.Equal(fmi, decoded.Fmi);
    }

    // ---- DM1 / DM2 (SAE J1939-73 §5.7.1 / §5.7.2) ----

    [Fact]
    public void Dm1_Decode_LampStatusAndActiveDtcs()
    {
        // Lamp byte 1 = 0x40 → MIL on (bits 7-6 = 01). Two DTCs: SPN 110 FMI 0 and SPN 639 FMI 9.
        var payload = new byte[]
        {
            0x40, 0x00,                         // lamp status (byte 2 = 0x00, no flash lamps)
            0x02, 0xC0, 0x0D, 0x00,             // SPN 110 FMI 0
            0x02, 0xE9, 0x4F, 0x00,             // SPN 639 FMI 9
        };

        var message = J1939DtcCodec.DecodeDmMessage(payload, isDm2: false);

        Assert.False(message.IsPreviouslyActive);
        Assert.True(message.Lamps.MalfunctionIndicatorLamp);
        Assert.False(message.Lamps.RedStopLamp);
        Assert.Equal(2, message.Dtcs.Count);
        Assert.Equal((110, 0), (message.Dtcs[0].Spn, message.Dtcs[0].Fmi));
        Assert.Equal((639, 9), (message.Dtcs[1].Spn, message.Dtcs[1].Fmi));
    }

    [Fact]
    public void Dm2_Decode_ProducesStoredStatus()
    {
        var payload = new byte[] { 0x00, 0x00, 0x02, 0xC0, 0x0D, 0x00 };

        var readouts = J1939DtcCodec.ToReadouts(J1939DtcCodec.DecodeDmMessage(payload, isDm2: true));

        var readout = Assert.Single(readouts);
        Assert.Equal(DtcStatus.Stored, readout.Status);
        Assert.Equal("SPN 110 FMI 0", readout.Code);
        Assert.Equal(ProtocolType.J1939, readout.Protocol);
        Assert.Equal(110, readout.Spn);
        Assert.Equal(0, readout.Fmi);
        Assert.False(readout.IsSimulated);
    }

    [Fact]
    public void Dm1_Decode_ProducesActiveStatusAndCanonicalCode()
    {
        var readouts = J1939DtcCodec.ToReadouts(
            J1939DtcCodec.DecodeDmMessage(new byte[] { 0x00, 0x00, 0x02, 0xE9, 0x4F, 0x00 }, isDm2: false));

        var readout = Assert.Single(readouts);
        Assert.Equal(DtcStatus.Active, readout.Status);
        Assert.Equal("SPN 639 FMI 9", readout.Code);
    }

    // ---- DM11 clear (SAE J1939-73 §5.7.8) ----

    [Fact]
    public void Dm11_ClearAll_IsTwoLampBytesOnly()
    {
        var payload = J1939Dm11.Encode(); // no DTC list = clear all active DTCs
        Assert.Equal(new byte[] { 0x00, 0x00 }, payload);
    }

    [Fact]
    public void Dm11_ClearSpecific_EncodesDtcEntriesWithOcAndCmSet()
    {
        var payload = J1939Dm11.Encode(new[] { (Spn: 110, Fmi: 0) });

        // [lamp0, lamp1, dtc0(4)] where dtc0[0] = 0xFF (OC=0x7F, CM=1).
        Assert.Equal(6, payload.Length);
        Assert.Equal(0x00, payload[0]);
        Assert.Equal(0x00, payload[1]);
        Assert.Equal(0xFF, payload[2]); // OC=all-ones, CM=1
    }

    // ---- NAME / address claim (SAE J1939-81 §4.2.1) ----

    [Fact]
    public void Name_RoundTrips()
    {
        var name = new J1939Name(
            ArbitraryAddressCapable: true,
            IndustryGroup: 0,            // Global
            VehicleSystemInstance: 0,
            VehicleSystem: 0,            // Non-specific system
            Function: 1,                 // Engine
            FunctionInstance: 0,
            EcuInstance: 0,
            ManufacturerCode: 74,
            IdentityNumber: 1234567);

        var decoded = J1939Name.Decode(name.Encode());

        Assert.Equal(name.ArbitraryAddressCapable, decoded.ArbitraryAddressCapable);
        Assert.Equal(name.IndustryGroup, decoded.IndustryGroup);
        Assert.Equal(name.VehicleSystem, decoded.VehicleSystem);
        Assert.Equal(name.Function, decoded.Function);
        Assert.Equal(name.FunctionInstance, decoded.FunctionInstance);
        Assert.Equal(name.EcuInstance, decoded.EcuInstance);
        Assert.Equal(name.ManufacturerCode, decoded.ManufacturerCode);
        Assert.Equal(name.IdentityNumber, decoded.IdentityNumber);
    }

    // ---- Transport protocol reassembly (SAE J1939-21 §5.10) ----

    [Fact]
    public void TransportProtocol_ReassemblesBamDm1()
    {
        var transport = new J1939TransportProtocol();

        // DM1 payload (10 bytes): 2 lamp + 2 DTCs.
        var dm1 = new byte[] { 0x40, 0x00, 0x02, 0xC0, 0x0D, 0x00, 0x02, 0xE9, 0x4F, 0x00 };

        // BAM announcement: control 0x20, size 10, 2 packets, PGN 65226 (0xFECA).
        var bamId = J1939Identifier.Build(6, 0, 0, 0xEC, 0xFF, 0x00);
        Assert.Null(transport.Feed(bamId, new byte[] { 0x20, 0x0A, 0x00, 0x02, 0xFF, 0xCA, 0xFE, 0x00 }));

        // Two data packets (destination = broadcast 0xFF).
        var dtId = J1939Identifier.Build(6, 0, 0, 0xEB, 0xFF, 0x00);
        Assert.Null(transport.Feed(dtId, new byte[] { 0x01, 0x40, 0x00, 0x02, 0xC0, 0x0D, 0x00, 0x02 }));
        var completed = transport.Feed(dtId, new byte[] { 0x02, 0xE9, 0x4F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF });

        Assert.NotNull(completed);
        Assert.Equal(J1939Pgn.Dm1, completed!.Value.Pgn);
        Assert.Equal(0x00, completed.Value.SourceAddress);
        Assert.Equal(dm1, completed.Value.Data);
    }

    [Fact]
    public void TransportProtocol_RequestPgnMessage()
    {
        var id = J1939PgnRequest.BuildId(destinationAddress: 0x00, sourceAddress: 0xF9);
        Assert.Equal(0x18EA00F9u, id);

        var payload = J1939PgnRequest.Encode(J1939Pgn.Dm1);
        Assert.Equal(J1939Pgn.Dm1, J1939PgnRequest.Decode(payload));
    }

    // ---- Live data extraction (SAE J1939-71, Appendix B SPN definitions) ----

    [Fact]
    public void LiveData_ExtractsEec1()
    {
        // EEC1 (PGN 61444): byte 2 = driver demand (% + 125), byte 3 = actual (% + 125),
        // bytes 4-5 = engine speed (0.125 rpm/bit). 1500 rpm → 12000 → 0x2EE0 (LE: E0 2E).
        var payload = new byte[] { 0x00, 0xAF, 0x9B, 0xE0, 0x2E, 0x00, 0x00, 0x00 };

        var values = J1939LiveData.Extract(J1939Pgn.ElectronicEngineController1, payload);

        Assert.Contains(values, v => v.Spn == 190 && v.Value == 1500.0 && v.Unit == "rpm" && v.Name == "Engine Speed");
        Assert.Contains(values, v => v.Spn == 512 && v.Value == 50.0);   // 0xAF = 175, 175 - 125 = 50
        Assert.Contains(values, v => v.Spn == 513 && v.Value == 30.0);   // 0x9B = 155, 155 - 125 = 30
    }

    [Fact]
    public void LiveData_ExtractsEngineTemperature()
    {
        // ET1 (PGN 65262): byte 1 = coolant temp (1 °C/bit, offset -40). 85 °C → 125 → 0x7D.
        var payload = new byte[] { 0x7D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        var values = J1939LiveData.Extract(J1939Pgn.EngineTemperature1, payload);

        Assert.Contains(values, v => v.Spn == 110 && v.Value == 85.0 && v.Unit == "°C");
    }

    [Fact]
    public void LiveData_UnknownPgn_ReturnsEmpty()
    {
        Assert.Empty(J1939LiveData.Extract(0x00F123, new byte[8]));
    }
}
