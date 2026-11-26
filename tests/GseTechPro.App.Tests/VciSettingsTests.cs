using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>
/// Setup &gt; Vehicle interfaces (docs/PLAN-vci-vehicle-selection.md, step 2): default VCI persistence,
/// dropdown filtering, and the "saved device not found" behavior. Device ids and names below are test fixtures only.
/// </summary>
public sealed class VciSettingsTests
{
    private const string SimId = "inproc-simulator";

    private static async Task<DataServices> CreateDataAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-vci-{Guid.NewGuid():N}.db");
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        return data;
    }

    private static DeviceInfo Device(string id, string name, string kind) =>
        new() { Id = id, Name = name, Kind = kind, IsSimulated = kind == DeviceKinds.Simulator };

    private static readonly DeviceInfo Sim = Device(SimId, "Sim", DeviceKinds.Simulator);

    private static DataServices GetData(MainShellViewModel vm) =>
        (DataServices)typeof(MainShellViewModel)
            .GetField("_dataServices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm)!;

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await condition())
                return;
            await Task.Delay(20);
        }
        Assert.Fail("Condition not met in time.");
    }

    // ---- VciSettingsService ----

    [Fact]
    public async Task MissingKey_ReturnsNull_ForBothSlots()
    {
        var service = new VciSettingsService((await CreateDataAsync()).Settings);

        Assert.Null(await service.LoadAsync(VehicleInterfaceSlot.LightDuty));
        Assert.Null(await service.LoadAsync(VehicleInterfaceSlot.HeavyDuty));
    }

    [Fact]
    public void Keys_AreTheDocumentedOnes()
    {
        Assert.Equal("vci.default.j2534", VciSettingsService.KeyFor(VehicleInterfaceSlot.LightDuty));
        Assert.Equal("vci.default.heavy_duty", VciSettingsService.KeyFor(VehicleInterfaceSlot.HeavyDuty));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_EachSlotIndependently()
    {
        var service = new VciSettingsService((await CreateDataAsync()).Settings);
        var light = new DefaultVciSelection { Id = "light-1", Name = "Light One", Kind = DeviceKinds.J2534 };
        var heavy = new DefaultVciSelection { Id = "heavy-1", Name = "Heavy One", Kind = DeviceKinds.Rp1210 };

        await service.SaveAsync(VehicleInterfaceSlot.LightDuty, light);
        Assert.Null(await service.LoadAsync(VehicleInterfaceSlot.HeavyDuty));

        await service.SaveAsync(VehicleInterfaceSlot.HeavyDuty, heavy);

        Assert.Equal(light, await service.LoadAsync(VehicleInterfaceSlot.LightDuty));
        Assert.Equal(heavy, await service.LoadAsync(VehicleInterfaceSlot.HeavyDuty));
    }

    [Fact]
    public async Task Save_Overwrites_ThePreviousChoice()
    {
        var service = new VciSettingsService((await CreateDataAsync()).Settings);
        await service.SaveAsync(VehicleInterfaceSlot.LightDuty, new DefaultVciSelection { Id = "a", Name = "A", Kind = DeviceKinds.J2534 });
        await service.SaveAsync(VehicleInterfaceSlot.LightDuty, new DefaultVciSelection { Id = "b", Name = "B", Kind = DeviceKinds.J2534 });

        Assert.Equal("b", (await service.LoadAsync(VehicleInterfaceSlot.LightDuty))!.Id);
    }

    [Fact]
    public async Task Save_RefusesAnEmptyId()
    {
        var service = new VciSettingsService((await CreateDataAsync()).Settings);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.SaveAsync(VehicleInterfaceSlot.LightDuty, new DefaultVciSelection()));
    }

    // ---- Dropdown contents ----

    [Fact]
    public void LightDuty_ListsSimulatorAndJ2534Only()
    {
        var discovered = new[]
        {
            Sim,
            Device("j-1", "J2534 One", DeviceKinds.J2534),
            Device("r-1", "RP1210 One", DeviceKinds.Rp1210),
            Device("j-2", "J2534 Two", DeviceKinds.J2534),
        };

        var list = VciDeviceChoices.Build(discovered, DeviceKinds.J2534, saved: null);

        Assert.Equal(new[] { SimId, "j-1", "j-2" }, list.Choices.Select(c => c.Id));
        Assert.Equal(-1, list.SelectedIndex);
        Assert.False(list.SavedDeviceMissing);
    }

    [Fact]
    public void HeavyDuty_ListsSimulatorAndRp1210Only()
    {
        var discovered = new[]
        {
            Device("j-1", "J2534 One", DeviceKinds.J2534),
            Device("r-1", "RP1210 One", DeviceKinds.Rp1210),
            Sim,
        };

        var list = VciDeviceChoices.Build(discovered, DeviceKinds.Rp1210, saved: null);

        Assert.Equal(new[] { SimId, "r-1" }, list.Choices.Select(c => c.Id));
    }

    [Fact]
    public void Simulator_StaysSelectable_WhenNothingElseIsFound()
    {
        var light = VciDeviceChoices.Build(new[] { Sim }, DeviceKinds.J2534, saved: null);
        var heavy = VciDeviceChoices.Build(new[] { Sim }, DeviceKinds.Rp1210, saved: null);

        Assert.Single(light.Choices);
        Assert.Single(heavy.Choices);
        Assert.Equal(SimId, light.Choices[0].Id);
        Assert.Equal(SimId, heavy.Choices[0].Id);
    }

    [Fact]
    public void SavedDevice_ThatIsFound_IsSelected()
    {
        var saved = new DefaultVciSelection { Id = "j-2", Name = "J2534 Two", Kind = DeviceKinds.J2534 };
        var discovered = new[] { Sim, Device("j-1", "J2534 One", DeviceKinds.J2534), Device("j-2", "J2534 Two", DeviceKinds.J2534) };

        var list = VciDeviceChoices.Build(discovered, DeviceKinds.J2534, saved);

        Assert.Equal(2, list.SelectedIndex);
        Assert.False(list.SavedDeviceMissing);
        Assert.Equal(3, list.Choices.Count);
    }

    [Fact]
    public void SavedSimulator_IsSelected_InEitherList()
    {
        var saved = new DefaultVciSelection { Id = SimId, Name = "Sim", Kind = DeviceKinds.Simulator };

        Assert.Equal(0, VciDeviceChoices.Build(new[] { Sim }, DeviceKinds.J2534, saved).SelectedIndex);
        Assert.Equal(0, VciDeviceChoices.Build(new[] { Sim }, DeviceKinds.Rp1210, saved).SelectedIndex);
    }

    [Fact]
    public void SavedDevice_NotFound_IsKeptAndStaysSelected()
    {
        var saved = new DefaultVciSelection { Id = "gone", Name = "Unplugged Adapter", Kind = DeviceKinds.J2534 };
        var discovered = new[] { Sim, Device("j-1", "J2534 One", DeviceKinds.J2534) };

        var list = VciDeviceChoices.Build(discovered, DeviceKinds.J2534, saved);

        Assert.True(list.SavedDeviceMissing);
        Assert.Equal(list.Choices.Count - 1, list.SelectedIndex);
        var kept = list.Choices[list.SelectedIndex];
        Assert.True(kept.IsMissing);
        Assert.Equal(saved, kept.Selection);
        Assert.Contains("not found", kept.Label);
        // The other real choices are still there to pick instead.
        Assert.Contains(list.Choices, c => c.Id == "j-1" && !c.IsMissing);
        Assert.Contains(list.Choices, c => c.Id == SimId);
    }

    // ---- View model ----

    [Fact]
    public async Task ViewModel_ShowsSavedDeviceNotFound_AndKeepsTheSavedValue()
    {
        var data = await CreateDataAsync();
        var service = new VciSettingsService(data.Settings);
        var saved = new DefaultVciSelection { Id = "gone", Name = "Unplugged Adapter", Kind = DeviceKinds.J2534 };
        await service.SaveAsync(VehicleInterfaceSlot.LightDuty, saved);
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory());

        await vm.LoadVciSettingsAsync();

        Assert.Contains("Saved device not found", vm.VciSettingsStatusMessage);
        Assert.Contains("Unplugged Adapter", vm.VciSettingsStatusMessage);
        Assert.Equal(vm.LightDutyVciChoices.Count - 1, vm.SelectedLightDutyVciIndex);
        Assert.True(vm.LightDutyVciChoices[vm.SelectedLightDutyVciIndex].IsMissing);
        // Loading must not rewrite or drop the saved value.
        Assert.Equal(saved, await service.LoadAsync(VehicleInterfaceSlot.LightDuty));
        Assert.Equal(saved, vm.GetSavedVci(VehicleInterfaceSlot.LightDuty));
        // The simulator can still be picked in both lists.
        Assert.Contains(vm.LightDutyVciChoices, c => c.Id == SimId);
        Assert.Contains(vm.HeavyDutyVciChoices, c => c.Id == SimId);
    }

    [Fact]
    public async Task ViewModel_PickingAnotherDevice_SavesIt()
    {
        var data = await CreateDataAsync();
        var service = new VciSettingsService(data.Settings);
        await service.SaveAsync(VehicleInterfaceSlot.HeavyDuty, new DefaultVciSelection { Id = "gone", Name = "Gone", Kind = DeviceKinds.Rp1210 });
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory());
        await vm.LoadVciSettingsAsync();

        var simIndex = vm.HeavyDutyVciChoices.ToList().FindIndex(c => c.Id == SimId);
        vm.SelectedHeavyDutyVciIndex = simIndex;

        await WaitForAsync(async () => (await service.LoadAsync(VehicleInterfaceSlot.HeavyDuty))?.Id == SimId);
        Assert.Equal(DeviceKinds.Simulator, (await service.LoadAsync(VehicleInterfaceSlot.HeavyDuty))!.Kind);
        // The other slot is untouched.
        Assert.Null(await service.LoadAsync(VehicleInterfaceSlot.LightDuty));
    }

    [Fact]
    public async Task ViewModel_PickingADevice_UpdatesSavedValueBeforeTheSaveCompletes()
    {
        var data = await CreateDataAsync();
        var service = new VciSettingsService(data.Settings);
        await service.SaveAsync(VehicleInterfaceSlot.HeavyDuty, new DefaultVciSelection { Id = "gone", Name = "Gone", Kind = DeviceKinds.Rp1210 });
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory());
        await vm.LoadVciSettingsAsync();
        var simIndex = vm.HeavyDutyVciChoices.ToList().FindIndex(c => c.Id == SimId);

        // No await between the pick and the check: the save is still in flight at this point.
        vm.SelectedHeavyDutyVciIndex = simIndex;
        Assert.Equal(SimId, vm.GetSavedVci(VehicleInterfaceSlot.HeavyDuty)?.Id);

        // A dropdown rebuild at that moment (background discovery finishing) keeps the new pick.
        typeof(MainShellViewModel)
            .GetMethod("RebuildVciChoices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, null);
        Assert.Equal(SimId, vm.HeavyDutyVciChoices[vm.SelectedHeavyDutyVciIndex].Id);

        await WaitForAsync(async () => (await service.LoadAsync(VehicleInterfaceSlot.HeavyDuty))?.Id == SimId);
    }

    [Fact]
    public async Task ViewModel_WithNothingSaved_SelectsNothingAndSavesNothing()
    {
        var data = await CreateDataAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory());

        await vm.LoadVciSettingsAsync();

        Assert.Equal(-1, vm.SelectedLightDutyVciIndex);
        Assert.Equal(-1, vm.SelectedHeavyDutyVciIndex);
        Assert.Null(await new VciSettingsService(GetData(vm).Settings).LoadAsync(VehicleInterfaceSlot.LightDuty));
        Assert.DoesNotContain("not found", vm.VciSettingsStatusMessage);
    }

    [Fact]
    public async Task ViewModel_LoadsSavedSimulator_AsSelected()
    {
        var data = await CreateDataAsync();
        await new VciSettingsService(data.Settings).SaveAsync(
            VehicleInterfaceSlot.LightDuty,
            new DefaultVciSelection { Id = SimId, Name = "Sim", Kind = DeviceKinds.Simulator });
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory());

        await vm.LoadVciSettingsAsync();

        Assert.Equal(SimId, vm.LightDutyVciChoices[vm.SelectedLightDutyVciIndex].Id);
        Assert.DoesNotContain("not found", vm.VciSettingsStatusMessage);
    }
}
