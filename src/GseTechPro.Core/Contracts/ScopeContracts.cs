using GseTechPro.Core.Models;

namespace GseTechPro.Core.Contracts;

/// <summary>Coupling mode for a scope channel. Phase 6.</summary>
public enum ScopeCoupling
{
    Dc,
    Ac,
}

/// <summary>Trigger source/edge. Phase 6.</summary>
public enum ScopeTriggerMode
{
    None,
    Rising,
    Falling,
}

/// <summary>Per-channel configuration. Phase 6.</summary>
public sealed record ScopeChannelConfig
{
    public int Channel { get; init; }
    public double VoltageRangeVolts { get; init; }
    public ScopeCoupling Coupling { get; init; } = ScopeCoupling.Dc;
    /// <summary>Probe multiplier (e.g. 1x, 10x, 20:1 attenuator).</summary>
    public double ProbeMultiplier { get; init; } = 1.0;
}

/// <summary>Capture arming request. Phase 6.</summary>
public sealed record ScopeCaptureRequest
{
    public IReadOnlyList<ScopeChannelConfig> Channels { get; init; } = Array.Empty<ScopeChannelConfig>();
    public int SampleRateHz { get; init; }
    public double TimebaseSecondsPerDivision { get; init; }
    public ScopeTriggerMode TriggerMode { get; init; } = ScopeTriggerMode.None;
    public int TriggerChannel { get; init; }
    public double TriggerLevelVolts { get; init; }
}

/// <summary>Completed capture: metadata + per-channel samples. Phase 6.</summary>
public sealed record ScopeCapture
{
    public int SampleCount { get; init; }
    public double SampleRateHz { get; init; }
    public double TimebaseSecondsPerDivision { get; init; }
    public string CapturedAt { get; init; } = Clock.UtcNow;
    /// <summary>Map of channel index -> samples (voltages, already probe-corrected).</summary>
    public IReadOnlyDictionary<int, double[]> Channels { get; init; } = new Dictionary<int, double[]>();
}

/// <summary>A computed measurement (frequency, Vpp, duty, rise time, ripple, current...). Phase 6.</summary>
public sealed record ScopeMeasurement
{
    public string Name { get; init; } = "";
    public double Value { get; init; }
    public string Unit { get; init; } = "";
    public int Channel { get; init; }
}
