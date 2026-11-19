using GseTechPro.Core.Enums;
using GseTechPro.Devices.Protocols.IsoTp;
using GseTechPro.Devices.Protocols.Uds;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// Unit tests for ISO 15765-2 (ISO-TP) framing and the UDS (ISO 14229-1) read/clear diagnostic subset.
/// </summary>
public class IsoTpUdsDecoderTests
{
    // ---- ISO-TP (ISO 15765-2) ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(20)]
    [InlineData(100)]
    public void IsoTp_RoundTrips(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
            payload[i] = (byte)i;

        var frames = IsoTpEncoder.Encode(payload);

        var decoder = new IsoTpDecoder();
        byte[]? result = null;
        foreach (var frame in frames)
        {
            result ??= decoder.Feed(frame);
            if (result is not null)
                break;
        }

        Assert.NotNull(result);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void IsoTp_SingleFrame()
    {
        var frames = IsoTpEncoder.Encode(new byte[] { 0x01, 0x23, 0x45 });
        var frame = Assert.Single(frames);
        Assert.Equal(new byte[] { 0x03, 0x01, 0x23, 0x45 }, frame);

        Assert.Equal(new byte[] { 0x01, 0x23, 0x45 }, new IsoTpDecoder().Feed(frame));
    }

    [Fact]
    public void IsoTp_FirstFrameCarries12BitLength()
    {
        // 300 bytes: first-frame PCI encodes length 300 = 0x012C → FF[0] = 0x11, FF[1] = 0x2C.
        // FF (6 payload) + 294/7 = 42 consecutive frames.
        var frames = IsoTpEncoder.Encode(new byte[300]);
        var first = frames[0];

        Assert.Equal(0x11, first[0]);
        Assert.Equal(0x2C, first[1]);
        Assert.Equal(43, frames.Count);
    }

    [Fact]
    public void IsoTp_ConsecutiveFrameSequenceWraps()
    {
        // 200 bytes → FF(6) + ceil(194/7)=28 CFs. Sequence runs 1..15 then wraps to 1.
        var frames = IsoTpEncoder.Encode(new byte[200]);

        Assert.Equal(0x1, frames[0][0] >> 4);           // first frame
        Assert.Equal(0x2, frames[1][0] >> 4);           // first consecutive frame
        Assert.Equal(0x21, frames[1][0]);                // sequence 1
        Assert.Equal(0x2F, frames[15][0]);               // sequence 15 (0x20 | 0x0F)
        Assert.Equal(0x21, frames[16][0]);               // wraps back to sequence 1
    }

    // ---- UDS (ISO 14229-1) ----

    [Fact]
    public void Uds_ReadDtcInformationRequest()
    {
        Assert.Equal(new byte[] { 0x19, 0x02, 0xFF }, UdsDecoder.EncodeReadDtcInformation());
    }

    [Fact]
    public void Uds_ClearDiagnosticInformationRequest()
    {
        Assert.Equal(new byte[] { 0x14, 0xFF, 0xFF, 0xFF }, UdsDecoder.EncodeClearDiagnosticInformation());
    }

    [Fact]
    public void Uds_DecodeReadDtcInformation_ThreeByteFormat_Confirmed()
    {
        // [0x59][0x02][statusMask][format=0x00][DTC 3 bytes = 02 30 00][status = 0x08 (confirmed)]
        var readouts = UdsDecoder.DecodeReadDtcInformation(
            new byte[] { 0x59, 0x02, 0xFF, 0x00, 0x02, 0x30, 0x00, 0x08 });

        var readout = Assert.Single(readouts);
        Assert.Equal("P0230", readout.Code);
        Assert.Equal(ProtocolType.UDS, readout.Protocol);
        Assert.Equal(DtcStatus.Active, readout.Status); // confirmedDTC bit set
    }

    [Fact]
    public void Uds_DecodeReadDtcInformation_TwoByteFormat_Pending()
    {
        // format 0x01 (2-byte DTC) + status 0x04 (pending).
        var readouts = UdsDecoder.DecodeReadDtcInformation(
            new byte[] { 0x59, 0x02, 0xFF, 0x01, 0x02, 0x30, 0x04 });

        var readout = Assert.Single(readouts);
        Assert.Equal("P0230", readout.Code);
        Assert.Equal(DtcStatus.Pending, readout.Status);
    }

    [Fact]
    public void Uds_StatusByte_Mapping()
    {
        Assert.Equal(DtcStatus.Active, UdsDtcStatus.ToDtcStatus(0x08));   // confirmed
        Assert.Equal(DtcStatus.Pending, UdsDtcStatus.ToDtcStatus(0x04));  // pending
        Assert.Equal(DtcStatus.Stored, UdsDtcStatus.ToDtcStatus(0x00));   // neither
        Assert.Equal(DtcStatus.Active, UdsDtcStatus.ToDtcStatus(0x0C));   // confirmed + pending → confirmed wins
    }

    [Fact]
    public void Uds_UnsupportedDtcFormat_ReturnsEmpty()
    {
        // format 0x02 (SAE J2012-DA, 4-byte) is not decoded in this round.
        var readouts = UdsDecoder.DecodeReadDtcInformation(
            new byte[] { 0x59, 0x02, 0xFF, 0x02, 0x02, 0x30, 0x00, 0x00, 0x08 });

        Assert.Empty(readouts);
    }
}
