using System.Runtime.InteropServices;
using GseTechPro.Core.Contracts;
using GseTechPro.Devices.Can;
using GseTechPro.Devices.Can.Ecom;
using GseTechPro.Devices.Protocols.J1939;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>Phase 10C.1: listen-only CAN bus monitor — simulator, ECOM wrapper (no hardware), factory.</summary>
public sealed class CanBusMonitorTests
{
    [Fact]
    public void Simulator_IsDeterministic_AndTimeOrdered()
    {
        var a = SimulatedCanBusMonitor.FramesBetween(0, 2.0);
        var b = SimulatedCanBusMonitor.FramesBetween(0, 2.0);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Id, b[i].Id);
            Assert.Equal(a[i].Data, b[i].Data);
            Assert.Equal(a[i].TimestampSeconds, b[i].TimestampSeconds);
        }
        Assert.True(a.Zip(a.Skip(1)).All(p => p.First.TimestampSeconds <= p.Second.TimestampSeconds));
        Assert.All(a, f => Assert.True(f.IsSimulated));
    }

    [Fact]
    public void Simulator_WindowsJoinWithoutGapsOrRepeats()
    {
        var whole = SimulatedCanBusMonitor.FramesBetween(0, 3.0);
        var split = SimulatedCanBusMonitor.FramesBetween(0, 1.234)
            .Concat(SimulatedCanBusMonitor.FramesBetween(1.234, 3.0))
            .ToList();

        Assert.Equal(whole.Count, split.Count);
    }

    [Fact]
    public void Simulator_SendsEachMessageAtItsPeriod()
    {
        var frames = SimulatedCanBusMonitor.FramesBetween(0, 10.0);

        foreach (var message in SimulatedCanBusMonitor.Schedule)
        {
            var expected = (int)Math.Round(10.0 / message.PeriodSeconds);
            var actual = frames.Count(f => f.Id == message.Id);
            Assert.InRange(actual, expected - 1, expected);
        }
    }

    [Fact]
    public void Simulator_EngineSpeed_DecodesWithTheJ1939Decoder()
    {
        var eec1 = SimulatedCanBusMonitor.FramesBetween(0, 0.5)
            .First(f => f.IsExtended && J1939Identifier.Parse(f.Id).Pgn == J1939Pgn.ElectronicEngineController1);

        var values = J1939LiveData.Extract(J1939Pgn.ElectronicEngineController1, eec1.Data);
        var speed = Assert.Single(values, v => v.Spn == 190);
        Assert.InRange(speed.Value, 400, 1200);
    }

    [Fact]
    public void Simulator_HasAStandardIdFrame()
    {
        var frames = SimulatedCanBusMonitor.FramesBetween(0, 1.0);
        Assert.Contains(frames, f => !f.IsExtended && f.Id == 0x123 && f.Dlc == 8);
    }

    [Fact]
    public async Task SimulatedMonitor_StreamsFrames_ThenStops()
    {
        await using var monitor = new SimulatedCanBusMonitor();
        await monitor.StartAsync(new CanMonitorOptions { Bitrate = 250_000 });
        Assert.True(monitor.IsRunning);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<CanFrame>();
        while (received.Count < 20)
            received.Add(await monitor.Frames.ReadAsync(cts.Token));

        await monitor.StopAsync();
        Assert.False(monitor.IsRunning);
        Assert.All(received, f => Assert.True(f.IsSimulated));
        Assert.True(monitor.IsSimulated);
    }

    [Fact]
    public async Task Monitor_DropsOldestFrames_WhenTheReaderFallsBehind()
    {
        await using var monitor = new SimulatedCanBusMonitor();
        await monitor.StartAsync(new CanMonitorOptions { BufferCapacity = 5 });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.DroppedFrameCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await monitor.StopAsync();

        Assert.True(monitor.DroppedFrameCount > 0);
        var buffered = 0;
        while (monitor.Frames.TryRead(out _))
            buffered++;
        Assert.InRange(buffered, 1, 5);
    }

    [Fact]
    public async Task Monitor_RefusesAnUnsupportedBitrate()
    {
        await using var monitor = new SimulatedCanBusMonitor();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            monitor.StartAsync(new CanMonitorOptions { Bitrate = 333_333 }));
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public void MonitorInterface_HasNoTransmitCall()
    {
        var methods = typeof(GseTechPro.Core.Abstractions.ICanBusMonitor).GetMethods().Select(m => m.Name);
        Assert.DoesNotContain(methods, m => m.Contains("Transmit", StringComparison.OrdinalIgnoreCase) ||
                                            m.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
                                            m.Contains("Write", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(125_000, 0x83)]
    [InlineData(250_000, 0x80)]
    [InlineData(500_000, 0x81)]
    [InlineData(1_000_000, 0x82)]
    public void Ecom_BaudCode_AlwaysHasListenOnly(int bitrate, byte expected) =>
        Assert.Equal(expected, EcomCanBusMonitor.ListenOnlyBaudCode(bitrate));

    [Fact]
    public void Ecom_BaudCode_RefusesOtherRates() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EcomCanBusMonitor.ListenOnlyBaudCode(100_000));

    [Theory]
    [InlineData(0x24, 0x60, 0x123)]
    [InlineData(0xFF, 0xE0, 0x7FF)]
    [InlineData(0x00, 0x00, 0x000)]
    public void Ecom_StandardId_Unpacks(byte high, byte low, uint id) =>
        Assert.Equal(id, EcomCanBusMonitor.UnpackStandardId(high, low));

    [Fact]
    public void Ecom_ExtendedFrame_MapsDataLengthAndRemoteBit()
    {
        var m = new EcomNative.EffMessage
        {
            Id = 0x0CF00400,
            Data1 = 1, Data2 = 2, Data3 = 3, Data4 = 4, Data5 = 5, Data6 = 6, Data7 = 7, Data8 = 8,
            DataLength = 3,
            TimeStamp = 42,
        };
        var frame = EcomCanBusMonitor.FromExtended(m, 1.5);
        Assert.True(frame.IsExtended);
        Assert.Equal(0x0CF00400u, frame.Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Data);
        Assert.Equal(42u, frame.DeviceTimestamp);
        Assert.False(frame.IsSimulated);

        m.Options = EcomNative.OptionRemoteFrame;
        m.DataLength = 200; // corrupt length is clamped, remote frames carry no data
        var remote = EcomCanBusMonitor.FromExtended(m, 1.5);
        Assert.True(remote.IsRemote);
        Assert.Empty(remote.Data);
    }

    [Fact]
    public void Ecom_MessageStructs_MatchTheVendorLayout()
    {
        // uint id + 8 data + options + length + uint timestamp (sequential, 4-byte aligned).
        Assert.Equal(20, Marshal.SizeOf<EcomNative.EffMessage>());
        // 2 id bytes + 8 data + options + length + uint timestamp.
        Assert.Equal(16, Marshal.SizeOf<EcomNative.SffMessage>());
    }

    [Fact]
    public void Ecom_BusErrors_HavePlainWords()
    {
        Assert.Contains("bus-off", EcomCanBusMonitor.DescribeBusError(EcomNative.CanErrBusOffEvent));
        Assert.Contains("unplugged", EcomCanBusMonitor.DescribeBusError(EcomNative.ErrDeviceUnplugged));
        Assert.Contains("0x77", EcomCanBusMonitor.DescribeBusError(0x77));
    }

    [Fact]
    public void Factory_WithoutDriver_ListsSimulatorFirst_AndSaysWhyEcomIsUnavailable()
    {
        var factory = new CanBusMonitorFactory(() => "driver missing", () => throw new InvalidOperationException("not called"));

        var devices = factory.Discover();

        Assert.Equal(CanBusMonitorFactory.SimulatorKind, devices[0].Kind);
        Assert.True(devices[0].IsSimulated);
        var ecom = Assert.Single(devices, d => d.Kind == CanBusMonitorFactory.EcomKind);
        Assert.Equal("driver missing", ecom.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() => factory.Create(ecom));
    }

    [Fact]
    public void Factory_WithDriver_ListsEachAdapter()
    {
        var factory = new CanBusMonitorFactory(() => null, () => new uint[] { 1001, 1002 });

        var devices = factory.Discover();

        Assert.Equal(3, devices.Count);
        Assert.Contains(devices, d => d.SerialNumber == 1001 && d.Name == "ECOM S/N 1001" && d.UnavailableReason is null);
        Assert.IsType<EcomCanBusMonitor>(factory.Create(devices[1]));
        Assert.IsType<SimulatedCanBusMonitor>(factory.Create(devices[0]));
    }

    [Fact]
    public void Factory_WithDriverButNoAdapter_OffersFirstFree()
    {
        var factory = new CanBusMonitorFactory(() => null, Array.Empty<uint>);
        var ecom = Assert.Single(factory.Discover(), d => d.Kind == CanBusMonitorFactory.EcomKind);
        Assert.Equal(0u, ecom.SerialNumber);
        Assert.Null(ecom.UnavailableReason);
    }

    [Fact]
    public void RealDriverCheck_DoesNotThrow_OnThisMachine()
    {
        var reason = EcomDriver.CheckLibrary();
        if (reason is not null)
            Assert.Contains("ECOM", reason);
        Assert.NotNull(EcomDriver.FindSerialNumbers());
    }
}
