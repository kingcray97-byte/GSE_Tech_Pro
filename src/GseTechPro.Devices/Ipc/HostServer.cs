using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices.Ipc;

/// <summary>
/// Server half of the app↔32-bit device-host named-pipe protocol (ARCHITECTURE.md §1.1.1, ADR-007).
/// Serves the pipe name the app generated, authenticates the first <c>hello</c> frame against the
/// per-launch token (constant-time), then dispatches <see cref="HostRequest"/> frames against the
/// configured <see cref="IHostDeviceProvider"/> (the simulator in round 1).
///
/// Lives in the AnyCPU <c>GseTechPro.Devices</c> assembly so it is reusable by the x86 host entry
/// point and testable in-process; it never touches a vendor DLL.
/// </summary>
public sealed class HostServer
{
    private const int MaxConnections = 16;

    private readonly string _pipeName;
    private readonly string _token;
    private readonly IHostDeviceProvider _devices;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly object _connectionsLock = new();
    private readonly List<Task> _connections = new();

    public HostServer(string pipeName, string token, IHostDeviceProvider? devices = null)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _devices = devices ?? new SimulatorDeviceProvider();
    }

    public static string HostVersion { get; } = typeof(HostServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Accepts connections until <paramref name="ct"/> is canceled, then drains in-flight ones.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxConnections,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    server.Dispose();
                    break;
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[Host32] Accept error: {ex.Message}");
                    server.Dispose();
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine($"[Host32] Accept error: {ex.Message}");
                    server.Dispose();
                    continue;
                }

                var task = HandleConnectionAsync(server, ct);
                lock (_connectionsLock)
                    _connections.Add(task);
                _ = task.ContinueWith(RemoveConnection, TaskScheduler.Default);
            }
        }
        finally
        {
            Task[] snapshot;
            lock (_connectionsLock)
                snapshot = _connections.ToArray();
            try { await Task.WhenAll(snapshot).ConfigureAwait(false); }
            catch { /* a connection handler must never fault the accept loop */ }
        }
    }

    private void RemoveConnection(Task task)
    {
        lock (_connectionsLock)
            _connections.Remove(task);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var writer = new HostPipeWriter(server);
        HostSession? session = null;
        try
        {
            // Token handshake: the first frame is a JSON "hello" carrying the token.
            var hello = await ReadHelloAsync(server, ct).ConfigureAwait(false);
            if (hello is null || hello.Type != "hello" || !TokensEqual(_token, hello.Token ?? ""))
            {
                await writer.WriteJsonAsync(Serialize(new HostHelloAck { Accepted = false, HostVersion = HostVersion, Error = HostErrorCodes.Unauthorized }), ct).ConfigureAwait(false);
                return; // close the pipe before dispatching any command
            }
            if (hello.Version != HostProtocol.Version)
            {
                await writer.WriteJsonAsync(Serialize(new HostHelloAck { Accepted = false, HostVersion = HostVersion, Error = "version_mismatch" }), ct).ConfigureAwait(false);
                return;
            }

            await writer.WriteJsonAsync(Serialize(new HostHelloAck { Accepted = true, HostVersion = HostVersion }), ct).ConfigureAwait(false);
            Console.WriteLine("[Host32] Client authorized.");

            session = new HostSession(writer, _devices, _uptime);
            await session.RunAsync(server, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Host32] Connection error: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
            try { if (server.IsConnected) server.Disconnect(); } catch { }
            server.Dispose();
            Console.WriteLine("[Host32] Client disconnected.");
        }
    }

    private static async Task<HostHello?> ReadHelloAsync(Stream stream, CancellationToken ct)
    {
        var frame = await PipeFrame.ReadAsync(stream, ct).ConfigureAwait(false);
        if (frame.Kind != HostFrameKind.Json)
            return null;
        return JsonSerializer.Deserialize<HostHello>(Encoding.UTF8.GetString(frame.Payload), HostJson.Options);
    }

    private static bool TokensEqual(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, HostJson.Options);
}

/// <summary>Serializes all writes to a single pipe so concurrent responders never interleave frames.</summary>
internal sealed class HostPipeWriter
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HostPipeWriter(Stream stream) => _stream = stream;

    public async Task WriteJsonAsync(string json, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PipeFrame.WriteJsonAsync(_stream, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// One authenticated connection's command loop. Owns the connected adapter and live-data
/// subscriptions; maps adapter calls to typed <see cref="HostResponse"/> results and forwards
/// <see cref="IDiagnosticAdapter.LiveDataReceived"/> events as JSON <c>stream</c> frames.
/// </summary>
internal sealed class HostSession : IAsyncDisposable
{
    private readonly HostPipeWriter _writer;
    private readonly IHostDeviceProvider _devices;
    private readonly Stopwatch _uptime;
    private readonly List<Subscription> _subscriptions = new();
    private IDiagnosticAdapter? _adapter;

    public HostSession(HostPipeWriter writer, IHostDeviceProvider devices, Stopwatch uptime)
    {
        _writer = writer;
        _devices = devices;
        _uptime = uptime;
    }

    public async Task RunAsync(Stream server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PipeFrame frame;
            try
            {
                frame = await PipeFrame.ReadAsync(server, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException) { break; }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidDataException ex)
            {
                Console.WriteLine($"[Host32] Malformed frame: {ex.Message}");
                break;
            }
            catch (OperationCanceledException) { break; }

            if (frame.Kind != HostFrameKind.Json)
                continue; // binary (0x02) / scope (0x03) frames arrive in round 2 / Phase 6-7.

            var response = await DispatchAsync(Encoding.UTF8.GetString(frame.Payload)).ConfigureAwait(false);
            await _writer.WriteJsonAsync(JsonSerializer.Serialize(response, HostJson.Options), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await TeardownAsync().ConfigureAwait(false);

    private async Task TeardownAsync()
    {
        foreach (var sub in _subscriptions.ToList())
            sub.Adapter.LiveDataReceived -= sub.Handler;
        _subscriptions.Clear();

        var adapter = _adapter;
        _adapter = null;
        if (adapter is not null)
        {
            try { await adapter.DisconnectAsync().ConfigureAwait(false); }
            catch { /* best-effort teardown */ }
        }
    }

    private async Task<HostResponse> DispatchAsync(string json)
    {
        HostRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<HostRequest>(json, HostJson.Options);
        }
        catch (JsonException)
        {
            return Error("", HostErrorCodes.InvalidRequest, "Malformed request.");
        }
        if (request is null)
            return Error("", HostErrorCodes.InvalidRequest, "Empty request.");

        var id = request.Id ?? "";
        try
        {
            switch (request.Command)
            {
                case HostCommands.ListDevices:
                    return Ok(id, new ListDevicesResult { Devices = await _devices.ListDevicesAsync(CancellationToken.None).ConfigureAwait(false) });
                case HostCommands.Connect:
                    return Ok(id, await ConnectAsync(request.Params).ConfigureAwait(false));
                case HostCommands.Disconnect:
                    return Ok(id, await DisconnectAsync().ConfigureAwait(false));
                case HostCommands.ReadDTCs:
                    return Ok(id, new ReadDTCsResult { Dtcs = await RequireAdapter().ReadDTCsAsync().ConfigureAwait(false) });
                case HostCommands.ClearDTCs:
                    return Ok(id, await ClearDTCsAsync().ConfigureAwait(false));
                case HostCommands.ReadLiveData:
                    return Ok(id, await ReadLiveDataAsync(request.Params).ConfigureAwait(false));
                case HostCommands.SubscribeLiveData:
                    return Ok(id, SubscribeLiveData(request.Params));
                case HostCommands.UnsubscribeLiveData:
                    return Ok(id, UnsubscribeLiveData(request.Params));
                case HostCommands.ReadFreezeFrame:
                    return Ok(id, new ReadFreezeFrameResult { FreezeFrame = await RequireAdapter().ReadFreezeFrameAsync().ConfigureAwait(false) });
                case HostCommands.GetVehicleInfo:
                    return Ok(id, new GetVehicleInfoResult { VehicleInfo = await RequireAdapter().GetVehicleInfoAsync().ConfigureAwait(false) });
                case HostCommands.Ping:
                    return Ok(id, new PingResult { Pong = "pong", UptimeMs = _uptime.ElapsedMilliseconds });
                default:
                    return Error(id, HostErrorCodes.UnknownCommand, $"Unknown command '{request.Command}'.");
            }
        }
        catch (HostIpcException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(id, HostErrorCodes.Timeout, "Operation canceled.");
        }
        catch (Exception ex)
        {
            return Error(id, HostErrorCodes.Internal, ex.Message);
        }
    }

    private IDiagnosticAdapter RequireAdapter() =>
        _adapter ?? throw new HostIpcException(HostErrorCodes.DeviceNotConnected, "No device connected.");

    private async Task<ConnectResult> ConnectAsync(object? paramsObj)
    {
        var p = DeserializeParams<ConnectParams>(paramsObj);
        var selected = p.Protocol ?? ProtocolType.J1939;

        await TeardownAsync().ConfigureAwait(false);
        _adapter = await _devices.ConnectAsync(p.DeviceId, selected, CancellationToken.None).ConfigureAwait(false);
        return new ConnectResult { Protocol = selected };
    }

    private async Task<HostAck> DisconnectAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
        return new HostAck { Acknowledged = true };
    }

    private async Task<ClearDTCsResult> ClearDTCsAsync()
    {
        var adapter = RequireAdapter();
        var before = await adapter.ReadDTCsAsync().ConfigureAwait(false);
        var cleared = before.Count(d => d.Status is DtcStatus.Active or DtcStatus.Pending);
        await adapter.ClearDTCsAsync().ConfigureAwait(false);
        Console.WriteLine($"[Host32] Clear DTCs requested ({cleared} active/pending code(s) cleared).");
        return new ClearDTCsResult { Cleared = cleared };
    }

    private async Task<ReadLiveDataResult> ReadLiveDataAsync(object? paramsObj)
    {
        var p = DeserializeParams<ReadLiveDataParams>(paramsObj);
        var pids = await RequireAdapter().ReadLiveDataAsync(p.PidNames).ConfigureAwait(false);
        return new ReadLiveDataResult { Pids = pids };
    }

    private SubscribeLiveDataResult SubscribeLiveData(object? paramsObj)
    {
        var p = DeserializeParams<SubscribeLiveDataParams>(paramsObj);
        var adapter = RequireAdapter();
        var streamId = Guid.NewGuid().ToString("D");

        // The simulator streams at a fixed ~10 Hz; intervalMs is accepted but not enforced yet.
        var wanted = p.PidNames is null || p.PidNames.Count == 0
            ? null
            : p.PidNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        EventHandler<LiveDataUpdateEventArgs> handler = async (_, e) =>
        {
            if (wanted is not null && !wanted.Contains(e.Value.Name))
                return;
            try
            {
                var frame = new HostStreamFrame
                {
                    StreamId = streamId,
                    Kind = HostStreamKinds.LiveData,
                    Payload = new LiveDataStreamPayload { Pids = new[] { e.Value } },
                };
                await _writer.WriteJsonAsync(JsonSerializer.Serialize(frame, HostJson.Options), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A stream write failure (e.g. the client closed) must not kill the host.
                Console.WriteLine($"[Host32] Stream write error: {ex.Message}");
            }
        };

        adapter.LiveDataReceived += handler;
        _subscriptions.Add(new Subscription(streamId, adapter, handler));
        return new SubscribeLiveDataResult { StreamId = streamId };
    }

    private HostAck UnsubscribeLiveData(object? paramsObj)
    {
        var p = DeserializeParams<UnsubscribeLiveDataParams>(paramsObj);
        var sub = _subscriptions.FirstOrDefault(s => s.StreamId == p.StreamId);
        if (sub is not null)
        {
            sub.Adapter.LiveDataReceived -= sub.Handler;
            _subscriptions.Remove(sub);
        }
        return new HostAck { Acknowledged = true };
    }

    private static T DeserializeParams<T>(object? paramsObj) where T : class, new()
    {
        if (paramsObj is JsonElement element)
            return element.Deserialize<T>(HostJson.Options) ?? new T();
        if (paramsObj is null)
            return new T();
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(paramsObj, HostJson.Options), HostJson.Options) ?? new T();
    }

    private static HostResponse Ok(string id, object result) => new() { Id = id, Ok = true, Result = result };

    private static HostResponse Error(string id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new HostError { Code = code, Message = message } };

    private sealed record Subscription(string StreamId, IDiagnosticAdapter Adapter, EventHandler<LiveDataUpdateEventArgs> Handler);
}
