using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// Wire-level constants for the app↔32-bit device-host named-pipe protocol (ARCHITECTURE.md §1.1,
/// ADR-007). These live in Core so the app, the IPC client in <c>GseTechPro.Devices</c>, and the
/// x86 host all reference the same numbers/names. The byte framing itself (kind byte + length
/// prefix) is implemented in <c>GseTechPro.Devices.Ipc</c>; Core carries the constants and DTOs only
/// and never references <c>System.IO.Pipes</c>.
/// </summary>
public static class HostProtocol
{
    /// <summary>Control-protocol version. The host rejects a mismatched major version on hello.</summary>
    public const int Version = 1;

    /// <summary>Pipe name prefix; the app appends a per-session GUID (see <see cref="PipeNamePattern"/>).</summary>
    public const string PipeNamePrefix = "GseTechPro.Devices.Host32";

    /// <summary>Exact pipe name shape the app generates and passes to the host: <c>prefix + "-" + 32 hex chars</c>.</summary>
    public const string PipeNamePattern = PipeNamePrefix + "-{guid:N}";

    /// <summary>Per-launch token length in bytes (32 random bytes → 64 lowercase hex chars).</summary>
    public const int TokenBytes = 32;

    /// <summary>Per-launch token length in hex characters (2 × <see cref="TokenBytes"/>).</summary>
    public const int TokenHexLength = TokenBytes * 2;

    /// <summary>Sanity cap for a single framed payload (JSON or binary), little-endian length prefix.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024; // 16 MiB
}

/// <summary>One-byte frame-kind tags that precede the 4-byte LE length prefix on the wire.</summary>
public static class HostFrameKind
{
    /// <summary>UTF-8 JSON (hello, request, response, stream announcement).</summary>
    public const byte Json = 0x01;

    /// <summary>Raw binary payload (e.g. a CAN-capture chunk). Format is declared by a prior JSON stream frame.</summary>
    public const byte Binary = 0x02;

    /// <summary>Reserved for scope sample streaming (Phase 6).</summary>
    public const byte Scope = 0x03;
}

/// <summary>Command names (the <c>command</c> field of a <see cref="HostRequest"/>).</summary>
public static class HostCommands
{
    public const string ListDevices = "list-devices";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string ReadDTCs = "read-dtcs";
    public const string ClearDTCs = "clear-dtcs";
    public const string ReadLiveData = "read-live-data";
    public const string SubscribeLiveData = "subscribe-live-data";
    public const string UnsubscribeLiveData = "unsubscribe-live-data";
    public const string ReadFreezeFrame = "read-freeze-frame";
    public const string GetVehicleInfo = "get-vehicle-info";
    public const string Ping = "ping";
}

/// <summary>Error codes returned in <see cref="HostError.Code"/>.</summary>
public static class HostErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string InvalidRequest = "invalid_request";
    public const string UnknownCommand = "unknown_command";
    public const string DeviceNotFound = "device_not_found";
    public const string DeviceNotConnected = "device_not_connected";
    public const string DeviceError = "device_error";
    public const string Unsupported = "unsupported";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
}

/// <summary>Stream payload kinds (the <c>kind</c> field of a <see cref="HostStreamFrame"/>).</summary>
public static class HostStreamKinds
{
    public const string LiveData = "live-data";
    public const string Can = "can";
    public const string Scope = "scope";
}

/// <summary>
/// First frame sent by the app after connecting: authenticates the session before any command is
/// dispatched. The host replies with <see cref="HostHelloAck"/> (or an error and closes the pipe).
/// </summary>
public sealed record HostHello
{
    public string Type { get; init; } = "hello";
    public int Version { get; init; } = HostProtocol.Version;
    /// <summary>Per-launch token (lowercase hex).</summary>
    public string Token { get; init; } = "";
    public string Client { get; init; } = "GseTechPro.App";
    public string ClientVersion { get; init; } = "";
}

/// <summary>Host's reply to <see cref="HostHello"/>.</summary>
public sealed record HostHelloAck
{
    public string Type { get; init; } = "hello-ack";
    public bool Accepted { get; init; }
    public string? HostVersion { get; init; }
    /// <summary>Set to <see cref="HostErrorCodes.Unauthorized"/> (or a version code) when rejected.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Request envelope from the app to the host. <c>Params</c> is command-specific (see the typed
/// param records below). <c>Id</c> is a client-generated correlation id echoed in the response.
/// </summary>
public sealed record HostRequest
{
    public string Type { get; init; } = "request";
    public string Id { get; init; } = System.Guid.NewGuid().ToString("D");
    public string Command { get; init; } = "";
    public object? Params { get; init; }
}

/// <summary>Response envelope from the host to the app. <c>Error</c> is present iff <c>Ok</c> is false.</summary>
public sealed record HostResponse
{
    public string Type { get; init; } = "response";
    public string Id { get; init; } = "";
    public bool Ok { get; init; }
    public object? Result { get; init; }
    public HostError? Error { get; init; }
}

/// <summary>Structured error in a <see cref="HostResponse"/>.</summary>
public sealed record HostError
{
    public string Code { get; init; } = HostErrorCodes.Internal;
    public string Message { get; init; } = "";
    public string? Detail { get; init; }
}

/// <summary>
/// Push frame from the host to the app (not correlated to a request id). For <c>Kind</c> ==
/// <see cref="HostStreamKinds.LiveData"/>, <c>Payload</c> is a <see cref="LiveDataStreamPayload"/>.
/// </summary>
public sealed record HostStreamFrame
{
    public string Type { get; init; } = "stream";
    /// <summary>Subscription id returned by <c>subscribe-live-data</c>.</summary>
    public string StreamId { get; init; } = "";
    public string Kind { get; init; } = HostStreamKinds.LiveData;
    public object? Payload { get; init; }
}

/// <summary>An installed/attached device as reported by <c>list-devices</c>.</summary>
public sealed record DeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Device class: "j2534" | "rp1210" | "simulator".</summary>
    public string Kind { get; init; } = "";
    public IReadOnlyList<ProtocolType> Protocols { get; init; } = Array.Empty<ProtocolType>();
    public bool IsSimulated { get; init; }
}

// ---- Command parameter/result records (typed shapes for Params/Result) ----

public sealed record ListDevicesResult
{
    public IReadOnlyList<DeviceInfo> Devices { get; init; } = Array.Empty<DeviceInfo>();
}

public sealed record ConnectParams
{
    public string DeviceId { get; init; } = "";
    /// <summary>Protocol to negotiate; null = device auto-selects its default.</summary>
    public ProtocolType? Protocol { get; init; }
}

public sealed record ConnectResult
{
    public ProtocolType Protocol { get; init; }
}

/// <summary>Empty acknowledgment for <c>disconnect</c> and <c>unsubscribe-live-data</c>.</summary>
public sealed record HostAck
{
    public bool Acknowledged { get; init; } = true;
}

public sealed record ReadDTCsResult
{
    public IReadOnlyList<DtcReadout> Dtcs { get; init; } = Array.Empty<DtcReadout>();
}

public sealed record ClearDTCsResult
{
    /// <summary>Number of active/pending codes cleared (0 when there were none).</summary>
    public int Cleared { get; init; }
}

public sealed record ReadLiveDataParams
{
    /// <summary>PID names to read; empty = adapter's default set.</summary>
    public IReadOnlyList<string> PidNames { get; init; } = Array.Empty<string>();
}

public sealed record ReadLiveDataResult
{
    public IReadOnlyList<PidValue> Pids { get; init; } = Array.Empty<PidValue>();
}

public sealed record SubscribeLiveDataParams
{
    public IReadOnlyList<string> PidNames { get; init; } = Array.Empty<string>();
    /// <summary>Target update period in milliseconds (adapter may coalesce).</summary>
    public int IntervalMs { get; init; } = 100;
}

public sealed record SubscribeLiveDataResult
{
    public string StreamId { get; init; } = "";
}

public sealed record UnsubscribeLiveDataParams
{
    public string StreamId { get; init; } = "";
}

public sealed record ReadFreezeFrameResult
{
    public FreezeFrameData? FreezeFrame { get; init; }
}

public sealed record GetVehicleInfoResult
{
    public VehicleInfo VehicleInfo { get; init; } = new();
}

public sealed record PingResult
{
    public string Pong { get; init; } = "pong";
    public long UptimeMs { get; init; }
}

/// <summary>Payload for a <see cref="HostStreamFrame"/> of kind <see cref="HostStreamKinds.LiveData"/>.</summary>
public sealed record LiveDataStreamPayload
{
    /// <summary>A batch of one or more PID readings captured at (approximately) the same instant.</summary>
    public IReadOnlyList<PidValue> Pids { get; init; } = Array.Empty<PidValue>();
}
