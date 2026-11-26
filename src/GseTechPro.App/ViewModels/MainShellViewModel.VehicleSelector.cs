using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Vehicle/VCI selection plan, Step 9: the manual vehicle selector modal. Sidebar region (Domestic / Asian /
/// European) and HD system (Engine / Trans / ABS / Truck) actions open it; Save commits the chosen vehicle to
/// <see cref="ActiveVehicle"/> and hands it to the OEM module hook.
/// </summary>
public sealed partial class MainShellViewModel
{
    private readonly IVehicleCatalog _vehicleCatalog;
    private bool _vehicleCatalogLoaded;
    private bool _vehicleSelectorOpen;

    /// <summary>Message shown when the selector is requested but no window is available to host it.</summary>
    public const string VehicleSelectorUnavailableMessage = "The vehicle selector cannot be shown right now.";

    /// <summary>Vehicle year / make / model catalog behind the selector.</summary>
    public IVehicleCatalog VehicleCatalog => _vehicleCatalog;

    /// <summary>
    /// Replaces the ContentDialog with another way of choosing a vehicle (used by tests and any non-WinUI host).
    /// Receives the catalog, region, optional HD system name and the active adapter; returns the chosen
    /// profile, or <c>null</c> when the user closed without saving. When <c>null</c> the real dialog is used.
    /// </summary>
    public Func<IVehicleCatalog, VehicleRegion, string?, IDiagnosticAdapter?, Task<VehicleProfile?>>? VehicleSelectorPresenter { get; set; }

    /// <summary>Loads the vehicle catalog once (lazy). A failed load is retried on the next call.</summary>
    public async Task EnsureVehicleCatalogLoadedAsync()
    {
        if (_vehicleCatalogLoaded)
            return;

        await _vehicleCatalog.LoadAsync();
        _vehicleCatalogLoaded = true;
    }

    /// <summary>
    /// Opens the vehicle selector for <paramref name="region"/> (and HD <paramref name="systemName"/>, if any).
    /// On Save: sets the active vehicle (Source = Manual) and loads the OEM module. On Close: nothing changes.
    /// Never throws; failures are reported on the status banner.
    /// </summary>
    public async Task OpenVehicleSelectorAsync(VehicleRegion region, string? systemName)
    {
        if (_vehicleSelectorOpen)
            return;

        _vehicleSelectorOpen = true;
        try
        {
            if (VehicleSelectorPresenter is null && DialogXamlRoot is null)
            {
                StatusBannerMessage = VehicleSelectorUnavailableMessage;
                return;
            }

            await EnsureVehicleCatalogLoadedAsync();

            var adapter = _activeAdapter;
            VehicleProfile? chosen;
            if (VehicleSelectorPresenter is { } presenter)
            {
                chosen = await presenter(_vehicleCatalog, region, systemName, adapter);
            }
            else
            {
                var dialog = new VehicleSelectorDialog(_vehicleCatalog, region, systemName, adapter)
                {
                    XamlRoot = DialogXamlRoot,
                };
                await dialog.InitializeAsync();
                await dialog.ShowAsync();
                chosen = dialog.Result;
            }

            if (chosen is not null)
                await CommitSelectedVehicleAsync(chosen, adapter);
        }
        catch (Exception ex)
        {
            AppendDiagnosticLog($"Vehicle selector failed: {ex.Message}");
            StatusBannerMessage = $"Vehicle selection failed: {ex.Message}";
        }
        finally
        {
            _vehicleSelectorOpen = false;
            ClearPendingVehicleSelection();
        }
    }

    private async Task CommitSelectedVehicleAsync(VehicleProfile profile, IDiagnosticAdapter? adapter)
    {
        SetActiveVehicle(profile);

        string? oemMessage = null;
        try
        {
            oemMessage = await TryLoadOemModuleAsync(profile, adapter);
        }
        catch (Exception ex)
        {
            AppendDiagnosticLog($"OEM module load failed: {ex.Message}");
            oemMessage = $"OEM module could not be loaded: {ex.Message} Generic diagnostics loaded.";
        }

        var selected = $"Vehicle selected: {OrUnknown(profile.Make)} {profile.Model ?? ""} ({profile.Year?.ToString() ?? UnknownText})".Replace("  ", " ");
        StatusBannerMessage = oemMessage is null ? selected : $"{selected}. {oemMessage}";
    }

    private void ClearPendingVehicleSelection()
    {
        if (PendingVehicleRegion is not null)
        {
            PendingVehicleRegion = null;
            OnPropertyChanged(nameof(PendingVehicleRegion));
        }

        if (PendingHdSystem is not null)
        {
            PendingHdSystem = null;
            OnPropertyChanged(nameof(PendingHdSystem));
        }
    }
}
