using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.4: a pressure transducer channel on the Scope screen.</summary>
public sealed class ScopePressureTests
{
    [Fact]
    public async Task PressureChannel_ScalesAxisReadoutAndRulers_OnlyOnceFullyEntered()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-pressure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        vm.SelectedSimulatorPreset = SimulatedSignalPreset.PressureTransducer;
        vm.ApplyCaptureRequestToUi(SimulatedScopePresets.GetCanonicalRequest(SimulatedSignalPreset.PressureTransducer));
        var a = vm.ScopeChannel1;

        // A clamp and a transducer never share a channel.
        a.CurrentScaleAmpsPerVolt = 10.0;
        a.IsPressureEnabled = true;
        Assert.Equal(0.0, a.CurrentScaleAmpsPerVolt);

        // Example numbers for the test only, not a spec of any part. Nothing scales until all are in.
        a.PressureUnit = "psi";
        a.PressureZeroOutputText = "0.5";
        a.PressureZeroText = "0";
        a.PressureFullOutputText = "4.5";
        a.PressureFullText = "5000";
        Assert.False(a.HasPressure);
        Assert.Contains("make/model", a.PressureStatusText);
        Assert.Equal("V", vm.ScopeLeftAxisUnit);

        a.PressureSourceText = "Test transducer datasheet";
        Assert.True(a.HasPressure);
        Assert.Equal("0–5000 psi", a.RangeDisplay);
        Assert.Equal("psi", vm.ScopeLeftAxisUnit);
        Assert.Equal("5625.0", vm.ScopeLeftAxisLabels[0]);    // +5 V
        Assert.Equal("-6875.0", vm.ScopeLeftAxisLabels[^1]);  // −5 V

        await vm.RunScopeAsync();
        Assert.NotNull(vm.LastScopeCapture);
        Assert.Equal(Visibility.Visible, vm.ScopePressureReadoutVisibility);
        Assert.StartsWith("A: min ", vm.ScopePressureReadoutText);
        Assert.EndsWith("psi (simulated)", vm.ScopePressureReadoutText);
        Assert.Contains(" psi)", vm.ScopeCursorALabel);

        // Saved with the capture's channel config, restored by ApplyConfig; a setup without it turns it off.
        var cfg = a.ToChannelConfig();
        Assert.Equal(5000.0, cfg.Pressure!.FullScalePressure);
        Assert.Equal(0.0, cfg.CurrentScaleAmpsPerVolt);
        a.ApplyConfig(new ScopeChannelConfig { Channel = 1, VoltageRangeVolts = 5 });
        Assert.False(a.IsPressureEnabled);
        Assert.Equal(Visibility.Collapsed, vm.ScopePressureReadoutVisibility);
        a.ApplyConfig(cfg);
        Assert.True(a.HasPressure);
        Assert.Equal("Test transducer datasheet", a.PressureSourceText);

        // Turning the clamp on turns the transducer off.
        a.CurrentScaleAmpsPerVolt = 10.0;
        Assert.False(a.IsPressureEnabled);
        Assert.Equal("A", vm.ScopeLeftAxisUnit);
    }
}
