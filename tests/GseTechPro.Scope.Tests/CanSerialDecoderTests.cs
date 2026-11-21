using GseTechPro.Core.Contracts;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.Scope.Tests;

/// <summary>Phase 10D.1: CAN serial decoding from a scope trace.</summary>
public sealed class CanSerialDecoderTests
{
    /// <summary>Turns bus bits (true = recessive) into a CAN-H trace: 2.5 V recessive, 3.5 V dominant.</summary>
    private static double[] CanHigh(IEnumerable<bool> bits, int samplesPerBit, int idleBefore = 20, int idleAfter = 20)
    {
        var all = Enumerable.Repeat(true, idleBefore).Concat(bits).Concat(Enumerable.Repeat(true, idleAfter));
        return all.SelectMany(b => Enumerable.Repeat(b ? 2.5 : 3.5, samplesPerBit)).ToArray();
    }

    [Fact]
    public void Crc15_MatchesKnownValue()
    {
        // Standard frame ID 0x123, DLC 1, data 0x01: SOF..data bits, CRC per ISO 11898-1 polynomial 0x4599.
        var bits = CanSerialDecoder.EncodeFrame(0x123, false, new byte[] { 0x01 });
        var r = CanSerialDecoder.Decode(CanHigh(bits, 10), 2_500_000, 250_000, dominantHigh: true);
        var f = Assert.Single(r.Frames);
        Assert.True(f.CrcOk);
    }

    [Theory]
    [InlineData(0x123u, false, new byte[] { 0x01 })]
    [InlineData(0x7FFu, false, new byte[] { })]
    [InlineData(0x0CF00400u, true, new byte[] { 0xF0, 0x7D, 0x7D, 0xE0, 0x2E, 0x00, 0xF0, 0x7D })]
    [InlineData(0x18FEEE00u, true, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F, 0xFF, 0xFF })] // long runs: many stuff bits
    public void EncodedFrame_DecodesBack(uint id, bool extended, byte[] data)
    {
        var bits = CanSerialDecoder.EncodeFrame(id, extended, data);
        var r = CanSerialDecoder.Decode(CanHigh(bits, 20), 5_000_000, 250_000, dominantHigh: true);
        Assert.True(r.IsOk, r.Error);
        var f = Assert.Single(r.Frames);
        Assert.True(f.IsOk, f.Error);
        Assert.Equal(id, f.Identifier);
        Assert.Equal(extended, f.IsExtended);
        Assert.Equal(data.Length, f.Dlc);
        Assert.Equal(data, f.Data);
        Assert.True(f.Acknowledged);
        Assert.Equal(20 * 4e-6, f.StartSeconds, 9);
    }

    [Fact]
    public void BackToBackFrames_OnCanLow_AndSlightlyOffBitRate_AllDecode()
    {
        var bits = new List<bool>();
        for (int i = 0; i < 3; i++)
        {
            bits.AddRange(CanSerialDecoder.EncodeFrame(0x18FEF100u + (uint)i, true, new byte[] { (byte)i, 2, 3, 4, 5, 6, 7, 8 }));
            bits.AddRange(Enumerable.Repeat(true, 3)); // intermission only
        }
        // CAN-L: dominant low. Transmitter 1 % fast against the decoder's bit rate: resync on edges must hold.
        int spb = 20;
        var trace = Enumerable.Repeat(true, 20).Concat(bits).Concat(Enumerable.Repeat(true, 20))
            .SelectMany(b => Enumerable.Repeat(b ? 2.5 : 1.5, spb)).ToArray();
        var r = CanSerialDecoder.Decode(trace, 5_000_000 * 1.01, 250_000, dominantHigh: false);
        Assert.Equal(3, r.Frames.Count);
        Assert.All(r.Frames, f => Assert.True(f.IsOk, f.Error));
        Assert.Equal(new uint[] { 0x18FEF100, 0x18FEF101, 0x18FEF102 }, r.Frames.Select(f => f.Identifier));
    }

    [Fact]
    public void FlippedBit_IsReportedAsCrcOrStuffError_AndNextFrameStillDecodes()
    {
        var bad = CanSerialDecoder.EncodeFrame(0x0CF00400, true, new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 });
        bad[60] = !bad[60]; // in the data field
        var good = CanSerialDecoder.EncodeFrame(0x18FEF100, true, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var bits = bad.Concat(Enumerable.Repeat(true, 15)).Concat(good);
        var r = CanSerialDecoder.Decode(CanHigh(bits, 20), 5_000_000, 250_000, dominantHigh: true);
        Assert.Equal(2, r.Frames.Count);
        Assert.False(r.Frames[0].IsOk);
        Assert.Matches("CRC|Stuff|Form", r.Frames[0].Error);
        Assert.True(r.Frames[1].IsOk, r.Frames[1].Error);
        Assert.Equal(1, r.ErrorFrameCount);
    }

    [Fact]
    public void NoAck_CutOff_AndPartialFrameAtStart()
    {
        var frame = CanSerialDecoder.EncodeFrame(0x123, false, new byte[] { 0xAA, 0x55 }, acknowledged: false);
        var r = CanSerialDecoder.Decode(CanHigh(frame, 20), 5_000_000, 250_000, dominantHigh: true);
        var f = Assert.Single(r.Frames);
        Assert.True(f.IsOk, f.Error);
        Assert.False(f.Acknowledged);

        // Cut off half way.
        var cut = CanHigh(frame, 20, idleAfter: 0);
        var c = CanSerialDecoder.Decode(cut[..(cut.Length / 2)], 5_000_000, 250_000, dominantHigh: true);
        Assert.Contains("Cut off", Assert.Single(c.Frames).Error);

        // Capture starts in the middle of a frame (no idle before it): that frame is skipped, the next one decodes.
        var second = CanSerialDecoder.EncodeFrame(0x456, false, new byte[] { 1 });
        var midStart = frame.Skip(10).Concat(Enumerable.Repeat(true, 12)).Concat(second);
        var m = CanSerialDecoder.Decode(CanHigh(midStart, 20, idleBefore: 0), 5_000_000, 250_000, dominantHigh: true);
        Assert.Equal(0x456u, Assert.Single(m.Frames).Identifier);
    }

    [Fact]
    public void TooSlowSampleRate_AndFlatTrace_GivePlainErrors()
    {
        var bits = CanSerialDecoder.EncodeFrame(0x123, false, new byte[] { 1 });
        var slow = CanSerialDecoder.Decode(CanHigh(bits, 2), 500_000, 250_000, dominantHigh: true);
        Assert.Contains("Sample rate too low", slow.Error);

        var flat = CanSerialDecoder.Decode(Enumerable.Repeat(2.5, 1000).ToArray(), 5_000_000, 250_000, true);
        Assert.Contains("No bus activity", flat.Error);
    }

    [Fact]
    public void SimulatorCanFramesPreset_DecodesDifferentially()
    {
        var device = new SimulatedScopeDevice();
        device.ConnectAsync().GetAwaiter().GetResult();
        device.ConfigurePreset(SimulatedSignalPreset.CanBusFrames);
        var request = device.GetPresetCaptureRequest(SimulatedSignalPreset.CanBusFrames);
        device.StartCaptureAsync(request).GetAwaiter().GetResult();
        var capture = device.ReadCaptureAsync().GetAwaiter().GetResult();

        var r = CanSerialDecoder.Decode(capture, new CanDecodeSettings(1, CanSignalSource.Differential, 250_000, 2));
        Assert.True(r.IsOk, r.Error);
        Assert.True(r.IsSimulated);
        var ok = r.Frames.Where(f => f.IsOk).ToList();
        Assert.True(ok.Count >= 6, $"{ok.Count} good frames; errors: {string.Join("; ", r.Frames.Where(f => !f.IsOk).Select(f => f.Error))}");
        Assert.Contains(ok, f => f.Identifier == 0x0CF00400);
        Assert.All(r.Frames.Where(f => !f.IsOk), f => Assert.StartsWith("Cut off", f.Error));

        // CAN-H alone and CAN-L alone give the same frames.
        var h = CanSerialDecoder.Decode(capture, new CanDecodeSettings(1, CanSignalSource.CanHigh, 250_000));
        var l = CanSerialDecoder.Decode(capture, new CanDecodeSettings(2, CanSignalSource.CanLow, 250_000));
        Assert.Equal(r.Frames.Select(f => f.Identifier), h.Frames.Select(f => f.Identifier));
        Assert.Equal(r.Frames.Select(f => f.Identifier), l.Frames.Select(f => f.Identifier));
    }
}
