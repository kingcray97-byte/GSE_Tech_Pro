using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1: scope waveform buffer in the view model (simulator).</summary>
public sealed class ScopeBufferTests
{
    [Fact]
    public async Task EachCapture_IsBuffered_AndSteppingShowsTheEarlierCapture()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-buffer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        Assert.Equal("— / 64", vm.ScopeBufferPositionText);
        Assert.False(vm.ScopeBufferPreviousCommand.CanExecute(null));

        var captures = new List<object>();
        for (var i = 0; i < 3; i++)
        {
            await vm.StartScopeCaptureAsync();
            Assert.NotNull(vm.LastScopeCapture);
            captures.Add(vm.LastScopeCapture!);
        }

        Assert.Equal(3, vm.ScopeBufferCount);
        Assert.Equal("3 / 3", vm.ScopeBufferPositionText);
        Assert.True(vm.ScopeBufferPreviousCommand.CanExecute(null));
        Assert.False(vm.ScopeBufferNextCommand.CanExecute(null));

        await vm.ScopeBufferPreviousCommand.ExecuteAsync(null);
        Assert.Equal("2 / 3", vm.ScopeBufferPositionText);
        Assert.Same(captures[1], vm.LastScopeCapture);

        await vm.ScopeBufferFirstCommand.ExecuteAsync(null);
        Assert.Same(captures[0], vm.LastScopeCapture);
        Assert.Contains("older capture on screen", vm.ScopeBufferDetailText);

        // Run again: the new capture goes to the end and is shown.
        await vm.StartScopeCaptureAsync();
        Assert.Equal("4 / 4", vm.ScopeBufferPositionText);
        Assert.NotSame(captures[0], vm.LastScopeCapture);

        vm.ClearScopeBufferCommand.Execute(null);
        Assert.Equal("— / 64", vm.ScopeBufferPositionText);
        Assert.NotNull(vm.LastScopeCapture); // the trace on screen stays until the next Run
    }
}
