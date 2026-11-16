using GseTechPro.Core.Enums;
using GseTechPro.Core.Units;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.Tests;

public class SimulatorTests : IAsyncLifetime
{
    private SimulatedDiagnosticAdapter? _adapter;

    public Task InitializeAsync()
    {
        // Create a fresh adapter for each test with a fixed seed
        var factory = new DiagnosticAdapterFactory();
        _adapter = (SimulatedDiagnosticAdapter)factory.CreateSimulator(ProtocolType.J1939, 42);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_adapter is not null && _adapter.IsConnected)
            await _adapter.DisconnectAsync();
    }

    [Fact]
    public async Task SameSeed_ProducesIdenticalValues()
    {
        // Create two adapters with the same seed
        var factory = new DiagnosticAdapterFactory();
        var adapter1 = (SimulatedDiagnosticAdapter)factory.CreateSimulator(ProtocolType.J1939, 42);
        var adapter2 = (SimulatedDiagnosticAdapter)factory.CreateSimulator(ProtocolType.J1939, 42);

        try
        {
            await adapter1.ConnectAsync();
            await adapter2.ConnectAsync();

            // Read the same PIDs from both
            var pids = new[] { "Engine Speed", "Coolant Temperature" };
            var values1 = await adapter1.ReadLiveDataAsync(pids);
            var values2 = await adapter2.ReadLiveDataAsync(pids);

            Assert.Equal(values1.Count, values2.Count);
            for (int i = 0; i < values1.Count; i++)
            {
                Assert.Equal(values1[i].Name, values2[i].Name);
                Assert.Equal(values1[i].Value, values2[i].Value);
                Assert.Equal(values1[i].Unit, values2[i].Unit);
            }
        }
        finally
        {
            await adapter1.DisconnectAsync();
            await adapter2.DisconnectAsync();
        }
    }

    [Fact]
    public async Task DifferentSeeds_ProduceDifferentValues()
    {
        var factory = new DiagnosticAdapterFactory();
        var adapter1 = (SimulatedDiagnosticAdapter)factory.CreateSimulator(ProtocolType.J1939, 42);
        var adapter2 = (SimulatedDiagnosticAdapter)factory.CreateSimulator(ProtocolType.J1939, 99);

        try
        {
            await adapter1.ConnectAsync();
            await adapter2.ConnectAsync();

            // Wait a bit for different tick values
            await Task.Delay(150);

            var pids = new[] { "Engine Speed" };
            var values1 = await adapter1.ReadLiveDataAsync(pids);
            var values2 = await adapter2.ReadLiveDataAsync(pids);

            // Values should differ due to different seeds (different noise)
            Assert.NotEqual(values1[0].Value, values2[0].Value);
        }
        finally
        {
            await adapter1.DisconnectAsync();
            await adapter2.DisconnectAsync();
        }
    }

    [Fact]
    public async Task DtcStatusMix_IncludesActiveAndPending()
    {
        await _adapter!.ConnectAsync();

        var dtcs = await _adapter.ReadDTCsAsync();

        Assert.NotEmpty(dtcs);
        
        // J1939 simulator has Active, Pending, and Stored codes
        Assert.Contains(dtcs, d => d.Status == DtcStatus.Active);
        Assert.Contains(dtcs, d => d.Status == DtcStatus.Pending);
        Assert.Contains(dtcs, d => d.Status == DtcStatus.Stored);
    }

    [Fact]
    public async Task ValuesAreCanonicalSI()
    {
        await _adapter!.ConnectAsync();

        var pids = new[] { "Engine Speed", "Coolant Temperature", "Battery Voltage", "Boost Pressure" };
        var values = await _adapter.ReadLiveDataAsync(pids);

        // Engine speed is in rpm (no unit dimension)
        Assert.Equal("rpm", values.First(v => v.Name == "Engine Speed").Unit);
        
        // Coolant temperature should be in °C (canonical)
        Assert.Equal(UnitConverter.Celsius, values.First(v => v.Name == "Coolant Temperature").Unit);
        
        // Battery voltage should be in V (canonical)
        Assert.Equal(UnitConverter.Volt, values.First(v => v.Name == "Battery Voltage").Unit);
        
        // Boost pressure should be in Pa (canonical)
        Assert.Equal(UnitConverter.Pascal, values.First(v => v.Name == "Boost Pressure").Unit);
    }

    [Fact]
    public async Task ClearDTCsAsync_LeavesOnlyStoredCodes()
    {
        await _adapter!.ConnectAsync();

        // Before clear: has Active and Pending codes
        var before = await _adapter.ReadDTCsAsync();
        Assert.Contains(before, d => d.Status == DtcStatus.Active);
        Assert.Contains(before, d => d.Status == DtcStatus.Pending);

        // Clear DTCs
        await _adapter.ClearDTCsAsync();

        // After clear: only Stored codes remain
        var after = await _adapter.ReadDTCsAsync();
        Assert.All(after, d => Assert.Equal(DtcStatus.Stored, d.Status));
    }

    [Fact]
    public async Task ReadLiveDataAsync_ReturnsRequestedPids()
    {
        await _adapter!.ConnectAsync();

        var requested = new[] { "Engine Speed", "Battery Voltage" };
        var values = await _adapter.ReadLiveDataAsync(requested);

        Assert.Equal(2, values.Count);
        Assert.Contains(values, v => v.Name == "Engine Speed");
        Assert.Contains(values, v => v.Name == "Battery Voltage");
    }

    [Fact]
    public async Task ReadLiveDataAsync_EmptyList_ReturnsAllPids()
    {
        await _adapter!.ConnectAsync();

        var values = await _adapter.ReadLiveDataAsync(Array.Empty<string>());

        // Should return all PIDs for J1939 (9 of them)
        Assert.True(values.Count >= 8);
    }

    [Fact]
    public async Task Disconnect_StopsLiveDataEvents()
    {
        var factory = new DiagnosticAdapterFactory();
        var adapter = (SimulatedDiagnosticAdapter)factory.CreateSimulator(ProtocolType.J1939, 42);
        
        try
        {
            await adapter.ConnectAsync();

            int eventCount = 0;
            adapter.LiveDataReceived += (s, e) => eventCount++;

            // Wait for some events
            await Task.Delay(250);
            var countBeforeDisconnect = eventCount;
            Assert.True(countBeforeDisconnect > 0, "Should have received live data events");

            // Disconnect
            await adapter.DisconnectAsync();

            // Wait and verify no more events
            await Task.Delay(250);
            Assert.Equal(countBeforeDisconnect, eventCount);
        }
        finally
        {
            if (adapter.IsConnected)
                await adapter.DisconnectAsync();
        }
    }

    [Fact]
    public async Task GetVehicleInfo_ReturnsSimulatedData()
    {
        await _adapter!.ConnectAsync();

        var info = await _adapter.GetVehicleInfoAsync();

        Assert.Equal("SIMULATED00000001", info.Vin);
        Assert.Equal(ProtocolType.J1939, info.Protocol);
        Assert.NotEmpty(info.Ecus);
    }

    [Fact]
    public async Task ReadFreezeFrame_ReturnsData()
    {
        await _adapter!.ConnectAsync();

        var frame = await _adapter.ReadFreezeFrameAsync();

        Assert.NotNull(frame);
        Assert.NotEmpty(frame.Pids);
    }

    [Theory]
    [InlineData(ProtocolType.J1939)]
    [InlineData(ProtocolType.J1708)]
    [InlineData(ProtocolType.OBD2)]
    public async Task AllSimulatedProtocols_Work(ProtocolType protocol)
    {
        var factory = new DiagnosticAdapterFactory();
        var adapter = (SimulatedDiagnosticAdapter)factory.CreateSimulator(protocol, 42);

        try
        {
            await adapter.ConnectAsync();

            // Should be able to read DTCs and live data for all protocols
            var dtcs = await adapter.ReadDTCsAsync();
            Assert.NotEmpty(dtcs);

            var values = await adapter.ReadLiveDataAsync(Array.Empty<string>());
            Assert.NotEmpty(values);
        }
        finally
        {
            if (adapter.IsConnected)
                await adapter.DisconnectAsync();
        }
    }
}
