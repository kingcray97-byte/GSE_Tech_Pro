using GseTechPro.Core.Ai;

namespace GseTechPro.Core.Contracts;

/// <summary>Message roles for the AI chat boundary (matches LiteLLM/OpenAI roles).</summary>
public static class AiChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>A single chat message. Phase 3.</summary>
public sealed record AiChatMessage
{
    public string Role { get; init; } = AiChatRoles.User;
    public string Content { get; init; } = "";
    /// <summary>Tool-call id; required when <see cref="Role"/> is <see cref="AiChatRoles.Tool"/>.</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>Request to the local AI chat endpoint. Phase 3.</summary>
public sealed record AiChatRequest
{
    public IReadOnlyList<AiChatMessage> Messages { get; init; } = Array.Empty<AiChatMessage>();
    /// <summary>Optional work order / asset context the model may reference.</summary>
    public string? WorkOrderId { get; init; }
    public string? AssetId { get; init; }
    public bool UseTools { get; init; } = true;
    public string? SystemInstruction { get; init; }
    /// <summary>Route choice (ADR-013). Null = this tablet only.</summary>
    public AiRoutingOptions? Routing { get; init; }
}

/// <summary>A manual search hit (one chunk) with a human-readable citation. Phase 3.</summary>
public sealed record SearchResult
{
    /// <summary>manual_chunks.guid.</summary>
    public string ChunkId { get; init; } = "";
    public string ManualId { get; init; } = "";
    public string ManualTitle { get; init; } = "";
    public int PageStart { get; init; }
    public int PageEnd { get; init; }
    public string Section { get; init; } = "";
    public string Text { get; init; } = "";
    public double Score { get; init; }
    /// <summary>e.g. "TUG 660-28 Maintenance Manual, page 12".</summary>
    public string Citation { get; init; } = "";
}
