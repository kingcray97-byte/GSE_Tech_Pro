using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1h: channel labels on the scope screen (simulator).</summary>
public sealed class ScopeChannelLabelsTests
{
    [Fact]
    public async Task Labels_ShowInLegend_AreSavedWithCapture_AndRestoredOnLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-labels-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        Assert.Equal("", vm.ScopeChannelLabelLegend);

        var chA = vm.ScopeChannel1;
        var chB = vm.ScopeChannel2;
        chA.Label = "  Crank   sensor ";
        chB.Label = "Cam sensor";
        Assert.Equal("A Crank sensor", chA.TileTitle);

        // Channel B is off: only enabled channels appear in the legend.
        Assert.False(chB.IsEnabled);
        Assert.Equal("A Crank sensor", vm.ScopeChannelLabelLegend);
        chB.IsEnabled = true;
        Assert.Equal("A Crank sensor · B Cam sensor", vm.ScopeChannelLabelLegend);

        await vm.StartScopeCaptureAsync();
        Assert.NotNull(vm.LastScopeCapture);
        var saved = await vm.SaveScopeCaptureToWorkOrderAsync();
        Assert.NotNull(saved);
        var configs = ScopeJson.DeserializeChannelConfigs(saved!.ChannelConfigJson);
        Assert.Equal("Crank sensor", configs.Single(c => c.Channel == 1).Label);
        Assert.Equal("Cam sensor", configs.Single(c => c.Channel == 2).Label);

        var row = Assert.Single(vm.WorkOrderWaveforms);
        Assert.Contains("CH1 Crank sensor", row.ChannelSummary);

        // Clear, then opening the saved capture brings its labels back.
        vm.ClearScopeChannelLabels();
        Assert.Equal("", vm.ScopeChannelLabelLegend);
        Assert.Equal("A", chA.TileTitle);

        await vm.LoadSavedWaveformAsync(row);
        Assert.Equal("Crank sensor", chA.CleanLabel);
        Assert.Equal("A Crank sensor · B Cam sensor", vm.ScopeChannelLabelLegend);
    }
}
