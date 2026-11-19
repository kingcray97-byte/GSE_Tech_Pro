using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices;
using GseTechPro.Devices.Ipc;
using Xunit;

namespace GseTechPro.Devices.Tests;

/// <summary>
/// Round-trip tests through the real named-pipe host server (the same <see cref="HostServer"/> the
/// x86 <c>GseTechPro.Devices.Host32.exe</c> entry point runs), backed by the simulator. No real
/// hardware and no subprocess required.
/// </summary>
public class HostIpcTests : IAsyncLifetime
{
    private readonly string _pipeName;
    private readonly string _token;
    private readonly HostServer _server;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public HostIpcTests()
    {
        (_pipeName, _token) = HostProcess.GenerateSessionIdentity();
        _server = new HostServer(_pipeName, _token);
    }

    public Task InitializeAsync()
    {
        _serverTask = _server.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
        _cts.Dispose();
    }

    private Task<HostConnection> ConnectAsync(string? token = null) =>
        HostConnection.ConnectAsync(_pipeName, token ?? _token);

    [Fact]
    public async Task TokenHandshake_RejectsWrongToken()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ConnectAsync(new string('0', 64)));
    }

    [Fact]
    public async Task TokenHandshake_AcceptsAndPings()
    {
        await using var connection = await ConnectAsync();
        var ping = await connection.SendTypedAsync<PingResult>(HostCommands.Ping, null);
        Assert.Equal("pong", ping.Pong);
        Assert.True(ping.UptimeMs >= 0);
    }

    [Fact]
    public async Task ListDevices_ReturnsSimulator()
    {
        await using var connection = await ConnectAsync();
        var result = await connection.SendTypedAsync<ListDevicesResult>(HostCommands.ListDevices, null);

        var device = Assert.Single(result.Devices);
        Assert.Equal("simulator", device.Id);
        Assert.Equal("simulator", device.Kind);
        Assert.True(device.IsSimulated);
        Assert.Contains(ProtocolType.J1939, device.Protocols);
        Assert.Contains(ProtocolType.J1708, device.Protocols);
        Assert.Contains(ProtocolType.OBD2, device.Protocols);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsErrorCode()
    {
        await using var connection = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<HostIpcException>(
            () => connection.SendTypedAsync<HostAck>("not-a-command", null));
        Assert.Equal(HostErrorCodes.UnknownCommand, ex.Code);
    }

    [Fact]
    public async Task FullCommandRoundTrip_ThroughAdapter()
    {
        await using var connection = await ConnectAsync();
        await using var adapter = new HostDiagnosticAdapter(connection, SimulatorDeviceProvider.SimulatorDevice, ProtocolType.J1939);

        await adapter.ConnectAsync();
        Assert.True(adapter.IsConnected);
        Assert.True(adapter.IsSimulated);

        var dtcs = await adapter.ReadDTCsAsync();
        Assert.NotEmpty(dtcs);
        Assert.All(dtcs, d => Assert.True(d.IsSimulated));

        var pids = await adapter.ReadLiveDataAsync(new[] { "Engine Speed", "Coolant Temperature" });
        Assert.Equal(2, pids.Count);
        Assert.All(pids, p => Assert.True(p.IsSimulated));

        var info = await adapter.GetVehicleInfoAsync();
        Assert.Equal("SIMULATED00000001", info.Vin);

        var freeze = await adapter.ReadFreezeFrameAsync();
        Assert.NotNull(freeze);
        Assert.NotEmpty(freeze.Pids);

        var clear = await adapter.PingAsync();
        Assert.Equal("pong", clear.Pong);
    }

    [Fact]
    public async Task ClearDTCs_ReportsCountAndClears()
    {
        await using var connection = await ConnectAsync();
        await using var adapter = new HostDiagnosticAdapter(connection, SimulatorDeviceProvider.SimulatorDevice, ProtocolType.J1939);

        await adapter.ConnectAsync();
        await adapter.ClearDTCsAsync();

        var after = await adapter.ReadDTCsAsync();
        Assert.NotEmpty(after);
        Assert.All(after, d => Assert.Equal(DtcStatus.Stored, d.Status));
    }

    [Fact]
    public async Task Subscribe_StreamsLiveData_ThenUnsubscribeStops()
    {
        await using var connection = await ConnectAsync();
        await using var adapter = new HostDiagnosticAdapter(connection, SimulatorDeviceProvider.SimulatorDevice, ProtocolType.J1939);

        await adapter.ConnectAsync();

        var received = 0;
        EventHandler<LiveDataUpdateEventArgs> handler = (_, _) => Interlocked.Increment(ref received);
        adapter.LiveDataReceived += handler;

        var streamId = await adapter.SubscribeLiveDataAsync(Array.Empty<string>(), 100);
        Assert.False(string.IsNullOrEmpty(streamId));

        await Task.Delay(400);
        Assert.True(Volatile.Read(ref received) > 0, "Expected live-data stream frames while subscribed.");

        await adapter.UnsubscribeLiveDataAsync(streamId);
        await Task.Delay(200);
        var snapshot = Volatile.Read(ref received);
        await Task.Delay(400);
        var delta = Volatile.Read(ref received) - snapshot;
        Assert.True(delta < 10, $"Expected streaming to stop after unsubscribe, but got {delta} more events.");

        adapter.LiveDataReceived -= handler;
    }

    [Fact]
    public async Task HostDeath_RaisesDisconnected_OnAdapter()
    {
        await using var connection = await ConnectAsync();
        await using var adapter = new HostDiagnosticAdapter(connection, SimulatorDeviceProvider.SimulatorDevice, ProtocolType.J1939);
        await adapter.ConnectAsync();

        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Disconnected += (_, _) => raised.TrySetResult();

        _cts.Cancel(); // simulate host death: the server closes the pipe, the client reader hits EOF

        var completed = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(raised.Task, completed);
    }
}
