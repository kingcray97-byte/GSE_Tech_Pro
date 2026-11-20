using GseTechPro.App.ViewModels;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>
/// Phase 10B.3: the shared diagnostic module frame (navy module menu + breadcrumb root link +
/// connection strip) used by Scan, Scope, CAN Capture and the guided troubleshooting pages.
/// </summary>
public sealed class ModuleFrameTests
{
    private static async Task<MainShellViewModel> CreateViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-frame-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();
        return new MainShellViewModel(dataServices, new DiagnosticAdapterFactory());
    }

    [Theory]
    [InlineData("Diagnostics")]
    [InlineData("OBDII")]
    [InlineData("HeavyDuty")]
    [InlineData("J2534")]
    [InlineData("Scope")]
    [InlineData("CanCapture")]
    public async Task DiagnosticModules_UseTheFrame_WithMenuAndRootLink(string action)
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction(action);

        Assert.True(vm.IsModuleFrameVisible);
        Assert.Equal(Visibility.Visible, vm.ModuleFrameVisibility);
        Assert.Equal(Visibility.Visible, vm.ModuleNavVisibility);
        Assert.Equal("Equipment Selection", vm.BreadcrumbRoot);
    }

    [Theory]
    [InlineData("WorkOrders")]
    [InlineData("Assets")]
    [InlineData("AiTechChat")]
    [InlineData("Settings")]
    public async Task NonDiagnosticScreens_HideTheFrame(string action)
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction(action);

        Assert.False(vm.IsModuleFrameVisible);
        Assert.Equal(Visibility.Collapsed, vm.ModuleFrameVisibility);
    }

    [Theory]
    [InlineData("GuidedTree", true)]
    [InlineData("Readings", true)]
    [InlineData("AiChat", false)]
    [InlineData("ManualSearch", false)]
    public async Task GuidedPages_UseTheFrame_InsideAiTechChat(string page, bool framed)
    {
        await using var vm = await CreateViewModelAsync();
        var item = vm.FindSidebarItem(page);
        Assert.NotNull(item);

        await vm.SelectSidebarItemAsync(item!);

        Assert.Equal("AiTechChat", vm.CurrentSidebarSectionKey);
        Assert.Equal(framed, vm.IsModuleFrameVisible);
    }

    [Fact]
    public async Task ScopeGuidedTests_UseTheFrame()
    {
        await using var vm = await CreateViewModelAsync();
        var guided = vm.SidebarSections.Single(s => s.Key == "Scope").Children.Single(c => c.Key == "GuidedTests");

        await vm.SelectSidebarItemAsync(guided);

        Assert.Equal("GuidedTests", vm.ActiveSectionKey);
        Assert.True(vm.IsModuleFrameVisible);
        Assert.Equal("GuidedTests", vm.SelectedModuleNavItem?.Key);
    }

    [Fact]
    public async Task ConnectionStrip_ShowsAdapterState_AndUpdatesWhenItChanges()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("Diagnostics");

        Assert.Equal("No Equipment Connection", vm.ModuleConnectionText);
        Assert.Equal(Visibility.Visible, vm.ModuleDisconnectedVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ModuleConnectedVisibility);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.AdapterDeviceName = "Simulator";
        vm.ConnectionStatusText = "Connected (SIMULATED)";
        vm.IsConnected = true;

        Assert.Contains(nameof(MainShellViewModel.ModuleConnectionText), raised);
        Assert.Contains(nameof(MainShellViewModel.ModuleConnectedVisibility), raised);
        Assert.Equal("Connected (SIMULATED) · Simulator", vm.ModuleConnectionText);
        Assert.True(vm.IsModuleConnected);
        Assert.Equal(Visibility.Visible, vm.ModuleConnectedVisibility);
    }

    [Fact]
    public async Task ConnectionStrip_InScope_ReportsTheScope_NotTheAdapter()
    {
        await using var vm = await CreateViewModelAsync();
        vm.IsConnected = true; // diagnostic adapter connected, scope not
        vm.SelectToolbarAction("Scope");

        Assert.False(vm.IsModuleConnected);
        Assert.Equal("No Scope Connection", vm.ModuleConnectionText);

        vm.ScopeDeviceName = "Simulated 4-Channel Scope";
        vm.IsScopeSimulated = true;
        vm.IsScopeConnected = true;

        Assert.True(vm.IsModuleConnected);
        Assert.Equal("Scope connected (SIMULATED) · Simulated 4-Channel Scope", vm.ModuleConnectionText);
    }

    [Fact]
    public async Task ConnectionStrip_ShowsNoWorkOrder_WhenNoneIsOpen()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("Diagnostics");

        Assert.Equal("No open work order", vm.ModuleWorkOrderText);
    }

    [Fact]
    public async Task CanCapture_HasItsOwnPages_AndStaysInTheModule()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("CanCapture");

        Assert.Equal("CAN Capture", vm.ModuleNavTitle);
        Assert.Equal(new[] { "CanMonitor", "CanGraph", "CanSnaplist" }, vm.CurrentModuleNavItems.Select(i => i.Key));
        Assert.Equal("CanMonitor", vm.SelectedModuleNavItem?.Key);
        Assert.Equal(LiveDataDisplayMode.List, vm.SelectedLiveDataMode);

        await vm.SelectSidebarItemAsync(vm.CurrentModuleNavItems.Single(i => i.Key == "CanGraph"));
        Assert.Equal("CAN Capture", vm.ActiveTool);
        Assert.Equal("LiveData", vm.ActiveSectionKey);
        Assert.Equal(LiveDataDisplayMode.LineGraph, vm.SelectedLiveDataMode);
        Assert.Equal("CanGraph", vm.SelectedModuleNavItem?.Key);
        Assert.Equal(Visibility.Visible, vm.DeviceCommandBarVisibility);

        await vm.SelectSidebarItemAsync(vm.CurrentModuleNavItems.Single(i => i.Key == "CanSnaplist"));
        Assert.Equal(LiveDataDisplayMode.SnaplistEditor, vm.SelectedLiveDataMode);
        Assert.Equal("CanCapture", vm.CurrentSidebarSectionKey);
    }

    [Fact]
    public async Task BreadcrumbRoot_OpensEquipmentSelection()
    {
        await using var vm = await CreateViewModelAsync();
        vm.SelectToolbarAction("Scope");

        vm.OpenBreadcrumbRootCommand.Execute(null);

        Assert.Equal("Equipment Registry", vm.ActiveTool);
        Assert.Equal("Assets", vm.CurrentSidebarSectionKey);
        Assert.False(vm.IsModuleFrameVisible);
    }
}
