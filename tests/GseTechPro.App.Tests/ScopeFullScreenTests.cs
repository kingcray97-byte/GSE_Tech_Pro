using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1j: scope full screen hides the chrome, never the scope controls or safety rows.</summary>
public sealed class ScopeFullScreenTests
{
    [Fact]
    public async Task FullScreen_OnlyFromScope_HidesChrome_AndExitsOnEscOrLeavingScope()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-fullscreen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();

        // Not on a Scope page: the toggle does nothing.
        vm.SelectToolbarAction("WorkOrders");
        vm.ToggleScopeFullScreen();
        Assert.False(vm.IsScopeFullScreen);
        Assert.False(vm.ExitScopeFullScreen());

        vm.SelectToolbarAction("Scope");
        Assert.True(vm.IsScopeViewVisible);
        var navBefore = vm.ModuleNavVisibility;
        Assert.Equal(Visibility.Visible, vm.AppChromeVisibility);
        Assert.Equal(200, vm.ScopeSidePanelWidth.Value);
        Assert.Equal("Full screen", vm.ScopeFullScreenButtonText);

        vm.ToggleScopeFullScreen();
        Assert.True(vm.IsScopeFullScreen);
        Assert.Equal(Visibility.Collapsed, vm.AppChromeVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ModuleNavVisibility);
        Assert.Equal(Visibility.Collapsed, vm.TabStripVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeSidePanelVisibility);
        Assert.Equal(0, vm.ScopeSidePanelWidth.Value);
        Assert.Equal("Exit full screen", vm.ScopeFullScreenButtonText);
        // The scope itself stays on screen, and Run still works.
        Assert.Equal(Visibility.Visible, vm.ScopeViewVisibility);
        await vm.StartScopeCaptureAsync();
        Assert.NotNull(vm.LastScopeCapture);

        // Esc leaves full screen.
        Assert.True(vm.ExitScopeFullScreen());
        Assert.False(vm.IsScopeFullScreen);
        Assert.Equal(Visibility.Visible, vm.AppChromeVisibility);
        Assert.Equal(navBefore, vm.ModuleNavVisibility);

        // Leaving the Scope pages also leaves full screen.
        vm.ToggleScopeFullScreen();
        Assert.True(vm.IsScopeFullScreen);
        vm.SelectToolbarAction("WorkOrders");
        Assert.False(vm.IsScopeFullScreen);
        Assert.Equal(Visibility.Visible, vm.AppChromeVisibility);
    }
}
