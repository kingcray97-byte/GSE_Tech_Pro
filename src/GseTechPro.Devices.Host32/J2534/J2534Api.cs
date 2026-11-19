using System.Runtime.InteropServices;

namespace GseTechPro.Devices.Host32.J2534;

/// <summary>Published SAE J2534-1 pass-thru protocol identifiers.</summary>
public static class J2534Protocol
{
    public const uint J1850Pwm = 0x01;
    public const uint J1850Vpw = 0x02;
    public const uint Iso9141 = 0x03;
    public const uint Iso14230 = 0x04;
    public const uint Can = 0x05;
    public const uint Iso15765 = 0x06;
    /// <summary>J1708/J1587 (SCI-A engine).</summary>
    public const uint SciAEngine = 0x07;
    public const uint SciATrans = 0x08;
    public const uint SciBEngine = 0x09;
    public const uint SciBTrans = 0x0A;
}

/// <summary>Published SAE J2534-1 transmit flags (PASSTHRU_MSG.TxFlags), Table 11.</summary>
public static class J2534TxFlags
{
    /// <summary>Pad ISO 15765 frames to full length (flow-control/framing indicator).</summary>
    public const uint Iso15765FramePad = 0x00000040;
    public const uint Iso15765AddrType = 0x00000080;
    /// <summary>29-bit CAN identifier (J1939): bit #8 in Table 11.</summary>
    public const uint Can29BitId = 0x00000100;
    /// <summary>Allow both 11-bit and 29-bit CAN identifiers on a channel (Table 9).</summary>
    public const uint CanIdBoth = 0x00000200;
}

/// <summary>Published SAE J2534-1 receive-status flags (PASSTHRU_MSG.RxStatus), Table 10.</summary>
public static class J2534RxStatus
{
    /// <summary>0x00010000 set = received message; clear = transmitted.</summary>
    public const uint RxMsg = 0x00010000;
    public const uint Iso15765FirstFrame = 0x02000000;
    public const uint Iso15765ConsecutiveFrame = 0x04000000;
    public const uint StartOfMessage = 0x00800000;
    /// <summary>29-bit CAN identifier (J1939): bit #8 in Table 10.</summary>
    public const uint Can29BitId = 0x00000100;
}

/// <summary>Published SAE J2534-1 message filter types.</summary>
public static class J2534FilterType
{
    public const uint PassFilter = 0x00000001;
    public const uint BlockFilter = 0x00000002;
    public const uint FlowControlFilter = 0x00000003;
}

/// <summary>Published SAE J2534-1 pass-thru return codes.</summary>
public static class J2534Status
{
    public const int StatusNoerror = 0x00;
    public const int ErrNotSupported = 0x01;
    public const int ErrInvalidChannelId = 0x02;
    public const int ErrInvalidProtocolId = 0x03;
    public const int ErrNullParameter = 0x04;
    public const int ErrInvalidIoctlValue = 0x05;
    public const int ErrInvalidFlags = 0x06;
    public const int ErrFailed = 0x07;
    public const int ErrDeviceNotConnected = 0x08;
    public const int ErrTimeout = 0x09;
    public const int ErrInvalidMsg = 0x0A;
    public const int ErrInvalidTimeInterval = 0x0B;
    public const int ErrExceededLimit = 0x0C;
    public const int ErrInvalidMsgId = 0x0D;
    public const int ErrDeviceInUse = 0x0E;
    public const int ErrInvalidIoctlId = 0x0F;
    public const int ErrBufferEmpty = 0x10;
    public const int ErrBufferFull = 0x11;
    public const int ErrBufferOverflow = 0x12;
    public const int ErrPinInvalid = 0x13;
    public const int ErrChannelInUse = 0x14;
    public const int ErrMsgProtocolId = 0x15;
    public const int ErrInvalidFilterId = 0x16;
    public const int ErrNoFlowControl = 0x17;
    public const int ErrNotUnique = 0x18;
    public const int ErrInvalidBaudrate = 0x19;
    public const int ErrInvalidDeviceId = 0x1A;
}

/// <summary>
/// The published SAE J2534-1 <c>PASSTHRU_MSG</c> structure. <c>Data</c> is the fixed 4128-byte buffer
/// from the spec; the number of valid bytes is <c>DataSize</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PassThruMsg
{
    public uint ProtocolID;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
    public byte[] Data;

    /// <summary>Creates a message with its 4128-byte buffer initialized.</summary>
    public static PassThruMsg Create() => new() { Data = new byte[4128] };
}

/// <summary>Published SAE J2534-1 <c>SCONFIG</c> structure (parameter/value pair).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SConfig
{
    public uint Parameter;
    public uint Value;
}

/// <summary>Published SAE J2534-1 <c>SCONFIG_LIST</c> structure.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SConfigList
{
    public uint NumOfParams;
    public IntPtr ConfigPtr;
}

/// <summary>
/// Dynamic P/Invoke wrapper for a vendor J2534 pass-thru DLL. The DLL path is discovered from the
/// registry at runtime, so the function pointers are resolved with <c>NativeLibrary</c> rather than a
/// compile-time <c>[DllImport]</c>. Built ONLY against the published SAE J2534-1 API
/// (<c>PassThruOpen</c>/<c>PassThruConnect</c>/<c>PassThruReadMsgs</c>/…); loaded ONLY in the x86 host.
///
/// NOTE: the J2534 header declares these functions returning a C <c>long</c>, which is 32-bit on
/// Windows, so the delegates return C# <c>int</c>; the unsigned parameters are C# <c>uint</c>.
/// Real-hardware verification of the exact calling convention/struct packing is deferred.
/// </summary>
public sealed class J2534PassThru : IDisposable
{
    private readonly nint _handle;

    private readonly PassThruOpenDelegate _open;
    private readonly PassThruCloseDelegate _close;
    private readonly PassThruConnectDelegate _connect;
    private readonly PassThruDisconnectDelegate _disconnect;
    private readonly PassThruReadMsgsDelegate _readMsgs;
    private readonly PassThruWriteMsgsDelegate _writeMsgs;
    private readonly PassThruStartMsgFilterDelegate _startFilter;
    private readonly PassThruStopMsgFilterDelegate _stopFilter;
    private readonly PassThruReadVersionDelegate _readVersion;
    private readonly PassThruGetLastErrorDelegate _getLastError;

    private J2534PassThru(nint handle,
        PassThruOpenDelegate open, PassThruCloseDelegate close, PassThruConnectDelegate connect,
        PassThruDisconnectDelegate disconnect, PassThruReadMsgsDelegate readMsgs,
        PassThruWriteMsgsDelegate writeMsgs, PassThruStartMsgFilterDelegate startFilter,
        PassThruStopMsgFilterDelegate stopFilter, PassThruReadVersionDelegate readVersion,
        PassThruGetLastErrorDelegate getLastError)
    {
        _handle = handle;
        _open = open;
        _close = close;
        _connect = connect;
        _disconnect = disconnect;
        _readMsgs = readMsgs;
        _writeMsgs = writeMsgs;
        _startFilter = startFilter;
        _stopFilter = stopFilter;
        _readVersion = readVersion;
        _getLastError = getLastError;
    }

    /// <summary>Loads a vendor J2534 DLL and resolves the published pass-thru entry points.</summary>
    public static J2534PassThru Open(string dllPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dllPath);

        var handle = NativeLibrary.Load(dllPath);
        try
        {
            return new J2534PassThru(
                handle,
                Resolve<PassThruOpenDelegate>(handle, "PassThruOpen"),
                Resolve<PassThruCloseDelegate>(handle, "PassThruClose"),
                Resolve<PassThruConnectDelegate>(handle, "PassThruConnect"),
                Resolve<PassThruDisconnectDelegate>(handle, "PassThruDisconnect"),
                Resolve<PassThruReadMsgsDelegate>(handle, "PassThruReadMsgs"),
                Resolve<PassThruWriteMsgsDelegate>(handle, "PassThruWriteMsgs"),
                Resolve<PassThruStartMsgFilterDelegate>(handle, "PassThruStartMsgFilter"),
                Resolve<PassThruStopMsgFilterDelegate>(handle, "PassThruStopMsgFilter"),
                Resolve<PassThruReadVersionDelegate>(handle, "PassThruReadVersion"),
                Resolve<PassThruGetLastErrorDelegate>(handle, "PassThruGetLastError"));
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    /// <summary>PassThruOpen. Returns a device id for the opened pass-thru device.</summary>
    public uint OpenDevice(string? name = null)
    {
        // pName is optional; IntPtr to an ANSI name, or null for the default device.
        var namePtr = name is null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(name);
        try
        {
            var status = _open(namePtr, out var deviceId);
            ThrowOnError(status, "PassThruOpen");
            return deviceId;
        }
        finally
        {
            if (namePtr != IntPtr.Zero)
                Marshal.FreeHGlobal(namePtr);
        }
    }

    /// <summary>PassThruClose.</summary>
    public void CloseDevice(uint deviceId)
    {
        var status = _close(deviceId);
        ThrowOnError(status, "PassThruClose");
    }

    /// <summary>PassThruConnect. Returns a channel id for the negotiated protocol.</summary>
    public uint Connect(uint deviceId, uint protocolId, uint flags, uint baudrate)
    {
        var status = _connect(deviceId, protocolId, flags, baudrate, out var channelId);
        ThrowOnError(status, "PassThruConnect");
        return channelId;
    }

    /// <summary>PassThruDisconnect.</summary>
    public void Disconnect(uint channelId)
    {
        var status = _disconnect(channelId);
        ThrowOnError(status, "PassThruDisconnect");
    }

    /// <summary>PassThruReadMsgs. Blocks up to <paramref name="timeoutMs"/>; returns the messages read.</summary>
    public IReadOnlyList<PassThruMsg> ReadMessages(uint channelId, uint maxCount, uint timeoutMs)
    {
        var messages = new PassThruMsg[maxCount];
        for (var i = 0; i < maxCount; i++)
            messages[i] = PassThruMsg.Create();

        var numMsgs = maxCount;
        var status = _readMsgs(channelId, messages, ref numMsgs, timeoutMs);
        ThrowOnError(status, "PassThruReadMsgs");

        var result = new PassThruMsg[numMsgs];
        for (var i = 0; i < numMsgs; i++)
            result[i] = messages[i];
        return result;
    }

    /// <summary>PassThruWriteMsgs.</summary>
    public void WriteMessages(uint channelId, IReadOnlyList<PassThruMsg> messages, uint timeoutMs)
    {
        var array = messages.ToArray();
        var numMsgs = (uint)array.Length;
        var status = _writeMsgs(channelId, array, ref numMsgs, timeoutMs);
        ThrowOnError(status, "PassThruWriteMsgs");
    }

    /// <summary>PassThruStartMsgFilter (no mask/pattern/flow-control: pass all). Returns a filter id.</summary>
    public uint StartPassFilter(uint channelId)
    {
        var status = _startFilter(channelId, J2534FilterType.PassFilter, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var filterId);
        ThrowOnError(status, "PassThruStartMsgFilter");
        return filterId;
    }

    /// <summary>PassThruStopMsgFilter.</summary>
    public void StopFilter(uint channelId, uint filterId)
    {
        var status = _stopFilter(channelId, filterId);
        ThrowOnError(status, "PassThruStopMsgFilter");
    }

    /// <summary>PassThruReadVersion. Returns (firmware, dll, api) version strings.</summary>
    public (string Firmware, string Dll, string Api) ReadVersion(uint deviceId)
    {
        var firmware = new byte[80];
        var dll = new byte[80];
        var api = new byte[80];
        var status = _readVersion(deviceId, firmware, dll, api);
        ThrowOnError(status, "PassThruReadVersion");
        return (AnsiString(firmware), AnsiString(dll), AnsiString(api));
    }

    /// <summary>PassThruGetLastError. Returns the human-readable description of the last error.</summary>
    public string GetLastError()
    {
        var buffer = new byte[80];
        var status = _getLastError(buffer);
        _ = status; // the description is authoritative, not the status code
        return AnsiString(buffer);
    }

    private static string AnsiString(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;
        return System.Text.Encoding.ASCII.GetString(buffer, 0, length).TrimEnd('\0');
    }

    private static void ThrowOnError(int status, string operation)
    {
        if (status == J2534Status.StatusNoerror)
            return;
        throw new J2534Exception(status, $"{operation} failed with J2534 status 0x{status:X2}.");
    }

    private static T Resolve<T>(nint handle, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(handle, name, out var ptr))
            throw new EntryPointNotFoundException($"The J2534 DLL does not export '{name}'.");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public void Dispose() => NativeLibrary.Free(_handle);

    #region Delegates (cdecl/stdcall: J2534 uses WINAPI = stdcall on x86)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruOpenDelegate(IntPtr pName, out uint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruCloseDelegate(uint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruConnectDelegate(uint deviceId, uint protocolId, uint flags, uint baudrate, out uint channelId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruDisconnectDelegate(uint channelId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruReadMsgsDelegate(uint channelId, [In, Out] PassThruMsg[] msgs, ref uint numMsgs, uint timeout);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruWriteMsgsDelegate(uint channelId, [In] PassThruMsg[] msgs, ref uint numMsgs, uint timeout);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruStartMsgFilterDelegate(uint channelId, uint filterType, IntPtr maskMsg, IntPtr patternMsg, IntPtr flowControlMsg, out uint filterId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruStopMsgFilterDelegate(uint channelId, uint filterId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruReadVersionDelegate(uint deviceId, [Out] byte[] firmwareVersion, [Out] byte[] dllVersion, [Out] byte[] apiVersion);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PassThruGetLastErrorDelegate([Out] byte[] errorDescription);

    #endregion
}

/// <summary>Raised when a J2534 pass-thru call returns a non-zero status.</summary>
public sealed class J2534Exception : Exception
{
    public J2534Exception(int status, string message) : base(message) => Status = status;

    public int Status { get; }
}
