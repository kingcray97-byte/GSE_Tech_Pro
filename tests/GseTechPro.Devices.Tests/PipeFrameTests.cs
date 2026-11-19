using System.Text;
using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Ipc;
using Xunit;

namespace GseTechPro.Devices.Tests;

public class PipeFrameTests
{
    [Fact]
    public async Task Json_RoundTrip_PreservesKindAndPayload()
    {
        var stream = new MemoryStream();
        const string json = "{\"type\":\"request\"}";
        await PipeFrame.WriteJsonAsync(stream, json);

        stream.Position = 0;
        var frame = await PipeFrame.ReadAsync(stream);

        Assert.Equal(HostFrameKind.Json, frame.Kind);
        Assert.Equal(json, frame.GetJson());
    }

    [Fact]
    public async Task Binary_RoundTrip_PreservesBytes()
    {
        var stream = new MemoryStream();
        var payload = new byte[] { 0x01, 0x02, 0x00, 0xFF, 0x7F, 0x80 };
        await PipeFrame.WriteBinaryAsync(stream, payload);

        stream.Position = 0;
        var frame = await PipeFrame.ReadAsync(stream);

        Assert.Equal(HostFrameKind.Binary, frame.Kind);
        Assert.Equal<byte>(payload, frame.Payload);
    }

    [Fact]
    public async Task MultipleFrames_ReadInOrder()
    {
        var stream = new MemoryStream();
        await PipeFrame.WriteJsonAsync(stream, "{\"n\":1}");
        await PipeFrame.WriteBinaryAsync(stream, new byte[] { 1, 2, 3 });
        await PipeFrame.WriteJsonAsync(stream, "{\"n\":2}");

        stream.Position = 0;

        var f1 = await PipeFrame.ReadAsync(stream);
        var f2 = await PipeFrame.ReadAsync(stream);
        var f3 = await PipeFrame.ReadAsync(stream);

        Assert.Equal(HostFrameKind.Json, f1.Kind);
        Assert.Equal("{\"n\":1}", f1.GetJson());
        Assert.Equal(HostFrameKind.Binary, f2.Kind);
        Assert.Equal<byte>(new byte[] { 1, 2, 3 }, f2.Payload);
        Assert.Equal(HostFrameKind.Json, f3.Kind);
    }

    [Fact]
    public async Task EmptyPayload_RoundTrip()
    {
        var stream = new MemoryStream();
        await PipeFrame.WriteBinaryAsync(stream, ReadOnlyMemory<byte>.Empty);

        stream.Position = 0;
        var frame = await PipeFrame.ReadAsync(stream);

        Assert.Equal(HostFrameKind.Binary, frame.Kind);
        Assert.Empty(frame.Payload);
    }

    [Fact]
    public async Task Read_TooLargeLength_Throws()
    {
        var stream = new MemoryStream();
        stream.WriteByte(HostFrameKind.Json);
        stream.Write(BitConverter.GetBytes(HostProtocol.MaxFrameBytes + 1));
        stream.Position = 0;

        await Assert.ThrowsAnyAsync<Exception>(() => PipeFrame.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_NegativeLength_Throws()
    {
        var stream = new MemoryStream();
        stream.WriteByte(HostFrameKind.Json);
        stream.Write(BitConverter.GetBytes(-1));
        stream.Position = 0;

        await Assert.ThrowsAnyAsync<Exception>(() => PipeFrame.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_IncompletePayload_Throws()
    {
        var stream = new MemoryStream();
        stream.WriteByte(HostFrameKind.Json);
        stream.Write(BitConverter.GetBytes(10));
        stream.Write(new byte[5]);
        stream.Position = 0;

        await Assert.ThrowsAnyAsync<Exception>(() => PipeFrame.ReadAsync(stream));
    }

    [Fact]
    public async Task UnicodeJson_RoundTrip()
    {
        var stream = new MemoryStream();
        const string json = "{\"text\":\"Hello 世界 🚗\"}";
        await PipeFrame.WriteJsonAsync(stream, json);

        stream.Position = 0;
        var frame = await PipeFrame.ReadAsync(stream);

        Assert.Equal(json, Encoding.UTF8.GetString(frame.Payload));
    }
}
