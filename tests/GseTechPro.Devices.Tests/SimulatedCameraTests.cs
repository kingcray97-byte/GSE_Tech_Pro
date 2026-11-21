using System.Buffers.Binary;
using System.IO.Compression;
using GseTechPro.Devices.Camera;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>Phase 7.1: simulator camera and its PNG encoder.</summary>
public sealed class SimulatedCameraTests
{
    [Fact]
    public async Task Photos_AreValidPngs_MarkedSimulated_AndDifferPerShot()
    {
        await using var cam = new SimulatedCameraSource();
        var a = await cam.CapturePhotoAsync();
        var b = await cam.CapturePhotoAsync();

        Assert.True(a.IsSimulated);
        Assert.Equal("image/png", a.MimeType);
        Assert.Equal(".png", a.Extension);
        Assert.Equal((640, 480), (a.Width, a.Height));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], a.Bytes[..4]);
        Assert.NotEqual(a.Bytes, b.Bytes);

        // Round trip: IHDR size and the decompressed pixel rows match what was drawn.
        Assert.Equal(640, BinaryPrimitives.ReadInt32BigEndian(a.Bytes.AsSpan(16)));
        Assert.Equal(480, BinaryPrimitives.ReadInt32BigEndian(a.Bytes.AsSpan(20)));
        int idatLen = BinaryPrimitives.ReadInt32BigEndian(a.Bytes.AsSpan(33));
        Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(a.Bytes, 37, 4));
        using var z = new ZLibStream(new MemoryStream(a.Bytes, 41, idatLen), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var rows = raw.ToArray();
        Assert.Equal(480 * (1 + 640 * 3), rows.Length);
        var drawn = SimulatedCameraSource.DrawTestCard(1);
        Assert.Equal(drawn.AsSpan(0, 640 * 3).ToArray(), rows.AsSpan(1, 640 * 3).ToArray());
    }

    [Fact]
    public async Task Factory_ListsOnlyTheSimulator_AndRefusesOthers()
    {
        var f = new SimulatedCameraSourceFactory();
        var list = await f.DiscoverAsync();
        var only = Assert.Single(list);
        Assert.True(only.IsSimulated);
        await using var cam = await f.OpenAsync(only);
        Assert.Equal(SimulatedCameraSource.SimulatorId, cam.Info.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.OpenAsync(new GseTechPro.Core.Abstractions.CameraSourceInfo("x", "Other", false)));
    }

    [Fact]
    public void PngEncoder_RejectsAWrongSizedBuffer() =>
        Assert.Throws<ArgumentException>(() => PngEncoder.EncodeRgb(new byte[10], 2, 2));
}
