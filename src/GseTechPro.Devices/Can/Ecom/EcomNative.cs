using System.Runtime.InteropServices;
using System.Text;

namespace GseTechPro.Devices.Can.Ecom;

/// <summary>
/// P/Invoke surface of the ECOM CAN-to-USB adapter library (<c>ecommlib.dll</c>, installed by the vendor driver).
/// Written from the vendor's published API examples (C, C# and Java samples shipped with the driver; read-only in
/// <c>Reference/GSE diagnostic apps/ECOM/</c>). Only the receive/enumerate calls we need are declared, plus the
/// constants they use. No transmit call is declared on purpose: the monitor is listen-only.
/// <para>
/// Handles are pointer-sized (<c>HANDLE</c> in the C sample). The vendor C# sample uses <c>UInt32</c>, which only
/// works when the DLL is 32-bit; <c>nint</c> is right for either bitness.
/// </para>
/// </summary>
internal static class EcomNative
{
    public const string LibraryName = "ecommlib.dll";

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

    [DllImport(LibraryName)]
    public static extern nint CANOpen(uint serial, byte baud, ref byte error);

    [DllImport(LibraryName)]
    public static extern byte CANReceiveMessageEx(nint deviceHandle, ref EffMessage message);

    [DllImport(LibraryName)]
    public static extern byte CANReceiveMessage(nint deviceHandle, ref SffMessage message);

    [DllImport(LibraryName)]
    public static extern byte GetErrorMessage(nint deviceHandle, ref ErrorMessage message);

    [DllImport(LibraryName)]
    public static extern byte GetDeviceInfo(nint deviceHandle, ref DeviceInfo deviceInfo);

    [DllImport(LibraryName)]
    public static extern byte CloseDevice(nint deviceHandle);

    [DllImport(LibraryName, CharSet = CharSet.Ansi)]
    public static extern void GetFriendlyErrorMessage(byte error, StringBuilder buffer, int bufferSize);

    [DllImport(LibraryName)]
    public static extern nint StartDeviceSearch(byte flag);

    [DllImport(LibraryName)]
    public static extern byte CloseDeviceSearch(nint searchHandle);

    [DllImport(LibraryName)]
    public static extern byte FindNextDevice(nint searchHandle, ref DeviceInfo deviceInfo);

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
