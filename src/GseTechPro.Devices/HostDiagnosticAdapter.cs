using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices;

/// <summary>
/// <see cref="IDiagnosticAdapter"/> whose commands are marshaled over the named pipe to the 32-bit
/// device host. Each <see cref="IDiagnosticAdapter"/> method becomes a <c>HostCommands</c> request;
/// responses are translated back to the Core DTOs, and incoming JSON <c>stream</c> frames are raised
/// as <see cref="LiveDataReceived"/>.
///
/// Unlike the in-process simulator (which streams automatically once connected), live-data streaming
/// over the pipe must be started with <see cref="SubscribeLiveDataAsync"/> and stopped with
/// <see cref="UnsubscribeLiveDataAsync"/>.
/// </summary>
public sealed class HostDiagnosticAdapter : IDiagnosticAdapter, IAsyncDisposable
{
    private readonly HostConnection _connection;
    private readonly DeviceInfo _device;
    private readonly ProtocolType? _requestedProtocol;
    private string? _activeStreamId;
    private bool _connected;
    private int _disposed;
    private int _disconnectedRaised;

    public HostDiagnosticAdapter(HostConnection connection, DeviceInfo device, ProtocolType? protocol = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _requestedProtocol = protocol ?? (device.Protocols.Count == 1 ? device.Protocols[0] : (ProtocolType?)null);
        _connection.LiveDataReceived += OnLiveData;
        _connection.Disconnected += OnDisconnected;
    }

    /// <inheritdoc />
    public string Name => _device.Name;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public bool IsSimulated => _device.IsSimulated;

    /// <inheritdoc />
    public IReadOnlyList<ProtocolType> SupportedProtocols => _device.Protocols;

    /// <inheritdoc />
    public event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived;

    /// <summary>
    /// Raised when the device host connection is lost mid-session (forwarded from <see cref="HostConnection"/>,
    /// and additionally when <see cref="PingAsync"/> fails). The app uses this to mark an in-flight
    /// recording "interrupted" and re-negotiate the session (ARCHITECTURE.md §1.1).
    /// </summary>
    public event EventHandler? Disconnected;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_connected)
            return;

        await _connection.SendTypedAsync<ConnectResult>(HostCommands.Connect,
            new ConnectParams { DeviceId = _device.Id, Protocol = _requestedProtocol }, cancellationToken).ConfigureAwait(false);
        _connected = true;
        _activeStreamId = null;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        if (_disposed == 1)
            return;

        if (_activeStreamId is not null)
        {
            try { await UnsubscribeLiveDataAsync(_activeStreamId).ConfigureAwait(false); }
            catch { }
            _activeStreamId = null;
        }

        if (_connected)
        {
            try { await _connection.SendTypedAsync<HostAck>(HostCommands.Disconnect, null).ConfigureAwait(false); }
            catch { }
            _connected = false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DtcReadout>> ReadDTCsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var result = await _connection.SendTypedAsync<ReadDTCsResult>(HostCommands.ReadDTCs, null, cancellationToken).ConfigureAwait(false);
        return result.Dtcs;
    }

    /// <inheritdoc />
    public async Task ClearDTCsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _connection.SendTypedAsync<ClearDTCsResult>(HostCommands.ClearDTCs, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PidValue>> ReadLiveDataAsync(IReadOnlyList<string> pidNames, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var result = await _connection.SendTypedAsync<ReadLiveDataResult>(HostCommands.ReadLiveData,
            new ReadLiveDataParams { PidNames = pidNames ?? Array.Empty<string>() }, cancellationToken).ConfigureAwait(false);
        return result.Pids;
    }

    /// <inheritdoc />
    public async Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var result = await _connection.SendTypedAsync<ReadFreezeFrameResult>(HostCommands.ReadFreezeFrame, null, cancellationToken).ConfigureAwait(false);
        return result.FreezeFrame;
    }

    /// <inheritdoc />
    public async Task<VehicleInfo> GetVehicleInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var result = await _connection.SendTypedAsync<GetVehicleInfoResult>(HostCommands.GetVehicleInfo, null, cancellationToken).ConfigureAwait(false);
        return result.VehicleInfo;
    }

    /// <summary>Starts live-data streaming; returns the subscription id to pass to <see cref="UnsubscribeLiveDataAsync"/>.</summary>
    public async Task<string> SubscribeLiveDataAsync(IReadOnlyList<string> pidNames, int intervalMs = 100, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var result = await _connection.SendTypedAsync<SubscribeLiveDataResult>(HostCommands.SubscribeLiveData,
            new SubscribeLiveDataParams { PidNames = pidNames ?? Array.Empty<string>(), IntervalMs = intervalMs }, cancellationToken).ConfigureAwait(false);
        _activeStreamId = result.StreamId;
        return result.StreamId;
    }

    /// <summary>Stops the live-data stream identified by <paramref name="streamId"/>.</summary>
    public async Task UnsubscribeLiveDataAsync(string streamId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        await _connection.SendTypedAsync<HostAck>(HostCommands.UnsubscribeLiveData,
            new UnsubscribeLiveDataParams { StreamId = streamId }, cancellationToken).ConfigureAwait(false);
        if (_activeStreamId == streamId)
            _activeStreamId = null;
    }

    /// <summary>Heartbeat to the host (not part of <see cref="IDiagnosticAdapter"/>; used by the app's health watch).</summary>
    public async Task<PingResult> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            return await _connection.SendTypedAsync<PingResult>(HostCommands.Ping, null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed heartbeat (host death or timeout) is treated as a disconnection for the UI.
            RaiseDisconnected();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _connection.LiveDataReceived -= OnLiveData;
        _connection.Disconnected -= OnDisconnected;
        try { await DisconnectAsync().ConfigureAwait(false); }
        catch { }
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void OnLiveData(object? sender, LiveDataUpdateEventArgs e) => LiveDataReceived?.Invoke(this, e);

    private void OnDisconnected(object? sender, EventArgs e) => RaiseDisconnected();

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0)
            return;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!_connected)
            throw new InvalidOperationException("Not connected to a device.");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}
