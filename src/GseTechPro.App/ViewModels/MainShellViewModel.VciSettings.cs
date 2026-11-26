using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.App.Services;
using GseTechPro.Core.Contracts;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Setup &gt; Vehicle interfaces. Two dropdowns, one per diagnostic mode: Standard / Light-Duty (J2534 devices)
/// and Heavy-Duty (RP1210 adapters); the in-process simulator is selectable in both. The pick is saved as soon as
/// the technician changes it. A saved device that discovery does not find stays saved and selected, with a
/// "Saved device not found" note.
/// </summary>
public sealed partial class MainShellViewModel
{
    private VciSettingsService? _vciSettings;
    private DefaultVciSelection? _savedLightDutyVci;
    private DefaultVciSelection? _savedHeavyDutyVci;
    private bool _suppressVciSave;

    private VciSettingsService VciSettings => _vciSettings ??= new VciSettingsService(_dataServices.Settings);

    /// <summary>Rows for the Standard / Light-Duty dropdown: simulator + J2534 devices.</summary>
    [ObservableProperty]
    private IReadOnlyList<VciDeviceChoice> _lightDutyVciChoices = Array.Empty<VciDeviceChoice>();

    /// <summary>Rows for the Heavy-Duty dropdown: simulator + RP1210 adapters.</summary>
    [ObservableProperty]
    private IReadOnlyList<VciDeviceChoice> _heavyDutyVciChoices = Array.Empty<VciDeviceChoice>();

    [ObservableProperty]
    private int _selectedLightDutyVciIndex = -1;

    [ObservableProperty]
    private int _selectedHeavyDutyVciIndex = -1;

    [ObservableProperty]
    private string _vciSettingsStatusMessage = "";

    public Visibility VciSettingsStatusMessageVisibility =>
        string.IsNullOrWhiteSpace(VciSettingsStatusMessage) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnVciSettingsStatusMessageChanged(string value) =>
        OnPropertyChanged(nameof(VciSettingsStatusMessageVisibility));

    /// <summary>The saved default for a mode as last loaded or saved (null = none saved).</summary>
    public DefaultVciSelection? GetSavedVci(VehicleInterfaceSlot slot) =>
        slot == VehicleInterfaceSlot.LightDuty ? _savedLightDutyVci : _savedHeavyDutyVci;

    /// <summary>Loads both saved defaults and rebuilds the dropdowns from the devices discovered so far.</summary>
    public async Task LoadVciSettingsAsync()
    {
        _savedLightDutyVci = await VciSettings.LoadAsync(VehicleInterfaceSlot.LightDuty);
        _savedHeavyDutyVci = await VciSettings.LoadAsync(VehicleInterfaceSlot.HeavyDuty);
        RebuildVciChoices();
    }

    /// <summary>Re-runs device discovery; <see cref="DiscoverDevicesAsync"/> rebuilds the dropdowns when it finishes.</summary>
    [RelayCommand]
    public Task RefreshVciDevicesAsync() => DiscoverDevicesAsync();

    /// <summary>
    /// Rebuilds both dropdowns from <see cref="AvailableDevices"/> (the simulator is always in it) and the saved
    /// defaults. Never writes to storage.
    /// </summary>
    private void RebuildVciChoices()
    {
        var devices = AvailableDevices.Select(d => d.Device).ToList();
        var light = VciDeviceChoices.Build(devices, DeviceKinds.J2534, _savedLightDutyVci);
        var heavy = VciDeviceChoices.Build(devices, DeviceKinds.Rp1210, _savedHeavyDutyVci);

        _suppressVciSave = true;
        try
        {
            LightDutyVciChoices = light.Choices;
            HeavyDutyVciChoices = heavy.Choices;
            // Reset to -1 first so a selection that did not change numerically still refreshes the ComboBox
            // after its items were replaced.
            SelectedLightDutyVciIndex = -1;
            SelectedHeavyDutyVciIndex = -1;
            SelectedLightDutyVciIndex = light.SelectedIndex;
            SelectedHeavyDutyVciIndex = heavy.SelectedIndex;
        }
        finally
        {
            _suppressVciSave = false;
        }

        VciSettingsStatusMessage = BuildVciStatus(light, heavy);
    }

    private string BuildVciStatus(VciDeviceChoiceList light, VciDeviceChoiceList heavy)
    {
        var lines = new List<string>();
        if (light.SavedDeviceMissing)
            lines.Add($"Saved device not found for Standard / Light-Duty: {DisplayName(_savedLightDutyVci)}.");
        if (heavy.SavedDeviceMissing)
            lines.Add($"Saved device not found for Heavy-Duty: {DisplayName(_savedHeavyDutyVci)}.");
        if (lines.Count > 0)
        {
            lines.Add("The saved choice is kept. Plug the device in and tap Refresh, or pick another.");
            return string.Join(" ", lines);
        }

        if (_savedLightDutyVci is null && _savedHeavyDutyVci is null)
            return "No default vehicle interface chosen yet.";
        return "Saved defaults found.";

        static string DisplayName(DefaultVciSelection? saved) =>
            saved is null ? "" : string.IsNullOrWhiteSpace(saved.Name) ? saved.Id : saved.Name;
    }

    partial void OnSelectedLightDutyVciIndexChanged(int value) =>
        OnVciSelectionChanged(VehicleInterfaceSlot.LightDuty, LightDutyVciChoices, value);

    partial void OnSelectedHeavyDutyVciIndexChanged(int value) =>
        OnVciSelectionChanged(VehicleInterfaceSlot.HeavyDuty, HeavyDutyVciChoices, value);

    private void OnVciSelectionChanged(VehicleInterfaceSlot slot, IReadOnlyList<VciDeviceChoice> choices, int index)
    {
        if (_suppressVciSave || index < 0 || index >= choices.Count)
            return;
        var choice = choices[index];
        // The "not found" row is the saved value itself; picking it again changes nothing.
        if (choice.IsMissing)
            return;
        var current = GetSavedVci(slot);
        if (current is not null && current.Id == choice.Id)
            return;
        _ = SaveVciSelectionAsync(slot, choice.Selection);
    }

    private async Task SaveVciSelectionAsync(VehicleInterfaceSlot slot, DefaultVciSelection selection)
    {
        var modeLabel = slot == VehicleInterfaceSlot.LightDuty ? "Standard / Light-Duty" : "Heavy-Duty";
        // Update the in-memory value first so a RebuildVciChoices() that runs while the save is in flight
        // (e.g. a background discovery finishing) keeps the technician's latest pick instead of the old one.
        var previous = GetSavedVci(slot);
        SetSavedVci(slot, selection);
        try
        {
            await VciSettings.SaveAsync(slot, selection);
            VciSettingsStatusMessage = $"Saved {selection.Name} as the {modeLabel} default.";
        }
        catch (Exception ex)
        {
            // Keep memory in step with storage, unless a newer pick has already replaced this one.
            if (ReferenceEquals(GetSavedVci(slot), selection))
                SetSavedVci(slot, previous);
            VciSettingsStatusMessage = $"Could not save the {modeLabel} default: {ex.Message}";
        }
    }

    private void SetSavedVci(VehicleInterfaceSlot slot, DefaultVciSelection? value)
    {
        if (slot == VehicleInterfaceSlot.LightDuty)
            _savedLightDutyVci = value;
        else
            _savedHeavyDutyVci = value;
    }
}
