namespace GseTechPro.Devices.Ipc;

/// <summary>
/// Raised when the app↔host32 protocol returns an error (a non-OK <c>response</c> frame), or when the
/// client-side connection fails. <see cref="Code"/> is a <c>HostErrorCodes</c> value.
/// </summary>
public sealed class HostIpcException : Exception
{
    public HostIpcException(string code, string message, string? detail = null)
        : base(message)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }

    public string? Detail { get; }
}
