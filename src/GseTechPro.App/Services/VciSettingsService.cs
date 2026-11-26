using GseTechPro.Core.Contracts;
using GseTechPro.Data.Repositories;

namespace GseTechPro.App.Services;

/// <summary>Which diagnostic mode a default vehicle interface (VCI) belongs to.</summary>
public enum VehicleInterfaceSlot
{
    /// <summary>Standard / light-duty: J2534 pass-thru devices.</summary>
    LightDuty,

    /// <summary>Heavy-duty: RP1210 adapters.</summary>
    HeavyDuty,
}

/// <summary>
/// Setup &gt; Vehicle interfaces. Remembers the default VCI per diagnostic mode in <c>app_settings</c>.
/// Only the device <see cref="DefaultVciSelection.Id"/> and <see cref="DefaultVciSelection.Kind"/> matter for
/// matching; the <see cref="DefaultVciSelection.Name"/> is a display snapshot taken from device discovery.
/// Nothing is hardcoded: no device names, no DLL paths, and no default when nothing was saved.
/// </summary>
public sealed class VciSettingsService(AppSettingsRepository settings)
{
    public const string LightDutyKey = "vci.default.j2534";
    public const string HeavyDutyKey = "vci.default.heavy_duty";

    public static string KeyFor(VehicleInterfaceSlot slot) => slot switch
    {
        VehicleInterfaceSlot.LightDuty => LightDutyKey,
        VehicleInterfaceSlot.HeavyDuty => HeavyDutyKey,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };

    /// <summary>The saved default for the slot, or <c>null</c> when none was ever saved.</summary>
    public async Task<DefaultVciSelection?> LoadAsync(VehicleInterfaceSlot slot, CancellationToken cancellationToken = default)
    {
        var saved = await settings.GetAsync<DefaultVciSelection?>(KeyFor(slot), null, cancellationToken).ConfigureAwait(false);
        // An unreadable or empty record is the same as nothing saved.
        return saved is null || string.IsNullOrWhiteSpace(saved.Id) ? null : saved;
    }

    public Task SaveAsync(VehicleInterfaceSlot slot, DefaultVciSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.Id);
        return settings.SetAsync(KeyFor(slot), selection, cancellationToken);
    }
}
