using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Vehicle/VCI selection plan, Step 8: mode-specific sidebar entries.
/// Heavy Duty mode adds Engine / Trans / ABS / Truck; OEM mode adds Auto / Domestic / Asian / European.
/// The entries are actions: they run a command instead of navigating. Region and HD system actions open the
/// Step 9 vehicle selector (<see cref="OpenVehicleSelectorAsync"/>); the choice is held in
/// <see cref="PendingVehicleRegion"/> / <see cref="PendingHdSystem"/> while it is open and cleared afterwards.
/// </summary>
public sealed partial class MainShellViewModel
{
    private const string ActiveToolHeavyDuty = "Heavy Duty";
    private const string ActiveToolOem = "OemDiagnostics";

    /// <summary>Exact message shown when automatic detection fails, times out, or no adapter is connected.</summary>
    public const string AutoDetectFailedMessage =
        "Automatic vehicle detection failed. Please select a region to enter vehicle details manually.";

    // Static, so the same instances are returned every time (list selection compares by reference).
    private static readonly IReadOnlyList<SidebarItem> HdModeSidebarItems = new[]
    {
        SidebarItem.Action("Engine", "Engine", "\uE9F5", "Diagnostics"),
        SidebarItem.Action("Trans", "Trans", "\uE713", "Diagnostics"),
        SidebarItem.Action("ABS", "ABS", "\uE7BA", "Diagnostics"),
        SidebarItem.Action("Truck", "Truck", "\uE806", "Diagnostics"),
    };

    private static readonly IReadOnlyList<SidebarItem> OemModeSidebarItems = new[]
    {
        SidebarItem.Action("Auto", "Auto", "\uE9D9", "Diagnostics"),
        SidebarItem.Action("Domestic", "Domestic", "\uE774", "Diagnostics"),
        SidebarItem.Action("Asian", "Asian", "\uE774", "Diagnostics"),
        SidebarItem.Action("European", "European", "\uE774", "Diagnostics"),
    };

    /// <summary>Region chosen with a sidebar region button; set while the selector modal is open, cleared afterwards.</summary>
    public VehicleRegion? PendingVehicleRegion { get; private set; }

    /// <summary>HD system (Engine / Trans / ABS / Truck) chosen in the sidebar; set while the selector modal is open, cleared afterwards.</summary>
    public string? PendingHdSystem { get; private set; }

    /// <summary>Mode-specific action entries for the active tool; empty for every other tool.</summary>
    public IReadOnlyList<SidebarItem> GetModeSidebarItems() => ActiveTool switch
    {
        ActiveToolHeavyDuty => HdModeSidebarItems,
        ActiveToolOem => OemModeSidebarItems,
        _ => Array.Empty<SidebarItem>(),
    };

    /// <summary>Runs a sidebar action entry. Never throws; failures are reported on the status banner.</summary>
    public async Task OnSidebarActionAsync(string actionKey)
    {
        switch (actionKey)
        {
            case "Auto":
                await RunAutoDetectAsync();
                break;

            case "Domestic":
                await SelectRegionAndOpenAsync(VehicleRegion.Domestic);
                break;
            case "Asian":
                await SelectRegionAndOpenAsync(VehicleRegion.Asian);
                break;
            case "European":
                await SelectRegionAndOpenAsync(VehicleRegion.European);
                break;

            case "Engine":
            case "Trans":
            case "ABS":
            case "Truck":
                PendingHdSystem = actionKey;
                OnPropertyChanged(nameof(PendingHdSystem));
                await OpenVehicleSelectorAsync(VehicleRegion.HeavyDuty, actionKey);
                break;

            default:
                AppendDiagnosticLog($"Unknown sidebar action '{actionKey}'.");
                break;
        }
    }

    private async Task SelectRegionAndOpenAsync(VehicleRegion region)
    {
        PendingVehicleRegion = region;
        OnPropertyChanged(nameof(PendingVehicleRegion));
        await OpenVehicleSelectorAsync(region, null);
    }

    private async Task RunAutoDetectAsync()
    {
        var adapter = _activeAdapter;
        if (adapter is null || !IsConnected)
        {
            StatusBannerMessage = AutoDetectFailedMessage;
            return;
        }

        VehicleProfile? profile = null;
        try
        {
            profile = await _vehicleIdentifier.IdentifyAsync(adapter);
        }
        catch (Exception ex)
        {
            AppendDiagnosticLog($"Automatic vehicle detection failed: {ex.Message}");
        }

        if (profile is null)
        {
            StatusBannerMessage = AutoDetectFailedMessage;
            return;
        }

        SetActiveVehicle(profile);
        StatusBannerMessage = $"Vehicle identified: {OrUnknown(profile.Make)} ({OrUnknown(profile.Vin)})";
    }
}
