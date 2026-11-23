using GseTechPro.App.ViewModels;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Camera;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 7.1: Camera tool, saving photos to the work order, and Integrated Diagnostics rows.</summary>
public sealed class CameraTests
{
    private static async Task<(MainShellViewModel Vm, DataServices Data)> CreateAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-camera-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(),
            cameraSourceFactory: new SimulatedCameraSourceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        return (vm, data);
    }

    [Fact]
    public async Task SimulatedPhoto_SavedToTheWorkOrder_ListedAndInIntegratedDiagnostics()
    {
        var (vm, data) = await CreateAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("Camera");
        Assert.Equal(Visibility.Visible, vm.CameraViewVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ToolPlaceholderVisibility);
        await vm.RefreshCameraSourcesAsync();
        Assert.True(vm.SelectedCameraSource!.IsSimulated);

        await vm.TakePhotoAsync();
        Assert.NotNull(vm.PendingPhoto);
        Assert.Equal(Visibility.Visible, vm.PendingPhotoSimulatedVisibility);
        Assert.True(File.Exists(vm.CameraPreviewPath));

        // No work order: nothing saved, and the photo is kept for when one is opened.
        Assert.Null(await vm.SavePhotoToWorkOrderAsync());
        Assert.Contains("Open a work order", vm.CameraStatusText);
        Assert.NotNull(vm.PendingPhoto);

        await vm.CreateActiveWorkOrderAsync();
        vm.CameraCaption = "Hydraulic leak at lift cylinder";
        var a = await vm.SavePhotoToWorkOrderAsync();

        Assert.NotNull(a);
        Assert.Equal(AttachmentKind.Photo, a!.Kind);
        Assert.Equal(NoteScope.WorkOrder, a.Scope);
        Assert.Equal(vm.ActiveWorkOrder!.Id, a.TargetId);
        Assert.Equal("SIMULATED Hydraulic leak at lift cylinder", a.Caption);
        Assert.Equal("image/png", a.MimeType);
        Assert.False(Path.IsPathRooted(a.FilePath));
        var full = Path.Combine(vm.GetScopeAppDataDirectory(), a.FilePath);
        Assert.True(File.Exists(full));
        Assert.StartsWith(vm.GetPhotoDirectory(a.TargetId), Path.GetFullPath(full));
        Assert.NotNull(await data.Attachments.GetByIdAsync(a.Id));

        Assert.Null(vm.PendingPhoto);
        Assert.Equal("", vm.CameraCaption);
        var row = Assert.Single(vm.WorkOrderPhotos);
        Assert.True(row.IsSimulated);
        Assert.Same(row, vm.SelectedWorkOrderPhoto);
        Assert.Equal(Path.GetFullPath(full), vm.CameraPreviewPath);

        var record = Assert.Single(vm.DiagnosticRecords, r => r.RecordType == "Photo");
        Assert.True(record.IsSimulated);
        Assert.Contains("Hydraulic leak", record.Preview);
    }

    [Fact]
    public async Task Import_TakesJpegOrPng_AsReal_AndRefusesOtherFiles()
    {
        var (vm, _) = await CreateAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("Camera");
        await vm.CreateActiveWorkOrderAsync();

        var dir = Path.Combine(Path.GetTempPath(), $"gse-camera-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var txt = Path.Combine(dir, "notes.txt");
        await File.WriteAllTextAsync(txt, "not a photo");
        await vm.ImportPhotoAsync(txt);
        Assert.Null(vm.PendingPhoto);
        Assert.Contains("Only JPEG and PNG", vm.CameraStatusText);

        var png = Path.Combine(dir, "leak.png");
        await File.WriteAllBytesAsync(png, PngEncoder.EncodeRgb(new byte[4 * 4 * 3], 4, 4));
        await vm.ImportPhotoAsync(png);
        Assert.NotNull(vm.PendingPhoto);
        Assert.False(vm.PendingPhoto!.IsSimulated);
        Assert.Equal("leak.png", vm.PendingPhoto.SourceName);

        var a = await vm.SavePhotoToWorkOrderAsync();
        Assert.Equal("Photo (leak.png)", a!.Caption); // no caption typed, and not simulated
        Assert.False(Assert.Single(vm.WorkOrderPhotos).IsSimulated);
        Assert.True(File.Exists(png)); // the original file is copied, never moved or changed
    }

    [Fact]
    public async Task SavedPhoto_CaptionCanBeEdited_KeepingTheSimulatedMark_AndListShowsAThumbnail()
    {
        var (vm, data) = await CreateAsync();
        await using var _ = vm;
        vm.SelectToolbarAction("Camera");
        await vm.RefreshCameraSourcesAsync();
        await vm.CreateActiveWorkOrderAsync();
        await vm.TakePhotoAsync();
        Assert.Equal(Visibility.Collapsed, vm.PhotoCaptionEditVisibility); // a new photo uses the Save-to-work-order caption box
        vm.CameraCaption = "Leak";
        var a = await vm.SavePhotoToWorkOrderAsync();

        var row = Assert.Single(vm.WorkOrderPhotos);
        Assert.NotNull(row.ThumbnailUri);
        Assert.Equal(Visibility.Visible, row.ThumbnailVisibility);
        Assert.Equal(Visibility.Collapsed, row.MissingThumbnailVisibility);

        Assert.Equal(Visibility.Visible, vm.PhotoCaptionEditVisibility);
        Assert.Equal(Visibility.Visible, vm.PhotoCaptionSimulatedVisibility);
        Assert.Equal("Leak", vm.EditPhotoCaption); // shown without the SIMULATED mark
        Assert.False(vm.SavePhotoCaptionCommand.CanExecute(null)); // unchanged

        vm.EditPhotoCaption = "   ";
        Assert.False(vm.SavePhotoCaptionCommand.CanExecute(null)); // blank

        vm.EditPhotoCaption = "  Hydraulic leak at lift cylinder, left side ";
        Assert.True(vm.SavePhotoCaptionCommand.CanExecute(null));
        Assert.True(await vm.SavePhotoCaptionAsync());

        var stored = await data.Attachments.GetByIdAsync(a!.Id);
        Assert.Equal("SIMULATED Hydraulic leak at lift cylinder, left side", stored!.Caption);
        Assert.Equal(a.FilePath, stored.FilePath); // the photo file is not touched
        var updated = Assert.Single(vm.WorkOrderPhotos);
        Assert.Equal(stored.Caption, updated.Caption);
        Assert.True(updated.IsSimulated);
        Assert.Same(updated, vm.SelectedWorkOrderPhoto); // selection kept
        Assert.Equal(stored.Caption, vm.CameraPreviewTitle);
        Assert.Equal("Hydraulic leak at lift cylinder, left side", vm.EditPhotoCaption);
        Assert.Contains("Hydraulic leak", Assert.Single(vm.DiagnosticRecords, r => r.RecordType == "Photo").Preview);

        // A missing file shows the placeholder instead of a thumbnail.
        File.Delete(updated.FullPath);
        await vm.ReloadWorkOrderAttachmentsAsync();
        var missing = Assert.Single(vm.WorkOrderPhotos);
        Assert.Null(missing.ThumbnailUri);
        Assert.Equal(Visibility.Visible, missing.MissingThumbnailVisibility);
    }
}
