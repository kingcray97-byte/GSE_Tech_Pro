using System.Buffers.Binary;
using System.Text;
using GseTechPro.Core.Contracts;
using Xunit;

namespace GseTechPro.Scope.Tests;

public sealed class WaveformFileTests
{
    [Fact]
    public async Task WriteAndReadAsync_Stream_RoundTripsHeaderAndSamplesExactly()
    {
        double[] ch1 = [0.0, 1.25, -2.5, 3.75, 5.0];
        double[] ch2 = [10.0, 9.5, 8.25, 7.125, -0.5];

        var original = new ScopeCapture
        {
            SampleCount = 5,
            SampleRateHz = 1_000_000.0,
            TimebaseSecondsPerDivision = 0.005,
            Channels = new Dictionary<int, double[]>
            {
                [1] = ch1,
                [2] = ch2,
            },
        };

        using var ms = new MemoryStream();
        await WaveformFile.WriteAsync(ms, original);

        byte[] rawBytes = ms.ToArray();
        // Verify exact binary layout per ADR-011 §6:
        // 36-byte header + 2 channels * (4-byte channelIndex + 5 * 8-byte doubles) = 36 + 88 = 124 bytes
        Assert.Equal(124, rawBytes.Length);
        Assert.Equal("GSEWAV1\0", Encoding.ASCII.GetString(rawBytes, 0, 8));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan(8, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan(12, 4)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan(16, 4)));
        Assert.Equal(1_000_000.0, BinaryPrimitives.ReadDoubleLittleEndian(rawBytes.AsSpan(20, 8)));
        Assert.Equal(0.005, BinaryPrimitives.ReadDoubleLittleEndian(rawBytes.AsSpan(28, 8)));

        ms.Position = 0;
        ScopeCapture roundTripped = await WaveformFile.ReadAsync(ms);

        Assert.Equal(original.SampleCount, roundTripped.SampleCount);
        Assert.Equal(original.SampleRateHz, roundTripped.SampleRateHz);
        Assert.Equal(original.TimebaseSecondsPerDivision, roundTripped.TimebaseSecondsPerDivision);
        Assert.Equal(2, roundTripped.Channels.Count);
        Assert.Equal(ch1, roundTripped.Channels[1]);
        Assert.Equal(ch2, roundTripped.Channels[2]);
    }

    [Fact]
    public async Task WriteAndReadAsync_FilePath_RoundTripsCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "opencode", "gse-scope-tests", Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(tempDir, "capture.wfm");

        try
        {
            double[] samples = Enumerable.Range(0, 1024).Select(i => Math.Sin(i * 0.01)).ToArray();
            var capture = new ScopeCapture
            {
                SampleCount = samples.Length,
                SampleRateHz = 100_000.0,
                TimebaseSecondsPerDivision = 0.010,
                Channels = new Dictionary<int, double[]> { [1] = samples },
            };

            await WaveformFile.WriteAsync(filePath, capture);
            Assert.True(File.Exists(filePath));

            ScopeCapture loaded = await WaveformFile.ReadAsync(filePath);
            Assert.Equal(capture.SampleCount, loaded.SampleCount);
            Assert.Equal(capture.SampleRateHz, loaded.SampleRateHz);
            Assert.Equal(capture.TimebaseSecondsPerDivision, loaded.TimebaseSecondsPerDivision);
            Assert.Equal(samples, loaded.Channels[1]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_InvalidMagicBytes_ThrowsInvalidDataException()
    {
        byte[] badHeader = new byte[36];
        Encoding.ASCII.GetBytes("BADMAGIC").CopyTo(badHeader, 0);
        using var ms = new MemoryStream(badHeader);

        await Assert.ThrowsAsync<InvalidDataException>(() => WaveformFile.ReadAsync(ms));
    }
}
