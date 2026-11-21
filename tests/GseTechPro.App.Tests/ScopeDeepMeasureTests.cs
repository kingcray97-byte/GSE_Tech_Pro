using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1: DeepMeasure on the scope screen (simulator).</summary>
public sealed class ScopeDeepMeasureTests
{
    [Fact]
    public async Task DeepMeasure_RunsOnlyWhenOn_MeasuresEachCycle_AndSorts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-deep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        await vm.StartScopeCaptureAsync();
        Assert.Null(vm.ScopeDeepMeasureResult); // off by default: nothing computed per capture
        Assert.Equal("DeepMeasure off.", vm.ScopeDeepMeasureStatusText);

        vm.IsScopeDeepMeasureEnabled = true;
        await vm.RefreshScopeDeepMeasureAsync();
        var r = Assert.IsType<DeepMeasureResult>(vm.ScopeDeepMeasureResult);
        Assert.True(r.IsOk, r.Error);
        Assert.True(r.Cycles.Count >= 2, vm.ScopeDeepMeasureStatusText);
        Assert.Equal(r.Cycles.Count, vm.ScopeDeepMeasureRows.Count);
        Assert.NotEmpty(vm.ScopeDeepMeasureStatRows);
        Assert.Contains("(simulated)", vm.ScopeDeepMeasureStatusText);

        // A new capture is measured automatically while on.
        await vm.StartScopeCaptureAsync();
        Assert.NotSame(r, vm.ScopeDeepMeasureResult);

        // Sort by period, highest first: rows follow the value, not the cycle number.
        vm.SelectedScopeDeepMeasureSort = vm.ScopeDeepMeasureSortOptions.Single(o => o.Kind == CycleMeasureKind.Period);
        vm.IsScopeDeepMeasureSortDescending = true;
        var longest = vm.ScopeDeepMeasureResult!.StatisticsFor(CycleMeasureKind.Period)!.MaximumCycle;
        Assert.Equal(longest, vm.ScopeDeepMeasureRows[0].Number);

        vm.IsScopeDeepMeasureEnabled = false;
        await vm.RefreshScopeDeepMeasureAsync();
        Assert.Empty(vm.ScopeDeepMeasureRows);
    }

    [Theory]
    [InlineData(0.0123, CycleMeasureKind.Period, "12.30 ms")]
    [InlineData(0.0000008, CycleMeasureKind.RiseTime, "800.0 ns")]
    [InlineData(1500.0, CycleMeasureKind.Frequency, "1.500 kHz")]
    [InlineData(25.0, CycleMeasureKind.DutyCycle, "25.0 %")]
    [InlineData(-2.75, CycleMeasureKind.Minimum, "-2.750 V")]
    [InlineData(null, CycleMeasureKind.FallTime, "—")]
    public void FormatDeep_UsesSiPrefixes(double? value, CycleMeasureKind kind, string expected) =>
        Assert.Equal(expected, MainShellViewModel.FormatDeep(value, kind));
}
