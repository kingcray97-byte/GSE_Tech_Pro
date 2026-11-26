using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Vehicle/VCI selection plan, Step 9: the Years / Makes / Models cascade behind the vehicle selector dialog.
/// Pure logic (no WinUI types) so it can be unit tested; <c>VehicleSelectorDialog</c> binds to it.
/// Choosing a year rebuilds Makes (and clears the make/model choice); choosing a make rebuilds Models.
/// </summary>
public sealed class VehicleSelectorModel : ObservableObject
{
    private int _selectedYearIndex = -1;
    private int _selectedMakeIndex = -1;
    private int _selectedModelIndex = -1;
    private bool _initialized;

    public VehicleSelectorModel(IVehicleCatalog catalog, VehicleRegion region, string? systemName = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Region = region;
        SystemName = systemName;
    }

    public IVehicleCatalog Catalog { get; }

    public VehicleRegion Region { get; }

    /// <summary>HD system name (Engine / Trans / ABS / Truck) or <c>null</c> for a plain vehicle selection.</summary>
    public string? SystemName { get; }

    public string Title => BuildTitle(SystemName);

    public ObservableCollection<int> Years { get; } = new();

    public ObservableCollection<string> Makes { get; } = new();

    public ObservableCollection<string> Models { get; } = new();

    /// <summary>The dialog title: names the system for HD selections, otherwise "Select Vehicle".</summary>
    public static string BuildTitle(string? systemName) =>
        systemName is not null ? $"Select Vehicle for {systemName} Diagnostics" : "Select Vehicle";

    /// <summary>Selected index in <see cref="Years"/>; -1 = none. Changing it rebuilds Makes and clears make/model.</summary>
    public int SelectedYearIndex
    {
        get => _selectedYearIndex;
        set
        {
            value = Normalize(value, Years.Count);
            if (!SetProperty(ref _selectedYearIndex, value))
                return;

            // Reset downstream choices first so a ListView writing -1 while the lists are rebuilt is a no-op.
            _selectedMakeIndex = -1;
            _selectedModelIndex = -1;
            Makes.Clear();
            Models.Clear();

            if (SelectedYear is int year)
            {
                foreach (var make in Catalog.GetMakes(Region, year))
                    Makes.Add(make.Name);
            }

            OnPropertyChanged(nameof(SelectedMakeIndex));
            OnPropertyChanged(nameof(SelectedModelIndex));
            RaiseSelectionChanged();
        }
    }

    /// <summary>Selected index in <see cref="Makes"/>; -1 = none. Changing it rebuilds Models and clears the model.</summary>
    public int SelectedMakeIndex
    {
        get => _selectedMakeIndex;
        set
        {
            value = Normalize(value, Makes.Count);
            if (!SetProperty(ref _selectedMakeIndex, value))
                return;

            _selectedModelIndex = -1;
            Models.Clear();

            if (SelectedYear is int year && SelectedMake is string make)
            {
                foreach (var model in Catalog.GetModels(Region, year, make))
                    Models.Add(model.Name);
            }

            OnPropertyChanged(nameof(SelectedModelIndex));
            RaiseSelectionChanged();
        }
    }

    /// <summary>Selected index in <see cref="Models"/>; -1 = none.</summary>
    public int SelectedModelIndex
    {
        get => _selectedModelIndex;
        set
        {
            value = Normalize(value, Models.Count);
            if (!SetProperty(ref _selectedModelIndex, value))
                return;

            RaiseSelectionChanged();
        }
    }

    public int? SelectedYear => InRange(_selectedYearIndex, Years.Count) ? Years[_selectedYearIndex] : null;

    public string? SelectedMake => InRange(_selectedMakeIndex, Makes.Count) ? Makes[_selectedMakeIndex] : null;

    public string? SelectedModel => InRange(_selectedModelIndex, Models.Count) ? Models[_selectedModelIndex] : null;

    /// <summary>True when a year, make and model are all chosen.</summary>
    public bool IsSaveEnabled => _selectedYearIndex >= 0 && _selectedMakeIndex >= 0 && _selectedModelIndex >= 0;

    /// <summary>One-line "2024 › Ford › F-150" summary of the current choice for the dialog footer.</summary>
    public string SelectionSummary
    {
        get
        {
            var parts = new List<string>();
            if (SelectedYear is int y) parts.Add(y.ToString());
            if (SelectedMake is string make) parts.Add(make);
            if (SelectedModel is string model) parts.Add(model);
            return parts.Count == 0 ? "Choose a year, make and model." : string.Join(" › ", parts);
        }
    }

    /// <summary>Loads the catalog and fills <see cref="Years"/> newest first. Safe to call more than once.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await Catalog.LoadAsync(cancellationToken);

        Years.Clear();
        foreach (var year in Catalog.GetYears().Select(y => y.Year).OrderByDescending(y => y))
            Years.Add(year);

        _initialized = true;
    }

    /// <summary>Builds the manual profile for the current choice, or <c>null</c> when the choice is incomplete.</summary>
    public VehicleProfile? BuildProfile()
    {
        if (!IsSaveEnabled || SelectedYear is not int year)
            return null;

        return new VehicleProfile
        {
            Vin = null,
            Make = SelectedMake,
            Model = SelectedModel,
            Year = year,
            Region = Region,
            Source = VehicleProfileSource.Manual,
            Protocol = null,
        };
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSaveEnabled));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private static bool InRange(int index, int count) => index >= 0 && index < count;

    private static int Normalize(int index, int count) => InRange(index, count) ? index : -1;
}
