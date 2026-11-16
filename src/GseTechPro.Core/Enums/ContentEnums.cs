using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>Manual source file format (converted to PDF before ingestion).</summary>
public enum ManualFormat
{
    [EnumMember(Value = "pdf")] Pdf,
    [EnumMember(Value = "docx")] Docx,
    [EnumMember(Value = "pptx")] Pptx,
}

/// <summary>Manual ingestion state.</summary>
public enum ManualStatus
{
    [EnumMember(Value = "imported")] Imported,
    [EnumMember(Value = "failed")] Failed,
}

/// <summary>Kind of manual chunk (drives section detection and search ranking).</summary>
public enum ChunkKind
{
    [EnumMember(Value = "text")] Text,
    [EnumMember(Value = "table")] Table,
    [EnumMember(Value = "procedure")] Procedure,
    [EnumMember(Value = "specs")] Specs,
    [EnumMember(Value = "troubleshooting")] Troubleshooting,
    [EnumMember(Value = "wiring")] Wiring,
    [EnumMember(Value = "torque")] Torque,
    [EnumMember(Value = "diagram_caption")] DiagramCaption,
}

/// <summary>Troubleshooting tree review state.</summary>
public enum TreeStatus
{
    [EnumMember(Value = "draft")] Draft,
    [EnumMember(Value = "approved")] Approved,
    [EnumMember(Value = "retired")] Retired,
}

/// <summary>How a troubleshooting tree originated.</summary>
public enum TreeSource
{
    [EnumMember(Value = "handwritten")] Handwritten,
    [EnumMember(Value = "ai_generated")] AiGenerated,
    [EnumMember(Value = "history_improved")] HistoryImproved,
}

/// <summary>Which scope hardware produced a capture.</summary>
public enum ScopeDeviceKind
{
    [EnumMember(Value = "picoscope")] PicoScope,
    [EnumMember(Value = "hantek")] Hantek,
    [EnumMember(Value = "simulator")] Simulator,
}

/// <summary>Where a reference waveform came from (never synthesized).</summary>
public enum WaveformSource
{
    [EnumMember(Value = "manual")] Manual,
    [EnumMember(Value = "real_capture")] RealCapture,
}

/// <summary>Sync log change type (change-data-capture for optional shop-server sync).</summary>
public enum SyncChangeType
{
    [EnumMember(Value = "upsert")] Upsert,
    [EnumMember(Value = "delete")] Delete,
}
