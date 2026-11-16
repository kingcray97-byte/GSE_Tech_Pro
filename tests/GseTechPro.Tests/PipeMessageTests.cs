using GseTechPro.Devices.Ipc;
using Xunit;

namespace GseTechPro.Tests;

public class PipeMessageTests
{
    [Fact]
    public async Task WriteRead_RoundTrip()
    {
        var stream = new MemoryStream();
        
        // Write a JSON message
        var payload = "{\"type\":\"request\",\"id\":1,\"data\":{\"protocol\":\"J1939\"}}";
        await PipeMessage.WriteAsync(stream, payload);

        // Reset position and read it back
        stream.Position = 0;
        var readBack = await PipeMessage.ReadAsync(stream);

        Assert.Equal(payload, readBack);
    }

    [Fact]
    public async Task WriteRead_EmptyString()
    {
        var stream = new MemoryStream();
        
        // Empty string is valid JSON (technically not, but the framing should handle it)
        await PipeMessage.WriteAsync(stream, "");
        stream.Position = 0;
        
        var readBack = await PipeMessage.ReadAsync(stream);
        Assert.Equal("", readBack);
    }

    [Fact]
    public async Task WriteRead_LargePayload()
    {
        var stream = new MemoryStream();
        
        // Large payload (1 MB of JSON-like data)
        var largeData = new string('x', 1_000_000);
        var payload = $"{{\"data\":\"{largeData}\"}}";
        await PipeMessage.WriteAsync(stream, payload);

        stream.Position = 0;
        var readBack = await PipeMessage.ReadAsync(stream);

        Assert.Equal(payload, readBack);
    }

    [Fact]
    public async Task WriteRead_MultipleMessages()
    {
        var stream = new MemoryStream();
        
        // Write multiple messages sequentially
        for (int i = 0; i < 5; i++)
        {
            await PipeMessage.WriteAsync(stream, $"{{\"seq\":{i}}}");
        }

        // Read them all back in order
        stream.Position = 0;
        for (int i = 0; i < 5; i++)
        {
            var readBack = await PipeMessage.ReadAsync(stream);
            Assert.Equal($"{{\"seq\":{i}}}", readBack);
        }
    }

    [Fact]
    public async Task WriteRead_Unicode()
    {
        var stream = new MemoryStream();
        
        // Unicode characters (including emoji)
        var payload = "{\"text\":\"Hello 世界 🚗\"}";
        await PipeMessage.WriteAsync(stream, payload);

        stream.Position = 0;
        var readBack = await PipeMessage.ReadAsync(stream);

        Assert.Equal(payload, readBack);
    }

    [Fact]
    public async Task LengthPrefix_Integrity()
    {
        var stream = new MemoryStream();
        
        // Write a message and inspect the raw bytes
        var payload = "test";
        await PipeMessage.WriteAsync(stream, payload);

        var bytes = stream.ToArray();
        
        // First 4 bytes should be the length prefix (little-endian)
        Assert.Equal(4, BitConverter.ToInt32(bytes[0..4], 0));
        
        // Remaining bytes should be the UTF-8 encoded payload
        var payloadBytes = bytes[4..];
        Assert.Equal("test", System.Text.Encoding.UTF8.GetString(payloadBytes));
    }

    [Fact]
    public async Task Read_MalformedLengthPrefix_Negative()
    {
        // Write a negative length prefix manually
        var stream = new MemoryStream();
        var badPrefix = BitConverter.GetBytes(-1);
        await stream.WriteAsync(badPrefix);

        await Assert.ThrowsAnyAsync<Exception>(() => PipeMessage.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_MalformedLengthPrefix_TooLarge()
    {
        // Write a length prefix larger than MaxMessageBytes
        var stream = new MemoryStream();
        var badPrefix = BitConverter.GetBytes(PipeMessage.MaxMessageBytes + 1);
        await stream.WriteAsync(badPrefix);

        await Assert.ThrowsAnyAsync<Exception>(() => PipeMessage.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_IncompletePayload_EndOfStream()
    {
        // Write a length prefix but not enough payload bytes
        var stream = new MemoryStream();
        var prefix = BitConverter.GetBytes(100); // Claim 100 bytes
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(new byte[50]);   // But only write 50

        await Assert.ThrowsAnyAsync<Exception>(() => PipeMessage.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_IncompleteLengthPrefix_EndOfStream()
    {
        // Write only part of the length prefix
        var stream = new MemoryStream();
        await stream.WriteAsync(new byte[2]); // Only 2 bytes, need 4

        await Assert.ThrowsAnyAsync<Exception>(() => PipeMessage.ReadAsync(stream));
    }

    [Fact]
    public async Task WriteRead_NewlinesAndSpecialChars()
    {
        var stream = new MemoryStream();
        
        // JSON with newlines and special characters
        var payload = "{\"text\":\"Line 1\r\nLine 2\tTabbed\"}";
        await PipeMessage.WriteAsync(stream, payload);

        stream.Position = 0;
        var readBack = await PipeMessage.ReadAsync(stream);

        Assert.Equal(payload, readBack);
    }

    [Fact]
    public async Task WriteRead_MaxSizePayload()
    {
        // Test with a payload close to MaxMessageBytes (but not exactly, to avoid memory issues)
        var stream = new MemoryStream();
        
        var largeData = new string('a', 1024 * 1024); // 1 MB
        var payload = $"{{\"data\":\"{largeData}\"}}";
        await PipeMessage.WriteAsync(stream, payload);

        stream.Position = 0;
        var readBack = await PipeMessage.ReadAsync(stream);

        Assert.Equal(payload, readBack);
    }
}
