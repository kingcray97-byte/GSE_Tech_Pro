using GseTechPro.App.ViewModels;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Can;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10C.2: CAN Capture &gt; Bus Monitor screen.</summary>
public sealed class CanCaptureTests
{
    private static async Task<(MainShellViewModel Vm, DataServices Data)> CreateViewModelAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-can-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory())
        {
            // No ECOM driver on CI: the simulator plus an "unavailable" ECOM entry.
            CanFactory = new CanBusMonitorFactory(() => "ECOM driver not installed (test)", Array.Empty<uint>),
        };
        return (vm, data);
    }

    private static async Task PumpUntilAsync(MainShellViewModel vm, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
            vm.ProcessPendingCanFrames();
        }
        Assert.True(done(), "Condition not met in time.");
    }

    [Fact]
    public async Task CanCapture_OpensTheBusMonitor_InTheModuleFrame()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _ = vm;

        vm.SelectToolbarAction("CanCapture");

        Assert.True(vm.IsCanMonitorViewVisible);
        Assert.Equal(Visibility.Visible, vm.CanMonitorViewVisibility);
        Assert.False(vm.IsDiagnosticsViewVisible);
        Assert.True(vm.IsModuleFrameVisible);
        Assert.Equal("No CAN Connection", vm.ModuleConnectionText);
        Assert.Equal(Visibility.Collapsed, vm.DeviceCommandBarVisibility); // the CAN page has its own adapter picker
        Assert.Equal(CanBusMonitorFactory.SimulatorKind, vm.SelectedCanDevice?.Kind);
        Assert.Contains(vm.CanDevices, d => d.Kind == CanBusMonitorFactory.EcomKind && d.UnavailableReason is not null);
    }

    [Fact]
    public async Task Simulator_FillsTheTable_WithJ1939Rows()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("CanCapture");

        await vm.StartCanMonitorAsync();
        Assert.True(vm.IsCanMonitoring);
        Assert.Contains("SIMULATED", vm.ModuleConnectionText);
        Assert.Equal(vm.CanStatusText, vm.StatusBannerMessage);
        Assert.Equal(Visibility.Visible, vm.CanSimulatedBadgeVisibility);

        await PumpUntilAsync(vm, () => vm.CanRows.Count >= SimulatedCanBusMonitor.Schedule.Count);

        var eec1 = Assert.Single(vm.CanRows, r => r.Entry.PgnLabel == "EEC1");
        Assert.Equal("SA 00", eec1.SourceText);
        Assert.Contains("rpm", eec1.DecodedText);
        Assert.Contains(vm.CanRows, r => r.FrameKind == "11-bit" && r.IdText == "123");
        Assert.Equal("11-bit", vm.CanRows[0].FrameKind); // 11-bit ids sort first

        await vm.StopCanMonitorAsync();
        Assert.False(vm.IsCanMonitoring);
        Assert.Equal("No CAN Connection", vm.ModuleConnectionText);
    }

    [Fact]
    public async Task Filter_NarrowsTheRows_AndClearingItShowsAll()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("CanCapture");
        await vm.StartCanMonitorAsync();
        await PumpUntilAsync(vm, () => vm.CanRows.Count >= SimulatedCanBusMonitor.Schedule.Count);
        await vm.StopCanMonitorAsync();

        vm.CanFilterText = "SA 03";
        var only = Assert.Single(vm.CanRows);
        Assert.Equal("SA 03", only.SourceText);

        vm.CanFilterText = "";
        Assert.Equal(SimulatedCanBusMonitor.Schedule.Count, vm.CanRows.Count);
    }

    [Fact]
    public async Task UnavailableEcom_SaysWhy_AndDoesNotStart()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("CanCapture");

        vm.SelectedCanDevice = vm.CanDevices.Single(d => d.Kind == CanBusMonitorFactory.EcomKind);
        await vm.StartCanMonitorAsync();

        Assert.False(vm.IsCanMonitoring);
        Assert.Contains("not installed", vm.CanStatusText);
        Assert.Contains("not installed", vm.CanSelectedDeviceNote);
    }

    [Fact]
    public async Task Record_ExportAsc_AndReplay()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("CanCapture");

        await vm.StartCanMonitorAsync();
        vm.ToggleCanRecording();
        Assert.True(vm.IsCanRecording);
        await PumpUntilAsync(vm, () => vm.CanRecordedFrameCount >= 50);
        vm.ToggleCanRecording();
        var recorded = vm.CanRecordedFrameCount;

        var asc = await vm.ExportCanCaptureAsync("asc");
        var csv = await vm.ExportCanCaptureAsync("csv");
        Assert.NotNull(asc);
        Assert.NotNull(csv);
        Assert.EndsWith("-SIM.asc", asc);
        Assert.Equal(recorded + 1, File.ReadAllLines(csv!).Length);

        vm.RefreshSavedCanCaptures();
        Assert.Equal(Path.GetFileName(asc), vm.SelectedSavedCanCapture);

        await vm.StartCanReplayAsync();
        Assert.True(vm.IsCanMonitoring);
        Assert.StartsWith("Replay:", vm.CanMonitorName);
        Assert.True(vm.IsCanSimulated);
        await PumpUntilAsync(vm, () => vm.CanTotalFrames >= recorded);
        Assert.Equal(recorded, vm.CanTotalFrames);
    }

    [Fact]
    public async Task Export_WithNothingRecorded_SaysSo()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _ = vm;

        Assert.Null(await vm.ExportCanCaptureAsync("asc"));
        Assert.Contains("Nothing recorded", vm.CanStatusText);
    }

    [Fact]
    public async Task SaveToWorkOrder_NeedsAnOpenWorkOrder()
    {
        var (vm, _) = await CreateViewModelAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("CanCapture");
        await vm.StartCanMonitorAsync();
        vm.ToggleCanRecording();
        await PumpUntilAsync(vm, () => vm.CanRecordedFrameCount >= 10);

        Assert.Null(vm.ActiveWorkOrder);
        Assert.Null(await vm.SaveCanCaptureToWorkOrderAsync());
        Assert.Contains("Open a work order", vm.CanStatusText);
    }

    [Fact]
    public async Task SaveToWorkOrder_AttachesTheCapture_MarkedSimulated()
    {
        var (vm, data) = await CreateViewModelAsync();
        await using var _ = vm;
        await vm.CreateActiveWorkOrderAsync();
        Assert.NotNull(vm.ActiveWorkOrder);

        vm.SelectToolbarAction("CanCapture");
        await vm.StartCanMonitorAsync();
        vm.ToggleCanRecording();
        await PumpUntilAsync(vm, () => vm.CanRecordedFrameCount >= 20);
        vm.ToggleCanRecording();

        var attachment = await vm.SaveCanCaptureToWorkOrderAsync();

        Assert.NotNull(attachment);
        Assert.Equal(AttachmentKind.Recording, attachment!.Kind);
        Assert.Equal(NoteScope.WorkOrder, attachment.Scope);
        Assert.Equal(vm.ActiveWorkOrder!.Id, attachment.TargetId);
        Assert.StartsWith("SIMULATED CAN capture", attachment.Caption);
        Assert.False(Path.IsPathRooted(attachment.FilePath));
        var stored = await data.Attachments.GetByIdAsync(attachment.Id);
        Assert.NotNull(stored);
        Assert.True(File.Exists(Path.Combine(vm.GetScopeAppDataDirectory(), attachment.FilePath)));
    }
}
