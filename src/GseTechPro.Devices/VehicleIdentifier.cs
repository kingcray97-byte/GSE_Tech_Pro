using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices;

/// <summary>
/// Implements <see cref="IVehicleIdentifier"/> by reading the raw <see cref="VehicleInfo"/> from an
/// <see cref="IDiagnosticAdapter"/> and decoding the VIN with <see cref="VinDecoder"/>. Works
/// against the simulator first and never invents values: any failure (timeout, cancellation,
/// missing/undecodable VIN) returns <c>null</c>.
/// </summary>
public sealed class VehicleIdentifier : IVehicleIdentifier
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task<VehicleProfile?> IdentifyAsync(IDiagnosticAdapter adapter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(DefaultTimeout);
        var linkedToken = linkedCts.Token;

        try
        {
            if (!adapter.IsConnected)
            {
                await adapter.ConnectAsync(linkedToken).ConfigureAwait(false);
            }

            var info = await adapter.GetVehicleInfoAsync(linkedToken).ConfigureAwait(false);
            if (info is null || string.IsNullOrWhiteSpace(info.Vin))
                return null;

            var decoded = VinDecoder.Decode(info.Vin);
            if (decoded is null)
                return null;

            return new VehicleProfile
            {
                Vin = info.Vin,
                Make = decoded.Make,
                Model = null,
                Engine = decoded.Engine,
                Region = decoded.Region,
                Year = decoded.Year,
                Protocol = info.Protocol,
                Source = VehicleProfileSource.Auto,
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // Adapter failures (unplugged cable, bus errors, host32 missing, etc.) mean the
            // vehicle could not be identified. The UI will prompt the technician to select manually.
            return null;
        }
    }
}
