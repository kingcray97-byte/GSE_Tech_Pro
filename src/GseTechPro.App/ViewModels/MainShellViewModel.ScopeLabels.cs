using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Channel labels (Phase 10D.1h): the technician names each channel ("Crank sensor",
/// "CAN-H"). Labels show on the channel tiles and in a legend above the graticule, and are
/// saved with the capture in <c>channel_config_json</c>. Display text only.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Longest label the Labels flyout accepts.</summary>
    public int ScopeChannelLabelMaxLength => ChannelLabels.MaxLength;

    /// <summary>
    /// Legend above the graticule for enabled, labelled channels ("A Crank sensor · B Cam
    /// sensor"); empty when no enabled channel has a label.
    /// </summary>
    public string ScopeChannelLabelLegend => string.Join(
        " · ",
        ScopeChannels.Where(c => c.IsEnabled && c.HasLabel).Select(c => c.DisplayName));

    public Visibility ScopeChannelLabelLegendVisibility =>
        string.IsNullOrEmpty(ScopeChannelLabelLegend) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Removes every channel label.</summary>
    [RelayCommand]
    public void ClearScopeChannelLabels()
    {
        foreach (var ch in ScopeChannels)
            ch.Label = "";
    }

    private void OnScopeChannelLabelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScopeChannelSetupViewModel.Label)
            or nameof(ScopeChannelSetupViewModel.IsEnabled))
        {
            OnPropertyChanged(nameof(ScopeChannelLabelLegend));
            OnPropertyChanged(nameof(ScopeChannelLabelLegendVisibility));
        }

        // Stacked views: lanes follow enabled channels; headings show label and range.
        if (IsScopeStacked && e.PropertyName is nameof(ScopeChannelSetupViewModel.Label)
                or nameof(ScopeChannelSetupViewModel.IsEnabled)
                or nameof(ScopeChannelSetupViewModel.VoltageRangeVolts)
                or nameof(ScopeChannelSetupViewModel.CurrentScaleAmpsPerVolt))
        {
            OnScopeLanesChanged();
        }
    }
}
