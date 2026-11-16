using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Oscilloscope device abstraction. Implementations arrive in Phase 6 (PicoSDK x64 in-process;
/// Hantek 6074BE — likely routed through the 32-bit device host). This contract exists now so the
/// scope screen, guided tests, and waveform storage can be built against a stable shape.
/// Simulator-first: a deterministic simulator backs all pre-Phase-6 scope work.
/// </summary>
public interface IScopeDevice
{
    /// <summary>Human-readable device name (e.g. "PicoScope 4425A", "Hantek 6074BE", "Simulator").</summary>
    string Name { get; }

    bool IsConnected { get; }

    /// <summary>Number of analog channels.</summary>
    int ChannelCount { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    /// <summary>Configure one channel (range, coupling, probe).</summary>
    Task ConfigureChannelAsync(int channel, ScopeChannelConfig config, CancellationToken cancellationToken = default);

    /// <summary>Arm the trigger and begin capture per the request.</summary>
    Task StartCaptureAsync(ScopeCaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stop an in-progress capture.</summary>
    Task StopCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>Retrieve the completed capture (samples + metadata).</summary>
    Task<ScopeCapture> ReadCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>Compute measurements (freq, Vpp, duty, rise, ripple, current) on the last capture.</summary>
    Task<IReadOnlyList<ScopeMeasurement>> ComputeMeasurementsAsync(CancellationToken cancellationToken = default);
}
