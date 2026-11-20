using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>
/// Bench-scope layout for the Scope screen: channel range steppers, timebase and trigger
/// steppers, graticule axis labels and the bottom tool panel.
/// </summary>
public sealed class ScopeLayoutTests
{
    private static async Task<MainShellViewModel> CreateViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-scope-layout-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();
        return new MainShellViewModel(dataServices, new DiagnosticAdapterFactory());
    }

    [Fact]
    public void ChannelRange_StepsThroughStandardRanges_AndTurnsOffBelowSmallest()
    {
        var ch = new ScopeChannelSetupViewModel(1, isEnabled: true) { VoltageRangeVolts = 5.0 };

        ch.RangeUp();
        Assert.Equal(10.0, ch.VoltageRangeVolts);
        Assert.Equal("±10 V", ch.RangeDisplay);

        ch.VoltageRangeVolts = ScopeChannelSetupViewModel.StandardVoltageRanges[0];
        ch.RangeDown();
        Assert.False(ch.IsEnabled);
        Assert.Equal("Off", ch.RangeDisplay);

        ch.RangeUp();
        Assert.True(ch.IsEnabled);
    }

    [Fact]
    public void ChannelTile_ShowsClampRangeInAmps_AndCouplingDetail()
    {
        var ch = new ScopeChannelSetupViewModel(2, isEnabled: true)
        {
            VoltageRangeVolts = 0.5,
            CurrentScaleAmpsPerVolt = 20.0,
        };

        Assert.Equal("B", ch.ChannelLetter);
        Assert.Equal("±10 A", ch.RangeDisplay);
        Assert.Equal("DC 20 A/V", ch.DetailDisplay);

        ch.ToggleCoupling();
        Assert.Equal(ScopeCoupling.Ac, ch.Coupling);
    }

    [Fact]
    public async Task TimebaseStepper_MovesToNeighbouringStandardOption()
    {
        await using var vm = await CreateViewModelAsync();
        vm.ScopeTimebaseSecondsPerDivision = 0.005;

        vm.ScopeTimebaseStepUp();
        Assert.Equal(0.010, vm.ScopeTimebaseSecondsPerDivision, 9);

        vm.ScopeTimebaseStepDown();
        vm.ScopeTimebaseStepDown();
        Assert.Equal(0.002, vm.ScopeTimebaseSecondsPerDivision, 9);
        Assert.Equal("2 ms/div", vm.ScopeTimebaseShortDisplay);
    }

    [Fact]
    public async Task TriggerStepper_StepsByATenthOfTheTriggerChannelRange()
    {
        await using var vm = await CreateViewModelAsync();
        vm.ScopeTriggerChannel = 1;
        vm.ScopeChannel1.VoltageRangeVolts = 20.0;
        vm.ScopeTriggerLevelVolts = 2.0;

        vm.ScopeTriggerLevelUp();
        Assert.Equal(4.0, vm.ScopeTriggerLevelVolts, 6);

        vm.ScopeTriggerLevelDown();
        vm.ScopeTriggerLevelDown();
        Assert.Equal(0.0, vm.ScopeTriggerLevelVolts, 6);
    }

    [Fact]
    public void VoltageAxis_HasElevenLabelsFromPlusToMinusFullScale()
    {
        var labels = MainShellViewModel.BuildVoltageAxis(100.0);

        Assert.Equal(11, labels.Count);
        Assert.Equal("100.0", labels[0]);
        Assert.Equal("0.0", labels[5]);
        Assert.Equal("-100.0", labels[10]);
    }

    [Fact]
    public void TimeAxis_UsesMillisecondsForMillisecondDivisions()
    {
        var labels = MainShellViewModel.BuildTimeAxis(0.0, 0.1); // 10 ms/div

        Assert.Equal(11, labels.Count);
        Assert.Equal("0.0", labels[0]);
        Assert.Equal("50.0", labels[5]);
        Assert.Equal("100.0", labels[10]);
    }

    [Fact]
    public async Task AxisLabels_FollowChannelARange()
    {
        await using var vm = await CreateViewModelAsync();
        vm.ScopeChannel1.IsEnabled = true;
        vm.ScopeChannel1.VoltageRangeVolts = 20.0;

        Assert.Equal("20.0", vm.ScopeLeftAxisLabels[0]);
        Assert.Equal(Visibility.Visible, vm.ScopeLeftAxisVisibility);
    }

    [Fact]
    public async Task BottomPanel_TogglesOpenAndClosed_AndRulersShowRulerLines()
    {
        await using var vm = await CreateViewModelAsync();
        Assert.Equal(MainShellViewModel.ScopePanelNone, vm.ScopeBottomPanel);

        vm.ToggleScopePanel(MainShellViewModel.ScopePanelRulers);
        Assert.Equal(Visibility.Visible, vm.ScopeRulersPanelVisibility);
        Assert.Equal(Visibility.Visible, vm.ScopeRulerLinesVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeMeasurementsPanelVisibility);

        vm.ToggleScopePanel(MainShellViewModel.ScopePanelRulers);
        Assert.Equal(Visibility.Collapsed, vm.ScopeBottomPanelVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ScopeRulerLinesVisibility);
    }

    [Fact]
    public async Task GuidedTestsPage_OpensTheGuidedPanel()
    {
        await using var vm = await CreateViewModelAsync();
        var guided = vm.SidebarSections.Single(s => s.Key == "Scope").Children.Single(c => c.Key == "GuidedTests");

        await vm.SelectSidebarItemAsync(guided);

        Assert.Equal(MainShellViewModel.ScopePanelGuided, vm.ScopeBottomPanel);
    }

    [Fact]
    public async Task RunStopButtons_FollowCaptureState()
    {
        await using var vm = await CreateViewModelAsync();
        Assert.Equal(Visibility.Visible, vm.ScopeRunButtonVisibility);

        vm.IsScopeCapturing = true;
        Assert.Equal(Visibility.Collapsed, vm.ScopeRunButtonVisibility);
        Assert.Equal(Visibility.Visible, vm.ScopeStopButtonVisibility);
    }
}
