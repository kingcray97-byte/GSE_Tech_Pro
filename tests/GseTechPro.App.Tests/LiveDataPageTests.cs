using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Live Data page: snaplist editor choices, steady row lists while streaming, and no Equipment Health duplication.</summary>
public sealed class LiveDataPageTests
{
    private static async Task<MainShellViewModel> CreateViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-livedata-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();
        return new MainShellViewModel(dataServices, new DiagnosticAdapterFactory());
    }

    // Placeholder readings only: these exercise the list plumbing and are not equipment specs.
    private static void StreamFrames(MainShellViewModel vm, int frames = 3)
    {
        for (var i = 0; i < frames; i++)
        {
            vm.ApplyLivePidFrame(new[]
            {
                new PidValue { Name = "Test PID A", Value = 1 + i, Unit = "V", IsSimulated = true },
                new PidValue { Name = "Test PID B", Value = 2 + i, Unit = "kPa", IsSimulated = true },
                new PidValue { Name = "Test PID C", Value = 3 + i, Unit = "rpm", IsSimulated = true },
            });
        }
    }

    private static SidebarItem Child(MainShellViewModel vm, string key) =>
        vm.SidebarSections.SelectMany(s => s.Children).First(c => c.Key == key);

    [Fact]
    public async Task SnaplistEditor_ListsVehiclePids_AllUnselectedByDefault()
    {
        await using var vm = await CreateViewModelAsync();
        StreamFrames(vm);

        Assert.NotEmpty(vm.LivePids);
        Assert.All(vm.LivePids, p => Assert.False(p.IsSelectedInSnaplist));

        // Until a custom snaplist is saved, the list view still shows every reported parameter.
        Assert.Equal(vm.LivePids.Count, vm.SnaplistPids.Count);
    }

    [Fact]
    public async Task SaveAndReturn_WithNothingChecked_KeepsEditorOpen()
    {
        await using var vm = await CreateViewModelAsync();
        StreamFrames(vm);
        vm.SelectedLiveDataMode = LiveDataDisplayMode.SnaplistEditor;

        vm.SaveAndReturnSnaplist();

        Assert.Equal(LiveDataDisplayMode.SnaplistEditor, vm.SelectedLiveDataMode);
        Assert.False(vm.OnlyShowSnaplistPids);
    }

    [Fact]
    public async Task SaveAndReturn_ShowsOnlyCheckedPids()
    {
        await using var vm = await CreateViewModelAsync();
        StreamFrames(vm);
        var picked = vm.LivePids.Take(2).ToList();
        foreach (var pid in picked)
            pid.IsSelectedInSnaplist = true;

        vm.SaveAndReturnSnaplist();

        Assert.Equal(LiveDataDisplayMode.List, vm.SelectedLiveDataMode);
        Assert.Equal(picked, vm.SnaplistPids.ToList());
    }

    [Fact]
    public async Task RefreshSnaplist_WhenNothingChanged_DoesNotRebuildRows()
    {
        await using var vm = await CreateViewModelAsync();
        StreamFrames(vm);

        var changes = 0;
        vm.SnaplistPids.CollectionChanged += (_, _) => changes++;

        // Each live frame calls this; rebuilding the rows every frame made the page jump while scrolling.
        StreamFrames(vm, frames: 5);

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task LiveDataPage_ShowsOnlyLiveData_NotEquipmentHealthSections()
    {
        await using var vm = await CreateViewModelAsync();

        await vm.SelectSidebarItemAsync(Child(vm, "LiveData"));

        Assert.Equal(Visibility.Visible, vm.LiveDataSectionVisibility);
        Assert.Equal(Visibility.Collapsed, vm.DtcSectionVisibility);
        Assert.Equal(Visibility.Collapsed, vm.FreezeFrameSectionVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ReadinessSectionVisibility);
        Assert.Equal(Visibility.Collapsed, vm.DiagnosticLogSectionVisibility);
    }

    [Fact]
    public async Task EquipmentHealth_StillShowsDtcsAndLog()
    {
        await using var vm = await CreateViewModelAsync();

        vm.SelectToolbarAction("Diagnostics");

        Assert.Equal(Visibility.Visible, vm.DtcSectionVisibility);
        Assert.Equal(Visibility.Visible, vm.DiagnosticLogSectionVisibility);
    }
}

