using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1: pass/fail mask on the scope screen (simulator).</summary>
public sealed class ScopeMaskTests
{
    private static async Task<MainShellViewModel> CapturedViewModelAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-mask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");
        await vm.StartScopeCaptureAsync();
        Assert.NotNull(vm.LastScopeCapture);
        return vm;
    }

    [Fact]
    public async Task Mask_NeedsATolerance_ThenPassesTheSameSignal_AndCountsEachCaptureOnce()
    {
        await using var vm = await CapturedViewModelAsync();

        await vm.CreateScopeMaskAsync();
        Assert.Null(vm.ScopeMask);
        Assert.Contains("voltage tolerance", vm.ScopeMaskStatusText);

        vm.ScopeMaskVoltageTolerance = 0.5;
        vm.ScopeMaskTimeToleranceDivisions = 0.1;
        await vm.CreateScopeMaskAsync();
        Assert.NotNull(vm.ScopeMask);
        Assert.NotEmpty(vm.ScopeMaskUpperRawPoints);
        Assert.Equal("Pass 0 · Fail 0", vm.ScopeMaskCountText); // the capture it was made from is not counted

        await vm.StartScopeCaptureAsync();
        var r = Assert.IsType<MaskTestResult>(vm.LastScopeMaskResult);
        Assert.True(r.Passed, r.Message);
        Assert.Equal("Pass 1 · Fail 0", vm.ScopeMaskCountText);
        Assert.Contains("(simulated)", vm.ScopeMaskStatusText);

        // Redrawing the same capture (zoom, buffer step) does not count it again.
        await vm.RefreshScopeMaskAsync();
        Assert.Equal("Pass 1 · Fail 0", vm.ScopeMaskCountText);

        vm.ClearScopeMask();
        Assert.Null(vm.ScopeMask);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.ScopeMaskTraceVisibility);
    }

    [Fact]
    public async Task Mask_OnDifferentSettings_ReportsMismatch_NotAVerdict()
    {
        await using var vm = await CapturedViewModelAsync();
        vm.ScopeMaskVoltageTolerance = 0.5;
        await vm.CreateScopeMaskAsync();

        vm.ScopeTimebaseSecondsPerDivision *= 2;
        await vm.StartScopeCaptureAsync();
        Assert.Null(vm.LastScopeMaskResult!.Passed);
        Assert.Contains("make a new mask", vm.ScopeMaskStatusText);
        Assert.Equal("Pass 0 · Fail 0", vm.ScopeMaskCountText);
        // No envelope over a capture it cannot test (found in the app check).
        Assert.Empty(vm.ScopeMaskUpperRawPoints);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.ScopeMaskTraceVisibility);

        // Back on the mask's settings, the envelope returns.
        vm.ScopeTimebaseSecondsPerDivision /= 2;
        await vm.StartScopeCaptureAsync();
        Assert.True(vm.LastScopeMaskResult!.Passed);
        Assert.NotEmpty(vm.ScopeMaskUpperRawPoints);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.ScopeMaskTraceVisibility);
    }
}
