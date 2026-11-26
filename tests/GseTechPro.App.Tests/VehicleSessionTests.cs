using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Vehicle/VCI selection plan Step 6: active vehicle, header line and breadcrumb sync.</summary>
public sealed class VehicleSessionTests
{
    private static async Task<MainShellViewModel> CreateViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-vehsess-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();
        return new MainShellViewModel(dataServices, new DiagnosticAdapterFactory());
    }

    private static VehicleProfile HondaProfile() => new()
    {
        Vin = "1HGCM82633A004352",
        Make = "Honda",
        Year = 2003,
        Region = VehicleRegion.Domestic,
        Protocol = ProtocolType.OBD2,
    };

    private static void AssertCleared(MainShellViewModel vm)
    {
        Assert.Null(vm.ActiveVehicle);
        Assert.Equal("VIN: <not connected>", vm.VehicleVinText);
        Assert.Equal("No vehicle selected", vm.ActiveVehicleHeaderLine);
        Assert.Equal("Equipment Selection", vm.BreadcrumbRoot);
    }

    [Fact]
    public async Task SetActiveVehicle_Null_ClearsAll()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SetActiveVehicle(HondaProfile());
        vm.SetActiveVehicle(null);
        AssertCleared(vm);
    }

    [Fact]
    public async Task SetActiveVehicle_Profile_UpdatesAll()
    {
        await using var vm = await CreateViewModelAsync();
        var profile = HondaProfile();
        vm.SetActiveVehicle(profile);

        Assert.Same(profile, vm.ActiveVehicle);
        Assert.Equal("VIN: 1HGCM82633A004352", vm.VehicleVinText);
        Assert.Contains("Honda", vm.ActiveVehicleHeaderLine);
        Assert.Contains("Engine: Unknown", vm.ActiveVehicleHeaderLine);
        Assert.Contains("2003", vm.ActiveVehicleHeaderLine);
        Assert.Contains("Domestic", vm.ActiveVehicleHeaderLine);
        Assert.Contains("OBD2", vm.ActiveVehicleHeaderLine);
        Assert.Equal("Vehicle: 1HGCM82633A004352", vm.BreadcrumbRoot);
    }

    [Fact]
    public async Task SetActiveVehicle_Engine_RenderedInHeaderLine()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SetActiveVehicle(new VehicleProfile { Make = "Honda", Engine = "TestEngine" });

        Assert.Contains("Model: Unknown | Engine: TestEngine | Year: Unknown", vm.ActiveVehicleHeaderLine);
    }

    [Fact]
    public async Task SetActiveVehicle_NullFields_RenderedAsUnknown()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SetActiveVehicle(new VehicleProfile());

        Assert.Equal("VIN: <unknown>", vm.VehicleVinText);
        Assert.Equal("Vehicle: <unknown>", vm.BreadcrumbRoot);
        foreach (var label in new[] { "Make: Unknown", "Model: Unknown", "Engine: Unknown", "Year: Unknown", "Region: Unknown", "Protocol: Unknown" })
            Assert.Contains(label, vm.ActiveVehicleHeaderLine);
        Assert.Contains("VIN: <unknown>", vm.ActiveVehicleHeaderLine);
        Assert.DoesNotContain(": |", vm.ActiveVehicleHeaderLine);
        Assert.False(vm.ActiveVehicleHeaderLine.EndsWith(": ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearActiveVehicle_Clears()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SetActiveVehicle(HondaProfile());
        vm.ClearActiveVehicle();
        AssertCleared(vm);
    }

    /// <summary>Integration test: connect flow sets ActiveVehicle from adapter vehicle info, disconnect clears it.</summary>
    [Fact]
    public async Task ConnectFlow_SetsActiveVehicle_AndDisconnectClearsIt()
    {
        await using var vm = await CreateViewModelAsync();

        // Simulate connected state (ConnectSimulatorAsync builds WinUI brushes that need a UI thread).
        var adapter = new DiagnosticAdapterFactory().CreateSimulator(ProtocolType.OBD2, seed: null);
        await adapter.ConnectAsync();
        typeof(MainShellViewModel)
            .GetField("_activeAdapter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, adapter);
        vm.IsConnected = true;

        // Simulate the connect flow that reads vehicle info and decodes VIN (as MainShellViewModel does).
        var vehicleInfo = await adapter.GetVehicleInfoAsync();
        var decoded = GseTechPro.Devices.VinDecoder.Decode(vehicleInfo.Vin);
        vm.SetActiveVehicle(new VehicleProfile
        {
            Vin = vehicleInfo.Vin,
            Make = decoded?.Make,
            Model = null,
            Engine = decoded?.Engine,
            Region = decoded?.Region,
            Year = decoded?.Year,
            Protocol = vehicleInfo.Protocol,
            Source = VehicleProfileSource.Auto,
        });

        Assert.NotNull(vm.ActiveVehicle);
        Assert.Equal("VIN: SIMULATED00000001", vm.VehicleVinText);
        Assert.Contains("European", vm.ActiveVehicleHeaderLine);
        Assert.Equal("Vehicle: SIMULATED00000001", vm.BreadcrumbRoot);

        // Simulate disconnect flow.
        await adapter.DisconnectAsync();
        typeof(MainShellViewModel)
            .GetField("_activeAdapter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, null);
        vm.IsConnected = false;
        vm.SetActiveVehicle(null);

        AssertCleared(vm);
    }
}
