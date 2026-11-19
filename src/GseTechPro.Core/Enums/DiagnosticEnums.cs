using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>The physical quantity being measured. Used by test points, readings, and tree steps.</summary>
public enum ReadingType
{
    [EnumMember(Value = "hydraulic_pressure")] HydraulicPressure,
    [EnumMember(Value = "flow")] Flow,
    [EnumMember(Value = "temperature")] Temperature,
    [EnumMember(Value = "voltage")] Voltage,
    [EnumMember(Value = "fuel_pressure")] FuelPressure,
    [EnumMember(Value = "current")] Current,
    [EnumMember(Value = "other")] Other,
}

/// <summary>DTC state. Active/confirmed renders red, pending renders blue in the UI.</summary>
public enum DtcStatus
{
    [EnumMember(Value = "active")] Active,
    [EnumMember(Value = "pending")] Pending,
    [EnumMember(Value = "stored")] Stored,
}

/// <summary>How a repair actually went.</summary>
public enum OutcomeResult
{
    [EnumMember(Value = "fixed")] Fixed,
    [EnumMember(Value = "not_fixed")] NotFixed,
    [EnumMember(Value = "unsure")] Unsure,
}

/// <summary>Pass/fail verdict for a reading against its spec range.</summary>
public enum PassFail
{
    [EnumMember(Value = "pass")] Pass,
    [EnumMember(Value = "fail")] Fail,
    [EnumMember(Value = "na")] NotApplicable,
}

/// <summary>How a reading entered the system.</summary>
public enum ReadingSource
{
    [EnumMember(Value = "manual")] Manual,
    [EnumMember(Value = "voice")] Voice,
    [EnumMember(Value = "scope")] Scope,
    [EnumMember(Value = "scan")] Scan,
}

/// <summary>How a DTC was observed.</summary>
public enum DtcSource
{
    [EnumMember(Value = "scan")] Scan,
    [EnumMember(Value = "manual")] Manual,
}

/// <summary>Symptom/complaint system category (content owned by gse-domain-expert).</summary>
public enum SymptomCategory
{
    [EnumMember(Value = "engine")] Engine,
    [EnumMember(Value = "hydraulic")] Hydraulic,
    [EnumMember(Value = "electrical")] Electrical,
    [EnumMember(Value = "drivetrain")] Drivetrain,
    [EnumMember(Value = "brakes")] Brakes,
    [EnumMember(Value = "hvac")] Hvac,
}

/// <summary>
/// Category of a safety warning shown before a diagnostic step or test-point procedure
/// (content authored by gse-domain-expert; table <c>safety_warnings.category</c>). Drives the
/// icon/severity styling the app renders and the order warnings are surfaced (ADR-009).
/// </summary>
public enum SafetyWarningCategory
{
    [EnumMember(Value = "hydraulic_pressure")] HydraulicPressure,
    [EnumMember(Value = "lockout_tagout")] LockoutTagout,
    [EnumMember(Value = "high_voltage")] HighVoltage,
    [EnumMember(Value = "aircraft_proximity")] AircraftProximity,
    [EnumMember(Value = "general")] General,
}

/// <summary>How a complaint was captured.</summary>
public enum RecordedBy
{
    [EnumMember(Value = "text")] Text,
    [EnumMember(Value = "voice")] Voice,
}

/// <summary>What a note or attachment is attached to.</summary>
public enum NoteScope
{
    [EnumMember(Value = "work_order")] WorkOrder,
    [EnumMember(Value = "asset")] Asset,
    [EnumMember(Value = "technician")] Technician,
    [EnumMember(Value = "record")] Record,
    [EnumMember(Value = "frame")] Frame,
}

/// <summary>Attachment media type.</summary>
public enum AttachmentKind
{
    [EnumMember(Value = "photo")] Photo,
    [EnumMember(Value = "waveform")] Waveform,
    [EnumMember(Value = "recording")] Recording,
    [EnumMember(Value = "scan")] Scan,
    [EnumMember(Value = "document")] Document,
}

/// <summary>Equipment category in the make/model catalog.</summary>
public enum EquipmentCategory
{
    [EnumMember(Value = "tug")] Tug,
    [EnumMember(Value = "belt_loader")] BeltLoader,
    [EnumMember(Value = "gpu")] Gpu,
    [EnumMember(Value = "air_start")] AirStart,
    [EnumMember(Value = "pushback")] Pushback,
    [EnumMember(Value = "deicer")] Deicer,
    [EnumMember(Value = "lavatory_truck")] LavatoryTruck,
    [EnumMember(Value = "loader")] Loader,
    [EnumMember(Value = "other")] Other,
}
