namespace GseTechPro.Devices.Ipc;

/// <summary>Request envelope from the 64-bit app (JSON over the named pipe).</summary>
public sealed record HostRequest(string? Command);

/// <summary>Response envelope returned to the 64-bit app.</summary>
public sealed record HostResponse(bool Ok, string? Command, string Message, string Status);
