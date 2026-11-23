using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>A photo saved to the open work order, for the Camera page's photo list.</summary>
public sealed class WorkOrderPhotoRowViewModel(Attachment attachment, string fullPath)
{
    public Attachment Attachment { get; } = attachment;
    public string Id => Attachment.Id;
    public string FullPath { get; } = fullPath;
    public string Caption => string.IsNullOrWhiteSpace(Attachment.Caption) ? "Photo" : Attachment.Caption!;
    public bool IsSimulated => MainShellViewModel.IsSimulatedCaption(Attachment.Caption);
    public Visibility SimulatedBadgeVisibility => IsSimulated ? Visibility.Visible : Visibility.Collapsed;
    public string TakenAtDisplay =>
        DateTime.TryParse(Attachment.TakenAt ?? Attachment.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "";
    public bool FileExists => File.Exists(FullPath);

    /// <summary>The photo file for the list thumbnail (decoded small by the list), or null when the file is missing.</summary>
    public Uri? ThumbnailUri => FileExists ? new Uri(FullPath) : null;
    public Visibility ThumbnailVisibility => FileExists ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingThumbnailVisibility => FileExists ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"2 marks" when the photo has pen annotations (7.2), else "".</summary>
    public string MarksSummary => GseTechPro.Core.Contracts.PhotoAnnotations.Summary(
        GseTechPro.Core.Contracts.PhotoAnnotations.Deserialize(Attachment.AnnotationsJson).Count);
}

/// <summary>
/// Camera tool (Phase 7.1): pick a camera (simulator first), take a still photo or import one from a
/// file, add a caption, and save it to the open work order as a <c>photo</c> attachment under
/// <c>{data}\photos\{work order id}\</c>. The work order's photos are listed with a preview, and
/// photos and CAN recordings also show in Integrated Diagnostics. Simulator photos are captioned
/// "SIMULATED" (the same provenance mark as 10C CAN recordings) and never pass as real.
/// </summary>
public sealed partial class MainShellViewModel
{
    public const string SimulatedCaptionPrefix = "SIMULATED ";
    public const int MaxPhotoCaptionLength = 200;
    public const long MaxImportPhotoBytes = 40L * 1024 * 1024;
    public static IReadOnlyList<string> ImportablePhotoExtensions { get; } = [".jpg", ".jpeg", ".png"];

    public static bool IsSimulatedCaption(string? caption) =>
        caption is not null && caption.StartsWith(SimulatedCaptionPrefix, StringComparison.Ordinal);

    private ICameraSource? _openCamera;
    private readonly List<Attachment> _workOrderAttachments = new();

    /// <summary>Set by the window: shows a file picker and returns the chosen photo path, or null.</summary>
    public Func<Task<string?>>? PickPhotoFileAsync { get; set; }

    /// <summary>Set by the window: asks the technician to confirm deleting a photo. Null means no dialog (tests).</summary>
    public Func<WorkOrderPhotoRowViewModel, Task<bool>>? ConfirmDeletePhotoAsync { get; set; }

    public bool IsCameraViewVisible => ActiveSectionKey == "Camera";
    public Visibility CameraViewVisibility => IsCameraViewVisible ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<CameraSourceInfo> CameraSources { get; } = new();

    [ObservableProperty]
    private CameraSourceInfo? _selectedCameraSource;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakePhotoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportPhotoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SavePhotoToWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartDataPlateReadingCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadDataPlateTextCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveDataPlateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SavePhotoCaptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePhotoCommand))]
    private bool _isCameraBusy;

    [ObservableProperty]
    private string _cameraStatusText = "Pick a camera (the simulator works without one), then Take photo or Import.";

    [ObservableProperty]
    private string _cameraCaption = "";

    /// <summary>The photo just taken or imported, not saved yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingPhotoVisibility))]
    [NotifyPropertyChangedFor(nameof(PendingPhotoSimulatedVisibility))]
    [NotifyPropertyChangedFor(nameof(AnnotateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(DataPlateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(InspectPhotoButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(PhotoCaptionEditVisibility))]
    [NotifyCanExecuteChangedFor(nameof(SavePhotoToWorkOrderCommand))]
    private CapturedPhoto? _pendingPhoto;

    private string? _pendingPhotoPath;

    public Visibility PendingPhotoVisibility => PendingPhoto is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PendingPhotoSimulatedVisibility => PendingPhoto is { IsSimulated: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Full path of the image in the preview (pending photo, or the selected saved one). The window loads it.</summary>
    [ObservableProperty]
    private string? _cameraPreviewPath;

    [ObservableProperty]
    private string _cameraPreviewTitle = "No photo yet";

    public ObservableCollection<WorkOrderPhotoRowViewModel> WorkOrderPhotos { get; } = new();

    [ObservableProperty]
    private WorkOrderPhotoRowViewModel? _selectedWorkOrderPhoto;

    /// <summary>Caption of the selected saved photo, being edited (without the SIMULATED mark, which is kept on save).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePhotoCaptionCommand))]
    private string _editPhotoCaption = "";

    /// <summary>The caption editor shows for a saved photo while no new photo waits to be saved.</summary>
    public Visibility PhotoCaptionEditVisibility =>
        PendingPhoto is null && SelectedWorkOrderPhoto is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PhotoCaptionSimulatedVisibility =>
        SelectedWorkOrderPhoto is { IsSimulated: true } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoWorkOrderPhotosVisibility => WorkOrderPhotos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string CameraWorkOrderText => ActiveWorkOrder is { } wo
        ? $"Saving to {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo)}"
        : "No work order open: open one to save photos.";

    /// <summary>Folder for a work order's photos.</summary>
    public string GetPhotoDirectory(string workOrderId) => Path.Combine(GetScopeAppDataDirectory(), "photos", workOrderId);

    private string GetUnsavedPhotoDirectory() => Path.Combine(GetScopeAppDataDirectory(), "photos", "unsaved");

    partial void OnSelectedCameraSourceChanged(CameraSourceInfo? value) => _ = CloseCameraAsync();

    partial void OnSelectedWorkOrderPhotoChanged(WorkOrderPhotoRowViewModel? value)
    {
        LoadAnnotationsFor(value); // 7.2: the photo's saved marks (or none)
        EndDataPlateReading(discarded: true); // 7.3: a reading belongs to one photo
        EditPhotoCaption = value is null ? "" : CaptionWithoutSimulatedMark(value.Attachment.Caption);
        OnPropertyChanged(nameof(PhotoCaptionEditVisibility));
        OnPropertyChanged(nameof(PhotoCaptionSimulatedVisibility));
        DeletePhotoCommand.NotifyCanExecuteChanged();
        if (value is null)
            return;
        CameraPreviewPath = value.FileExists ? value.FullPath : null;
        CameraPreviewTitle = value.FileExists ? value.Caption : $"{value.Caption} (file missing: {value.Attachment.FilePath})";
    }

    private async Task CloseCameraAsync()
    {
        var cam = _openCamera;
        _openCamera = null;
        if (cam is not null)
            await cam.DisposeAsync();
    }

    /// <summary>Lists cameras (simulator first). Called when the Camera tool opens.</summary>
    [RelayCommand]
    public async Task RefreshCameraSourcesAsync()
    {
        var keep = SelectedCameraSource?.Id;
        IReadOnlyList<CameraSourceInfo> found;
        try
        {
            found = await _cameraSourceFactory.DiscoverAsync();
        }
        catch (Exception ex)
        {
            found = [GseTechPro.Devices.Camera.SimulatedCameraSource.SimulatorInfo];
            CameraStatusText = $"Could not list cameras ({ex.Message}); the simulator is available.";
        }
        CameraSources.Clear();
        foreach (var c in found)
            CameraSources.Add(c);
        SelectedCameraSource = CameraSources.FirstOrDefault(c => c.Id == keep) ?? CameraSources.FirstOrDefault();
        OnPropertyChanged(nameof(CameraWorkOrderText));
    }

    private bool CanUseCamera() => !IsCameraBusy;

    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    public async Task TakePhotoAsync()
    {
        if (SelectedCameraSource is not { } info)
        {
            CameraStatusText = "No camera picked.";
            return;
        }
        IsCameraBusy = true;
        try
        {
            if (_openCamera is null || _openCamera.Info.Id != info.Id)
            {
                await CloseCameraAsync();
                _openCamera = await _cameraSourceFactory.OpenAsync(info);
            }
            var photo = await _openCamera.CapturePhotoAsync();
            await SetPendingPhotoAsync(photo);
            CameraStatusText = $"Photo taken with {info.Name}{(photo.IsSimulated ? " (simulated)" : "")}. Add a caption, then Save to work order.";
        }
        catch (Exception ex)
        {
            await CloseCameraAsync();
            CameraStatusText = $"Could not take a photo with {info.Name}: {ex.Message}";
        }
        finally
        {
            IsCameraBusy = false;
        }
    }

    /// <summary>Imports a JPEG or PNG (e.g. from the Windows Camera app's Pictures folder) as a real photo.</summary>
    [RelayCommand(CanExecute = nameof(CanUseCamera))]
    public async Task ImportPhotoAsync(string? path = null)
    {
        path ??= PickPhotoFileAsync is null ? null : await PickPhotoFileAsync();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!ImportablePhotoExtensions.Contains(ext))
        {
            CameraStatusText = "Only JPEG and PNG photos can be imported.";
            return;
        }

        IsCameraBusy = true;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                CameraStatusText = $"File not found: {path}";
                return;
            }
            if (info.Length > MaxImportPhotoBytes)
            {
                CameraStatusText = $"That file is {info.Length / (1024 * 1024)} MB; photos up to {MaxImportPhotoBytes / (1024 * 1024)} MB can be imported.";
                return;
            }
            var bytes = await File.ReadAllBytesAsync(path);
            await SetPendingPhotoAsync(new CapturedPhoto
            {
                Bytes = bytes,
                MimeType = ext == ".png" ? "image/png" : "image/jpeg",
                Extension = ext == ".jpeg" ? ".jpg" : ext,
                IsSimulated = false,
                TakenAt = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                SourceName = info.Name,
            });
            CameraStatusText = $"Imported {info.Name}. Add a caption, then Save to work order.";
        }
        catch (Exception ex)
        {
            CameraStatusText = $"Could not import the photo: {ex.Message}";
        }
        finally
        {
            IsCameraBusy = false;
        }
    }

    private async Task SetPendingPhotoAsync(CapturedPhoto photo)
    {
        // Written to disk at once so the preview can show it; moved into the work order folder on Save.
        var dir = GetUnsavedPhotoDirectory();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}{photo.Extension}");
        await File.WriteAllBytesAsync(file, photo.Bytes);

        _pendingPhotoPath = file;
        PendingPhoto = photo;
        SelectedWorkOrderPhoto = null;
        CameraPreviewPath = file;
        CameraPreviewTitle = $"New photo: {photo.SourceName}{(photo.IsSimulated ? " (SIMULATED)" : "")} — not saved yet";
    }

    private bool CanSavePhoto() => !IsCameraBusy && PendingPhoto is not null;

    /// <summary>Saves the pending photo to the open work order as a <c>photo</c> attachment.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePhoto))]
    public async Task<Attachment?> SavePhotoToWorkOrderAsync()
    {
        if (PendingPhoto is not { } photo || _pendingPhotoPath is not { } pendingPath)
        {
            CameraStatusText = "Take or import a photo first.";
            return null;
        }
        if (ActiveWorkOrder is not WorkOrder wo)
        {
            CameraStatusText = "Open a work order first. Photos are saved to the open work order.";
            return null;
        }

        IsCameraBusy = true;
        try
        {
            var attachment = new Attachment
            {
                Scope = NoteScope.WorkOrder,
                TargetId = wo.Id,
                Kind = AttachmentKind.Photo,
                MimeType = photo.MimeType,
                SizeBytes = photo.Bytes.LongLength,
                TakenAt = photo.TakenAt,
                Caption = (photo.IsSimulated ? SimulatedCaptionPrefix : "") +
                          (string.IsNullOrWhiteSpace(CameraCaption) ? $"Photo ({photo.SourceName})" : CameraCaption.Trim()),
            };
            var dir = GetPhotoDirectory(wo.Id);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, attachment.Id + photo.Extension);
            File.Move(pendingPath, dest);
            attachment = attachment with { FilePath = Path.GetRelativePath(GetScopeAppDataDirectory(), dest) };

            try
            {
                await _dataServices.Attachments.AddAsync(attachment);
            }
            catch (Exception ex)
            {
                CameraStatusText = $"Saved the file to {dest} but could not attach it to the work order: {ex.Message}";
                return null;
            }

            PendingPhoto = null;
            _pendingPhotoPath = null;
            CameraCaption = "";
            await ReloadWorkOrderAttachmentsAsync(wo.Id);
            SelectedWorkOrderPhoto = WorkOrderPhotos.FirstOrDefault(p => p.Id == attachment.Id);
            CameraStatusText = $"Saved to {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo)}: {attachment.Caption}";
            StatusBannerMessage = CameraStatusText;
            return attachment;
        }
        catch (Exception ex)
        {
            CameraStatusText = $"Could not save the photo: {ex.Message}";
            return null;
        }
        finally
        {
            IsCameraBusy = false;
        }
    }

    /// <summary>The caption as the technician typed it (no SIMULATED mark).</summary>
    public static string CaptionWithoutSimulatedMark(string? caption) =>
        IsSimulatedCaption(caption) ? caption![SimulatedCaptionPrefix.Length..] : caption ?? "";

    private bool CanSavePhotoCaption() =>
        !IsCameraBusy && SelectedWorkOrderPhoto is { } p
        && !string.IsNullOrWhiteSpace(EditPhotoCaption)
        && EditPhotoCaption.Trim() != CaptionWithoutSimulatedMark(p.Attachment.Caption);

    /// <summary>Changes a saved photo's caption. A simulator photo keeps its SIMULATED mark; the photo file is not touched.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePhotoCaption))]
    public async Task<bool> SavePhotoCaptionAsync()
    {
        if (SelectedWorkOrderPhoto is not { } photo)
            return false;
        var text = EditPhotoCaption.Trim();
        if (text.Length == 0)
        {
            CameraStatusText = "Type a caption first.";
            return false;
        }
        if (text.Length > MaxPhotoCaptionLength)
            text = text[..MaxPhotoCaptionLength];
        var caption = (photo.IsSimulated ? SimulatedCaptionPrefix : "") + text;
        var updated = photo.Attachment with { Caption = caption };

        IsCameraBusy = true;
        try
        {
            if (!await _dataServices.Attachments.UpdateAsync(updated))
            {
                CameraStatusText = "Could not save the caption: the photo record was not found.";
                return false;
            }
        }
        catch (Exception ex)
        {
            CameraStatusText = $"Could not save the caption: {ex.Message}";
            return false;
        }
        finally
        {
            IsCameraBusy = false;
        }

        await ReloadWorkOrderAttachmentsAsync(photo.Attachment.TargetId); // keeps the selection, refreshes the list
        CameraStatusText = $"Caption saved: {caption}";
        return true;
    }

    private bool CanDeletePhoto() => !IsCameraBusy && SelectedWorkOrderPhoto is not null;

    /// <summary>
    /// Deletes the selected saved photo from the work order after the technician confirms. This is a soft
    /// delete (the attachment row gets <c>deleted_at</c>): the photo file stays on disk and nothing is erased
    /// (rule 10). The next photo in the list is selected, or the preview is cleared.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeletePhoto))]
    public async Task<bool> DeletePhotoAsync()
    {
        if (SelectedWorkOrderPhoto is not { } photo)
            return false;
        if (ConfirmDeletePhotoAsync is not null && !await ConfirmDeletePhotoAsync(photo))
            return false;

        int index = WorkOrderPhotos.IndexOf(photo);
        IsCameraBusy = true;
        try
        {
            // The repository's delete is a soft delete on tables with deleted_at (sets it; never removes the row).
            if (!await _dataServices.Attachments.DeleteAsync(photo.Id))
            {
                CameraStatusText = "Could not delete the photo: the photo record was not found.";
                return false;
            }
        }
        catch (Exception ex)
        {
            CameraStatusText = $"Could not delete the photo: {ex.Message}";
            return false;
        }
        finally
        {
            IsCameraBusy = false;
        }

        // Leave any tool that was working on this photo.
        IsAnnotating = false;
        if (IsPhotoInspectionOpen)
            ClosePhotoInspection();
        SelectedWorkOrderPhoto = null;
        await ReloadWorkOrderAttachmentsAsync(photo.Attachment.TargetId);
        if (WorkOrderPhotos.Count > 0)
            SelectedWorkOrderPhoto = WorkOrderPhotos[Math.Clamp(index, 0, WorkOrderPhotos.Count - 1)];
        else
        {
            CameraPreviewPath = null;
            CameraPreviewTitle = "No photo yet";
        }
        CameraStatusText = $"Deleted from the work order: {photo.Caption}";
        StatusBannerMessage = CameraStatusText;
        return true;
    }

    /// <summary>Loads the work order's attachments: photos for the Camera page, photos and recordings for Integrated Diagnostics.</summary>
    public async Task ReloadWorkOrderAttachmentsAsync(string? workOrderId = null)
    {
        var woId = workOrderId ?? ActiveWorkOrder?.Id;
        _workOrderAttachments.Clear();
        var keep = SelectedWorkOrderPhoto?.Id;
        WorkOrderPhotos.Clear();
        try
        {
            if (!string.IsNullOrWhiteSpace(woId))
            {
                var rows = await _dataServices.Attachments.FindAsync(nameof(Attachment.TargetId), woId);
                _workOrderAttachments.AddRange(rows
                    .Where(a => a.Scope == NoteScope.WorkOrder && a.DeletedAt is null)
                    .OrderByDescending(a => a.TakenAt ?? a.CreatedAt, StringComparer.Ordinal));
                var dataDir = GetScopeAppDataDirectory();
                foreach (var a in _workOrderAttachments.Where(a => a.Kind == AttachmentKind.Photo))
                    WorkOrderPhotos.Add(new WorkOrderPhotoRowViewModel(a, Path.GetFullPath(Path.Combine(dataDir, a.FilePath))));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GSE Tech Pro] ReloadWorkOrderAttachmentsAsync notice: {ex.Message}");
        }

        OnPropertyChanged(nameof(NoWorkOrderPhotosVisibility));
        OnPropertyChanged(nameof(CameraWorkOrderText));
        if (keep is not null)
            SelectedWorkOrderPhoto = WorkOrderPhotos.FirstOrDefault(p => p.Id == keep);
        RebuildWorkOrderDocument();
    }

    /// <summary>Photo and recording rows for Integrated Diagnostics.</summary>
    private IEnumerable<DiagnosticRecordRow> BuildAttachmentRecords()
    {
        foreach (var a in _workOrderAttachments)
        {
            string type = a.Kind switch
            {
                AttachmentKind.Photo => "Photo",
                AttachmentKind.Recording => "Recording",
                AttachmentKind.Scan => "Scan",
                AttachmentKind.Document => "Document",
                _ => "Attachment",
            };
            if (a.Kind == AttachmentKind.Waveform)
                continue; // waveforms already have their own rows
            string caption = string.IsNullOrWhiteSpace(a.Caption) ? type : a.Caption!;
            string size = a.SizeBytes is long b ? $" · {b / 1024.0:0} KB" : "";
            string marks = GseTechPro.Core.Contracts.PhotoAnnotations.Summary(
                GseTechPro.Core.Contracts.PhotoAnnotations.Deserialize(a.AnnotationsJson).Count);
            var preview = $"{caption}\nFile: {a.FilePath}{size}{(marks.Length > 0 ? $"\nAnnotated: {marks}" : "")}";
            var when = a.TakenAt ?? a.CreatedAt;
            yield return new DiagnosticRecordRow(
                type, "", caption, FormatTimestamp(when) ?? NotRecorded, NotRecorded, string.Empty, preview,
                IsSimulatedCaption(a.Caption), when + "|a|" + a.Id);
        }
    }
}
