using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Vehicle/VCI selection plan Step 8: mode-specific sidebar entries and their actions.</summary>
public sealed class SidebarModeTests
{
    private sealed class FakeIdentifier(VehicleProfile? result, bool throws = false) : IVehicleIdentifier
    {
        public int Calls { get; private set; }

        public Task<VehicleProfile?> IdentifyAsync(IDiagnosticAdapter adapter, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (throws)
                throw new InvalidOperationException("boom");
            return Task.FromResult(result);
        }
    }

    private static async Task<MainShellViewModel> CreateAsync(IVehicleIdentifier? identifier = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-sidebar-{Guid.NewGuid():N}.db");
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        return new MainShellViewModel(data, new DiagnosticAdapterFactory(), vehicleIdentifier: identifier);
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

    private static string[] Labels(IEnumerable<SidebarItem> items) => items.Select(i => i.Label).ToArray();

    [Fact]
    public async Task HeavyDuty_SidebarAppendsEngineTransAbsTruck()
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "Heavy Duty";

        var items = vm.CurrentModuleNavItems;

        Assert.Equal(new[] { "Engine", "Trans", "ABS", "Truck" }, Labels(items.TakeLast(4)));
        Assert.Contains(items, i => i.Key == "Overview");
        Assert.Equal(6 + 4, items.Count);
    }

    [Fact]
    public async Task Oem_SidebarAppendsAutoDomesticAsianEuropean()
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "OemDiagnostics";

        var items = vm.CurrentModuleNavItems;

        Assert.Equal(new[] { "Auto", "Domestic", "Asian", "European" }, Labels(items.TakeLast(4)));
        Assert.Equal(6 + 4, items.Count);
    }

    [Theory]
    [InlineData("Heavy Duty")]
    [InlineData("OemDiagnostics")]
    public async Task ModeEntries_AreActions_WithActionKey(string tool)
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = tool;

        var modeItems = vm.GetModeSidebarItems();

        Assert.Equal(4, modeItems.Count);
        Assert.All(modeItems, i =>
        {
            Assert.True(i.IsAction);
            Assert.Equal(i.Key, i.ActionKey);
            Assert.Equal("Diagnostics", i.ParentKey);
        });
        // Base pages stay plain navigation entries.
        Assert.All(vm.CurrentModuleNavItems.Except(modeItems), i => Assert.False(i.IsAction));
    }

    [Theory]
    [InlineData("Diagnostics")]
    [InlineData("OBD II")]
    [InlineData("Scope")]
    [InlineData("Camera")]
    [InlineData("AI Tech Chat")]
    [InlineData("Settings")]
    public async Task OtherTools_HaveNoModeEntries(string tool)
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = tool;

        Assert.Empty(vm.GetModeSidebarItems());
        Assert.DoesNotContain(vm.CurrentModuleNavItems, i => i.IsAction);
    }

    [Fact]
    public async Task ModeEntries_AreStableInstances_ForListSelection()
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "OemDiagnostics";

        Assert.Same(vm.CurrentModuleNavItems.Last(), vm.CurrentModuleNavItems.Last());
    }

    [Fact]
    public async Task SwitchingTool_RaisesNavItemsChanged()
    {
        await using var vm = await CreateAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ActiveTool = "Heavy Duty";

        Assert.Contains(nameof(MainShellViewModel.CurrentModuleNavItems), raised);
    }

    [Fact]
    public async Task Auto_NotConnected_ShowsExactFailureMessage_AndDoesNotCallIdentifier()
    {
        var identifier = new FakeIdentifier(new VehicleProfile { Vin = "X" });
        await using var vm = await CreateAsync(identifier);
        vm.ActiveTool = "OemDiagnostics";
        var auto = vm.GetModeSidebarItems().First(i => i.Key == "Auto");

        await vm.SelectSidebarItemAsync(auto);

        Assert.Equal(
            "Automatic vehicle detection failed. Please select a region to enter vehicle details manually.",
            vm.StatusBannerMessage);
        Assert.Equal(0, identifier.Calls);
        Assert.Null(vm.ActiveVehicle);
        Assert.Equal("OemDiagnostics", vm.ActiveTool); // action did not navigate
    }

    [Fact]
    public async Task Auto_Connected_Success_SetsActiveVehicle_AndBanner()
    {
        var identifier = new FakeIdentifier(new VehicleProfile { Vin = "FIXTUREVIN", Make = "Fixture" });
        await using var vm = await CreateAsync(identifier);
        await SimulateConnectedAsync(vm);

        await vm.OnSidebarActionAsync("Auto");

        Assert.Equal(1, identifier.Calls);
        Assert.Equal("FIXTUREVIN", vm.ActiveVehicle?.Vin);
        Assert.Equal("Vehicle identified: Fixture (FIXTUREVIN)", vm.StatusBannerMessage);
    }

    [Fact]
    public async Task Auto_Connected_MissingMake_ShowsUnknown()
    {
        var identifier = new FakeIdentifier(new VehicleProfile { Vin = "ABC" });
        await using var vm = await CreateAsync(identifier);
        await SimulateConnectedAsync(vm);

        await vm.OnSidebarActionAsync("Auto");

        Assert.Equal("Vehicle identified: Unknown (ABC)", vm.StatusBannerMessage);
    }

    [Fact]
    public async Task Auto_Connected_NullResult_ShowsFailureMessage()
    {
        await using var vm = await CreateAsync(new FakeIdentifier(null));
        await SimulateConnectedAsync(vm);

        await vm.OnSidebarActionAsync("Auto");

        Assert.Equal(MainShellViewModel.AutoDetectFailedMessage, vm.StatusBannerMessage);
        Assert.Null(vm.ActiveVehicle);
    }

    [Fact]
    public async Task Auto_Connected_IdentifierThrows_ShowsFailureMessage()
    {
        await using var vm = await CreateAsync(new FakeIdentifier(null, throws: true));
        await SimulateConnectedAsync(vm);

        await vm.OnSidebarActionAsync("Auto");

        Assert.Equal(MainShellViewModel.AutoDetectFailedMessage, vm.StatusBannerMessage);
    }

    [Fact]
    public async Task Auto_Connected_WithRealIdentifierAndSimulator_SetsVehicleOrShowsFailure()
    {
        await using var vm = await CreateAsync(new VehicleIdentifier());
        await SimulateConnectedAsync(vm);

        await vm.OnSidebarActionAsync("Auto");

        if (vm.ActiveVehicle is not null)
            Assert.StartsWith("Vehicle identified: ", vm.StatusBannerMessage);
        else
            Assert.Equal(MainShellViewModel.AutoDetectFailedMessage, vm.StatusBannerMessage);
    }

    [Theory]
    [InlineData("Domestic", VehicleRegion.Domestic)]
    [InlineData("Asian", VehicleRegion.Asian)]
    [InlineData("European", VehicleRegion.European)]
    public async Task RegionActions_OpenSelectorForRegion_AndClearPending(string key, VehicleRegion expected)
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "OemDiagnostics";
        VehicleRegion? openedRegion = null;
        string? openedSystem = "unset";
        VehicleRegion? pendingWhileOpen = null;
        vm.VehicleSelectorPresenter = (_, region, system, _) =>
        {
            openedRegion = region;
            openedSystem = system;
            pendingWhileOpen = vm.PendingVehicleRegion;
            return Task.FromResult<VehicleProfile?>(null);
        };
        var item = vm.GetModeSidebarItems().First(i => i.Key == key);

        await vm.SelectSidebarItemAsync(item);

        Assert.Equal(expected, openedRegion);
        Assert.Null(openedSystem);
        Assert.Equal(expected, pendingWhileOpen);
        Assert.Null(vm.PendingVehicleRegion);
        Assert.Null(vm.PendingHdSystem);
    }

    [Theory]
    [InlineData("Engine")]
    [InlineData("Trans")]
    [InlineData("ABS")]
    [InlineData("Truck")]
    public async Task HdActions_OpenSelectorForSystem_AndClearPending(string key)
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "Heavy Duty";
        VehicleRegion? openedRegion = null;
        string? openedSystem = null;
        string? pendingWhileOpen = null;
        vm.VehicleSelectorPresenter = (_, region, system, _) =>
        {
            openedRegion = region;
            openedSystem = system;
            pendingWhileOpen = vm.PendingHdSystem;
            return Task.FromResult<VehicleProfile?>(null);
        };
        var item = vm.GetModeSidebarItems().First(i => i.Key == key);

        await vm.SelectSidebarItemAsync(item);

        Assert.Equal(VehicleRegion.HeavyDuty, openedRegion);
        Assert.Equal(key, openedSystem);
        Assert.Equal(key, pendingWhileOpen);
        Assert.Null(vm.PendingHdSystem);
        Assert.Null(vm.PendingVehicleRegion);
        Assert.Equal("Heavy Duty", vm.ActiveTool);
    }

    [Fact]
    public async Task ActionSelection_RestoresHighlightToLastPage()
    {
        await using var vm = await CreateAsync();
        vm.ActiveTool = "OemDiagnostics";
        var domestic = vm.GetModeSidebarItems().First(i => i.Key == "Domestic");

        vm.SelectedModuleNavItem = domestic; // what a tap on the list does; fires the action in the background
        await vm.SelectSidebarItemAsync(domestic);

        Assert.False(vm.SelectedModuleNavItem?.IsAction ?? false);
    }

    [Fact]
    public void SidebarItem_Action_Factory_SetsMarker()
    {
        var item = SidebarItem.Action("Engine", "Engine", "x", "Diagnostics");

        Assert.True(item.IsAction);
        Assert.Equal("Engine", item.ActionKey);
        Assert.False(new SidebarItem("a", "b", "c").IsAction);
        Assert.Null(new SidebarItem("a", "b", "c").ActionKey);
    }
}
