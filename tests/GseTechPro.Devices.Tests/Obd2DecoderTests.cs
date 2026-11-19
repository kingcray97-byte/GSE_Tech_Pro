using System.Text;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Protocols.Obd2;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// Unit tests for the OBD-II (SAE J1979 / ISO 15031-5) decoder. DTC byte packing per ISO 15031-6; PID
/// scalings per SAE J1979 Annex B (standard PIDs).
/// </summary>
public class Obd2DecoderTests
{
    // ---- DTC codec (ISO 15031-6 2-byte format) ----

    [Fact]
    public void DtcCodec_Decode_P0230()
    {
        // P0230 → byte1 = (0<<6)|(0<<4)|2 = 0x02, byte2 = (3<<4)|0 = 0x30.
        Assert.Equal("P0230", Obd2DtcCodec.Decode(0x02, 0x30));
    }

    [Fact]
    public void DtcCodec_Encode_P0230()
    {
        var (b1, b2) = Obd2DtcCodec.Encode("P0230");
        Assert.Equal(0x02, b1);
        Assert.Equal(0x30, b2);
    }

    [Theory]
    [InlineData("P0230")]
    [InlineData("P0300")]
    [InlineData("P0118")]
    [InlineData("C0201")]
    [InlineData("U0100")]
    public void DtcCodec_RoundTrips(string code)
    {
        var (b1, b2) = Obd2DtcCodec.Encode(code);
        Assert.Equal(code, Obd2DtcCodec.Decode(b1, b2));
    }

    // ---- Mode $03 DTC read (ISO 15031-5 §6.5.3.3) ----

    [Fact]
    public void Mode03_DecodesDtcList()
    {
        // 0x43 [count=2] [P0230 = 02 30] [P0300 = 03 00]
        var readouts = Obd2Decoder.DecodeMode03Response(new byte[] { 0x43, 0x02, 0x02, 0x30, 0x03, 0x00 });

        Assert.Equal(2, readouts.Count);
        Assert.Equal("P0230", readouts[0].Code);
        Assert.Equal("P0300", readouts[1].Code);
        Assert.All(readouts, r => Assert.Equal(ProtocolType.OBD2, r.Protocol));
        Assert.All(readouts, r => Assert.Equal(DtcStatus.Active, r.Status));
        Assert.All(readouts, r => Assert.Null(r.Spn));
        Assert.All(readouts, r => Assert.Null(r.Fmi));
    }

    // ---- Mode $01 PID scaling (SAE J1979 Annex B) ----

    [Fact]
    public void Mode01_EngineRpm()
    {
        // PID 0x0C: ((A×256)+B)/4. 1500 rpm → 6000 → A=0x17, B=0x70.
        var value = Obd2Decoder.DecodePidResponse(new byte[] { 0x41, 0x0C, 0x17, 0x70 });

        Assert.Equal("Engine RPM", value.Name);
        Assert.Equal(1500.0, value.Value);
        Assert.Equal("rpm", value.Unit);
    }

    [Fact]
    public void Mode01_CoolantTemperature()
    {
        // PID 0x05: A − 40 °C. 85 °C → 125 → 0x7D.
        var value = Obd2Decoder.DecodePidResponse(new byte[] { 0x41, 0x05, 0x7D });

        Assert.Equal("Engine Coolant Temperature", value.Name);
        Assert.Equal(85.0, value.Value);
        Assert.Equal("°C", value.Unit);
    }

    [Fact]
    public void Mode01_UnknownPid_ReturnsRaw()
    {
        var value = Obd2Decoder.DecodePidResponse(new byte[] { 0x41, 0x99, 0x2A });

        Assert.False(value.Known);
        Assert.Equal(0x99, value.Pid);
    }

    // ---- Mode $09 VIN (ISO 15031-5 §6.9) ----

    [Fact]
    public void Mode09_DecodesVin()
    {
        const string vin = "1FTNF21L43EA12345";
        var vinBytes = Encoding.ASCII.GetBytes(vin);
        var response = new byte[4 + vinBytes.Length];
        response[0] = 0x49;
        response[1] = 0x02;
        response[2] = 0x01;
        response[3] = (byte)vinBytes.Length;
        vinBytes.CopyTo(response, 4);

        Assert.Equal(vin, Obd2Decoder.DecodeVin(response));
    }

    [Fact]
    public void Mode09_NonVinResponse_ReturnsNull()
    {
        Assert.Null(Obd2Decoder.DecodeVin(new byte[] { 0x49, 0x04, 0x01, 0x00 }));
    }

    // ---- Mode $04 clear request ----

    [Fact]
    public void Mode04_ClearRequest()
    {
        Assert.Equal(new byte[] { 0x04 }, Obd2Decoder.BuildRequest(Obd2Decoder.Mode04));
    }
}
