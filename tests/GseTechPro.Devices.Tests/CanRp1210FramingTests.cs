using GseTechPro.Devices.Protocols;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// Unit tests for the big-endian CAN-id framing shared by the J2534 and RP1210 adapters. Byte sequences
/// are derived from the published layouts: SAE J2534-1 §6.4.2/§7.2.3.1 (<c>Data[0..3]</c> = CAN id,
/// <c>Data[4..]</c> = payload) and TMC RP1210C §10.3/§10.4 (4-byte timestamp prefix, then
/// <c>[frame-type][id BE(4)][data]</c>).
/// </summary>
public class CanRp1210FramingTests
{
    // ---- CAN id big-endian (SAE J2534-1 §7.2.3.1) ----

    [Fact]
    public void CanFraming_WriteId_IsBigEndian()
    {
        // EEC1 (PGN 61444) from engine SA 0x00 at priority 6 → raw 0x18F00400.
        var bytes = new byte[4];
        CanFraming.WriteId(0x18F00400u, bytes);

        Assert.Equal(new byte[] { 0x18, 0xF0, 0x04, 0x00 }, bytes);
    }

    [Fact]
    public void CanFraming_ReadId_IsBigEndian()
    {
        Assert.Equal(0x0CFECA00u, CanFraming.ReadId(new byte[] { 0x0C, 0xFE, 0xCA, 0x00 }));
    }

    [Theory]
    [InlineData(0x18F00400u)] // EEC1
    [InlineData(0x0CFECA00u)] // DM1
    [InlineData(0x18EA00F9u)] // Request (PDU1)
    [InlineData(0x000007E8u)] // OBD-II 11-bit response id, zero-extended
    [InlineData(0x1FFFFFFFu)] // max 29-bit id
    public void CanFraming_RoundTrips29BitId(uint id)
    {
        var bytes = new byte[4];
        CanFraming.WriteId(id, bytes);
        Assert.Equal(id, CanFraming.ReadId(bytes));
    }

    [Fact]
    public void CanFraming_Encode_PutsIdThenPayload()
    {
        // Frame = [id BE(4)][payload]; the J2534 Data layout (DataSize = 4 + payload).
        var frame = CanFraming.Encode(0x18F00400u, new byte[] { 0xAA, 0xBB, 0xCC });

        Assert.Equal(new byte[] { 0x18, 0xF0, 0x04, 0x00, 0xAA, 0xBB, 0xCC }, frame);
    }

    // ---- RP1210 timestamp + CAN framing (TMC RP1210C §10.3/§10.4) ----

    [Fact]
    public void Rp1210Framing_StripTimestamp_RemovesFourBytePrefix()
    {
        // [ts(4) = 0x01 0x02 0x03 0x04][body]
        var message = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB };

        Assert.Equal(new byte[] { 0xAA, 0xBB }, Rp1210Framing.StripTimestamp(message).ToArray());
    }

    [Fact]
    public void Rp1210Framing_StripTimestamp_TooShortIsEmpty()
    {
        Assert.True(Rp1210Framing.StripTimestamp(new byte[] { 0x01, 0x02, 0x03 }).IsEmpty);
    }

    [Fact]
    public void Rp1210Framing_TryParseCan_Extracts29BitIdAndData()
    {
        // [frame-type = 0x01 (29-bit)][id BE(4) = 0x18F00400][data = 0xDE 0xAD]
        var body = new byte[] { 0x01, 0x18, 0xF0, 0x04, 0x00, 0xDE, 0xAD };

        var ok = Rp1210Framing.TryParseCan(body, out var id, out var data);

        Assert.True(ok);
        Assert.Equal(0x18F00400u, id);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, data.ToArray());
    }

    [Fact]
    public void Rp1210Framing_TryParseCan_RejectsNon29BitFrameType()
    {
        // frame-type 0x00 = 11-bit, which the J1939 path does not consume.
        var body = new byte[] { 0x00, 0x18, 0xF0, 0x04, 0x00 };

        Assert.False(Rp1210Framing.TryParseCan(body, out _, out _));
    }

    [Fact]
    public void Rp1210Framing_BuildCan_RoundTrips()
    {
        var body = Rp1210Framing.BuildCan(0x0CFECA00u, new byte[] { 0x40, 0x00 });

        Assert.True(Rp1210Framing.TryParseCan(body, out var id, out var data));
        Assert.Equal(0x0CFECA00u, id);
        Assert.Equal(new byte[] { 0x40, 0x00 }, data.ToArray());
    }
}
