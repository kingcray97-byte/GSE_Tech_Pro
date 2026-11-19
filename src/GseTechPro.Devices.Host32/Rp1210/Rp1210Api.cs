using System.Runtime.InteropServices;

namespace GseTechPro.Devices.Host32.Rp1210;

/// <summary>Published TMC RP1210 client return/error codes (the subset used by this wrapper).</summary>
public static class Rp1210Status
{
    public const int NoErrors = 0;
    public const int ErrDllNotInitialized = 128;        // 0x80
    public const int ErrHardwareNotResponding = 129;    // 0x81
    public const int ErrInvalidClientId = 139;          // 0x8B
    public const int ErrInvalidDeviceId = 141;          // 0x8D
}

/// <summary>
/// Dynamic P/Invoke wrapper for a vendor RP1210 API DLL. The DLL path comes from RP121032.ini, so the
/// function pointers are resolved with <c>NativeLibrary</c>. Built ONLY against the published TMC
/// RP1210 client API (<c>RP1210_ClientConnect</c>/<c>RP1210_ReadMessage</c>/<c>RP1210_SendMessage</c>/
/// <c>RP1210_ClientDisconnect</c>/<c>RP1210_ReadVersion</c>); loaded ONLY in the x86 host.
///
/// NOTE: the RP1210 spec has evolved (A/B/C). This wrapper targets the widely-supported
/// <c>RP1210_ClientConnect(HWND, short, char*, long, long)</c> signature; the exact connect-status
/// bitfield and message byte framing for a specific vendor DLL are confirmed on hardware (Nexiq/Noregon).
/// </summary>
public sealed class Rp1210Api : IDisposable
{
    private readonly nint _handle;

    private readonly ClientConnectDelegate _clientConnect;
    private readonly ClientDisconnectDelegate _clientDisconnect;
    private readonly SendMessageDelegate _sendMessage;
    private readonly ReadMessageDelegate _readMessage;
    private readonly ReadVersionDelegate _readVersion;

    private Rp1210Api(nint handle, ClientConnectDelegate connect, ClientDisconnectDelegate disconnect,
        SendMessageDelegate send, ReadMessageDelegate read, ReadVersionDelegate version)
    {
        _handle = handle;
        _clientConnect = connect;
        _clientDisconnect = disconnect;
        _sendMessage = send;
        _readMessage = read;
        _readVersion = version;
    }

    /// <summary>Loads a vendor RP1210 DLL and resolves the published client entry points.</summary>
    public static Rp1210Api Open(string dllPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dllPath);
        var handle = NativeLibrary.Load(dllPath);
        try
        {
            return new Rp1210Api(
                handle,
                Resolve<ClientConnectDelegate>(handle, "RP1210_ClientConnect"),
                Resolve<ClientDisconnectDelegate>(handle, "RP1210_ClientDisconnect"),
                Resolve<SendMessageDelegate>(handle, "RP1210_SendMessage"),
                Resolve<ReadMessageDelegate>(handle, "RP1210_ReadMessage"),
                Resolve<ReadVersionDelegate>(handle, "RP1210_ReadVersion"));
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    /// <summary>RP1210_ClientConnect. Returns the client id, or throws on a connect error.</summary>
    public short ClientConnect(short deviceId, string protocol, int connectStatus, int bufferSize)
    {
        var clientId = _clientConnect(IntPtr.Zero, deviceId, protocol, connectStatus, bufferSize);
        // TMC RP1210C §10.3: a valid client id is 0..127; 0 is a legitimate id. Negative values are
        // error codes (see Rp1210Status).
        if (clientId < 0 || clientId > 127)
            throw new Rp1210Exception(clientId, $"RP1210_ClientConnect failed (device {deviceId}, protocol '{protocol}').");
        return clientId;
    }

    /// <summary>RP1210_ClientDisconnect.</summary>
    public void ClientDisconnect(short clientId)
    {
        var status = _clientDisconnect(clientId);
        if (status != Rp1210Status.NoErrors)
            throw new Rp1210Exception(status, "RP1210_ClientDisconnect failed.");
    }

    /// <summary>RP1210_SendMessage. Returns the number of bytes sent.</summary>
    public int SendMessage(short clientId, ReadOnlySpan<byte> message)
    {
        var buffer = message.ToArray();
        var sent = _sendMessage(clientId, buffer, (short)buffer.Length, notifyStatus: 0, txBufferSize: (short)buffer.Length);
        if (sent < 0)
            throw new Rp1210Exception(sent, "RP1210_SendMessage failed.");
        return sent;
    }

    /// <summary>RP1210_ReadMessage. Returns the bytes read, or an empty span on no data/timeout.</summary>
    public byte[] ReadMessage(short clientId, int bufferSize, bool blockOnRead)
    {
        var buffer = new byte[bufferSize];
        var read = _readMessage(clientId, buffer, (short)bufferSize, blockOnRead ? (short)1 : (short)0);
        if (read < 0)
            throw new Rp1210Exception(read, "RP1210_ReadMessage failed.");
        if (read == 0)
            return Array.Empty<byte>();
        return buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>RP1210_ReadVersion. Returns (dllMajor, dllMinor, apiMajor, apiMinor).</summary>
    public (string DllMajor, string DllMinor, string ApiMajor, string ApiMinor) ReadVersion()
    {
        var dllMajor = new byte[16];
        var dllMinor = new byte[16];
        var apiMajor = new byte[16];
        var apiMinor = new byte[16];
        var status = _readVersion(dllMajor, dllMinor, apiMajor, apiMinor);
        if (status != Rp1210Status.NoErrors)
            throw new Rp1210Exception(status, "RP1210_ReadVersion failed.");
        return (AnsiString(dllMajor), AnsiString(dllMinor), AnsiString(apiMajor), AnsiString(apiMinor));
    }

    private static string AnsiString(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;
        return System.Text.Encoding.ASCII.GetString(buffer, 0, length).TrimEnd('\0');
    }

    private static T Resolve<T>(nint handle, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(handle, name, out var ptr))
            throw new EntryPointNotFoundException($"The RP1210 DLL does not export '{name}'.");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public void Dispose() => NativeLibrary.Free(_handle);

    #region Delegates (RP1210 uses WINAPI = stdcall on x86)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short ClientConnectDelegate(IntPtr hWnd, short deviceId, string protocol, int connectStatus, int bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short ClientDisconnectDelegate(short clientId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short SendMessageDelegate(short clientId, [In] byte[] message, short messageSize, short notifyStatus, short txBufferSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short ReadMessageDelegate(short clientId, [Out] byte[] message, short bufferSize, short blockOnRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short ReadVersionDelegate([Out] byte[] dllMajor, [Out] byte[] dllMinor, [Out] byte[] apiMajor, [Out] byte[] apiMinor);

    #endregion
}

/// <summary>Raised when an RP1210 client call returns an error code.</summary>
public sealed class Rp1210Exception : Exception
{
    public Rp1210Exception(int status, string message) : base(message) => Status = status;

    public int Status { get; }
}
