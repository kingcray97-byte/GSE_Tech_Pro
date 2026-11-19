using GseTechPro.Core.Enums;
using GseTechPro.Devices;
using GseTechPro.Devices.Ipc;
using Xunit;
using Xunit.Abstractions;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// End-to-end through the real <c>GseTechPro.Devices.Host32.exe</c> subprocess: <see cref="HostProcess"/>
/// spawns the x86 host with <c>--pipe</c>/<c>--token</c>, and the client connects over the named pipe.
///
/// The x86 host is framework-dependent and requires the x86 .NET 8 runtime; on dev machines without
/// it (the installer ships it to the target tablet) these tests skip rather than fail. The same
/// host-serving code is fully exercised in-process by <see cref="HostIpcTests"/>.
/// </summary>
public class HostProcessTests
{
    private static bool? _x86HostAvailable;

    private readonly ITestOutputHelper _output;

    public HostProcessTests(ITestOutputHelper output) => _output = output;

    /// <summary>Detects (once) whether the x86 host can actually launch on this machine.</summary>
    private static bool X86HostAvailable()
    {
        if (_x86HostAvailable is not null)
            return _x86HostAvailable.Value;
        try
        {
            using var probe = HostProcess.Start();
            _x86HostAvailable = !probe.WaitForExit(1500);
        }
        catch
        {
            _x86HostAvailable = false;
        }
        return _x86HostAvailable.Value;
    }

    private bool SkipIfHostUnavailable()
    {
        if (X86HostAvailable())
            return false;
        _output.WriteLine("SKIPPED: x86 .NET runtime not installed, so GseTechPro.Devices.Host32.exe cannot launch.");
        return true;
    }

    [Fact]
    public async Task SpawnedHost32_RoundTrip_ThroughAdapter()
    {
        if (SkipIfHostUnavailable())
            return;

        await using var host = HostProcess.Start();
        await using var connection = await HostConnection.ConnectAsync(host.PipeName, host.Token);
        await using var adapter = new HostDiagnosticAdapter(connection, SimulatorDeviceProvider.SimulatorDevice, ProtocolType.J1939);

        await adapter.ConnectAsync();
        Assert.True(adapter.IsConnected);

        var dtcs = await adapter.ReadDTCsAsync();
        Assert.NotEmpty(dtcs);
        Assert.All(dtcs, d => Assert.True(d.IsSimulated));

        var pids = await adapter.ReadLiveDataAsync(new[] { "Engine Speed" });
        Assert.Single(pids);

        var ping = await adapter.PingAsync();
        Assert.Equal("pong", ping.Pong);

        await adapter.DisconnectAsync();
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public async Task Factory_ListDevicesAndConnectHost_RoundTrip()
    {
        if (SkipIfHostUnavailable())
            return;

        var factory = new DiagnosticAdapterFactory();
        try
        {
            var devices = await factory.ListDevicesAsync();
            var device = Assert.Single(devices);
            Assert.True(device.IsSimulated);

            var adapter = await factory.ConnectHostAsync(device, ProtocolType.OBD2);
            try
            {
                await adapter.ConnectAsync();
                Assert.True(adapter.IsConnected);

                var dtcs = await adapter.ReadDTCsAsync();
                Assert.NotEmpty(dtcs);
                Assert.All(dtcs, d => Assert.True(d.IsSimulated));
            }
            finally
            {
                if (adapter is IAsyncDisposable d)
                    await d.DisposeAsync();
            }
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
