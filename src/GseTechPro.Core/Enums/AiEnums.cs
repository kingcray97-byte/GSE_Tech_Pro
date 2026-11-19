using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>Overall health of the local AI service (drives the app's "warming up / ready / degraded" UI).</summary>
public enum AiServiceStatus
{
    /// <summary>Booting or chat model still warming up.</summary>
    [EnumMember(Value = "starting")] Starting,
    /// <summary>Fully ready (chat + search available).</summary>
    [EnumMember(Value = "ok")] Ok,
    /// <summary>Running with reduced capability (e.g. Ollama unreachable, chat unavailable).</summary>
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
