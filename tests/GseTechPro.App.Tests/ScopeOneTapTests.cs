using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.3a: one-tap battery / cranking test on the Scope screen.</summary>
public sealed class ScopeOneTapTests
{
    private static async Task<MainShellViewModel> NewViewModelAsync(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-onetap-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");
        await vm.SelectNavRailItem("OneTapTests");
        return vm;
    }

    [Fact]
    public async Task BatteryCranking_WarningsFirst_ThenCapturesAndFillsTheTable()
    {
        await using var vm = await NewViewModelAsync("battery");
        Assert.Equal(Visibility.Visible, vm.ScopeOneTapPanelVisibility);
        Assert.Equal(Visibility.Visible, vm.ScopeViewVisibility);

        // Start before reading the warnings: they are shown in full and nothing is captured.
        vm.ScopeCrankingClampText = "1000";
        await vm.StartScopeOneTapTestAsync();
        Assert.Equal(MainShellViewModel.OneTapBatteryCrankingId, vm.SelectedScopeOneTapTest?.Id);
        Assert.True(vm.HasScopeSafetyWarnings);
        Assert.Contains(vm.ScopeSafetyWarnings, w => w.Id == "sw-lockout-tagout");
        Assert.Null(vm.LastScopeCapture);
        Assert.Equal(MainShellViewModel.ScopeSafetyBlockedMessage, vm.ScopeOneTapStatusText);

        vm.AcknowledgeScopeSafetyWarnings();
        Assert.StartsWith($"{vm.ScopeSafetyWarnings.Count} safety warnings for 'Battery / cranking'", vm.ScopeSafetyAcknowledgedText);

        // Bad input: plain message, no capture.
        vm.ScopeCrankingRatedCcaText = "lots";
        await vm.StartScopeOneTapTestAsync();
        Assert.Null(vm.LastScopeCapture);
        Assert.Contains("Battery rating", vm.ScopeOneTapStatusText);

        vm.ScopeCrankingRatedCcaText = "800";
        vm.ScopeCrankingMinimumVoltsText = "9";
        vm.ScopeCrankingMinimumSource = "Test manual p.1";
        await vm.StartScopeOneTapTestAsync();

        var cap = vm.LastScopeCapture;
        Assert.NotNull(cap);
        Assert.True(cap!.IsSimulated);
        Assert.Equal(10_000, cap.SampleRateHz);
        Assert.True(cap.Channels.ContainsKey(1) && cap.Channels.ContainsKey(2));
        Assert.Equal(1000.0, vm.ScopeChannels[1].CurrentScaleAmpsPerVolt);
        Assert.Equal("Battery voltage", vm.ScopeChannels[0].Label);

        Assert.Contains("Crank found at 0.80 s", vm.ScopeOneTapStatusText);
        Assert.EndsWith("(simulated)", vm.ScopeOneTapStatusText);
        Assert.Equal(Visibility.Collapsed, vm.ScopeOneTapNoResultsVisibility);
        var lowest = vm.ScopeOneTapResults.Single(r => r.Parameter == "Lowest cranking voltage");
        Assert.Equal("PASS", lowest.VerdictText);
        Assert.Contains(vm.ScopeOneTapResults, r => r.Parameter == "Peak current / battery rating" && r.VerdictText == "");

        // Analyse again on the capture on screen with a different limit.
        vm.ScopeCrankingMinimumVoltsText = "11";
        await vm.AnalyseScopeOneTapTestAsync();
        Assert.Same(cap, vm.LastScopeCapture);
        Assert.Equal("FAIL", vm.ScopeOneTapResults.Single(r => r.Parameter == "Lowest cranking voltage").VerdictText);
    }

    [Fact]
    public async Task RelativeCompression_NeedsEngineInputs_ThenFindsTheWeakPosition()
    {
        await using var vm = await NewViewModelAsync("compression");
        vm.SelectedScopeOneTapTest = vm.ScopeOneTapTests.Single(t => t.Id == MainShellViewModel.OneTapRelativeCompressionId);
        Assert.Equal(Visibility.Visible, vm.ScopeOneTapCompressionInputsVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeOneTapBatteryInputsVisibility);

        await vm.StartScopeOneTapTestAsync();
        Assert.Equal(MainShellViewModel.ScopeSafetyBlockedMessage, vm.ScopeOneTapStatusText);
        vm.AcknowledgeScopeSafetyWarnings();
        Assert.Contains("'Relative compression'", vm.ScopeSafetyAcknowledgedText);

        // Cylinders and engine cycle have no defaults: nothing is captured until they are entered.
        await vm.StartScopeOneTapTestAsync();
        Assert.Null(vm.LastScopeCapture);
        Assert.Contains("number of cylinders", vm.ScopeOneTapStatusText);
        vm.ScopeCompressionCylindersText = "4";
        await vm.StartScopeOneTapTestAsync();
        Assert.Null(vm.LastScopeCapture);
        Assert.Contains("4-stroke or 2-stroke", vm.ScopeOneTapStatusText);

        vm.ScopeCompressionEngineCycle = "4-stroke";
        vm.ScopeCrankingClampText = "1000";
        await vm.StartScopeOneTapTestAsync();
        Assert.NotNull(vm.LastScopeCapture);
        Assert.Contains("weakest is position", vm.ScopeOneTapStatusText);
        Assert.EndsWith("(simulated)", vm.ScopeOneTapStatusText);
        var positions = vm.ScopeOneTapResults.Where(r => r.Parameter.StartsWith("Position ")).ToList();
        Assert.Equal(4, positions.Count);
        Assert.Single(positions, r => double.Parse(r.Value.TrimEnd(' ', '%'), System.Globalization.CultureInfo.InvariantCulture) < 80);
        Assert.Contains(vm.ScopeOneTapResults, r => r.Parameter == "Cranking speed" && r.Value == "200 rpm");
        Assert.Equal("", vm.ScopeOneTapResults.Single(r => r.Parameter.StartsWith("Spread")).VerdictText);

        // Same hookup, battery test analysed on this capture: the engine never started.
        vm.SelectedScopeOneTapTest = vm.ScopeOneTapTests[0];
        await vm.AnalyseScopeOneTapTestAsync();
        Assert.Contains(vm.ScopeOneTapResults, r => r.Parameter == "Cranking time" && r.Comment.Contains("still going"));
    }

    [Fact]
    public async Task CylinderBalance_NeedsWheelInputs_ThenFindsTheWeakPosition()
    {
        await using var vm = await NewViewModelAsync("balance");
        Assert.All(vm.ScopeOneTapTests, t => Assert.True(t.IsAvailable));
        vm.SelectedScopeOneTapTest = vm.ScopeOneTapTests.Single(t => t.Id == MainShellViewModel.OneTapCylinderBalanceId);
        Assert.Equal(Visibility.Visible, vm.ScopeOneTapBalanceInputsVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeOneTapCompressionInputsVisibility);
        await vm.StartScopeOneTapTestAsync();
        vm.AcknowledgeScopeSafetyWarnings();

        vm.ScopeCompressionCylindersText = "4";
        vm.ScopeCompressionEngineCycle = "4-stroke";
        await vm.StartScopeOneTapTestAsync();
        Assert.Null(vm.LastScopeCapture);
        Assert.Contains("tooth count", vm.ScopeOneTapStatusText);
        vm.ScopeBalanceWheelTeethText = "60";
        await vm.StartScopeOneTapTestAsync();
        Assert.Null(vm.LastScopeCapture);
        Assert.Contains("missing teeth", vm.ScopeOneTapStatusText);

        vm.ScopeBalanceMissingTeethText = "2";
        vm.ScopeBalanceMaxSpreadText = "20";
        vm.ScopeBalanceMaxSpreadSource = "Test manual p.3";
        await vm.StartScopeOneTapTestAsync();
        var cap = vm.LastScopeCapture;
        Assert.NotNull(cap);
        Assert.Equal(100_000, cap!.SampleRateHz);
        Assert.Equal("Crank sensor", vm.ScopeChannels[0].Label);
        Assert.False(vm.ScopeChannels[1].IsEnabled);
        Assert.Contains("weakest is position", vm.ScopeOneTapStatusText);
        Assert.Contains(vm.ScopeOneTapResults, r => r.Parameter == "Engine speed" && r.Value == "800 rpm");
        Assert.Equal("FAIL", vm.ScopeOneTapResults.Single(r => r.Parameter.StartsWith("Spread")).VerdictText);
    }

    [Fact]
    public async Task AGuidedTestBringsItsOwnWarnings_ThenStartAsksForTheOneTapWarningsAgain()
    {
        await using var vm = await NewViewModelAsync("guided");

        // Battery test acknowledged, then a guided test: Start asks for the battery warnings again.
        vm.SelectedScopeOneTapTest = vm.ScopeOneTapTests[0];
        await vm.StartScopeOneTapTestAsync();
        vm.AcknowledgeScopeSafetyWarnings();
        await vm.SelectGuidedScopeTestAsync("gt-crank-sensor");
        Assert.False(vm.AreScopeSafetyWarningsAcknowledged);

        await vm.StartScopeOneTapTestAsync();
        Assert.Null(vm.SelectedGuidedTest);
        Assert.Null(vm.LastScopeCapture);
        Assert.Equal(MainShellViewModel.ScopeSafetyBlockedMessage, vm.ScopeOneTapStatusText);
    }
}
