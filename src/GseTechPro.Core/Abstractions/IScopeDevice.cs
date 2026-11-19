using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Oscilloscope device abstraction (ADR-011). Implementations arrive in Phase 6 in
/// <c>GseTechPro.Scope</c>: PicoSDK (x64, in-process) and Hantek 6074BE (x64, in-process) both
/// P/Invoke vendor DLLs here, and the simulator backs all pre-hardware work. The app never touches
/// vendor SDKs — it only sees this interface and <see cref="IScopeDeviceFactory"/>.
///
/// Threading: every method returns <see cref="Task"/> and must never block the UI thread; capture
/// runs on a worker. No events are required in the block-capture path — callers await the
/// <c>Task</c>s. Discovery/opening is via <see cref="IScopeDeviceFactory"/>.
///
/// Data guarantees (implementations must uphold): samples are <b>probe-corrected volts</b> at a
/// uniform <see cref="ScopeCapture.SampleRateHz"/>; channel indices are <b>1-based</b> (CH1…CH4)
/// everywhere.
/// </summary>
public interface IScopeDevice
{
    /// <summary>Human-readable device name (e.g. "PicoScope 4425A", "Hantek 6074BE", "Simulator").</summary>
    string Name { get; }

    /// <summary>Which brand/simulator this device is.</summary>
    ScopeDeviceKind Kind { get; }

    /// <summary>
    /// True when this device produces simulated/demo data rather than real hardware samples. Real
    /// wrappers (PicoScope/Hantek) keep the default <c>false</c>; the simulator MUST override to
    /// <c>true</c>. Consumers use it (with <see cref="ScopeCapture.IsSimulated"/> and
    /// <see cref="ScopeMeasurement.IsSimulated"/>) to badge/refuse simulated data (ADR-007 provenance).
    /// </summary>
    bool IsSimulated => false;

    /// <summary>True once <see cref="ConnectAsync"/> has completed successfully.</summary>
    bool IsConnected { get; }

    /// <summary>Number of analog channels (1-based: CH1…CH<see cref="ChannelCount"/>).</summary>
    int ChannelCount { get; }

    /// <summary>Open the connection. Idempotent.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Close the connection. Idempotent.</summary>
    Task DisconnectAsync();

    /// <summary>Configure one 1-based channel (range, coupling, probe, clamp scale).</summary>
    Task ConfigureChannelAsync(int channel, ScopeChannelConfig config, CancellationToken cancellationToken = default);

    /// <summary>Arm the trigger and begin a block capture per the request.</summary>
    Task StartCaptureAsync(ScopeCaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stop an in-progress capture.</summary>
    Task StopCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>Retrieve the completed capture (probe-corrected volt samples + metadata).</summary>
    Task<ScopeCapture> ReadCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Compute the six measurements (freq, peak, Vpp, duty, rise, ripple, current) on one 1-based
    /// channel of the last capture, with per-kind status for no-trigger / too-few-samples /
    /// out-of-range / flat-signal / not-configured (ADR-011 §4). Comparison against a reference is
    /// a separate, deterministic step in the scope math engine — not a device concern.
    /// </summary>
    Task<IReadOnlyList<ScopeMeasurement>> ComputeMeasurementsAsync(int channel, CancellationToken cancellationToken = default);
}
