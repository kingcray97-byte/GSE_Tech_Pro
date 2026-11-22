using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Models;
using GseTechPro.Devices.Camera;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Data-plate OCR (Phase 7.3). On a saved photo: optionally drag a box around the plate, tap Read text,
/// and offline OCR (Windows.Media.Ocr; the simulator for simulated photos) reads the lines. Deterministic
/// label rules (<see cref="DataPlates.Parse"/>) suggest manufacturer, model, serial, part numbers and build
/// date; the technician corrects every value, ticks "I checked", and Save stores the confirmed reading on
/// the work order's asset (<c>assets.configuration_json["data_plate"]</c>) and, for real photos, the
/// asset's serial number. The AI model never reads IDs.
/// </summary>
public sealed partial class MainShellViewModel
{
    private readonly ITextRecognizer _simulatedTextRecognizer = new SimulatedTextRecognizer();
    private TextRecognitionResult? _dataPlateResult;
    private string? _dataPlatePhotoId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataPlatePanelVisibility))]
    [NotifyPropertyChangedFor(nameof(PhotoListVisibility))]
    [NotifyPropertyChangedFor(nameof(DataPlateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(AnnotateButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(InspectPhotoButtonVisibility))]
    private bool _isReadingDataPlate;

    public Visibility DataPlatePanelVisibility => IsReadingDataPlate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PhotoListVisibility => IsReadingDataPlate ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"Read data plate" shows for a saved photo on screen, while not annotating or already reading.</summary>
    public Visibility DataPlateButtonVisibility =>
        !IsAnnotating && !IsReadingDataPlate && PendingPhoto is null && SelectedWorkOrderPhoto is { FileExists: true }
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Part of the photo to read; null = whole photo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataPlateRegionText))]
    private OcrRegion? _dataPlateRegion;

    public string DataPlateRegionText => DataPlateRegion is null
        ? "Reading the whole photo. Drag a box around the plate to read only that part."
        : "Reading the boxed part of the photo.";

    /// <summary>Raised when the region box changes, so the window can redraw it.</summary>
    public event EventHandler? DataPlateRegionChanged;

    partial void OnDataPlateRegionChanged(OcrRegion? value) => DataPlateRegionChanged?.Invoke(this, EventArgs.Empty);

    private (double X, double Y)? _regionStart;

    [ObservableProperty] private string _dataPlateRawText = "";
    [ObservableProperty] private string _dataPlateEngineText = "";
    [ObservableProperty] private string _dataPlateAssetText = "";
    [ObservableProperty] private string _dataPlateExistingText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataPlateManufacturerHint))]
    private string _dataPlateManufacturer = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataPlateModelHint))]
    private string _dataPlateModel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataPlateSerialHint))]
    [NotifyPropertyChangedFor(nameof(DataPlateSerialWarning))]
    private string _dataPlateSerial = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataPlatePartNumbersHint))]
    private string _dataPlatePartNumbers = "";

    [ObservableProperty] private string _dataPlateDate = "";

    /// <summary>The "I checked every value against the plate" tick; Save needs it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDataPlateCommand))]
    private bool _dataPlateConfirmed;

    public string DataPlateManufacturerHint => DataPlates.LookAlikeHint(DataPlateManufacturer, idField: false);
    public string DataPlateModelHint => DataPlates.LookAlikeHint(DataPlateModel);
    public string DataPlateSerialHint => DataPlates.LookAlikeHint(DataPlateSerial);
    public string DataPlatePartNumbersHint => DataPlates.LookAlikeHint(DataPlatePartNumbers.Replace('\n', ' ').Replace('\r', ' '));

    private Asset? _dataPlateAsset;

    /// <summary>Says when Save will replace the asset's serial number (or why it will not).</summary>
    public string DataPlateSerialWarning
    {
        get
        {
            var serial = DataPlates.CleanValue(DataPlateSerial);
            if (serial is null || _dataPlateAsset is null)
                return "";
            if (_dataPlateResult is { IsSimulated: true })
                return "Simulated reading: the asset's serial number will not be changed.";
            var current = DataPlates.CleanValue(_dataPlateAsset.SerialNumber);
            if (current is null)
                return "Save will set the asset's serial number.";
            return string.Equals(current, serial, StringComparison.OrdinalIgnoreCase)
                ? "Matches the asset's serial number."
                : $"Save will replace the asset's serial number {current} (the old value is kept with the reading).";
        }
    }

    private bool CanStartDataPlate() => !IsCameraBusy;

    [RelayCommand(CanExecute = nameof(CanStartDataPlate))]
    public async Task StartDataPlateReadingAsync()
    {
        if (PendingPhoto is not null)
        {
            CameraStatusText = "Save the new photo to the work order first, then read its data plate.";
            return;
        }
        if (SelectedWorkOrderPhoto is not { FileExists: true } photo)
        {
            CameraStatusText = "Pick a saved photo of the data plate.";
            return;
        }
        if (IsAnnotating)
        {
            CameraStatusText = "Save or cancel the marks first.";
            return;
        }

        ResetDataPlateFields();
        _dataPlatePhotoId = photo.Id;
        await LoadDataPlateAssetAsync();
        IsReadingDataPlate = true;
        CameraStatusText = "Drag a box around the plate (or read the whole photo), then tap Read text. " +
                           "Check every value against the plate before saving.";
    }

    private async Task LoadDataPlateAssetAsync()
    {
        _dataPlateAsset = null;
        DataPlateExistingText = "";
        if (ActiveWorkOrder is not WorkOrder wo || string.IsNullOrWhiteSpace(wo.AssetId))
        {
            DataPlateAssetText = "No work order with an asset is open: open one to save the reading.";
            return;
        }
        try
        {
            _dataPlateAsset = await _dataServices.Assets.GetByIdAsync(wo.AssetId);
        }
        catch (Exception ex)
        {
            DataPlateAssetText = $"Could not load the work order's asset: {ex.Message}";
            return;
        }
        if (_dataPlateAsset is null)
        {
            DataPlateAssetText = "The work order's asset was not found.";
            return;
        }
        var serial = DataPlates.CleanValue(_dataPlateAsset.SerialNumber) ?? "none";
        DataPlateAssetText = $"Saving to asset {_dataPlateAsset.AssetNumber} (serial on file: {serial}).";
        if (DataPlates.Read(_dataPlateAsset.ConfigurationJson) is { } existing)
            DataPlateExistingText = $"Plate on file: {DataPlates.Summary(existing)} (confirmed {FormatTimestamp(existing.ConfirmedAt) ?? existing.ConfirmedAt}).";
        OnPropertyChanged(nameof(DataPlateSerialWarning));
    }

    // ---------------------------------------------------------------- region box (drag on the photo)

    public void BeginDataPlateRegion(double x, double y)
    {
        if (!IsReadingDataPlate)
            return;
        _regionStart = (x, y);
    }

    public void ExtendDataPlateRegion(double x, double y)
    {
        if (!IsReadingDataPlate || _regionStart is not { } s)
            return;
        DataPlateRegion = OcrRegion.FromCorners(s.X, s.Y, x, y);
    }

    public void EndDataPlateRegion()
    {
        if (_regionStart is null)
            return;
        _regionStart = null;
        if (DataPlateRegion is not null)
            CameraStatusText = "Box set. Tap Read text.";
    }

    [RelayCommand]
    public void ClearDataPlateRegion()
    {
        _regionStart = null;
        DataPlateRegion = null;
    }

    // ---------------------------------------------------------------- OCR

    [RelayCommand(CanExecute = nameof(CanStartDataPlate))]
    public async Task ReadDataPlateTextAsync()
    {
        if (!IsReadingDataPlate || SelectedWorkOrderPhoto is not { FileExists: true } photo || photo.Id != _dataPlatePhotoId)
        {
            CameraStatusText = "Tap Read data plate on a saved photo first.";
            return;
        }

        IsCameraBusy = true;
        try
        {
            var recognizer = photo.IsSimulated ? _simulatedTextRecognizer : _textRecognizer;
            var bytes = await File.ReadAllBytesAsync(photo.FullPath);
            var result = await Task.Run(() => recognizer.RecognizeAsync(bytes, DataPlateRegion));
            if (!IsReadingDataPlate || SelectedWorkOrderPhoto?.Id != _dataPlatePhotoId)
                return; // the photo changed while reading

            _dataPlateResult = result;
            DataPlateRawText = string.Join(Environment.NewLine, result.Lines);
            DataPlateEngineText = $"Read by {result.EngineName}{(result.IsSimulated ? " (SIMULATED)" : "")}, offline. " +
                                  "Suggested values below: correct anything that does not match the plate.";
            ApplyCandidates(DataPlates.Parse(result.Lines));
            DataPlateConfirmed = false;
            OnPropertyChanged(nameof(DataPlateSerialWarning));
            CameraStatusText = result.Lines.Count == 0
                ? "No text found. Try a box closer around the plate, a sharper photo, or type the values from the plate."
                : $"Read {result.Lines.Count} line{(result.Lines.Count == 1 ? "" : "s")}. Check every value against the plate, tick the box, then Save to asset.";
        }
        catch (Exception ex)
        {
            CameraStatusText = $"Could not read text: {ex.Message} You can still type the values from the plate.";
        }
        finally
        {
            IsCameraBusy = false;
        }
    }

    private void ApplyCandidates(IReadOnlyList<DataPlateFieldCandidate> candidates)
    {
        string First(DataPlateFieldKind kind) => candidates.FirstOrDefault(c => c.Kind == kind)?.Value ?? "";
        DataPlateManufacturer = First(DataPlateFieldKind.Manufacturer);
        DataPlateModel = First(DataPlateFieldKind.Model);
        DataPlateSerial = First(DataPlateFieldKind.SerialNumber);
        DataPlateDate = First(DataPlateFieldKind.ManufactureDate);
        DataPlatePartNumbers = string.Join(Environment.NewLine,
            candidates.Where(c => c.Kind == DataPlateFieldKind.PartNumber).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase));

        var extraSerials = candidates.Where(c => c.Kind == DataPlateFieldKind.SerialNumber).Skip(1).Select(c => c.Value).ToList();
        if (extraSerials.Count > 0)
            DataPlateEngineText += $" Other serial-like values found: {string.Join(", ", extraSerials)}.";
    }

    // ---------------------------------------------------------------- save / cancel

    private bool CanSaveDataPlate() => !IsCameraBusy && DataPlateConfirmed;

    /// <summary>Saves the technician-confirmed values to the work order's asset.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveDataPlate))]
    public async Task<DataPlateReading?> SaveDataPlateAsync()
    {
        if (!IsReadingDataPlate)
            return null;
        if (!DataPlateConfirmed)
        {
            CameraStatusText = "Tick \"I checked every value against the plate\" first.";
            return null;
        }
        if (ActiveWorkOrder is not WorkOrder wo)
        {
            CameraStatusText = "Open a work order first. The reading is saved to its asset.";
            return null;
        }

        var parts = DataPlates.SplitPartNumbers(DataPlatePartNumbers);
        var reading = new DataPlateReading
        {
            Manufacturer = DataPlates.CleanValue(DataPlateManufacturer),
            Model = DataPlates.CleanValue(DataPlateModel),
            SerialNumber = DataPlates.CleanValue(DataPlateSerial),
            PartNumbers = parts,
            ManufactureDate = DataPlates.CleanValue(DataPlateDate),
            RawText = DataPlateRawText,
            PhotoAttachmentId = _dataPlatePhotoId,
            WorkOrderId = wo.Id,
            Engine = _dataPlateResult?.EngineName ?? "Typed by technician",
            IsSimulated = _dataPlateResult?.IsSimulated == true || SelectedWorkOrderPhoto?.IsSimulated == true,
            TechnicianConfirmed = true,
            ConfirmedAt = Clock.UtcNow,
        };
        if (reading.Manufacturer is null && reading.Model is null && reading.SerialNumber is null
            && reading.PartNumbers.Count == 0 && reading.ManufactureDate is null)
        {
            CameraStatusText = "Nothing to save: every value is empty.";
            return null;
        }

        IsCameraBusy = true;
        try
        {
            var asset = string.IsNullOrWhiteSpace(wo.AssetId) ? null : await _dataServices.Assets.GetByIdAsync(wo.AssetId);
            if (asset is null)
            {
                CameraStatusText = "The work order has no asset to save the reading to.";
                return null;
            }

            string? oldSerial = DataPlates.CleanValue(asset.SerialNumber);
            bool changeSerial = !reading.IsSimulated && reading.SerialNumber is not null
                                && !string.Equals(oldSerial, reading.SerialNumber, StringComparison.OrdinalIgnoreCase);
            if (changeSerial)
                reading = reading with { PreviousSerialNumber = oldSerial };

            string config;
            try
            {
                config = DataPlates.MergeInto(asset.ConfigurationJson, reading);
            }
            catch (FormatException ex)
            {
                CameraStatusText = $"{ex.Message} The asset was not changed.";
                return null;
            }

            var updated = asset with
            {
                ConfigurationJson = config,
                SerialNumber = changeSerial ? reading.SerialNumber : asset.SerialNumber,
                UpdatedAt = Clock.UtcNow,
            };
            if (!await _dataServices.Assets.UpdateAsync(updated))
            {
                CameraStatusText = "Could not save the reading: the asset was not found.";
                return null;
            }
            int index = Assets.ToList().FindIndex(a => a.Id == updated.Id);
            if (index >= 0)
                Assets[index] = updated;

            string serialNote = changeSerial
                ? oldSerial is null ? $" Serial number set to {reading.SerialNumber}." : $" Serial number changed from {oldSerial} to {reading.SerialNumber}."
                : "";
            EndDataPlateReading(discarded: false);
            CameraStatusText = $"Data plate saved to asset {asset.AssetNumber}: {DataPlates.Summary(reading)}.{serialNote}";
            StatusBannerMessage = CameraStatusText;
            return reading;
        }
        catch (Exception ex)
        {
            CameraStatusText = $"Could not save the reading: {ex.Message}";
            return null;
        }
        finally
        {
            IsCameraBusy = false;
        }
    }

    [RelayCommand]
    public void CancelDataPlateReading()
    {
        bool hadValues = _dataPlateResult is not null;
        EndDataPlateReading(discarded: false);
        CameraStatusText = hadValues ? "Data-plate reading cancelled; nothing was saved." : "Data-plate reading cancelled.";
    }

    private void EndDataPlateReading(bool discarded)
    {
        if (!IsReadingDataPlate)
            return;
        if (discarded && _dataPlateResult is not null)
            CameraStatusText = "The data-plate reading that was not saved was discarded.";
        IsReadingDataPlate = false;
        ResetDataPlateFields();
    }

    private void ResetDataPlateFields()
    {
        _dataPlateResult = null;
        _dataPlatePhotoId = null;
        _regionStart = null;
        DataPlateRegion = null;
        DataPlateRawText = "";
        DataPlateEngineText = "";
        DataPlateManufacturer = "";
        DataPlateModel = "";
        DataPlateSerial = "";
        DataPlatePartNumbers = "";
        DataPlateDate = "";
        DataPlateConfirmed = false;
    }
}
