using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Scope;

/// <summary>
/// Simulator-only implementation of <see cref="IScopeDeviceFactory"/>. Always discovers the
/// canonical simulator entry (<c>Id = "sim-1"</c>, <c>Name = "Simulator"</c>, <c>ChannelCount = 4</c>,
/// <c>IsSimulated = true</c>).
/// </summary>
public sealed class SimulatedScopeDeviceFactory : IScopeDeviceFactory
{
    public static readonly ScopeDeviceInfo SimulatorDeviceInfo = new()
    {
        Id = SimulatedScopeDevice.DefaultDeviceId,
        Name = SimulatedScopeDevice.DefaultDeviceName,
        Kind = ScopeDeviceKind.Simulator,
        ChannelCount = SimulatedScopeDevice.DefaultChannelCount,
        Serial = "SIM-0001",
        IsSimulated = true,
    };

    private readonly int _seed;
    private readonly SimulatedSignalPreset _defaultPreset;

    public SimulatedScopeDeviceFactory(
        int seed = SimulatedScopeDevice.DefaultSeed,
        SimulatedSignalPreset defaultPreset = SimulatedSignalPreset.Sine)
    {
        _seed = seed;
        _defaultPreset = defaultPreset;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ScopeDeviceInfo>>([SimulatorDeviceInfo]);
    }

    /// <inheritdoc />
    public Task<IScopeDevice> OpenAsync(ScopeDeviceInfo device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(device);

        if (device.Kind != ScopeDeviceKind.Simulator)
            throw new ArgumentException($"SimulatedScopeDeviceFactory only opens simulator devices (got {device.Kind}).", nameof(device));

        SimulatedSignalPreset preset = _defaultPreset;
        if (SimulatedScopePresets.TryResolvePreset(device.Id, out var resolvedFromId) &&
            !string.Equals(device.Id, SimulatedScopeDevice.DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            preset = resolvedFromId;
        }

        IScopeDevice scope = new SimulatedScopeDevice(_seed, preset);
        return Task.FromResult(scope);
    }
}
