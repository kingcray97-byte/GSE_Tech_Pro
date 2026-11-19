using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices.Ipc;

/// <summary>
/// Client half of the app↔32-bit device-host named-pipe protocol. Owns a duplex
/// <see cref="NamedPipeClientStream"/>, performs the token handshake on connect, runs a background
/// reader that correlates <c>response</c> frames by id and routes <c>stream</c> frames to
/// <see cref="LiveDataReceived"/>, and serializes outgoing writes.
/// </summary>
public sealed class HostConnection : IAsyncDisposable
{
    private const int ConnectTimeoutMs = 5000;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<HostResponse>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readerTask;
    private int _disposed;
    private int _disconnectedRaised;

    private HostConnection(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Raised for each live-data PID carried by an incoming JSON <c>stream</c> frame.</summary>
    public event EventHandler<LiveDataUpdateEventArgs>? LiveDataReceived;

    /// <summary>
    /// Raised exactly once when the connection to the device host is lost (the background reader ends on
    /// EOF/fault) while the connection was not deliberately disposed. Consumed by the app to detect a
    /// mid-session host death and re-negotiate (ARCHITECTURE.md §1.1).
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>Connects to the host pipe and completes the token handshake.</summary>
    /// <exception cref="UnauthorizedAccessException">The host rejected the token.</exception>
    public static async Task<HostConnection> ConnectAsync(string pipeName, string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);

        var connection = new HostConnection(pipe);
        try
        {
            var hello = new HostHello { Token = token, Client = "GseTechPro.App", ClientVersion = "0.2.0" };
            await connection.WriteJsonAsync(JsonSerializer.Serialize(hello, HostJson.Options), ct).ConfigureAwait(false);

            var ackJson = await connection.ReadJsonAsync(ct).ConfigureAwait(false);
            var ack = JsonSerializer.Deserialize<HostHelloAck>(ackJson, HostJson.Options);
            if (ack is null || !ack.Accepted)
                throw new UnauthorizedAccessException($"Device host rejected the session: {ack?.Error ?? "unauthorized"}.");

            connection.StartReader();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a request and awaits its correlated response.</summary>
    public async Task<HostResponse> SendAsync(string command, object? @params, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var id = Guid.NewGuid().ToString("D");
        var tcs = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
            _pending[id] = tcs;

        var request = new HostRequest { Id = id, Command = command, Params = @params };
        try
        {
            await WriteJsonAsync(JsonSerializer.Serialize(request, HostJson.Options), ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_pending)
                _pending.Remove(id);
            tcs.TrySetCanceled(ct);
            throw;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        if (!ct.CanBeCanceled)
            linked.CancelAfter(DefaultRequestTimeout);

        try
        {
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_pending)
                _pending.Remove(id);
            throw;
        }
    }

    /// <summary>Sends a request and deserializes its <c>result</c> into <typeparamref name="T"/>.</summary>
    /// <exception cref="HostIpcException">The host returned a non-OK response.</exception>
    public async Task<T> SendTypedAsync<T>(string command, object? @params, CancellationToken ct = default)
    {
        var response = await SendAsync(command, @params, ct).ConfigureAwait(false);
        if (!response.Ok)
        {
            var code = response.Error?.Code ?? HostErrorCodes.Internal;
            throw new HostIpcException(code, response.Error?.Message ?? "Device host returned an error.", response.Error?.Detail);
        }
        if (response.Result is null)
            return default!;
        if (response.Result is JsonElement element)
            return element.Deserialize<T>(HostJson.Options)!;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(response.Result, HostJson.Options), HostJson.Options)!;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        try { _pipe.Dispose(); } catch { }

        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); }
            catch { }
        }

        _lifetime.Dispose();
        _writeGate.Dispose();
    }

    private async Task WriteJsonAsync(string json, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PipeFrame.WriteJsonAsync(_pipe, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<string> ReadJsonAsync(CancellationToken ct)
    {
        var frame = await PipeFrame.ReadAsync(_pipe, ct).ConfigureAwait(false);
        if (frame.Kind != HostFrameKind.Json)
            throw new InvalidDataException($"Expected a JSON frame but got kind 0x{frame.Kind:X2}.");
        return Encoding.UTF8.GetString(frame.Payload);
    }

    private void StartReader() => _readerTask = Task.Run(ReaderLoopAsync);

    private async Task ReaderLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await PipeFrame.ReadAsync(_pipe, _lifetime.Token).ConfigureAwait(false);
                if (frame.Kind == HostFrameKind.Json)
                    DispatchJson(Encoding.UTF8.GetString(frame.Payload));
                // Binary (0x02) and scope (0x03) frames are ignored in round 1.
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            FaultPending();
            // EOF/fault here means the host is gone; only report it when we did not close on purpose.
            if (Volatile.Read(ref _disposed) == 0)
                RaiseDisconnected();
        }
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0)
            return;

        var handler = Disconnected;
        if (handler is null)
            return;
        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try { subscriber(this, EventArgs.Empty); }
            catch { /* a misbehaving subscriber must not kill the reader loop */ }
        }
    }

    private void DispatchJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
                return;
            var type = typeElement.GetString();

            if (type == "response")
            {
                var response = JsonSerializer.Deserialize<HostResponse>(json, HostJson.Options);
                if (response is null)
                    return;
                lock (_pending)
                {
                    if (_pending.Remove(response.Id, out var tcs))
                        tcs.TrySetResult(response);
                }
            }
            else if (type == "stream")
            {
                var frame = JsonSerializer.Deserialize<HostStreamFrame>(json, HostJson.Options);
                if (frame is null || frame.Kind != HostStreamKinds.LiveData)
                    return;
                var payload = DeserializePayload<LiveDataStreamPayload>(root);
                if (payload is null)
                    return;
                foreach (var pid in payload.Pids)
                    RaiseLiveData(pid);
            }
        }
        catch (JsonException) { }
        catch (Exception) { }
    }

    private static T? DeserializePayload<T>(JsonElement root) where T : class
    {
        if (root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object)
            return payloadElement.Deserialize<T>(HostJson.Options);
        return null;
    }

    private void RaiseLiveData(PidValue value)
    {
        var handler = LiveDataReceived;
        if (handler is null)
            return;

        foreach (EventHandler<LiveDataUpdateEventArgs> subscriber in handler.GetInvocationList())
        {
            try { subscriber(this, new LiveDataUpdateEventArgs(value)); }
            catch { /* a misbehaving subscriber must not kill the reader loop */ }
        }
    }

    private void FaultPending()
    {
        List<TaskCompletionSource<HostResponse>> pending;
        lock (_pending)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }
        var error = new HostIpcException(HostErrorCodes.Internal, "Device host connection closed.");
        foreach (var tcs in pending)
            tcs.TrySetException(error);
    }
}
