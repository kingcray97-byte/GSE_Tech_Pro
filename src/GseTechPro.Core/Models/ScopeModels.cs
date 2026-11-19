using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Models;

/// <summary>
/// A guided scope test definition (e.g. 'alternator ripple', 'injector current'). Table:
/// guided_tests. <see cref="ScopeSetupJson"/> is the recipe (serialized
/// <see cref="Contracts.ScopeCaptureRequest"/>: channels, timebase, sample rate, trigger); the
/// linked <see cref="ReferenceWaveform"/> supplies the expected outcome and optional known-good
/// reference sample (ADR-011 §7).
/// </summary>
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
    /// <summary>
    /// Serialized <see cref="Contracts.ScopeCaptureRequest"/> (channel setup, timebase, sample rate,
    /// trigger). Null/empty = the tech sets the scope up manually. Column: guided_tests.scope_setup_json.
    /// </summary>
    public string? ScopeSetupJson { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>
/// A known-good reference waveform (from real captures/manuals only, never synthesized). Table:
/// reference_waveforms. <see cref="ExpectedJson"/> is the numeric outcome
/// (<see cref="Contracts.ReferenceExpectations"/>) the scope compares against; <see cref="DataPath"/>
/// is an optional known-good sample (<c>.wfm</c>) to overlay (ADR-011 §5/§6).
/// </summary>
public sealed record ReferenceWaveform : EntityBase
{
    public string Name { get; init; } = "";
    public string? EquipmentModelId { get; init; }
    public string? System { get; init; }
    public string? TestName { get; init; }
    /// <summary>JSON numeric expectations: <see cref="Contracts.ReferenceExpectations"/> (ADR-011 §5).</summary>
    public string? ExpectedJson { get; init; }
    /// <summary>Optional reference sample file (relative <c>.wfm</c> path), or "" when none.</summary>
    public string DataPath { get; init; } = "";
    public WaveformSource Source { get; init; }
    public string? Citation { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string UpdatedAt { get; init; } = Clock.UtcNow;
}

/// <summary>
/// A saved scope capture, tied to a work order. Table: waveforms. <see cref="MeasurementsJson"/>
/// holds the computed measurements <b>plus</b> the comparison verdicts (serialized
/// <see cref="Contracts.ScopeMeasurement"/>[]); <see cref="DataPath"/> points at the <c>.wfm</c>
/// binary samples (ADR-011 §6).
/// </summary>
public sealed record Waveform : EntityBase
{
    public string WorkOrderId { get; init; } = "";
    public ScopeDeviceKind ScopeDevice { get; init; }
    /// <summary>Serialized <see cref="Contracts.ScopeChannelConfig"/>[] (JSON array).</summary>
    public string ChannelConfigJson { get; init; } = "[]";
    public double? Timebase { get; init; }
    public int? SampleRate { get; init; }
    public int? SampleCount { get; init; }
    /// <summary>Binary samples on disk (relative <c>waveforms/{id}.wfm</c> path).</summary>
    public string DataPath { get; init; } = "";
    public string? GuidedTestId { get; init; }
    public string? ReferenceWaveformId { get; init; }
    /// <summary>Serialized <see cref="Contracts.ScopeMeasurement"/>[] — measurements + verdicts.</summary>
    public string? MeasurementsJson { get; init; }
    public string CreatedAt { get; init; } = Clock.UtcNow;
    public string? DeletedAt { get; init; }
}
