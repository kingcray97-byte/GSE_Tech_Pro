using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>Free-form note attached to a work order, asset, technician, record, or frame. Table: notes.</summary>
public sealed record Note : EntityBase
{
    public NoteScope Scope { get; init; }
    public string TargetId { get; init; } = "";
    public string? TechnicianId { get; init; }
    public string Body { get; init; } = "";
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Photos, waveforms, recordings, scans, documents. Table: attachments.</summary>
public sealed record Attachment : EntityBase
{
    public NoteScope Scope { get; init; }
    public string TargetId { get; init; } = "";
    public AttachmentKind Kind { get; init; }
    /// <summary>Relative to the data dir (files live on disk).</summary>
    public string FilePath { get; init; } = "";
    public string? MimeType { get; init; }
    public long? SizeBytes { get; init; }
    public string? TakenAt { get; init; }
    public string? Caption { get; init; }
    /// <summary>Pen annotations (JSON).</summary>
    public string? AnnotationsJson { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}
