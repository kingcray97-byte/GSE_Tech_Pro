using System.Collections.ObjectModel;
using System.ComponentModel;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GseTechPro.App;

/// <summary>
/// Vehicle/VCI selection plan, Step 9: the manual vehicle selector. Tabs Years / Makes / Models,
/// Save (enabled once all three are chosen) and Close (discards). The cascade logic lives in
/// <see cref="VehicleSelectorModel"/>; this class is the WinUI shell around it.
/// </summary>
public sealed partial class VehicleSelectorDialog : ContentDialog
{
    private const int YearsTab = 0;
    private const int MakesTab = 1;
    private const int ModelsTab = 2;

    public VehicleSelectorDialog(
        IVehicleCatalog catalog,
        VehicleRegion region,
        string? systemName,
        IDiagnosticAdapter? adapter)
    {
        Catalog = catalog;
        Region = region;
        SystemName = systemName;
        Adapter = adapter;
        Model = new VehicleSelectorModel(catalog, region, systemName);

        InitializeComponent();

        Title = Model.Title;
        PrimaryButtonText = "Save";
        CloseButtonText = "Close";
        PrimaryButtonStyle = (Style)Resources["VehicleSelectorSaveButtonStyle"];
        CloseButtonStyle = (Style)Resources["VehicleSelectorCloseButtonStyle"];
        IsPrimaryButtonEnabled = Model.IsSaveEnabled;

        Model.PropertyChanged += OnModelPropertyChanged;
        PrimaryButtonClick += OnPrimaryButtonClick;
        Opened += OnOpened;
    }

    public IVehicleCatalog Catalog { get; }

    public VehicleRegion Region { get; }

    public string? SystemName { get; }

    public IDiagnosticAdapter? Adapter { get; }

    /// <summary>Cascade state bound by the XAML lists.</summary>
    public VehicleSelectorModel Model { get; }

    public ObservableCollection<int> Years => Model.Years;

    public ObservableCollection<string> Makes => Model.Makes;

    public ObservableCollection<string> Models => Model.Models;

    public int SelectedYearIndex
    {
        get => Model.SelectedYearIndex;
        set => Model.SelectedYearIndex = value;
    }

    public int SelectedMakeIndex
    {
        get => Model.SelectedMakeIndex;
        set => Model.SelectedMakeIndex = value;
    }

    public int SelectedModelIndex
    {
        get => Model.SelectedModelIndex;
        set => Model.SelectedModelIndex = value;
    }

    public bool IsSaveEnabled => Model.IsSaveEnabled;

    /// <summary>The chosen vehicle (<c>Source = Manual</c>) after Save; <c>null</c> when closed without saving.</summary>
    public VehicleProfile? Result { get; private set; }

    /// <summary>Loads the catalog and fills Years. Idempotent; the host may await it before <c>ShowAsync</c>.</summary>
    public Task InitializeAsync() => Model.InitializeAsync();

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            await Model.InitializeAsync();
        }
        catch (Exception)
        {
            // Catalog failed to load: the Years list stays empty and Save stays disabled.
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Model.IsSaveEnabled)
        {
            args.Cancel = true;
            return;
        }

        Result = Model.BuildProfile();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VehicleSelectorModel.IsSaveEnabled):
                IsPrimaryButtonEnabled = Model.IsSaveEnabled;
                break;

            // Move to the next tab after a choice so a gloved tap flows Year -> Make -> Model.
            case nameof(VehicleSelectorModel.SelectedYearIndex) when Model.SelectedYearIndex >= 0:
                SelectorPivot.SelectedIndex = MakesTab;
                break;
            case nameof(VehicleSelectorModel.SelectedMakeIndex) when Model.SelectedMakeIndex >= 0:
                SelectorPivot.SelectedIndex = ModelsTab;
                break;
        }
    }
}
