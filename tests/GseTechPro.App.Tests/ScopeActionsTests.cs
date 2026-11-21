using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1: repeat run and mask-fail actions (simulator).</summary>
public sealed class ScopeActionsTests
{
    private static async Task<MainShellViewModel> CapturedViewModelAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-actions-{Guid.NewGuid():N}");
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

    /// <summary>A mask the simulated signal can never meet (envelope 10 V below it), to force a fail.</summary>
    private static WaveformMask ImpossibleMask(MainShellViewModel vm)
    {
        var cap = vm.LastScopeCapture!;
        var shifted = cap.Channels[1].Select(v => v - 10).ToArray();
        return WaveformMask.Create(shifted, cap.SampleRateHz, 1, 0.1, 0);
    }

    [Fact]
    public async Task RepeatRun_StopsOnFirstMaskFail_AfterBeepingAndSaving()
    {
        await using var vm = await CapturedViewModelAsync();
        int beeps = 0;
        vm.ScopeAlarmSound = () => beeps++;
        vm.ScopeMask = ImpossibleMask(vm);
        vm.IsScopeRepeatEnabled = true;
        vm.ScopeActionStopOnFail = true;
        vm.ScopeActionBeepOnFail = true;
        vm.ScopeActionSaveOnFail = true;

        var run = vm.RunScopeAsync();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(run, finished);
        await run;

        Assert.False(vm.IsScopeRepeatRunning);
        Assert.Equal(1, vm.LastScopeRunCaptureCount);
        Assert.Equal(1, beeps);
        Assert.Equal("Pass 0 · Fail 1", vm.ScopeMaskCountText);
        Assert.Contains("saved to the work order (1/20", vm.ScopeActionsStatusText);
        Assert.Contains("stopped the repeat run", vm.ScopeActionsStatusText);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.ScopeRunButtonVisibility);
    }

    [Fact]
    public async Task RepeatRun_KeepsCapturingWhilePassing_UntilStop()
    {
        await using var vm = await CapturedViewModelAsync();
        vm.ScopeMaskVoltageTolerance = 0.5;
        await vm.CreateScopeMaskAsync();
        vm.IsScopeRepeatEnabled = true;

        var run = vm.RunScopeAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (vm.ScopeMaskPassCount < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(vm.IsScopeRepeatRunning);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.ScopeStopButtonVisibility);

        await vm.StopScopeCaptureAsync();
        await run;
        Assert.False(vm.IsScopeRepeatRunning);
        Assert.True(vm.LastScopeRunCaptureCount >= 3);
        Assert.Equal(0, vm.ScopeMaskFailCount);
        Assert.Equal("No actions taken.", vm.ScopeActionsStatusText);
    }

    [Fact]
    public async Task SingleRun_FailWithNoActionsOn_SaysSo()
    {
        await using var vm = await CapturedViewModelAsync();
        vm.ScopeAlarmSound = () => throw new InvalidOperationException("must not beep");
        vm.ScopeMask = ImpossibleMask(vm);
        vm.ScopeActionStopOnFail = false;

        await vm.RunScopeAsync();
        Assert.Equal(1, vm.LastScopeRunCaptureCount);
        Assert.Contains("no actions are on", vm.ScopeActionsStatusText);
    }
}
