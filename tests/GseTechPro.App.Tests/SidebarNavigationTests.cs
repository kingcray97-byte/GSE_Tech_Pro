using GseTechPro.App.ViewModels;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Page routing behind the Shop Foreman Pro shell (Phase 10B): toolbar modules, menu items and the navy module menu share SidebarSections.</summary>
public sealed class SidebarNavigationTests
{
    private static async Task<MainShellViewModel> CreateViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-sidebar-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();
        return new MainShellViewModel(dataServices, new DiagnosticAdapterFactory());
    }

    [Fact]
    public async Task Sidebar_HasOneEntryPerSection_AndNoDuplicateOrBackItems()
    {
        await using var vm = await CreateViewModelAsync();

        var topKeys = vm.SidebarSections.Select(s => s.Key).ToList();
        Assert.Equal(new[] { "Diagnostics", "Scope", "CanCapture", "WorkOrders", "Assets", "AiTechChat", "Camera" }, topKeys);

        // J2534 / Heavy Duty / OBD II are merged into one Diagnostics entry
        Assert.DoesNotContain("J2534", topKeys);
        Assert.DoesNotContain("HeavyDuty", topKeys);
        Assert.DoesNotContain("OBDII", topKeys);

        foreach (var section in vm.SidebarSections)
        {
            Assert.Null(section.ParentKey);
            Assert.DoesNotContain(section.Children, c => c.Key == "Back");
            Assert.All(section.Children, c => Assert.Equal(section.Key, c.ParentKey));
            Assert.Equal(section.Children.Count, section.Children.Select(c => c.Key).Distinct().Count());
        }

        // Each page key appears under only one section, so a page is never offered twice
        var childKeys = vm.SidebarSections.SelectMany(s => s.Children).Where(c => c.Key != "Overview").Select(c => c.Key).ToList();
        Assert.Equal(childKeys.Count, childKeys.Distinct().Count());
    }

    [Fact]
    public async Task Diagnostics_KeepsSelectedProtocol_AndShowsDeviceCommandBar()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectedProtocol = ProtocolType.OBD2;

        vm.SelectToolbarAction("WorkOrders");
        Assert.Equal(Visibility.Collapsed, vm.DeviceCommandBarVisibility);
        Assert.Equal(Visibility.Visible, vm.WorkOrdersCommandBarVisibility);

        vm.SelectToolbarAction("Diagnostics");
        Assert.Equal("Diagnostics", vm.CurrentSidebarSectionKey);
        Assert.True(vm.IsDiagnosticsViewVisible);
        Assert.Equal(ProtocolType.OBD2, vm.SelectedProtocol);
        Assert.Equal(Visibility.Visible, vm.DeviceCommandBarVisibility);
        Assert.Equal(Visibility.Collapsed, vm.WorkOrdersCommandBarVisibility);
    }

    [Fact]
    public async Task Settings_ShowsSettingsPage_AndHidesPlaceholder()
    {
        await using var vm = await CreateViewModelAsync();

        vm.SelectToolbarAction("Settings");

        Assert.Equal("Settings", vm.CurrentSidebarSectionKey);
        Assert.True(vm.IsSettingsViewVisible);
        Assert.Equal(Visibility.Visible, vm.SettingsViewVisibility);
        Assert.False(vm.IsToolPlaceholderVisible);
        Assert.False(vm.IsDiagnosticsViewVisible);
        Assert.Equal(Visibility.Collapsed, vm.DeviceCommandBarVisibility);
    }

    [Fact]
    public async Task SubPage_SwitchesToItsSection_FirstAndOpensThePage()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("WorkOrders");

        var dtcs = vm.SidebarSections.Single(s => s.Key == "Diagnostics").Children.Single(c => c.Key == "DTCs");
        await vm.SelectSidebarItemAsync(dtcs);

        Assert.Equal("Diagnostics", vm.CurrentSidebarSectionKey);
        Assert.Equal("DTCs", vm.ActiveSectionKey);

        var chat = vm.SidebarSections.Single(s => s.Key == "AiTechChat").Children.Single(c => c.Key == "AiChat");
        await vm.SelectSidebarItemAsync(chat);
        Assert.Equal("AiTechChat", vm.CurrentSidebarSectionKey);
        Assert.True(vm.IsTechChatViewVisible);
    }

    [Fact]
    public async Task InPageLink_ToAnotherSection_ResolvesThroughSidebar()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("WorkOrders");

        // "Repair Recommendations" on a work order lives under AI Tech Chat in the sidebar
        var item = vm.FindSidebarItem("Recommendations");
        Assert.NotNull(item);
        Assert.Equal("AiTechChat", item!.ParentKey);

        // "Work Orders & Close" from Recommendations is a top-level section
        var workOrders = vm.FindSidebarItem("WorkOrders");
        Assert.NotNull(workOrders);
        Assert.Null(workOrders!.ParentKey);

        // "Overview" prefers the active section's own overview
        vm.SelectToolbarAction("Scope");
        Assert.Equal("Scope", vm.FindSidebarItem("Overview")!.ParentKey);
    }

    [Fact]
    public async Task ConnectAndDisconnect_ShowOneAtATime()
    {
        await using var vm = await CreateViewModelAsync();

        Assert.False(vm.IsConnected);
        Assert.Equal(Visibility.Visible, vm.ConnectButtonVisibility);
        Assert.Equal(Visibility.Collapsed, vm.DisconnectButtonVisibility);

        vm.IsConnected = true;
        Assert.Equal(Visibility.Collapsed, vm.ConnectButtonVisibility);
        Assert.Equal(Visibility.Visible, vm.DisconnectButtonVisibility);
    }

    [Fact]
    public async Task ModuleMenu_ListsTheActiveModulesPages_AndHidesForScreensWithoutPages()
    {
        await using var vm = await CreateViewModelAsync();

        vm.SelectToolbarAction("Scope");
        Assert.Equal("Scope", vm.ModuleNavTitle);
        Assert.Equal(new[] { "Overview", "ScopeChannels", "GuidedTests", "OneTapTests", "RefWaveforms" }, vm.CurrentModuleNavItems.Select(i => i.Key));
        Assert.Equal(Visibility.Visible, vm.ModuleNavVisibility);
        Assert.Equal("Overview", vm.SelectedModuleNavItem?.Key); // first page highlighted on entry

        vm.SelectToolbarAction("WorkOrders");
        Assert.Empty(vm.CurrentModuleNavItems);
        Assert.Equal(Visibility.Collapsed, vm.ModuleNavVisibility);
        Assert.Null(vm.SelectedModuleNavItem);
    }

    [Fact]
    public async Task ModuleMenu_SelectingAPage_OpensIt_AndKeepsItHighlighted()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("Diagnostics");

        vm.SelectedModuleNavItem = vm.CurrentModuleNavItems.Single(i => i.Key == "LiveData");
        // Selection from the UI navigates asynchronously; give it a moment.
        for (var i = 0; i < 50 && vm.BreadcrumbLeaf != "Live Snaplist & Multi-View"; i++)
            await Task.Delay(20);

        Assert.Equal("Live Snaplist & Multi-View", vm.BreadcrumbLeaf);
        Assert.Equal("LiveData", vm.SelectedModuleNavItem?.Key);
        Assert.Equal("Diagnostics", vm.CurrentSidebarSectionKey);
    }

    [Fact]
    public async Task InPageLink_HighlightsTheTargetPage_InTheModuleMenu()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("WorkOrders");

        var item = vm.FindSidebarItem("ManualSearch");
        Assert.NotNull(item);
        await vm.SelectSidebarItemAsync(item!);

        Assert.Equal("AiTechChat", vm.CurrentSidebarSectionKey);
        Assert.Equal("ManualSearch", vm.SelectedModuleNavItem?.Key);
    }

    [Theory]
    [InlineData("Diagnostics", "Diagnostics")]
    [InlineData("HeavyDuty", "Heavy Duty")]
    [InlineData("OBDII", "OBD II")]
    [InlineData("Scope", "Scope")]
    [InlineData("AiTechChat", "AI Tech Chat")]
    [InlineData("WorkOrders", "Work Orders")]
    [InlineData("Home", "Equipment Registry")]
    public async Task ToolbarTile_UnderlinesOnlyTheActiveModule(string action, string tool)
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction(action);

        Assert.Equal(tool, vm.ActiveTool);
        Assert.Equal(Visibility.Visible, MainShellViewModel.ToolbarIndicator(vm.ActiveTool, tool));
        Assert.Equal(Visibility.Collapsed, MainShellViewModel.ToolbarIndicator(vm.ActiveTool, tool == "Scope" ? "OBD II" : "Scope"));
    }
}
