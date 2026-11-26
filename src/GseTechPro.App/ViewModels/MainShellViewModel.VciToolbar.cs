using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.App.Services;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Vehicle/VCI selection plan, Step 7: toolbar and screen behavior. OBD II, HD and the VCI (OEM) tile each use
/// the saved default device for their mode, discover devices on entry, and (OBD II) run the vehicle identifier.
/// </summary>
public sealed partial class MainShellViewModel
{
    private const string ActiveToolObd2 = "OBD II";
    private const string DefaultBreadcrumbRootText = "Equipment Selection";

    private readonly IVehicleIdentifier _vehicleIdentifier;

    /// <summary>
    /// The protocol dropdown is removed in OBD II mode (discovery is automatic there); it stays in every other mode.
    /// Computed from <see cref="ActiveTool"/>; <c>OnActiveToolChanged</c> raises the change notification.
    /// </summary>
    public Visibility ProtocolDropdownVisibility =>
        ActiveTool == ActiveToolObd2 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Breadcrumb root for diagnostic screens: the active vehicle when one is selected, else the equipment picker.</summary>
    public string DefaultBreadcrumbRoot =>
        ActiveVehicle is null
            ? DefaultBreadcrumbRootText
            : $"Vehicle: {ActiveVehicle.Vin ?? "<unknown>"}";

    partial void OnActiveVehicleChanged(VehicleProfile? value) =>
        OnPropertyChanged(nameof(DefaultBreadcrumbRoot));

    /// <summary>
    /// Loads the saved default VCI for <paramref name="slot"/> and selects the matching discovered device.
    /// Does nothing when none is saved or the saved device is not currently discovered (the saved value is kept).
    /// Never throws; safe to fire and forget.
    /// </summary>
    public async Task ApplySavedVciAsync(VehicleInterfaceSlot slot)
    {
        try
        {
            var saved = await VciSettings.LoadAsync(slot);
            if (saved is null)
                return;

            var match = AvailableDevices.FirstOrDefault(d =>
                string.Equals(d.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                SelectedDevice = match;
        }
        catch (Exception ex)
        {
            AppendDiagnosticLog($"Could not apply the saved {slot} vehicle interface: {ex.Message}");
        }
    }

    /// <summary>
    /// When an adapter is connected, reads and decodes the vehicle identity and makes it the active vehicle.
    /// A <c>null</c> result (not identified) leaves the current vehicle untouched. Never throws.
    /// </summary>
    public async Task RunIdentifierOnEntryAsync()
    {
        var adapter = _activeAdapter;
        if (adapter is null || !IsConnected)
            return;

        try
        {
            var profile = await _vehicleIdentifier.IdentifyAsync(adapter);
            if (profile is not null)
                SetActiveVehicle(profile);
        }
        catch (Exception ex)
        {
            AppendDiagnosticLog($"Vehicle identification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Entry work for a VCI-backed screen: discover devices, then select the saved device for the mode.
    /// Skipped while a session is connected so the live adapter is never swapped underneath the technician.
    /// Discovery must finish first because it rebuilds the device list.
    /// </summary>
    private async Task EnterVciModeAsync(VehicleInterfaceSlot slot, bool runIdentifier = false)
    {
        try
        {
            if (!IsConnected)
            {
                await DiscoverDevicesAsync();
                await ApplySavedVciAsync(slot);
            }

            if (runIdentifier && ActiveVehicle is null)
                await RunIdentifierOnEntryAsync();
        }
        catch (Exception ex)
        {
            AppendDiagnosticLog($"Entering {slot} mode: {ex.Message}");
        }
    }
}
