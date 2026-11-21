using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1: CAN serial decoding on the scope screen (simulator).</summary>
public sealed class ScopeSerialTests
{
    [Fact]
    public async Task SerialDecoding_NeedsABitRate_SetsUpForCan_AndLabelsJ1939()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-serial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        vm.IsScopeSerialEnabled = true;
        await vm.RefreshScopeSerialAsync();
        Assert.Contains("Choose the bus bit rate", vm.ScopeSerialStatusText);
        Assert.False(vm.SetUpScopeForCanCommand.CanExecute(null));

        vm.SelectedScopeSerialBitRate = vm.ScopeSerialBitRates.Single(r => r.BitRate == 250_000);
        Assert.True(vm.SetUpScopeForCanCommand.CanExecute(null));
        vm.SetUpScopeForCan();
        Assert.Equal(5_000_000, vm.ScopeSampleRateHz);
        Assert.Equal(0.0005, vm.ScopeTimebaseSecondsPerDivision, 9);
        Assert.True(vm.ScopeChannels.Single(c => c.Channel == 2).IsEnabled);

        await vm.StartScopeCaptureAsync();
        var r = Assert.IsType<CanDecodeResult>(vm.ScopeSerialResult);
        Assert.True(r.IsOk, r.Error);
        Assert.Contains("(simulated)", vm.ScopeSerialStatusText);
        var eec1 = vm.ScopeSerialRows.First(row => row.Identifier == "0CF00400");
        Assert.StartsWith("EEC1 PGN 61444 · SA 00 · P3", eec1.J1939);
        Assert.Equal("OK", eec1.Status);
        Assert.Equal("F0 7D 7D E0 2E 00 F0 7D", eec1.Data);

        // Problems only: just the frames cut off by the end of the capture remain.
        vm.ScopeSerialErrorsOnly = true;
        Assert.All(vm.ScopeSerialRows, row => Assert.True(row.IsError));

        // Wrong bit rate gives errors, not made-up frames.
        vm.SelectedScopeSerialBitRate = vm.ScopeSerialBitRates.Single(r => r.BitRate == 500_000);
        await vm.RefreshScopeSerialAsync();
        Assert.DoesNotContain(vm.ScopeSerialResult!.Frames, f => f.IsOk && f.Identifier == 0x0CF00400);
    }
}
