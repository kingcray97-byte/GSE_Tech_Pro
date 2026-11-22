using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.App.Services;
using GseTechPro.Core.Ai;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Camera tool, Phase 7.4 photo inspection (<c>vision_inspect</c>): the AI describes a saved photo and the AI service
/// suggests manual pages to compare. The answer is labelled as the AI's description (a suggestion), carries the route
/// badge (ADR-013), shows the service's rule-based notes, and never sets a reading, a spec or a pass/fail. Serial and
/// data-plate text stays with Read data plate (7.3). Nothing is saved: the technician decides what to record.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Photo file → bytes sent to the service. Tests replace it; the app shrinks to JPEG with Windows imaging.</summary>
    public Func<string, CancellationToken, Task<(byte[] Bytes, string MediaType)>> PhotoInspectionEncoder { get; set; } =
        PhotoInspectionImage.PrepareAsync;

    private CancellationTokenSource? _photoInspectionCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionPanelVisibility))]
    [NotifyPropertyChangedFor(nameof(InspectPhotoButtonVisibility))]
    private bool _isPhotoInspectionOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionBusyVisibility))]
    [NotifyCanExecuteChangedFor(nameof(InspectPhotoCommand))]
    private bool _isInspectingPhoto;

    /// <summary>What the technician wants looked at (starts as the photo's caption).</summary>
    [ObservableProperty]
    private string _photoInspectionQuestion = "";

    [ObservableProperty]
    private string _photoInspectionStatusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionDescription))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionResultVisibility))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionRouteBadgeText))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionRouteBadgeToolTip))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionRouteBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionCloudBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionPagesVisibility))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionNotesVisibility))]
    [NotifyPropertyChangedFor(nameof(PhotoInspectionSearchText))]
    private PhotoInspectionResponse? _photoInspectionResult;

    public ObservableCollection<CitationItemViewModel> PhotoInspectionPages { get; } = new();
    public ObservableCollection<string> PhotoInspectionNotes { get; } = new();

    public string PhotoInspectionLabel => PhotoInspection.DescriptionLabel;

    public Visibility PhotoInspectionPanelVisibility => IsPhotoInspectionOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PhotoInspectionBusyVisibility => IsInspectingPhoto ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PhotoInspectionResultVisibility => PhotoInspectionResult is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"Inspect photo" shows for a saved photo on screen, while not annotating, reading a plate or already open.</summary>
    public Visibility InspectPhotoButtonVisibility =>
        !IsPhotoInspectionOpen && !IsAnnotating && !IsReadingDataPlate && PendingPhoto is null
        && SelectedWorkOrderPhoto is { FileExists: true }
            ? Visibility.Visible : Visibility.Collapsed;

    public string PhotoInspectionDescription => PhotoInspectionResult switch
    {
        null => "",
        { Description.Length: > 0 } r => r.Description,
        _ => "(No description.)",
    };

    public string PhotoInspectionRouteBadgeText => AiRouting.BadgeText(PhotoInspectionResult?.Route);
    public string PhotoInspectionRouteBadgeToolTip => PhotoInspectionResult?.Model is { Length: > 0 } m
        ? $"Answered by: {PhotoInspectionRouteBadgeText} — model {m}"
        : $"Answered by: {PhotoInspectionRouteBadgeText}";
    public Visibility PhotoInspectionRouteBadgeVisibility =>
        PhotoInspectionResult is not null && PhotoInspectionResult.Route != AiRoute.Cloud ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PhotoInspectionCloudBadgeVisibility =>
        PhotoInspectionResult?.Route == AiRoute.Cloud ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PhotoInspectionPagesVisibility => PhotoInspectionPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PhotoInspectionNotesVisibility => PhotoInspectionNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PhotoInspectionSearchText => PhotoInspectionResult?.SearchQuery is { Length: > 0 } q ? $"Manual search used: \"{q}\"" : "";

    /// <summary>Opens the panel under the photo; the question starts as the photo's caption.</summary>
    [RelayCommand]
    public void OpenPhotoInspection()
    {
        if (SelectedWorkOrderPhoto is not { FileExists: true } photo)
            return;
        ClearPhotoInspectionResult();
        string caption = photo.Attachment.Caption ?? "";
        if (IsSimulatedCaption(caption))
            caption = caption[SimulatedCaptionPrefix.Length..];
        PhotoInspectionQuestion = caption.Trim();
        PhotoInspectionStatusText =
            "Say what to look at (optional), then tap Describe photo. The AI describes the photo and suggests manual pages; you measure and decide.";
        IsPhotoInspectionOpen = true;
    }

    [RelayCommand]
    public void ClosePhotoInspection()
    {
        _photoInspectionCts?.Cancel();
        IsPhotoInspectionOpen = false;
        ClearPhotoInspectionResult();
    }

    private bool CanInspectPhoto() => !IsInspectingPhoto;

    /// <summary>Sends the selected photo (shrunk, as JPEG) to <c>/vision/inspect</c> and shows the answer.</summary>
    [RelayCommand(CanExecute = nameof(CanInspectPhoto))]
    public async Task InspectPhotoAsync()
    {
        if (SelectedWorkOrderPhoto is not { FileExists: true } photo)
        {
            PhotoInspectionStatusText = "Pick a saved photo first.";
            return;
        }

        _photoInspectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _photoInspectionCts = cts;
        IsInspectingPhoto = true;
        ClearPhotoInspectionResult();
        PhotoInspectionStatusText = "The AI is looking at the photo. On the tablet this can take a minute…";
        try
        {
            var (bytes, mediaType) = await PhotoInspectionEncoder(photo.FullPath, cts.Token);
            string question = (PhotoInspectionQuestion ?? "").Trim();
            if (question.Length > PhotoInspection.MaxCaptionLength)
                question = question[..PhotoInspection.MaxCaptionLength];
            var request = new PhotoInspectionRequest
            {
                ImageBase64 = Convert.ToBase64String(bytes),
                MediaType = mediaType,
                Caption = question.Length > 0 ? question : null,
                WorkOrderId = ActiveWorkOrder?.Id,
                AssetId = ActiveWorkOrder?.AssetId,
                IsSimulatedPhoto = photo.IsSimulated,
                Routing = CurrentChatRouting,
            };
            var response = await _aiClient.InspectPhotoAsync(request, cts.Token);
            if (cts.IsCancellationRequested || SelectedWorkOrderPhoto?.Id != photo.Id)
                return; // the technician moved on

            foreach (var c in response.Citations)
                PhotoInspectionPages.Add(new CitationItemViewModel(c));
            foreach (var n in response.Notes)
                PhotoInspectionNotes.Add(n);
            PhotoInspectionResult = response;
            PhotoInspectionStatusText = response.Citations.Count > 0
                ? $"Compare the photo with the {response.Citations.Count} manual page{(response.Citations.Count == 1 ? "" : "s")} below. You make the call."
                : "No manual page was suggested. Use Manual Search if you need one.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // closed or replaced: nothing to show
        }
        catch (AiServiceException ex)
        {
            PhotoInspectionStatusText = string.IsNullOrWhiteSpace(ex.Error.Message)
                ? $"Photo inspection failed ({ex.Code})."
                : ex.Error.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            PhotoInspectionStatusText = "The AI service did not answer. Check AI status in Settings, then try again.";
        }
        catch (Exception ex)
        {
            PhotoInspectionStatusText = "Could not prepare the photo: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_photoInspectionCts, cts))
            {
                _photoInspectionCts = null;
                IsInspectingPhoto = false;
            }
            cts.Dispose();
        }
    }

    private void ClearPhotoInspectionResult()
    {
        PhotoInspectionPages.Clear();
        PhotoInspectionNotes.Clear();
        PhotoInspectionResult = null;
    }

    /// <summary>Another photo was picked or a new one taken: the old answer does not belong to it.</summary>
    private void ResetPhotoInspectionForSelection()
    {
        if (IsPhotoInspectionOpen || IsInspectingPhoto)
            ClosePhotoInspection();
        OnPropertyChanged(nameof(InspectPhotoButtonVisibility));
    }
}
