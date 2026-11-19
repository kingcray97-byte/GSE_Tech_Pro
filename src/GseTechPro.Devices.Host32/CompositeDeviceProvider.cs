using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices.Host32;

/// <summary>
/// Merges several <see cref="IHostDeviceProvider"/>s (simulator + J2534 + RP1210) into one host-side
/// device source. <c>list-devices</c> concatenates all providers; <c>connect</c> routes by device id,
/// trying each provider in order until one accepts the id (providers signal a foreign id with
/// <see cref="HostIpcException"/> code <see cref="HostErrorCodes.DeviceNotFound"/>).
/// </summary>
public sealed class CompositeDeviceProvider : IHostDeviceProvider
{
    private readonly IReadOnlyList<IHostDeviceProvider> _providers;

    public CompositeDeviceProvider(params IHostDeviceProvider[] providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<DeviceInfo>();
        foreach (var provider in _providers)
            all.AddRange(await provider.ListDevicesAsync(cancellationToken).ConfigureAwait(false));
        return all;
    }

    public async Task<IDiagnosticAdapter> ConnectAsync(string deviceId, ProtocolType? protocol, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                return await provider.ConnectAsync(deviceId, protocol, cancellationToken).ConfigureAwait(false);
            }
            catch (HostIpcException ex) when (ex.Code == HostErrorCodes.DeviceNotFound)
            {
                // Not this provider's device; try the next one.
            }
        }
        throw new HostIpcException(HostErrorCodes.DeviceNotFound, $"Unknown device '{deviceId}'.");
    }
}
