using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Scope;

/// <summary>
/// Default implementation of <see cref="IScopeDeviceFactory"/> (ADR-011 §1/§3).
/// Combines the built-in <see cref="SimulatedScopeDevice"/> with guarded hardware discovery for
/// <see cref="PicoScopeDevice"/> and <see cref="Hantek6074BeDevice"/> so missing vendor DLLs or
/// unplugged scopes never throw during discovery.
/// </summary>
public sealed class ScopeDeviceFactory : IScopeDeviceFactory
{
    private readonly int _simulatorSeed;
    private readonly SimulatedSignalPreset _defaultSimulatorPreset;

    public ScopeDeviceFactory(
        int simulatorSeed = SimulatedScopeDevice.DefaultSeed,
        SimulatedSignalPreset defaultSimulatorPreset = SimulatedSignalPreset.Sine)
    {
        _simulatorSeed = simulatorSeed;
        _defaultSimulatorPreset = defaultSimulatorPreset;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScopeDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run<IReadOnlyList<ScopeDeviceInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var devices = new List<ScopeDeviceInfo>
            {
                SimulatedScopeDeviceFactory.SimulatorDeviceInfo,
            };

            try
            {
                foreach (var pico in PicoScopeDevice.DiscoverDevices())
                    devices.Add(pico);
            }
            catch
            {
                // Guarded: missing PicoSDK DLL must never fail discovery
            }

            try
            {
                foreach (var hantek in Hantek6074BeDevice.DiscoverDevices())
                    devices.Add(hantek);
            }
            catch
            {
                // Guarded: missing Hantek DLL/driver must never fail discovery
            }

            return devices;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IScopeDevice> OpenAsync(ScopeDeviceInfo device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(device);

        IScopeDevice scope = device.Kind switch
        {
            ScopeDeviceKind.Simulator => CreateSimulatorDevice(device),
            ScopeDeviceKind.Hantek => CreateHantekDevice(device),
            ScopeDeviceKind.PicoScope => new PicoScopeDevice(device.Serial, device.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(device), device.Kind, $"Unsupported scope device kind '{device.Kind}'."),
        };

        return Task.FromResult(scope);
    }

    private SimulatedScopeDevice CreateSimulatorDevice(ScopeDeviceInfo device)
    {
        SimulatedSignalPreset preset = _defaultSimulatorPreset;
        if (SimulatedScopePresets.TryResolvePreset(device.Id, out var fromId) &&
            !string.Equals(device.Id, SimulatedScopeDevice.DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            preset = fromId;
        }
        return new SimulatedScopeDevice(_simulatorSeed, preset);
    }

    private static Hantek6074BeDevice CreateHantekDevice(ScopeDeviceInfo device)
    {
        ushort index = 0;
        if (!string.IsNullOrWhiteSpace(device.Id) &&
            device.Id.StartsWith("hantek-", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(device.Id.AsSpan("hantek-".Length), out ushort parsed))
        {
            index = parsed;
        }

        return new Hantek6074BeDevice(index, device.Name);
    }
}
