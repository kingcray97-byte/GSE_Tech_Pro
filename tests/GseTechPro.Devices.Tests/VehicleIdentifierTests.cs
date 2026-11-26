using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Xunit;

namespace GseTechPro.Devices.Tests;

public class VehicleIdentifierTests
{
    private readonly VehicleIdentifier _identifier = new();

    [Fact]
    public async Task IdentifyAsync_Simulator_ReturnsAutoProfileWithDecodedRegion()
    {
        var adapter = new SimulatedDiagnosticAdapter(ProtocolType.OBD2, seed: 42);
        await adapter.ConnectAsync();
        try
        {
            var profile = await _identifier.IdentifyAsync(adapter);

            Assert.NotNull(profile);
            Assert.Equal(VehicleProfileSource.Auto, profile!.Source);
            Assert.Equal("SIMULATED00000001", profile.Vin);
            Assert.Null(profile.Make);
            Assert.Null(profile.Model);
            Assert.Null(profile.Engine);
            Assert.Equal(VehicleRegion.European, profile.Region);
            Assert.Null(profile.Year);
            Assert.Equal(ProtocolType.OBD2, profile.Protocol);
        }
        finally
        {
            await adapter.DisconnectAsync();
        }
    }

    [Fact]
    public async Task IdentifyAsync_AlreadyCancelledToken_ReturnsNull()
    {
        var adapter = new SimulatedDiagnosticAdapter(ProtocolType.OBD2, seed: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var profile = await _identifier.IdentifyAsync(adapter, cts.Token);

        Assert.Null(profile);
    }

    [Fact]
    public async Task IdentifyAsync_AdapterWithNullVin_ReturnsNull()
    {
        var adapter = new NullVinAdapter();

        var profile = await _identifier.IdentifyAsync(adapter);

        Assert.Null(profile);
    }

    [Fact]
    public async Task IdentifyAsync_AdapterThatFailsToConnect_ReturnsNull()
    {
        var adapter = new FailingConnectAdapter();

        var profile = await _identifier.IdentifyAsync(adapter);

        Assert.Null(profile);
    }

    private sealed class NullVinAdapter : IDiagnosticAdapter
    {
        public string Name => "Null VIN stub";
        public bool IsConnected => true;
        public bool IsSimulated => true;
        public IReadOnlyList<ProtocolType> SupportedProtocols => new[] { ProtocolType.OBD2 };
        public event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<DtcReadout>> ReadDTCsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DtcReadout>>(Array.Empty<DtcReadout>());
        public Task ClearDTCsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PidValue>> ReadLiveDataAsync(IReadOnlyList<string> pidNames, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PidValue>>(Array.Empty<PidValue>());
        public Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken = default) => Task.FromResult<FreezeFrameData?>(null);
        public Task<VehicleInfo> GetVehicleInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new VehicleInfo { Vin = null, Protocol = ProtocolType.OBD2 });
    }

    private sealed class FailingConnectAdapter : IDiagnosticAdapter
    {
        public string Name => "Failing connect stub";
        public bool IsConnected => false;
        public bool IsSimulated => false;
        public IReadOnlyList<ProtocolType> SupportedProtocols => new[] { ProtocolType.OBD2 };
        public event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("Unit-test connect failure"));
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<DtcReadout>> ReadDTCsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearDTCsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PidValue>> ReadLiveDataAsync(IReadOnlyList<string> pidNames, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<VehicleInfo> GetVehicleInfoAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
