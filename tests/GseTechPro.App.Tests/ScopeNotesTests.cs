using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Scope;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 10D.1i: capture notes on the scope screen (simulator).</summary>
public sealed class ScopeNotesTests
{
    [Fact]
    public async Task Notes_KeptAcrossRuns_SavedWithCapture_AndRestoredOnLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gse-scope-notes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var data = new DataServices(new DatabaseOptions { DatabasePath = Path.Combine(dir, "gse.db") });
        await data.InitializeAsync();
        await using var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), dispatcherQueue: null,
            scopeDeviceFactory: new ScopeDeviceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Scope");

        Assert.False(vm.HasScopeNotes);
        Assert.Equal($"0 / {CaptureNotes.MaxLength} characters", vm.ScopeNotesCountText);

        vm.ScopeNotes = "Engine idling, A on CKP at ECU.\r\nDropout every 4th rev.  ";
        Assert.True(vm.HasScopeNotes);

        await vm.StartScopeCaptureAsync();
        await vm.StartScopeCaptureAsync();
        Assert.StartsWith("Engine idling", vm.ScopeNotes);

        var saved = await vm.SaveScopeCaptureToWorkOrderAsync();
        Assert.Equal("Engine idling, A on CKP at ECU.\nDropout every 4th rev.", saved!.Notes);

        var persisted = await data.Waveforms.GetByIdAsync(saved.Id);
        Assert.Equal(saved.Notes, persisted!.Notes);

        var row = Assert.Single(vm.WorkOrderWaveforms);
        Assert.Equal("Engine idling, A on CKP at ECU. …", row.NotesPreview);
        Assert.Contains("Note: Engine idling", row.DisplayLabel);

        vm.ClearScopeNotes();
        Assert.False(vm.HasScopeNotes);

        await vm.LoadSavedWaveformAsync(row);
        Assert.Equal(saved.Notes, vm.ScopeNotes);

        // A capture saved with no notes clears the box when opened.
        vm.ClearScopeNotes();
        await vm.StartScopeCaptureAsync();
        var bare = await vm.SaveScopeCaptureToWorkOrderAsync();
        Assert.Null(bare!.Notes);
        vm.ScopeNotes = "typed for another hookup";
        await vm.LoadSavedWaveformAsync(vm.WorkOrderWaveforms.Single(w => w.Id == bare.Id));
        Assert.Equal("", vm.ScopeNotes);
    }
}
