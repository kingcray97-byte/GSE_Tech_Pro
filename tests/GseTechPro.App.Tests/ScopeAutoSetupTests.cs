using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1: Auto setup on the scope screen (simulator).</summary>
public sealed class ScopeAutoSetupTests
{
    private static async Task<MainShellViewModel> ViewModelAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-auto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");
        return vm;
    }

    [Fact]
    public async Task AutoSetup_OnCrankSensor_SetsRangeTimebaseTrigger_AndRuns()
    {
        await using var vm = await ViewModelAsync();
        vm.SelectedSimulatorPreset = SimulatedSignalPreset.CrankSensor;
        vm.ScopeChannel1.IsEnabled = true;
        vm.ScopeChannel1.VoltageRangeVolts = 100;
        vm.ScopeTimebaseSecondsPerDivision = 0.5;
        vm.ScopeTriggerMode = ScopeTriggerMode.None;

        await vm.RunScopeAutoSetupAsync();

        var r = Assert.IsType<AutoSetupResult>(vm.LastScopeAutoSetup);
        var a = r.Channels.Single(c => c.Channel == 1);
        Assert.NotNull(a.FrequencyHz);
        // The simulated crank signal peaks below 4.5 V, so the 5 V range fits; nothing asserted about what it "should" be.
        Assert.Equal(a.RangeVolts, vm.ScopeChannel1.VoltageRangeVolts);
        Assert.True(vm.ScopeChannel1.VoltageRangeVolts < 100);
        Assert.Equal(r.TimebaseSecondsPerDivision, vm.ScopeTimebaseSecondsPerDivision);
        Assert.True(vm.ScopeTimebaseSecondsPerDivision * 10 * a.FrequencyHz.Value >= AutoSetup.TargetCyclesOnScreen);
        Assert.Equal(ScopeTriggerMode.Rising, vm.ScopeTriggerMode);
        Assert.Equal(1, vm.ScopeTriggerChannel);
        Assert.InRange(vm.ScopeTriggerLevelVolts, a.MinVolts, a.MaxVolts);

        // Auto setup ends with a normal Run at the new settings, on the same simulated signal.
        var cap = Assert.IsType<ScopeCapture>(vm.LastScopeCapture);
        Assert.Equal(vm.ScopeTimebaseSecondsPerDivision, cap.TimebaseSecondsPerDivision);
        Assert.StartsWith("Auto setup:", vm.ScopeStatusMessage);
        var sim = Assert.IsType<SimulatedScopeDevice>(vm.ActiveScopeDevice);
        Assert.Equal(SimulatedSignalPreset.CrankSensor, sim.CurrentPreset);
    }
}
