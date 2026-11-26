using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Core.Contracts;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Vehicle/VCI selection plan, Step 6: the active vehicle for the current diagnostic session.
/// One place keeps the header VIN text, the vehicle summary line and the breadcrumb root in sync
/// for both the auto (adapter VIN read) and manual (selector modal) paths.
/// </summary>
public sealed partial class MainShellViewModel
{
    private const string UnknownText = "Unknown";

    /// <summary>The vehicle the session is working on; <c>null</c> when none is selected/connected.</summary>
    [ObservableProperty]
    private VehicleProfile? _activeVehicle;

    /// <summary>One-line vehicle summary for the header. Unknown fields render as "Unknown", never blank.</summary>
    [ObservableProperty]
    private string _activeVehicleHeaderLine = "No vehicle selected";

    /// <summary>Sets (or clears, when <c>null</c>) the active vehicle and syncs header text and breadcrumb root.</summary>
    public void SetActiveVehicle(VehicleProfile? profile)
    {
        ActiveVehicle = profile;

        if (profile is null)
        {
            VehicleVinText = "VIN: <not connected>";
            ActiveVehicleHeaderLine = "No vehicle selected";
            BreadcrumbRoot = DefaultBreadcrumbRoot;
            return;
        }

        var vin = string.IsNullOrWhiteSpace(profile.Vin) ? "<unknown>" : profile.Vin;
        VehicleVinText = $"VIN: {vin}";
        ActiveVehicleHeaderLine =
            $"VIN: {vin} | Make: {OrUnknown(profile.Make)} | Model: {OrUnknown(profile.Model)} | " +
            $"Engine: {OrUnknown(profile.Engine)} | " +
            $"Year: {(profile.Year?.ToString() ?? UnknownText)} | " +
            $"Region: {(profile.Region?.ToString() ?? UnknownText)} | " +
            $"Protocol: {(profile.Protocol?.ToString() ?? UnknownText)}";
        BreadcrumbRoot = $"Vehicle: {vin}";
    }

    /// <summary>Clears the active vehicle.</summary>
    public void ClearActiveVehicle() => SetActiveVehicle(null);

    private static string OrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? UnknownText : value;
}
