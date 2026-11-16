using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>Change-data-capture log for optional shop-server sync. Table: sync_log.</summary>
public sealed record SyncLogEntry : EntityBase
{
    /// <summary>Table name.</summary>
    public string EntityType { get; init; } = "";
    /// <summary>Row GUID.</summary>
    public string EntityId { get; init; } = "";
    /// <summary>Row updated_at.</summary>
    public string Revision { get; init; } = "";
    public SyncChangeType ChangeType { get; init; }
    public bool Pushed { get; init; }
    public string? PushedAt { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
}
