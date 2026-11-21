using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1: math channel on the scope screen (simulator).</summary>
public sealed class ScopeMathTests
{
    private static async Task<MainShellViewModel> CapturedViewModelAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-math-{Guid.NewGuid():N}");
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
    public async Task Invert_DrawsTheNegatedChannelA_AsAFifthTrace()
    {
        await using var vm = await CapturedViewModelAsync();
        Assert.Equal("Math channel off.", vm.ScopeMathStatusText);

        vm.SelectedScopeMathOperation = vm.ScopeMathOperations.Single(o => o.Operation == MathOperation.Invert);
        vm.IsScopeMathEnabled = true;
        await vm.RefreshScopeMathAsync();

        var result = Assert.IsType<MathChannelResult>(vm.ScopeMathResult);
        Assert.True(result.IsOk, result.Error);
        var a = vm.LastScopeCapture!.Channels[1];
        Assert.Equal(-a[10], result.Values[10], 9);
        Assert.NotEmpty(vm.ScopeMathRawPoints);
        Assert.StartsWith("M = −A:", vm.ScopeMathStatusText);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.ScopeMathTraceVisibility);
    }

    [Fact]
    public async Task Rpm_WaitsForPulsesPerRev_ThenComputes()
    {
        await using var vm = await CapturedViewModelAsync();
        vm.SelectedScopeMathOperation = vm.ScopeMathOperations.Single(o => o.Operation == MathOperation.RpmFromFrequency);
        vm.IsScopeMathEnabled = true;
        await vm.RefreshScopeMathAsync();
        Assert.Contains("pulses per revolution", vm.ScopeMathStatusText);
        Assert.Empty(vm.ScopeMathRawPoints);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.ScopeMathParameterVisibility);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.ScopeMathSourceBVisibility);

        vm.ScopeMathParameter = 1;
        await vm.RefreshScopeMathAsync();
        Assert.True(vm.ScopeMathResult!.IsOk, vm.ScopeMathResult.Error);
        Assert.Contains("rpm", vm.ScopeMathStatusText);
    }

    [Fact]
    public async Task Difference_WithChannelBOff_SaysSo()
    {
        await using var vm = await CapturedViewModelAsync();
        vm.SelectedScopeMathOperation = vm.ScopeMathOperations.Single(o => o.Operation == MathOperation.Difference);
        vm.ScopeMathSourceB = 2;
        vm.IsScopeMathEnabled = true;
        await vm.RefreshScopeMathAsync();
        if (vm.LastScopeCapture!.Channels.ContainsKey(2))
            Assert.True(vm.ScopeMathResult!.IsOk);
        else
            Assert.Contains("Channel B is not in this capture", vm.ScopeMathStatusText);
    }
}
