using System.Runtime.InteropServices;
using System.Text;

namespace GseTechPro.Devices.Can.Ecom;

/// <summary>
/// P/Invoke surface of the ECOM CAN-to-USB adapter library (<c>ecommlib.dll</c>, installed by the vendor driver).
/// Written from the vendor's published API examples (C, C# and Java samples shipped with the driver; read-only in
/// <c>Reference/GSE diagnostic apps/ECOM/</c>). Only the receive/enumerate calls we need are declared, plus the
/// constants they use. No transmit call is declared on purpose: the monitor is listen-only.
/// <para>
/// The 64-bit driver library is named <c>ecommlib64.dll</c> (the vendor driver's x64 folder and System32); the
/// 32-bit one is <c>ecommlib.dll</c>. <see cref="LibraryNames"/> tries the 64-bit name first and, when it does not
/// load in this process, the 32-bit name. Exports are resolved through the loaded library handle
/// (<see cref="GetFunction{T}"/>) rather than DllImport, so a call uses the library that actually loaded.
/// </para>
/// <para>
/// Handles are pointer-sized (<c>HANDLE</c> in the C sample). The vendor C# sample uses <c>UInt32</c>, which only
/// works when the DLL is 32-bit; <c>nint</c> is right for either bitness.
/// </para>
/// </summary>
internal static class EcomNative
{
    /// <summary>Candidate library names, most specific first (the 64-bit DLL is called <c>ecommlib64.dll</c>).</summary>
    public static readonly string[] LibraryNames = { "ecommlib64.dll", "ecommlib.dll" };

    // Return codes (vendor sample).
    public const byte EciNoError = 0x00;
    public const byte CanNoRxMessages = 0x88;
    public const byte CanNoErrorMessages = 0x89;
    public const byte EciNoMoreDevices = 0x80;

    // Bus/controller error codes delivered through GetErrorMessage (vendor sample).
    public const byte CanErrBus = 0x11;
    public const byte CanErrBusOffEvent = 0x12;
    public const byte CanErrPassive = 0x1A;
    public const byte CanErrOverrun = 0x1B;
    public const byte CanErrOverrunPc = 0x1C;
    public const byte CanBusBackOnEvent = 0x18;
    public const byte ErrEffRxFifoOverrun = 0x21;
    public const byte ErrSffRxFifoOverrun = 0x22;
    public const byte ErrDeviceUnplugged = 0xE0;

    // CANOpen baud codes; OR with CanListenOnly for listen-only mode (vendor sample).
    public const byte CanBaud250K = 0x00;
    public const byte CanBaud500K = 0x01;
    public const byte CanBaud1M = 0x02;
    public const byte CanBaud125K = 0x03;
    public const byte CanListenOnly = 0x80;

    // StartDeviceSearch flags.
    public const byte FindAll = 0x87;

    /// <summary>"options" bit 6 = remote frame (vendor sample).</summary>
    public const byte OptionRemoteFrame = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    public struct EffMessage
    {
        public uint Id;
        public byte Data1, Data2, Data3, Data4, Data5, Data6, Data7, Data8;
        public byte Options;
        public byte DataLength;
        public uint TimeStamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SffMessage
    {
        public byte IdH;
        public byte IdL;
        public byte Data1, Data2, Data3, Data4, Data5, Data6, Data7, Data8;
        public byte Options;
        public byte DataLength;
        public uint TimeStamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public uint SerialNumber;
        public byte CanOpen;
        public byte SerOpen;
        public byte Reserved;
        public byte SyncCanTx;
        public nint DeviceHandle;
        public uint Reserved1;
        public uint Reserved2;
        public ushort Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ErrorMessage
    {
        public uint ErrorFifoSize;
        public byte ErrorCode;
        public byte ErrorData;
        public double Timestamp;
        public byte Reserved1;
        public byte Reserved2;
    }

    private static IntPtr _handle;

    /// <summary>The handle of the first vendor library name that loads in this process (cached), 0 when none do.</summary>
    internal static IntPtr GetHandle()
    {
        if (_handle != 0)
            return _handle;
        foreach (var name in LibraryNames)
        {
            if (NativeLibrary.TryLoad(name, typeof(EcomNative).Assembly, null, out var lib))
            {
                _handle = lib;
                return lib;
            }
        }
        return 0;
    }

    /// <summary>The export <paramref name="name"/> of the loaded library, as a delegate.</summary>
    private static T GetFunction<T>(string name) where T : Delegate
    {
        var handle = GetHandle();
        if (handle == 0)
            throw new DllNotFoundException($"No ECOM driver library could be loaded ({string.Join(", ", LibraryNames)}).");
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
    }

    public static nint CANOpen(uint serial, byte baud, ref byte error) =>
        GetFunction<CANOpenDelegate>("CANOpen")(serial, baud, ref error);

    public static byte CANReceiveMessageEx(nint deviceHandle, ref EffMessage message) =>
        GetFunction<CANReceiveMessageExDelegate>("CANReceiveMessageEx")(deviceHandle, ref message);

    public static byte CANReceiveMessage(nint deviceHandle, ref SffMessage message) =>
        GetFunction<CANReceiveMessageDelegate>("CANReceiveMessage")(deviceHandle, ref message);

    public static byte GetErrorMessage(nint deviceHandle, ref ErrorMessage message) =>
        GetFunction<GetErrorMessageDelegate>("GetErrorMessage")(deviceHandle, ref message);

    public static byte GetDeviceInfo(nint deviceHandle, ref DeviceInfo deviceInfo) =>
        GetFunction<GetDeviceInfoDelegate>("GetDeviceInfo")(deviceHandle, ref deviceInfo);

    public static byte CloseDevice(nint deviceHandle) =>
        GetFunction<CloseDeviceDelegate>("CloseDevice")(deviceHandle);

    public static void GetFriendlyErrorMessage(byte error, StringBuilder buffer, int bufferSize) =>
        GetFunction<GetFriendlyErrorMessageDelegate>("GetFriendlyErrorMessage")(error, buffer, bufferSize);

    public static nint StartDeviceSearch(byte flag) =>
        GetFunction<StartDeviceSearchDelegate>("StartDeviceSearch")(flag);

    public static byte CloseDeviceSearch(nint searchHandle) =>
        GetFunction<CloseDeviceSearchDelegate>("CloseDeviceSearch")(searchHandle);

    public static byte FindNextDevice(nint searchHandle, ref DeviceInfo deviceInfo) =>
        GetFunction<FindNextDeviceDelegate>("FindNextDevice")(searchHandle, ref deviceInfo);

    // Export delegates for GetFunction.
    private delegate nint CANOpenDelegate(uint serial, byte baud, ref byte error);
    private delegate byte CANReceiveMessageExDelegate(nint deviceHandle, ref EffMessage message);
    private delegate byte CANReceiveMessageDelegate(nint deviceHandle, ref SffMessage message);
    private delegate byte GetErrorMessageDelegate(nint deviceHandle, ref ErrorMessage message);
    private delegate byte GetDeviceInfoDelegate(nint deviceHandle, ref DeviceInfo deviceInfo);
    private delegate byte CloseDeviceDelegate(nint deviceHandle);
    private delegate void GetFriendlyErrorMessageDelegate(byte error, StringBuilder buffer, int bufferSize);
    private delegate nint StartDeviceSearchDelegate(byte flag);
    private delegate byte CloseDeviceSearchDelegate(nint searchHandle);
    private delegate byte FindNextDeviceDelegate(nint searchHandle, ref DeviceInfo deviceInfo);

    public static string FriendlyError(byte code)
    {
        var buffer = new StringBuilder(400);
        try
        {
            GetFriendlyErrorMessage(code, buffer, buffer.Capacity);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return $"ECOM error 0x{code:X2}";
        }
        var text = buffer.ToString().Trim();
        return string.IsNullOrEmpty(text) ? $"ECOM error 0x{code:X2}" : $"{text} (0x{code:X2})";
    }
}
