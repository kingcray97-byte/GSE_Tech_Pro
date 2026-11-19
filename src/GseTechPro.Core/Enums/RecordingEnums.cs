using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>What kind of data a recording session captures. Phase 2 records live data only.</summary>
public enum RecordingKind
{
    [EnumMember(Value = "live_data")] LiveData,
    // Phase 7: [EnumMember(Value = "can")] Can,
    // Phase 6: [EnumMember(Value = "scope")] Scope,
}

/// <summary>
/// Provenance of a recording's captured data: whether the underlying adapter was the simulator or
/// real hardware. This is the persisted (storage) form of the per-value <c>IsSimulated</c> wire flag.
/// </summary>
public enum RecordingSource
{
    [EnumMember(Value = "simulated")] Simulated = 0,  // 0 = safe default for unknown DB values
    [EnumMember(Value = "real")] Real = 1,
}

/// <summary>Lifecycle state of a recording session.</summary>
public enum RecordingStatus
{
    /// <summary>Recording in progress.</summary>
    [EnumMember(Value = "recording")] Recording,
    /// <summary>Stopped normally (manual stop or trigger auto-stop).</summary>
    [EnumMember(Value = "completed")] Completed,
    /// <summary>Ended abnormally (device host died, adapter disconnected, app closed).</summary>
    [EnumMember(Value = "interrupted")] Interrupted,
}

/// <summary>What starts/stops a recording (matches Shop Foreman Pro's trigger setup, screen 18).</summary>
public enum TriggerKind
{
    /// <summary>Tech presses record/stop; no automatic trigger.</summary>
    [EnumMember(Value = "manual")] Manual,
    /// <summary>Start/stop when a specific DTC appears on the bus.</summary>
    [EnumMember(Value = "on_dtc")] OnDtc,
    /// <summary>Start/stop when a PID crosses a configured threshold.</summary>
    [EnumMember(Value = "on_pid_threshold")] OnPidThreshold,
}

/// <summary>Threshold comparison for an <c>OnPidThreshold</c> trigger.</summary>
public enum ThresholdComparison
{
    /// <summary>Value &gt;= threshold.</summary>
    [EnumMember(Value = "above")] Above,
    /// <summary>Value &lt;= threshold.</summary>
    [EnumMember(Value = "below")] Below,
    /// <summary>Value crosses the threshold in either direction (edge-triggered).</summary>
    [EnumMember(Value = "crosses")] Crosses,
}

/// <summary>Playback transport state for the VCR-style bar (screen 17).</summary>
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    /// <summary>Not playing back — showing the live stream (playback bar hidden/idle).</summary>
    Live,
}
