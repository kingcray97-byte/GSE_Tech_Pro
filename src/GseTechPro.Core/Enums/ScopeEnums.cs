using System.Runtime.Serialization;

namespace GseTechPro.Core.Enums;

/// <summary>
/// The six measurements the scope math engine computes from a captured waveform, per channel
/// (ADR-011 §4). Exact algorithms and units are defined in ADR-011; the values here are the wire
/// vocabulary shared by the math engine, the UI, the persisted <c>waveforms.measurements_json</c>,
/// and <c>reference_waveforms.expected_json</c>. EnumMember strings are authoritative and match the
/// snake_case JSON produced by <see cref="GseTechPro.Core.Contracts.ScopeJson"/>.
/// </summary>
public enum MeasurementKind
{
    /// <summary>Frequency of the fundamental, Hz. Mean period of rising 50% crossings.</summary>
    [EnumMember(Value = "frequency")] Frequency,
    /// <summary>Peak voltage Vp, V. <c>max(samples)</c>.</summary>
    [EnumMember(Value = "peak")] Peak,
    /// <summary>Peak-to-peak voltage Vpp, V. <c>max − min</c>.</summary>
    [EnumMember(Value = "peak_to_peak")] PeakToPeak,
    /// <summary>Duty cycle, %. Time above the 50% level as a fraction of the period.</summary>
    [EnumMember(Value = "duty_cycle")] DutyCycle,
    /// <summary>Rise time 10%→90% of Vpp, seconds.</summary>
    [EnumMember(Value = "rise_time")] RiseTime,
    /// <summary>Ripple, V. AC RMS of the mean-subtracted signal (ADR-011 §4).</summary>
    [EnumMember(Value = "ripple")] Ripple,
    /// <summary>Current, A. <c>max(samples) × clamp scale (A/V)</c>; requires a clamp probe.</summary>
    [EnumMember(Value = "current")] Current,
}

/// <summary>
/// Whether a measurement was computable, and why not. A non-<c>Computed</c> result has a null
/// <c>Value</c> and must never be compared against a reference (ADR-011 §4).
/// </summary>
public enum ScopeMeasurementStatus
{
    /// <summary>The value was computed normally.</summary>
    [EnumMember(Value = "computed")] Computed,
    /// <summary>No trigger fired within the device's timeout.</summary>
    [EnumMember(Value = "no_trigger")] NoTrigger,
    /// <summary>Not enough samples to compute the quantity.</summary>
    [EnumMember(Value = "too_few_samples")] TooFewSamples,
    /// <summary>The signal exceeded the configured voltage range; the value is untrustworthy.</summary>
    [EnumMember(Value = "out_of_range")] OutOfRange,
    /// <summary>The signal has no measurable cycle (frequency/duty/rise undefined).</summary>
    [EnumMember(Value = "flat_signal")] FlatSignal,
    /// <summary>A required probe setting is absent (e.g. current requested without a clamp scale).</summary>
    [EnumMember(Value = "not_configured")] NotConfigured,
}

/// <summary>
/// Per-measurement comparison verdict for a scope capture against a reference expectation
/// (ADR-011 §5). Mirrors the <c>pass</c>/<c>fail</c>/<c>na</c> concept of
/// <see cref="PassFail"/> (used by readings) but is scoped to waveform comparison: <c>na</c> means
/// "no expectation, a <c>TODO</c> reference, an un-computable measurement, or a unit mismatch" —
/// never a numeric judgment.
/// </summary>
public enum ScopeVerdict
{
    [EnumMember(Value = "pass")] Pass,
    [EnumMember(Value = "fail")] Fail,
    [EnumMember(Value = "na")] Na,
}
