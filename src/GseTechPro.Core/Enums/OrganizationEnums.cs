using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>Vehicle bus / diagnostic protocol. DB values match ARCHITECTURE.md §2.2.</summary>
public enum ProtocolType
{
    [EnumMember(Value = "J1939")] J1939,
    [EnumMember(Value = "J1708")] J1708,
    [EnumMember(Value = "OBD2")] OBD2,
    [EnumMember(Value = "UDS")] UDS,
    [EnumMember(Value = "manufacturer")] Manufacturer,
}

/// <summary>Whether an asset is equipment or a vehicle.</summary>
public enum AssetKind
{
    [EnumMember(Value = "equipment")] Equipment,
    [EnumMember(Value = "vehicle")] Vehicle,
}

/// <summary>Operational status of an asset.</summary>
public enum AssetStatus
{
    [EnumMember(Value = "in_service")] InService,
    [EnumMember(Value = "down")] Down,
    [EnumMember(Value = "awaiting_parts")] AwaitingParts,
    [EnumMember(Value = "retired")] Retired,
}

/// <summary>Lifecycle state of a work order.</summary>
public enum WorkOrderStatus
{
    [EnumMember(Value = "open")] Open,
    [EnumMember(Value = "in_progress")] InProgress,
    [EnumMember(Value = "waiting_parts")] WaitingParts,
    [EnumMember(Value = "closed")] Closed,
}

/// <summary>Technician role (controls which actions require a PIN).</summary>
public enum TechnicianRole
{
    [EnumMember(Value = "tech")] Tech,
    [EnumMember(Value = "lead")] Lead,
    [EnumMember(Value = "admin")] Admin,
}
