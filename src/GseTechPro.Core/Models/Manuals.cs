using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>A repair manual. Table: manuals.</summary>
public sealed record Manual : EntityBase
{
    public string Title { get; init; } = "";
    public string? Manufacturer { get; init; }
    public string? ModelFamily { get; init; }
    /// <summary>Relative path on disk.</summary>
    public string FilePath { get; init; } = "";
    /// <summary>Content hash for dedupe.</summary>
    public string FileHash { get; init; } = "";
    public int? PageCount { get; init; }
    public ManualFormat Format { get; init; } = ManualFormat.Pdf;
    public ManualStatus Status { get; init; } = ManualStatus.Imported;
    public string ImportedAt { get; init; } = Clock.UtcNow;
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Page-level metadata (open PDF to cited page). Table: manual_pages (INTEGER PK).</summary>
public sealed record ManualPage
{
    /// <summary>SQLite INTEGER rowid.</summary>
    public long Id { get; init; }
    /// <summary>UUID for sync (separate from the rowid).</summary>
    public string Guid { get; init; } = System.Guid.NewGuid().ToString("D");
    public string ManualId { get; init; } = "";
    public int PageNumber { get; init; }
    /// <summary>Detected: specs/troubleshooting/procedures/wiring/torque.</summary>
    public string? Section { get; init; }
    public bool HasImage { get; init; }
    public string? ThumbnailPath { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>A searchable text chunk. Table: manual_chunks (INTEGER PK for FTS5/vec0 rowid alignment).</summary>
public sealed record ManualChunk
{
    /// <summary>SQLite INTEGER rowid — shared key with manual_chunks_fts and embeddings.</summary>
    public long Id { get; init; }
    /// <summary>UUID for sync (separate from the rowid).</summary>
    public string Guid { get; init; } = System.Guid.NewGuid().ToString("D");
    public string ManualId { get; init; } = "";
    public int PageStart { get; init; }
    public int PageEnd { get; init; }
    public string? Section { get; init; }
    public ChunkKind ChunkKind { get; init; }
    public string? Heading { get; init; }
    public string ChunkText { get; init; } = "";
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}

/// <summary>Manufacturer/model/system tag for filtering. Table: chunk_tags (composite PK).</summary>
public sealed record ChunkTag
{
    public long ChunkId { get; init; }
    public string Tag { get; init; } = "";
}
