using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Abstractions;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices;

/// <summary>
/// Default implementation of <see cref="IDiagnosticAdapterFactory"/>. Phase 1 only constructed the
/// in-process simulator. Phase 2 adds a host-based creation path: <see cref="ListDevicesAsync"/> and
/// <see cref="ConnectHostAsync"/> spawn the 32-bit device host on first use (generating the pipe name
/// + token) and marshal commands over the named pipe. The vendor SDK loading stays inside the host
/// process — never here in the 64-bit app.
/// </summary>
public sealed class DiagnosticAdapterFactory : IDiagnosticAdapterFactory
{
    /// <summary>Default deterministic seed so simulator runs are reproducible.</summary>
    public const int DefaultSeed = 12345;

    private readonly SemaphoreSlim _hostGate = new(1, 1);
    private HostProcess? _hostProcess;

    /// <inheritdoc />
    public IDiagnosticAdapter CreateSimulator(ProtocolType protocol, int? seed = null) =>
        new SimulatedDiagnosticAdapter(protocol, seed ?? DefaultSeed);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var host = await EnsureHostAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await HostConnection.ConnectAsync(host.PipeName, host.Token, cancellationToken).ConfigureAwait(false);
        var result = await connection.SendTypedAsync<ListDevicesResult>(HostCommands.ListDevices, null, cancellationToken).ConfigureAwait(false);
        return result.Devices;
    }

    /// <inheritdoc />
    public async Task<IDiagnosticAdapter> ConnectHostAsync(DeviceInfo device, ProtocolType? protocol = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var host = await EnsureHostAsync(cancellationToken).ConfigureAwait(false);
        var connection = await HostConnection.ConnectAsync(host.PipeName, host.Token, cancellationToken).ConfigureAwait(false);
        return new HostDiagnosticAdapter(connection, device, protocol);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        HostProcess? host;
        await _hostGate.WaitAsync().ConfigureAwait(false);
        host = _hostProcess;
        _hostProcess = null;
        _hostGate.Release();

        if (host is not null)
            await host.DisposeAsync().ConfigureAwait(false);

        _hostGate.Dispose();
    }

    private async Task<HostProcess> EnsureHostAsync(CancellationToken cancellationToken)
    {
        await _hostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ARCHITECTURE.md §1.1 crash recovery: if the host process died, dispose the stale handle
            // and start a fresh one instead of handing back a dead process.
            if (_hostProcess is { } existing)
            {
                if (!existing.HasExited)
                    return existing;

                await existing.DisposeAsync().ConfigureAwait(false);
                _hostProcess = null;
            }

            return _hostProcess = HostProcess.Start();
        }
        finally
        {
            _hostGate.Release();
        }
    }
}
