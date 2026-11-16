using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;

namespace GseTechPro.Core.Contracts;

/// <summary>A DTC as read from the bus/adapter (not yet persisted).</summary>
public sealed record DtcReadout
{
    public string Code { get; init; } = "";
    public ProtocolType Protocol { get; init; }
    public int? Spn { get; init; }
    public int? Fmi { get; init; }
    public DtcStatus Status { get; init; } = DtcStatus.Active;
    public string? Description { get; init; }
    public string? Severity { get; init; }
}

/// <summary>A single live-data parameter (PID) value as reported by the adapter.</summary>
public sealed record PidValue
{
    public string Name { get; init; } = "";
    public double Value { get; init; }
    /// <summary>
    /// Unit as reported by the adapter (e.g. "kPa", "V", "°C"). Adapters MAY report display units,
    /// so consumers MUST canonicalize before display: <c>UnitConverter.ToCanonical(Value, Unit)</c>
    /// guarded by <c>UnitConverter.IsKnownUnit(Unit)</c> (skip/raw display when unknown).
    /// </summary>
    public string Unit { get; init; } = "";
    /// <summary>ISO-8601 UTC.</summary>
    public string Timestamp { get; init; } = Clock.UtcNow;
}

/// <summary>A freeze-frame snapshot: capture time + the PIDs captured at that moment.</summary>
public sealed record FreezeFrameData
{
    public string CapturedAt { get; init; } = Clock.UtcNow;
    public IReadOnlyList<PidValue> Pids { get; init; } = Array.Empty<PidValue>();
}

/// <summary>Vehicle identification returned by <c>GetVehicleInfoAsync</c>.</summary>
public sealed record VehicleInfo
{
    public string? Vin { get; init; }
    public ProtocolType Protocol { get; init; }
    public IReadOnlyList<EcuInfo> Ecus { get; init; } = Array.Empty<EcuInfo>();
}

/// <summary>A single ECU/controller discovered on the bus.</summary>
public sealed record EcuInfo
{
    public string Id { get; init; } = "";
    public string? Description { get; init; }
}

/// <summary>Raised per live-data update; carries a single PID reading.</summary>
public sealed class LiveDataUpdateEventArgs : EventArgs
{
    public LiveDataUpdateEventArgs(PidValue value) => Value = value;

    public PidValue Value { get; }
}
