using System.Runtime.InteropServices;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices.Can.Ecom;

/// <summary>
/// ECOM CAN-to-USB adapter as a listen-only bus monitor (Phase 10C.1). Opens the controller with
/// <c>CAN_LISTEN_ONLY</c> OR'd into the baud code, polls 29-bit then 11-bit receive queues and the error queue, the
/// way the vendor's published receive example does. Never transmits.
/// <para>
/// <b>Not verified on hardware</b> (no ECOM on the dev machine, and the driver is not installed here). Open items:
/// the bitness of <c>ecommlib.dll</c> (in-process needs a 64-bit DLL; a 32-bit-only DLL needs the 32-bit host),
/// the unit of the adapter timestamp, and the 11-bit id packing (see <see cref="UnpackStandardId"/>).
/// </para>
/// </summary>
public sealed class EcomCanBusMonitor : CanBusMonitorBase
{
    private const int MaxFramesPerPoll = 512;

    private readonly uint _serialNumber;
    private nint _handle;

    /// <param name="serialNumber">Adapter serial number, or 0 for the first free ECOM.</param>
    public EcomCanBusMonitor(uint serialNumber = 0) => _serialNumber = serialNumber;

    public override string Name => _serialNumber == 0 ? "ECOM CAN adapter" : $"ECOM S/N {_serialNumber}";
    public override bool IsSimulated => false;

    /// <summary>Bit rates with an ECOM baud code (vendor sample).</summary>
    public override IReadOnlyList<int> SupportedBitrates { get; } = new[] { 125_000, 250_000, 500_000, 1_000_000 };

    /// <summary>The CANOpen baud byte for a bit rate, always with the listen-only flag set.</summary>
    public static byte ListenOnlyBaudCode(int bitrate) => (byte)(bitrate switch
    {
        125_000 => EcomNative.CanBaud125K,
        250_000 => EcomNative.CanBaud250K,
        500_000 => EcomNative.CanBaud500K,
        1_000_000 => EcomNative.CanBaud1M,
        _ => throw new ArgumentOutOfRangeException(nameof(bitrate), bitrate, "ECOM supports 125k, 250k, 500k and 1M bit/s."),
    } | EcomNative.CanListenOnly);

    /// <summary>
    /// 11-bit id from the two id bytes. Assumes the SJA1000 standard-frame layout (ID.10–ID.3 in the first byte,
    /// ID.2–ID.0 in the top three bits of the second; NXP SJA1000 data sheet, standard frame format), since the ECOM
    /// error codes refer to an SJA1000 controller. TODO: confirm against <c>ecommlib.h</c> or a real capture.
    /// </summary>
    public static uint UnpackStandardId(byte idHigh, byte idLow) => ((uint)idHigh << 3) | ((uint)idLow >> 5);

    protected override Task OpenAsync(CanMonitorOptions options, CancellationToken cancellationToken)
    {
        var baud = ListenOnlyBaudCode(options.Bitrate);
        byte error = 0;
        nint handle;
        try
        {
            handle = EcomNative.CANOpen(_serialNumber, baud, ref error);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException(EcomDriver.UnavailableReason(ex), ex);
        }

        if (handle == 0 || error != EcomNative.EciNoError)
        {
            if (handle != 0)
                EcomNative.CloseDevice(handle);
            throw new InvalidOperationException($"Could not open the ECOM adapter: {EcomNative.FriendlyError(error)}");
        }

        _handle = handle;
        return Task.CompletedTask;
    }

    protected override Task CloseAsync()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            try { EcomNative.CloseDevice(handle); }
            catch (Exception) { /* closing must not throw */ }
        }
        return Task.CompletedTask;
    }

    protected override int Poll(CancellationToken cancellationToken)
    {
        var handle = _handle;
        if (handle == 0)
            return 0;

        var count = 0;
        var now = Clock.Elapsed.TotalSeconds;

        while (count < MaxFramesPerPoll)
        {
            var eff = new EcomNative.EffMessage();
            var rc = EcomNative.CANReceiveMessageEx(handle, ref eff);
            if (rc == EcomNative.CanNoRxMessages)
                break;
            if (rc != EcomNative.EciNoError)
            {
                ReportReceiveError(rc);
                break;
            }
            Publish(FromExtended(eff, now));
            count++;
        }

        while (count < MaxFramesPerPoll)
        {
            var sff = new EcomNative.SffMessage();
            var rc = EcomNative.CANReceiveMessage(handle, ref sff);
            if (rc == EcomNative.CanNoRxMessages)
                break;
            if (rc != EcomNative.EciNoError)
            {
                ReportReceiveError(rc);
                break;
            }
            Publish(FromStandard(sff, now));
            count++;
        }

        for (var i = 0; i < 16; i++)
        {
            var err = new EcomNative.ErrorMessage();
            var rc = EcomNative.GetErrorMessage(handle, ref err);
            if (rc != EcomNative.EciNoError)
                break; // CAN_NO_ERROR_MESSAGES or a read failure: nothing to report
            RaiseBusError(err.ErrorCode, DescribeBusError(err.ErrorCode));
        }

        return count;
    }

    /// <summary>Technician-readable text for the bus error codes the vendor sample documents.</summary>
    public static string DescribeBusError(byte code) => code switch
    {
        EcomNative.CanErrBus => "CAN bus error (check wiring, termination and bit rate).",
        EcomNative.CanErrBusOffEvent => "Adapter went bus-off (too many errors). Check wiring, termination and bit rate.",
        EcomNative.CanBusBackOnEvent => "Adapter is back on the bus after a bus-off.",
        EcomNative.CanErrPassive => "Adapter is error-passive (many bus errors). Check the bit rate.",
        EcomNative.CanErrOverrun or EcomNative.CanErrOverrunPc => "Receive overrun: some frames were lost.",
        EcomNative.ErrEffRxFifoOverrun or EcomNative.ErrSffRxFifoOverrun => "Receive buffer full: some frames were lost.",
        EcomNative.ErrDeviceUnplugged => "The ECOM adapter was unplugged.",
        _ => $"ECOM bus event 0x{code:X2}.",
    };

    internal static CanFrame FromExtended(in EcomNative.EffMessage m, double timestampSeconds)
    {
        var remote = (m.Options & EcomNative.OptionRemoteFrame) != 0;
        var length = Math.Min((int)m.DataLength, 8);
        Span<byte> all = stackalloc byte[8] { m.Data1, m.Data2, m.Data3, m.Data4, m.Data5, m.Data6, m.Data7, m.Data8 };
        return new CanFrame
        {
            Id = m.Id & 0x1FFF_FFFF,
            IsExtended = true,
            IsRemote = remote,
            Data = remote ? Array.Empty<byte>() : all[..length].ToArray(),
            TimestampSeconds = timestampSeconds,
            DeviceTimestamp = m.TimeStamp,
        };
    }

    internal static CanFrame FromStandard(in EcomNative.SffMessage m, double timestampSeconds)
    {
        var remote = (m.Options & EcomNative.OptionRemoteFrame) != 0;
        var length = Math.Min((int)m.DataLength, 8);
        Span<byte> all = stackalloc byte[8] { m.Data1, m.Data2, m.Data3, m.Data4, m.Data5, m.Data6, m.Data7, m.Data8 };
        return new CanFrame
        {
            Id = UnpackStandardId(m.IdH, m.IdL),
            IsExtended = false,
            IsRemote = remote,
            Data = remote ? Array.Empty<byte>() : all[..length].ToArray(),
            TimestampSeconds = timestampSeconds,
            DeviceTimestamp = m.TimeStamp,
        };
    }

    private void ReportReceiveError(byte rc) =>
        RaiseBusError(rc, rc == EcomNative.ErrDeviceUnplugged
            ? DescribeBusError(rc)
            : $"ECOM receive failed: {EcomNative.FriendlyError(rc)}");
}

/// <summary>Finds whether the ECOM driver library can be loaded in this process, and lists attached adapters.</summary>
public static class EcomDriver
{
    /// <summary>Null when the vendor library loads in this process; otherwise why not, in plain words.</summary>
    public static string? CheckLibrary()
    {
        try
        {
            if (EcomNative.GetHandle() != 0)
                return null;
            return UnavailableReason(new DllNotFoundException());
        }
        catch (BadImageFormatException ex)
        {
            return UnavailableReason(ex);
        }
    }

    /// <summary>Attached ECOM adapters, or an empty list when the driver is missing or none are plugged in.</summary>
    public static IReadOnlyList<uint> FindSerialNumbers()
    {
        if (CheckLibrary() is not null)
            return Array.Empty<uint>();

        var serials = new List<uint>();
        try
        {
            var search = EcomNative.StartDeviceSearch(EcomNative.FindAll);
            if (search == 0)
                return serials;
            try
            {
                var info = new EcomNative.DeviceInfo();
                while (serials.Count < 32 && EcomNative.FindNextDevice(search, ref info) == EcomNative.EciNoError)
                    serials.Add(info.SerialNumber);
            }
            finally
            {
                EcomNative.CloseDeviceSearch(search);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Array.Empty<uint>();
        }
        return serials;
    }

    internal static string UnavailableReason(Exception ex) => ex switch
    {
        BadImageFormatException =>
            "The installed ECOM driver library is 32-bit; this app needs the 64-bit library (32-bit host support for ECOM is not built yet).",
        _ => "The ECOM driver library (ecommlib64.dll or ecommlib.dll) was not found. Install the vendor's ECOM driver on this tablet, then try again.",
    };
}
