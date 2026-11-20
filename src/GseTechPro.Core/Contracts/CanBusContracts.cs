namespace GseTechPro.Core.Contracts;

/// <summary>One received CAN frame (classic CAN, 0–8 data bytes).</summary>
public sealed record CanFrame
{
    /// <summary>11-bit (standard) or 29-bit (extended) identifier, right-aligned.</summary>
    public uint Id { get; init; }

    /// <summary>True for a 29-bit identifier (J1939 uses 29-bit).</summary>
    public bool IsExtended { get; init; }

    /// <summary>Remote transmission request (no data).</summary>
    public bool IsRemote { get; init; }

    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>Seconds since the monitor started, from the PC's monotonic clock.</summary>
    public double TimestampSeconds { get; init; }

    /// <summary>The adapter's own raw timestamp, when it gives one (unit is adapter-specific).</summary>
    public uint? DeviceTimestamp { get; init; }

    public bool IsSimulated { get; init; }

    public int Dlc => Data.Length;
}

/// <summary>A bus or adapter error reported while monitoring.</summary>
public sealed record CanBusErrorEvent
{
    /// <summary>Adapter-specific error code.</summary>
    public int Code { get; init; }

    public string Message { get; init; } = "";

    public double TimestampSeconds { get; init; }
}

public sealed record CanMonitorOptions
{
    /// <summary>Common CAN bit rates (bit/s). J1939 is 250 000 (500 000 on newer J1939-14 buses).</summary>
    public static IReadOnlyList<int> StandardBitrates { get; } = new[] { 125_000, 250_000, 500_000, 1_000_000 };

    public int Bitrate { get; init; } = 250_000;

    /// <summary>Capacity of the receive buffer, in frames.</summary>
    public int BufferCapacity { get; init; } = 20_000;
}

/// <summary>A CAN monitor the PC can open (from discovery).</summary>
public sealed record CanMonitorDevice
{
    /// <summary>"simulator" or "ecom".</summary>
    public string Kind { get; init; } = "simulator";

    public string Name { get; init; } = "";

    /// <summary>Adapter serial number (ECOM); 0 = first free device.</summary>
    public uint SerialNumber { get; init; }

    public bool IsSimulated { get; init; }

    /// <summary>Why the device cannot be opened, or null when it can.</summary>
    public string? UnavailableReason { get; init; }
}
