using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>Overall health of the local AI service (drives the app's "warming up / ready / degraded" UI).</summary>
public enum AiServiceStatus
{
    /// <summary>Booting or chat model still warming up.</summary>
    [EnumMember(Value = "starting")] Starting,
    /// <summary>Fully ready (chat + search available).</summary>
    [EnumMember(Value = "ok")] Ok,
    /// <summary>Running with reduced capability (e.g. model runtime unreachable, chat unavailable).</summary>
    [EnumMember(Value = "degraded")] Degraded,
}

/// <summary>What a <see cref="GseTechPro.Core.Contracts.Citation"/> points at.</summary>
public enum CitationKind
{
    [EnumMember(Value = "manual-page")] ManualPage,
    [EnumMember(Value = "work-order")] WorkOrder,
    [EnumMember(Value = "dtc")] Dtc,
}

/// <summary>Lifecycle of an asynchronous ingestion job.</summary>
public enum IngestionState
{
    [EnumMember(Value = "pending")] Pending,
    [EnumMember(Value = "running")] Running,
    [EnumMember(Value = "succeeded")] Succeeded,
    [EnumMember(Value = "failed")] Failed,
    [EnumMember(Value = "cancelled")] Cancelled,
}

/// <summary>Where a chat answer came from (ADR-013). Wire values are the route names on <c>/chat</c> and <c>/health</c>.</summary>
public enum AiRoute
{
    /// <summary>llama.cpp on this tablet (ADR-012). Always there; the last fallback.</summary>
    [EnumMember(Value = "tablet")] Tablet,
    /// <summary>The shop's own LiteLLM proxy (shop LAN or private tunnel).</summary>
    [EnumMember(Value = "shop-server")] ShopServer,
    /// <summary>A hosted model through LiteLLM. Administrator opt-in, online only.</summary>
    [EnumMember(Value = "cloud")] Cloud,
}

/// <summary>How the AI service should route a chat question (ADR-013). Default <see cref="TabletOnly"/>.</summary>
public enum AiRoutingMode
{
    /// <summary>Never leaves the tablet.</summary>
    [EnumMember(Value = "tablet-only")] TabletOnly,
    /// <summary>Shop server when healthy, else the tablet. Never the cloud.</summary>
    [EnumMember(Value = "prefer-shop-server")] PreferShopServer,
    /// <summary>Cloud when allowed and reachable, else shop server, else the tablet.</summary>
    [EnumMember(Value = "prefer-cloud")] PreferCloud,
}
