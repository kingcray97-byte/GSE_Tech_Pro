using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>A guided scope test definition (e.g. 'alternator ripple', 'injector current'). Table: guided_tests.</summary>
public sealed record GuidedTest : EntityBase
{
    public string Name { get; init; } = "";
    public string? EquipmentModelId { get; init; }
    public string? System { get; init; }
    public string? Description { get; init; }
    public string? Procedure { get; init; }
    /// <summary>JSON array of safety warning ids.</summary>
    public string? SafetyWarningIds { get; init; }
    public string? ReferenceWaveformId { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>A known-good reference waveform (from real captures/manuals only, never synthesized). Table: reference_waveforms.</summary>
public sealed record ReferenceWaveform : EntityBase
{
    public string Name { get; init; } = "";
    public string? EquipmentModelId { get; init; }
    public string? System { get; init; }
    public string? TestName { get; init; }
    /// <summary>JSON numeric expectations (freq, vpp, duty, rise, ripple, current).</summary>
    public string? ExpectedJson { get; init; }
    public string DataPath { get; init; } = "";
    public WaveformSource Source { get; init; }
    public string? Citation { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>A saved scope capture, tied to a work order. Table: waveforms.</summary>
public sealed record Waveform : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public ScopeDeviceKind ScopeDevice { get; init; }
    /// <summary>JSON channel configuration.</summary>
    public string ChannelConfigJson { get; init; } = "[]";
    public double? Timebase { get; init; }
    public int? SampleRate { get; init; }
    public int? SampleCount { get; init; }
    /// <summary>Binary samples on disk.</summary>
    public string DataPath { get; init; } = "";
    public string? GuidedTestId { get; init; }
    public string? ReferenceWaveformId { get; init; }
    /// <summary>JSON computed measurements (freq, vpp, duty, rise, ripple, current).</summary>
    public string? MeasurementsJson { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}
