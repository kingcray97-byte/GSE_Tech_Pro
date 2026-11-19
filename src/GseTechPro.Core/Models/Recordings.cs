using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>
/// A live-data recording session. Table: recordings (ARCHITECTURE.md §2.2). One row per recording;
/// the samples live in <see cref="RecordingFrame"/>. <see cref="TriggerJson"/> and
/// <see cref="PidNamesJson"/> are JSON strings matching the schema convention (typed form in
/// <see cref="Contracts.TriggerConfig"/>).
/// </summary>
public sealed record Recording : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public string? AssetId { get; init; }
    public string? TechnicianId { get; init; }
    public RecordingKind Kind { get; init; } = RecordingKind.LiveData;
    /// <summary>Provenance: whether the captured data came from the simulator or real hardware.</summary>
    public RecordingSource Source { get; init; } = RecordingSource.Real;
    /// <summary>Adapter/device name at capture time (e.g. "TOPDON RLink X3", "Simulator (J1939)").</summary>
    public string AdapterName { get; init; } = "";
    public ProtocolType Protocol { get; init; }
    /// <summary>JSON array of the PID names recorded (e.g. ["Engine Speed","Coolant Temperature"]).</summary>
    public string PidNamesJson { get; init; } = "[]";
    /// <summary>Serialized <see cref="Contracts.TriggerConfig"/>; null/empty = manual recording.</summary>
    public string? TriggerJson { get; init; }
    public string StartedAt { get; init; } = Clock.UtcNow;
    public string? EndedAt { get; init; }
    public RecordingStatus Status { get; init; } = RecordingStatus.Recording;
    /// <summary>Denormalized count of <see cref="RecordingFrame"/> rows; updated on close.</summary>
    public int FrameCount { get; init; }
    public string? Notes { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>
/// One sample frame in a recording. Table: recording_frames (INTEGER PK + guid, high-volume).
/// A "frame" is one timestamp's batch of all recorded PIDs; <see cref="PidsJson"/> is a JSON array
/// of <see cref="Contracts.PidValue"/> (each preserving its <c>IsSimulated</c> flag). Recording
/// frames are excluded from shop-server sync (only the parent <see cref="Recording"/> metadata
/// syncs; raw frames stay local).
/// </summary>
public sealed record RecordingFrame
{
    /// <summary>SQLite INTEGER rowid (like <see cref="ManualPage"/>/<see cref="ManualChunk"/>).</summary>
    public long Id { get; init; }
    /// <summary>UUID for uniqueness/export; separate from the rowid.</summary>
    public string Guid { get; init; } = System.Guid.NewGuid().ToString("D");
    public string RecordingId { get; init; } = "";
    /// <summary>Monotonic, 0-based frame index within the recording.</summary>
    public long Sequence { get; init; }
    public string CapturedAt { get; init; } = Clock.UtcNow;
    /// <summary>JSON array of <see cref="Contracts.PidValue"/> for this instant.</summary>
    public string PidsJson { get; init; } = "[]";
    public string CreatedAt { get; init; } = Clock.UtcNow;
}
