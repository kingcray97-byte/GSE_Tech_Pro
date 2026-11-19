using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// The expected outcome of a guided scope test, serialized into
/// <c>reference_waveforms.expected_json</c> (ADR-011 §5). Authored by <c>gse-domain-expert</c> from
/// manuals or real captures — never synthesized. A test may have no expectations (empty list = the
/// scope screen still overlays the reference sample but issues no verdicts).
/// </summary>
public sealed record ReferenceExpectations
{
    public IReadOnlyList<MeasurementExpectation> Measurements { get; init; } = Array.Empty<MeasurementExpectation>();
}

/// <summary>
/// One expected measurement. Exactly one range form is used: <see cref="Nominal"/>+<see cref="Tolerance"/>
/// (absolute ±, in the measurement's unit) or <see cref="Min"/>/<see cref="Max"/> (inclusive).
/// <see cref="IsTodo"/> marks a placeholder authored before the real spec is known → verdict <c>na</c>.
/// </summary>
public sealed record MeasurementExpectation
{
    public MeasurementKind Kind { get; init; }
    /// <summary>1-based channel the expectation applies to.</summary>
    public int Channel { get; init; } = 1;
    /// <summary>Nominal value (used with <see cref="Tolerance"/>).</summary>
    public double? Nominal { get; init; }
    /// <summary>Absolute ± tolerance in the measurement's unit (used with <see cref="Nominal"/>).</summary>
    public double? Tolerance { get; init; }
    /// <summary>Inclusive lower bound (alternative to nominal+tolerance).</summary>
    public double? Min { get; init; }
    /// <summary>Inclusive upper bound (alternative to nominal+tolerance).</summary>
    public double? Max { get; init; }
    /// <summary>Canonical unit ("Hz", "V", "%", "s", "A"). Must match the measurement's unit.</summary>
    public string? Unit { get; init; }
    /// <summary>True = the real spec is not yet known; comparison verdict is <c>na</c>.</summary>
    public bool IsTodo { get; init; }
}

/// <summary>
/// The aggregate comparison of a capture's measurements against a reference
/// (ADR-011 §5). Produced by the deterministic compare step in the scope math engine (never the
/// model). <see cref="Measurements"/> are the computed measurements with their verdict/expected
/// fields filled in.
/// </summary>
public sealed record ScopeComparisonResult
{
    /// <summary>The <c>reference_waveforms.id</c> this was compared against; null when none.</summary>
    public string? ReferenceWaveformId { get; init; }
    /// <summary>The computed measurements, each with its <see cref="ScopeMeasurement.Verdict"/> set.</summary>
    public IReadOnlyList<ScopeMeasurement> Measurements { get; init; } = Array.Empty<ScopeMeasurement>();
    /// <summary>fail if any measurement fails; else pass if ≥ 1 passes; else na.</summary>
    public ScopeVerdict Overall { get; init; } = ScopeVerdict.Na;
}
