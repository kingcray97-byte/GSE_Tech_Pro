using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// A recording session in its runtime (typed) form. Mirrors the persisted
/// <see cref="Models.Recording"/> row, but carries the typed <see cref="Trigger"/> and
/// <see cref="PidNames"/> instead of the JSON strings the table stores — the data layer converts
/// between the two (same idiom as <see cref="FreezeFrameData"/> vs <see cref="Models.FreezeFrame"/>).
/// </summary>
public sealed record RecordingSession
{
    public string Id { get; init; } = System.Guid.NewGuid().ToString("D");
    public string WorkOrderId { get; init; } = "";
    public string? AssetId { get; init; }
    public string? TechnicianId { get; init; }
    public RecordingKind Kind { get; init; } = RecordingKind.LiveData;
    public RecordingSource Source { get; init; } = RecordingSource.Real;
    public string AdapterName { get; init; } = "";
    public ProtocolType Protocol { get; init; }
    public IReadOnlyList<string> PidNames { get; init; } = Array.Empty<string>();
    public TriggerConfig? Trigger { get; init; }
    public string StartedAt { get; init; } = Clock.UtcNow;
    public string? EndedAt { get; init; }
    public RecordingStatus Status { get; init; } = RecordingStatus.Recording;
    public int FrameCount { get; init; }
}

/// <summary>
/// Trigger configuration for a live-data recording (Shop Foreman Pro trigger setup, screen 18).
/// Serialized to <c>recordings.trigger_json</c>; the typed form lives here so the recorder, the
/// trigger evaluator, and the playback UI share one shape.
/// </summary>
public sealed record TriggerConfig
{
    /// <summary>Manual, on-DTC, or threshold-on-PID.</summary>
    public TriggerKind Kind { get; init; } = TriggerKind.Manual;

    // ---- OnDtc (Kind == OnDtc) ----
    /// <summary>DTC code to match (e.g. "SPN 94 FMI 3"). Null = any DTC.</summary>
    public string? DtcCode { get; init; }

    // ---- OnPidThreshold (Kind == OnPidThreshold) ----
    /// <summary>PID name to watch (e.g. "Coolant Temperature").</summary>
    public string? PidName { get; init; }
    /// <summary>Threshold value in the unit given by <see cref="ThresholdUnit"/>.</summary>
    public double? ThresholdValue { get; init; }
    /// <summary>Unit the threshold was entered in (canonicalized before comparison).</summary>
    public string? ThresholdUnit { get; init; }
    public ThresholdComparison Comparison { get; init; } = ThresholdComparison.Above;

    /// <summary>Seconds of pre-trigger data to keep (buffered before the trigger fired). Default 2 s.</summary>
    public double PreTriggerSeconds { get; init; } = 2.0;
    /// <summary>Seconds to keep recording after the trigger fires before auto-stop. Default 5 s.</summary>
    public double PostTriggerSeconds { get; init; } = 5.0;
    /// <summary>Arm the trigger automatically when the recording session starts.</summary>
    public bool AutoStart { get; init; } = true;
    /// <summary>Stop recording automatically once the post-trigger window elapses.</summary>
    public bool AutoStop { get; init; } = true;
}

/// <summary>
/// A single recorded frame: one timestamp's batch of PID readings. This is the playback shape; the
/// persisted row is <see cref="Models.RecordingFrame"/> (which stores the same PIDs as JSON).
/// </summary>
public sealed record RecordedFrameBatch
{
    /// <summary>Monotonic frame index within the recording (0-based).</summary>
    public long Sequence { get; init; }
    /// <summary>ISO-8601 UTC capture time.</summary>
    public string CapturedAt { get; init; } = "";
    public IReadOnlyList<PidValue> Pids { get; init; } = Array.Empty<PidValue>();
}

/// <summary>
/// Playback cursor for the VCR-style bar (screen 17). Positions are seconds from the recording's
/// first frame so the UI can seek without loading every frame.
/// </summary>
public sealed record PlaybackCursor
{
    public string RecordingId { get; init; } = "";
    /// <summary>Current frame sequence.</summary>
    public long Sequence { get; init; }
    /// <summary>Capture time of the current frame (null when no frame is loaded).</summary>
    public string? CapturedAt { get; init; }
    /// <summary>Seconds since the recording's first frame.</summary>
    public double PositionSeconds { get; init; }
    /// <summary>Total recorded duration in seconds (0 while recording / unknown).</summary>
    public double DurationSeconds { get; init; }
    public PlaybackState State { get; init; } = PlaybackState.Stopped;
    /// <summary>Playback rate multiplier (0.25, 0.5, 1, 2, 4, …). 1.0 = real time.</summary>
    public double Rate { get; init; } = 1.0;
}

/// <summary>Seek request used to fetch a window of frames for playback.</summary>
public sealed record RecordingSeekRequest
{
    public string RecordingId { get; init; } = "";
    /// <summary>First frame sequence to return (inclusive).</summary>
    public long StartSequence { get; init; }
    /// <summary>Maximum number of frames to return.</summary>
    public int MaxFrames { get; init; } = 1000;
}

/// <summary>A window of frames returned for playback.</summary>
public sealed record RecordingFrameWindow
{
    public long StartSequence { get; init; }
    public IReadOnlyList<RecordedFrameBatch> Frames { get; init; } = Array.Empty<RecordedFrameBatch>();
    /// <summary>True when no more frames follow this window.</summary>
    public bool EndOfRecording { get; init; }
}
