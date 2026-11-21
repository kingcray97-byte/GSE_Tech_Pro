using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1k: multi-view (stacked) layout on the scope screen (simulator).</summary>
public sealed class ScopeViewsTests
{
    [Fact]
    public async Task Stacked_GivesEachEnabledChannelItsOwnLane_AndOverlayIsUnchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-views-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        vm.ScopeChannel2.IsEnabled = true;
        vm.ScopeChannel2.Label = "Cam sensor";
        await vm.StartScopeCaptureAsync();
        var rawA = vm.ScopeChannel1RawPoints;
        Assert.NotEmpty(rawA);

        // Overlay: no lanes, points untouched, side axes shown.
        Assert.False(vm.IsScopeStacked);
        Assert.Empty(vm.ScopeViewLaneKeys);
        Assert.Same(rawA, vm.ToScopeLanePoints(rawA, 1));
        Assert.Equal(Visibility.Visible, vm.ScopeLeftAxisVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeLaneOverlayVisibility);

        vm.SelectedScopeViewLayout = vm.ScopeViewLayouts.Single(l => l.Layout == ScopeViewLayout.Stacked);
        Assert.True(vm.IsScopeStacked);
        Assert.Equal([1, 2], vm.ScopeViewLaneKeys);
        Assert.Equal(Visibility.Visible, vm.ScopeLaneOverlayVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeLeftAxisVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeRightAxisVisibility);
        Assert.Equal(1, vm.ScopeLane1Height.Value);
        Assert.Equal(0, vm.ScopeLane3Height.Value);
        Assert.Equal("B Cam sensor  ±5 V", vm.ScopeLane2Header);
        Assert.Contains("2 views", vm.ScopeViewsStatusText);

        // Channel A lives in the top half, channel B in the bottom half.
        var laneA = vm.ToScopeLanePoints(rawA, 1);
        Assert.All(laneA, p => Assert.InRange(p.Y, 0.0, 300.0));
        var laneB = vm.ToScopeLanePoints(vm.ScopeChannel2RawPoints, 2);
        Assert.All(laneB, p => Assert.InRange(p.Y, 300.0, 600.0));
        Assert.Equal(rawA.Select(p => p.X), laneA.Select(p => p.X));

        // The capture and its measurements are not changed by the layout.
        Assert.Same(rawA, vm.ScopeChannel1RawPoints);

        // Math channel M gets its own lane at the bottom.
        vm.IsScopeMathEnabled = true;
        await vm.RefreshScopeMathAsync();
        Assert.Equal([1, 2, ScopeViewLanes.MathLane], vm.ScopeViewLaneKeys);
        Assert.Equal(1, vm.ScopeLaneMathHeight.Value);

        // Turning a channel off removes its lane.
        vm.ScopeChannel2.IsEnabled = false;
        Assert.Equal([1, ScopeViewLanes.MathLane], vm.ScopeViewLaneKeys);
        Assert.Equal(0, vm.ScopeLane2Height.Value);
    }
}
