using System.Runtime.Serialization;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;

namespace GseTechPro.Core.Contracts;

/// <summary>Coupling mode for a scope channel (ADR-011).</summary>
public enum ScopeCoupling
{
    [EnumMember(Value = "dc")] Dc,
    [EnumMember(Value = "ac")] Ac,
}

/// <summary>Trigger source edge (ADR-011). <c>None</c> = free-running / manual.</summary>
public enum ScopeTriggerMode
{
    [EnumMember(Value = "none")] None,
    [EnumMember(Value = "rising")] Rising,
    [EnumMember(Value = "falling")] Falling,
}

/// <summary>
/// Per-channel configuration. Serialized into <c>waveforms.channel_config_json</c> and
/// <c>guided_tests.scope_setup_json</c>. Channel indices are 1-based (CH1…CH4).
/// </summary>
public sealed record ScopeChannelConfig
{
    /// <summary>1-based channel number (CH1 = 1).</summary>
    public int Channel { get; init; }
    /// <summary>Full-scale vertical range in volts (e.g. 20 V for ±20 V).</summary>
    public double VoltageRangeVolts { get; init; }
    public ScopeCoupling Coupling { get; init; } = ScopeCoupling.Dc;
    /// <summary>Probe multiplier (e.g. 1x, 10x, 20:1 attenuator). Samples are corrected by this.</summary>
    public double ProbeMultiplier { get; init; } = 1.0;
    /// <summary>
    /// Current-clamp scale in A/V, used only by the <c>Current</c> measurement. 0 (or negative)
    /// means "not a current probe": the channel is measured as voltage and <c>Current</c> returns
    /// <see cref="ScopeMeasurementStatus.NotConfigured"/> (ADR-011 §4).
    /// </summary>
    public double CurrentScaleAmpsPerVolt { get; init; }
    /// <summary>
    /// Optional name the technician gives the channel ("Crank sensor", "CAN-H"), shown on the
    /// channel tile and saved with the capture. Null = no label (left out of the JSON, so older
    /// rows and guided-test setups are unchanged). Display text only: never used to pick a test.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }
}

/// <summary>
/// Capture arming request: which channels, at what rate/timebase, and the trigger. Serialized into
/// <c>guided_tests.scope_setup_json</c>. <see cref="SampleCount"/>, when omitted, is derived by the
/// device from <see cref="TimebaseSecondsPerDivision"/> × 10 divisions × <see cref="SampleRateHz"/>.
/// </summary>
public sealed record ScopeCaptureRequest
{
    /// <summary>The enabled channels (each with its range/coupling/probe). Empty = error.</summary>
    public IReadOnlyList<ScopeChannelConfig> Channels { get; init; } = Array.Empty<ScopeChannelConfig>();
    /// <summary>Samples per second per channel (Hz, ≥ 1).</summary>
    public int SampleRateHz { get; init; }
    /// <summary>Seconds per horizontal division (e.g. 0.001).</summary>
    public double TimebaseSecondsPerDivision { get; init; }
    /// <summary>Optional explicit per-channel sample count; null = derive from timebase.</summary>
    public int? SampleCount { get; init; }
    public ScopeTriggerMode TriggerMode { get; init; } = ScopeTriggerMode.None;
    /// <summary>1-based channel the trigger arms on (ignored when <see cref="TriggerMode"/> is None).</summary>
    public int TriggerChannel { get; init; }
    /// <summary>Trigger level in volts (ignored when <see cref="TriggerMode"/> is None).</summary>
    public double TriggerLevelVolts { get; init; }
}

/// <summary>
/// Completed block capture: metadata + per-channel samples. Samples are probe-corrected volts at a
/// uniform rate; sample times are <c>i / SampleRateHz</c>. Channel keys are 1-based. This is the
/// transient in-memory shape; persistence is the <c>waveforms</c> row + the <c>.wfm</c> binary file
/// (ADR-011 §6).
/// </summary>
public sealed record ScopeCapture
{
    public int SampleCount { get; init; }
    public double SampleRateHz { get; init; }
    public double TimebaseSecondsPerDivision { get; init; }
    public string CapturedAt { get; init; } = Clock.UtcNow;
    /// <summary>
    /// True when this capture came from the simulator rather than real hardware. Defaults to false
    /// (real) so real wrappers need not set it; the simulator must set it to true so a simulated
    /// capture is never persisted as if real (ADR-007 provenance rule).
    /// </summary>
    public bool IsSimulated { get; init; }
    /// <summary>Map of 1-based channel index → samples (volts, probe-corrected).</summary>
    public IReadOnlyDictionary<int, double[]> Channels { get; init; } = new Dictionary<int, double[]>();
}

/// <summary>
/// A computed waveform measurement (one of <see cref="MeasurementKind"/>), per channel, with the
/// optional comparison verdict against a reference. This is both the runtime result of
/// <c>ComputeMeasurementsAsync</c> and the persisted shape of <c>waveforms.measurements_json</c>:
/// when a reference was compared, the verdict/expected fields are populated; when not, they stay
/// null. Algorithms and units are defined in ADR-011 §4/§5.
/// </summary>
public sealed record ScopeMeasurement
{
    public MeasurementKind Kind { get; init; }
    /// <summary>1-based channel the measurement was taken on.</summary>
    public int Channel { get; init; }
    /// <summary>
    /// The measured value, or null when <see cref="Status"/> is not <c>Computed</c>.
    /// Canonical units: Hz, V, %, s, A.
    /// </summary>
    public double? Value { get; init; }
    /// <summary>Canonical unit of <see cref="Value"/>: "Hz", "V", "%", "s", or "A".</summary>
    public string Unit { get; init; } = "";
    public ScopeMeasurementStatus Status { get; init; } = ScopeMeasurementStatus.Computed;
    /// <summary>Human-readable reason when <see cref="Status"/> is not <c>Computed</c>.</summary>
    public string? Detail { get; init; }
    /// <summary>Provenance: true when the underlying capture was simulated.</summary>
    public bool IsSimulated { get; init; }

    // ---- Comparison (populated by the deterministic compare step; null when no reference) ----
    /// <summary>Per-measurement verdict vs the reference; null = no expectation supplied.</summary>
    public ScopeVerdict? Verdict { get; init; }
    public double? ExpectedNominal { get; init; }
    public double? ExpectedTolerance { get; init; }
    public double? ExpectedMin { get; init; }
    public double? ExpectedMax { get; init; }
    /// <summary>Unit the expectation was authored in (must match <see cref="Unit"/> or verdict is na).</summary>
    public string? ExpectedUnit { get; init; }
}

/// <summary>
/// A discovered scope device (returned by <see cref="Abstractions.IScopeDeviceFactory"/>).
/// <see cref="Id"/> is the stable handle used to open the device. The factory's discovery always
/// includes the simulator entry so the app works with no hardware.
/// </summary>
public sealed record ScopeDeviceInfo
{
    /// <summary>Stable identifier (USB serial / bus location / <c>"sim-1"</c> for the simulator).</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-readable name (e.g. "PicoScope 4425A", "Hantek 6074BE", "Simulator").</summary>
    public string Name { get; init; } = "";
    public ScopeDeviceKind Kind { get; init; }
    /// <summary>Number of analog channels.</summary>
    public int ChannelCount { get; init; }
    /// <summary>Hardware serial number, when the device reports one.</summary>
    public string? Serial { get; init; }
    /// <summary>True when this is the simulator (never real hardware).</summary>
    public bool IsSimulated { get; init; }
}
