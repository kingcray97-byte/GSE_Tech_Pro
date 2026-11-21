using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Camera;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 7.2: pen annotation on a saved photo.</summary>
public sealed class PhotoAnnotationTests
{
    private static async Task<(MainShellViewModel Vm, DataServices Data)> CreateWithSavedPhotoAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-annotate-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(),
            cameraSourceFactory: new SimulatedCameraSourceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Camera");
        await vm.RefreshCameraSourcesAsync();
        await vm.CreateActiveWorkOrderAsync();
        await vm.TakePhotoAsync();
        Assert.Equal(Visibility.Collapsed, vm.AnnotateButtonVisibility); // a new photo is saved first
        await vm.SavePhotoToWorkOrderAsync();
        return (vm, data);
    }

    private static void Draw(MainShellViewModel vm, string tool, params (double X, double Y)[] points)
    {
        vm.SelectedAnnotationTool = tool;
        vm.BeginAnnotationStroke(points[0].X, points[0].Y);
        foreach (var p in points.Skip(1))
            vm.ExtendAnnotationStroke(p.X, p.Y);
        vm.EndAnnotationStroke();
    }

    [Fact]
    public async Task Marks_AreSavedToTheAttachment_PhotoFileUnchanged_AndReloaded()
    {
        var (vm, data) = await CreateWithSavedPhotoAsync();
        await using var _ = vm;
        var photo = vm.SelectedWorkOrderPhoto!;
        var before = await File.ReadAllBytesAsync(photo.FullPath);
        Assert.Equal(Visibility.Visible, vm.AnnotateButtonVisibility);

        // Not annotating: pointer input draws nothing.
        Draw(vm, MainShellViewModel.AnnotationToolPen, (0.1, 0.1), (0.3, 0.3));
        Assert.Equal(0, vm.AnnotationCount);

        vm.StartAnnotating();
        Assert.Equal(Visibility.Visible, vm.AnnotationToolbarVisibility);
        int redraws = 0;
        vm.AnnotationShapesChanged += (_, _) => redraws++;

        Draw(vm, MainShellViewModel.AnnotationToolPen, (0.10, 0.10), (0.15, 0.12), (0.20, 0.18), (0.30, 0.20));
        vm.SelectedAnnotationColorItem = vm.AnnotationColorItems.Single(c => c.Name == "Yellow");
        Draw(vm, MainShellViewModel.AnnotationToolArrow, (0.6, 0.6), (0.65, 0.55), (0.8, 0.4));
        Draw(vm, MainShellViewModel.AnnotationToolCircle, (0.2, 0.6), (0.4, 0.9));
        Draw(vm, MainShellViewModel.AnnotationToolArrow, (0.5, 0.5), (0.501, 0.5)); // stray tap: not a mark
        Assert.Equal(3, vm.AnnotationCount);
        Assert.True(redraws > 3);
        Assert.True(vm.HasUnsavedAnnotations);

        vm.UndoAnnotation();
        Assert.Equal(2, vm.AnnotationCount);
        Draw(vm, MainShellViewModel.AnnotationToolCircle, (0.2, 0.6), (0.4, 0.9));

        Assert.True(await vm.SaveAnnotationsAsync());
        Assert.False(vm.IsAnnotating);
        Assert.Contains("Saved 3 marks", vm.CameraStatusText);

        var stored = await data.Attachments.GetByIdAsync(photo.Id);
        var marks = PhotoAnnotations.Deserialize(stored!.AnnotationsJson);
        Assert.Equal(3, marks.Count);
        Assert.Equal(PhotoAnnotationKind.Ink, marks[0].Kind);
        Assert.Equal(4, marks[0].Points.Count);
        Assert.Equal(PhotoAnnotationKind.Arrow, marks[1].Kind);
        Assert.Equal("#FFD800", marks[1].Color);
        Assert.Equal([0.8, 0.4], marks[1].Points[1]);
        Assert.Equal(PhotoAnnotationKind.Circle, marks[2].Kind);

        // The photo file is never changed.
        Assert.Equal(before, await File.ReadAllBytesAsync(photo.FullPath));

        // Reloaded with the photo, listed and summarised in Integrated Diagnostics.
        Assert.Equal(3, vm.AnnotationCount);
        Assert.Equal("3 marks", vm.SelectedWorkOrderPhoto!.MarksSummary);
        Assert.Contains("Annotated: 3 marks", vm.DiagnosticRecords.Single(r => r.RecordType == "Photo").Preview);
    }

    [Fact]
    public async Task Cancel_ClearAndSwitching_NeverChangeSavedMarksUnlessSaved()
    {
        var (vm, data) = await CreateWithSavedPhotoAsync();
        await using var _ = vm;
        var photo = vm.SelectedWorkOrderPhoto!;

        vm.StartAnnotating();
        Draw(vm, MainShellViewModel.AnnotationToolCircle, (0.2, 0.2), (0.5, 0.5));
        await vm.SaveAnnotationsAsync();
        Assert.Equal(1, vm.AnnotationCount);

        // Clear, then Cancel: the saved mark comes back.
        vm.StartAnnotating();
        vm.ClearAnnotations();
        Assert.Equal(0, vm.AnnotationCount);
        vm.CancelAnnotating();
        Assert.False(vm.IsAnnotating);
        Assert.Equal(1, vm.AnnotationCount);

        // Unsaved marks are discarded (and said so) when another photo is picked.
        vm.StartAnnotating();
        Draw(vm, MainShellViewModel.AnnotationToolPen, (0.1, 0.1), (0.4, 0.4));
        await vm.TakePhotoAsync();
        Assert.False(vm.IsAnnotating);
        Assert.Equal(0, vm.AnnotationCount); // a new, unsaved photo has no marks
        var stored = await data.Attachments.GetByIdAsync(photo.Id);
        Assert.Single(PhotoAnnotations.Deserialize(stored!.AnnotationsJson));

        // While a new photo is pending, annotation waits for it to be saved.
        vm.SelectedWorkOrderPhoto = vm.WorkOrderPhotos.Single(p => p.Id == photo.Id);
        Assert.Equal(Visibility.Collapsed, vm.AnnotateButtonVisibility);
        vm.StartAnnotating();
        Assert.False(vm.IsAnnotating);
        Assert.Contains("Save the new photo", vm.CameraStatusText);

        // Clear + Save removes the marks: the column goes back to NULL.
        await vm.SavePhotoToWorkOrderAsync();
        vm.SelectedWorkOrderPhoto = vm.WorkOrderPhotos.Single(p => p.Id == photo.Id);
        Assert.Equal(1, vm.AnnotationCount);
        vm.StartAnnotating();
        vm.ClearAnnotations();
        Assert.True(await vm.SaveAnnotationsAsync());
        Assert.Contains("Marks removed", vm.CameraStatusText);
        Assert.Null((await data.Attachments.GetByIdAsync(photo.Id))!.AnnotationsJson);
        Assert.Equal("", vm.WorkOrderPhotos.Single(p => p.Id == photo.Id).MarksSummary);
    }
}
