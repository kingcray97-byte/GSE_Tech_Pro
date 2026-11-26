using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Vehicle/VCI selection plan Step 7: toolbar and screen behavior (OBD II, HD, VCI/OEM tiles).</summary>
public sealed class VciToolbarTests
{
    private sealed class FakeIdentifier(VehicleProfile? result) : IVehicleIdentifier
    {
        public int Calls { get; private set; }

        public Task<VehicleProfile?> IdentifyAsync(IDiagnosticAdapter adapter, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static async Task<(MainShellViewModel Vm, DataServices Data)> CreateAsync(IVehicleIdentifier? identifier = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-vcitb-{Guid.NewGuid():N}.db");
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), vehicleIdentifier: identifier);
        return (vm, data);
    }

    /// <summary>ConnectSimulatorAsync builds WinUI brushes (needs a UI thread), so put the VM in a connected state directly.</summary>
    private static async Task SimulateConnectedAsync(MainShellViewModel vm)
    {
        var adapter = new DiagnosticAdapterFactory().CreateSimulator(ProtocolType.OBD2, seed: null);
        await adapter.ConnectAsync();
        typeof(MainShellViewModel)
            .GetField("_activeAdapter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, adapter);
        vm.IsConnected = true;
    }

    [Theory]
    [InlineData("OBD II", Visibility.Collapsed)]
    [InlineData("Heavy Duty", Visibility.Visible)]
    [InlineData("OemDiagnostics", Visibility.Visible)]
    [InlineData("Diagnostics", Visibility.Visible)]
    public async Task ProtocolDropdownVisibility_FollowsActiveTool(string tool, Visibility expected)
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;

        vm.ActiveTool = tool;

        Assert.Equal(expected, vm.ProtocolDropdownVisibility);
    }

    [Fact]
    public async Task ProtocolDropdownVisibility_RaisesChangeWhenToolChanges()
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ActiveTool = "OBD II";

        Assert.Contains(nameof(MainShellViewModel.ProtocolDropdownVisibility), raised);
    }

    [Fact]
    public async Task DefaultBreadcrumbRoot_NoVehicle_IsEquipmentSelection()
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;

        Assert.Equal("Equipment Selection", vm.DefaultBreadcrumbRoot);
    }

    [Fact]
    public async Task DefaultBreadcrumbRoot_ActiveVehicle_UsesVin_AndUnknownWhenMissing()
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;

        vm.SetActiveVehicle(new VehicleProfile { Vin = "1HGCM82633A004352" });
        Assert.Equal("Vehicle: 1HGCM82633A004352", vm.DefaultBreadcrumbRoot);

        vm.SetActiveVehicle(new VehicleProfile());
        Assert.Equal("Vehicle: <unknown>", vm.DefaultBreadcrumbRoot);

        vm.ClearActiveVehicle();
        Assert.Equal("Equipment Selection", vm.DefaultBreadcrumbRoot);
    }

    [Fact]
    public async Task SelectToolbarAction_OemDiagnostics_ActivatesOemTool()
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;

        vm.SelectToolbarAction("OemDiagnostics");

        Assert.Equal("OemDiagnostics", vm.ActiveTool);
        Assert.Equal("Diagnostics", vm.ActiveSectionKey);
        Assert.Equal("OEM Diagnostics", vm.BreadcrumbCategory);
        Assert.Equal(Visibility.Visible, MainShellViewModel.ToolbarIndicator(vm.ActiveTool, "OemDiagnostics"));
    }

    [Fact]
    public async Task SelectToolbarAction_HeavyDuty_StillActivatesHeavyDuty()
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;

        vm.SelectToolbarAction("HeavyDuty");

        Assert.Equal("Heavy Duty", vm.ActiveTool);
        Assert.Equal(ProtocolType.J1939, vm.SelectedProtocol);
        Assert.Equal(Visibility.Visible, vm.ProtocolDropdownVisibility);
    }

    [Fact]
    public async Task SelectToolbarAction_ObdII_HidesProtocolDropdown()
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;

        vm.SelectToolbarAction("OBDII");

        Assert.Equal("OBD II", vm.ActiveTool);
        Assert.Equal(Visibility.Collapsed, vm.ProtocolDropdownVisibility);
    }

    [Fact]
    public async Task ActivatingATool_KeepsTheVehicleBreadcrumbRoot()
    {
        var (vm, _) = await CreateAsync();
        await using var _vm = vm;
        vm.SetActiveVehicle(new VehicleProfile { Vin = "1HGCM82633A004352" });

        vm.SelectToolbarAction("HeavyDuty");

        Assert.Equal("Vehicle: 1HGCM82633A004352", vm.BreadcrumbRoot);
    }

    [Fact]
    public async Task ApplySavedVciAsync_SelectsTheSavedDevice_WhenDiscovered()
    {
        var (vm, data) = await CreateAsync();
        await using var _vm = vm;
        var fake = new DeviceInfo { Id = "fixture-j2534", Name = "Fixture J2534", Kind = DeviceKinds.J2534 };
        vm.AvailableDevices.Add(new SelectableDeviceItem(fake, isInProcessSimulator: false));
        await new VciSettingsService(data.Settings).SaveAsync(
            VehicleInterfaceSlot.LightDuty,
            new DefaultVciSelection { Id = fake.Id, Name = fake.Name, Kind = fake.Kind });

        await vm.ApplySavedVciAsync(VehicleInterfaceSlot.LightDuty);

        Assert.Equal("fixture-j2534", vm.SelectedDevice?.Id);
    }

    [Fact]
    public async Task ApplySavedVciAsync_LeavesSelection_WhenNothingSavedOrDeviceMissing()
    {
        var (vm, data) = await CreateAsync();
        await using var _vm = vm;
        var before = vm.SelectedDevice;

        await vm.ApplySavedVciAsync(VehicleInterfaceSlot.HeavyDuty);
        Assert.Same(before, vm.SelectedDevice);

        await new VciSettingsService(data.Settings).SaveAsync(
            VehicleInterfaceSlot.HeavyDuty,
            new DefaultVciSelection { Id = "gone", Name = "Gone", Kind = DeviceKinds.Rp1210 });
        await vm.ApplySavedVciAsync(VehicleInterfaceSlot.HeavyDuty);

        Assert.Same(before, vm.SelectedDevice);
    }

    [Fact]
    public async Task RunIdentifierOnEntryAsync_NotConnected_DoesNothing()
    {
        var identifier = new FakeIdentifier(new VehicleProfile { Vin = "X" });
        var (vm, _) = await CreateAsync(identifier);
        await using var _vm = vm;

        await vm.RunIdentifierOnEntryAsync();

        Assert.Equal(0, identifier.Calls);
        Assert.Null(vm.ActiveVehicle);
    }

    [Fact]
    public async Task RunIdentifierOnEntryAsync_Connected_SetsActiveVehicle()
    {
        var identifier = new FakeIdentifier(new VehicleProfile { Vin = "FIXTUREVIN", Make = "Fixture" });
        var (vm, _) = await CreateAsync(identifier);
        await using var _vm = vm;
        await SimulateConnectedAsync(vm);

        await vm.RunIdentifierOnEntryAsync();

        Assert.Equal(1, identifier.Calls);
        Assert.Equal("FIXTUREVIN", vm.ActiveVehicle?.Vin);
        Assert.Equal("Vehicle: FIXTUREVIN", vm.DefaultBreadcrumbRoot);
    }

    [Fact]
    public async Task RunIdentifierOnEntryAsync_NullResult_KeepsCurrentVehicle()
    {
        var identifier = new FakeIdentifier(null);
        var (vm, _) = await CreateAsync(identifier);
        await using var _vm = vm;
        await SimulateConnectedAsync(vm);
        var current = new VehicleProfile { Vin = "KEEPME" };
        vm.SetActiveVehicle(current);

        await vm.RunIdentifierOnEntryAsync();

        Assert.Same(current, vm.ActiveVehicle);
    }
}
