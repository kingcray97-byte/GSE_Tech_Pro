using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Camera;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 7.3: data-plate OCR on a saved photo, technician confirmation, and saving to the asset.</summary>
public sealed class DataPlateTests
{
    /// <summary>Stands in for Windows OCR on real (imported) photos; records the region it was asked to read.</summary>
    private sealed class FakeRecognizer(params string[] lines) : ITextRecognizer
    {
        public OcrRegion? LastRegion { get; private set; }
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }

        public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, OcrRegion? region, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRegion = region;
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(new TextRecognitionResult { Lines = lines, EngineName = "Fake OCR" });
        }
    }

    private static async Task<(MainShellViewModel Vm, DataServices Data)> CreateAsync(ITextRecognizer recognizer)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-plate-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(),
            cameraSourceFactory: new SimulatedCameraSourceFactory(), textRecognizer: recognizer);
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Camera");
        await vm.RefreshCameraSourcesAsync();
        await vm.CreateActiveWorkOrderAsync();
        return (vm, data);
    }

    private static async Task ImportRealPhotoAsync(MainShellViewModel vm)
    {
        await using var cam = new SimulatedCameraSource();
        var png = (await cam.CapturePhotoAsync()).Bytes;
        var file = Path.Combine(Path.GetTempPath(), $"plate-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, png);
        await vm.ImportPhotoAsync(file);
        vm.CameraCaption = "Data plate";
        Assert.NotNull(await vm.SavePhotoToWorkOrderAsync());
    }

    [Fact]
    public async Task RealPhoto_Read_Corrected_Confirmed_SavedToAsset_SerialReplacedAndOldKept()
    {
        var ocr = new FakeRecognizer("ACME", "MODEL TM-1 S/N 4711-A", "P/N AB-100", "P/N 200-7", "MFG DATE 2019");
        var (vm, data) = await CreateAsync(ocr);
        await using var _ = vm;
        var assetId = vm.ActiveWorkOrder!.AssetId;
        var asset = (await data.Assets.GetByIdAsync(assetId))!;
        await data.Assets.UpdateAsync(asset with { SerialNumber = "OLD-1", ConfigurationJson = """{"keep":true}""" });

        // A new photo must be saved first.
        await vm.TakePhotoAsync();
        Assert.Equal(Visibility.Collapsed, vm.DataPlateButtonVisibility);
        // The bound button must hear about it when the saved photo becomes the selection (found in the app check).
        var seen = new List<Visibility>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.DataPlateButtonVisibility)) seen.Add(vm.DataPlateButtonVisibility);
        };
        await vm.SavePhotoToWorkOrderAsync();
        Assert.Equal(Visibility.Visible, seen.LastOrDefault(Visibility.Collapsed));
        Assert.Equal(Visibility.Visible, vm.DataPlateButtonVisibility);
        await ImportRealPhotoAsync(vm);
        var photo = vm.SelectedWorkOrderPhoto!;
        Assert.False(photo.IsSimulated);
        Assert.Equal(Visibility.Visible, vm.DataPlateButtonVisibility);

        await vm.StartDataPlateReadingAsync();
        Assert.True(vm.IsReadingDataPlate);
        Assert.Equal(Visibility.Visible, vm.DataPlatePanelVisibility);
        Assert.Equal(Visibility.Collapsed, vm.PhotoListVisibility);
        Assert.Equal(Visibility.Collapsed, vm.AnnotateButtonVisibility);
        Assert.Contains("OLD-1", vm.DataPlateAssetText);

        // Drag a box: a stray tap is ignored, a real drag sets the region passed to OCR.
        vm.BeginDataPlateRegion(0.5, 0.5);
        vm.ExtendDataPlateRegion(0.505, 0.505);
        vm.EndDataPlateRegion();
        Assert.Null(vm.DataPlateRegion);
        vm.BeginDataPlateRegion(0.6, 0.7);
        vm.ExtendDataPlateRegion(0.1, 0.2);
        vm.EndDataPlateRegion();
        Assert.Equal(0.1, vm.DataPlateRegion!.X, 6);

        await vm.ReadDataPlateTextAsync();
        Assert.Equal(1, ocr.Calls);
        Assert.Equal(vm.DataPlateRegion, ocr.LastRegion);
        Assert.Contains("MODEL TM-1 S/N 4711-A", vm.DataPlateRawText);
        Assert.Contains("Fake OCR", vm.DataPlateEngineText);
        Assert.Equal("TM-1", vm.DataPlateModel);
        Assert.Equal("4711-A", vm.DataPlateSerial);
        Assert.Equal("AB-100" + Environment.NewLine + "200-7", vm.DataPlatePartNumbers);
        Assert.Equal("2019", vm.DataPlateDate);
        Assert.Contains("replace the asset's serial number OLD-1", vm.DataPlateSerialWarning);

        // Save needs the "I checked" tick.
        Assert.False(vm.SaveDataPlateCommand.CanExecute(null));
        Assert.Null(await vm.SaveDataPlateAsync());
        Assert.Contains("Tick", vm.CameraStatusText);

        vm.DataPlateManufacturer = "Acme Works"; // technician types what OCR missed
        vm.DataPlateSerial = "4711-B";          // and corrects a misread
        Assert.Contains("8 / B", vm.DataPlateSerialHint); // mixed letters and digits: look-alikes flagged
        vm.DataPlateConfirmed = true;
        Assert.True(vm.SaveDataPlateCommand.CanExecute(null));
        var saved = await vm.SaveDataPlateAsync();

        Assert.NotNull(saved);
        Assert.False(vm.IsReadingDataPlate);
        Assert.Contains("Serial number changed from OLD-1 to 4711-B", vm.CameraStatusText);
        var after = (await data.Assets.GetByIdAsync(assetId))!;
        Assert.Equal("4711-B", after.SerialNumber);
        Assert.Contains("\"keep\":true", after.ConfigurationJson);
        var plate = DataPlates.Read(after.ConfigurationJson)!;
        Assert.Equal("Acme Works", plate.Manufacturer);
        Assert.Equal("4711-B", plate.SerialNumber);
        Assert.Equal("OLD-1", plate.PreviousSerialNumber);
        Assert.Equal(["AB-100", "200-7"], plate.PartNumbers);
        Assert.Equal(photo.Id, plate.PhotoAttachmentId);
        Assert.Equal(vm.ActiveWorkOrder!.Id, plate.WorkOrderId);
        Assert.Equal("Fake OCR", plate.Engine);
        Assert.False(plate.IsSimulated);
        Assert.True(plate.TechnicianConfirmed);
        Assert.Contains("MODEL TM-1", plate.RawText);
        var listed = vm.Assets.Single(a => a.Id == assetId);
        Assert.Equal(after.SerialNumber, listed.SerialNumber);
        Assert.Equal(after.ConfigurationJson, listed.ConfigurationJson);

        // Reopening shows the plate on file.
        await vm.StartDataPlateReadingAsync();
        Assert.Contains("Plate on file: Acme Works · Model TM-1 · S/N 4711-B", vm.DataPlateExistingText);
        vm.CancelDataPlateReading();
        Assert.False(vm.IsReadingDataPlate);
    }

    [Fact]
    public async Task SimulatedPhoto_UsesSimulatorOcr_AndNeverChangesTheAssetSerial()
    {
        var ocr = new FakeRecognizer("should not be used");
        var (vm, data) = await CreateAsync(ocr);
        await using var _ = vm;
        var assetId = vm.ActiveWorkOrder!.AssetId;
        await vm.TakePhotoAsync();
        await vm.SavePhotoToWorkOrderAsync();
        Assert.True(vm.SelectedWorkOrderPhoto!.IsSimulated);

        await vm.StartDataPlateReadingAsync();
        await vm.ReadDataPlateTextAsync();
        Assert.Equal(0, ocr.Calls);
        Assert.Contains("SIMULATED", vm.DataPlateEngineText);
        Assert.Equal("SIM-0O1I7", vm.DataPlateSerial);
        Assert.Equal("SIM-100", vm.DataPlateModel);
        Assert.Equal("SIMULATOR WORKS", vm.DataPlateManufacturer);
        Assert.Contains("0 / O", vm.DataPlateSerialHint);
        Assert.Contains("will not be changed", vm.DataPlateSerialWarning);

        vm.DataPlateConfirmed = true;
        Assert.NotNull(await vm.SaveDataPlateAsync());
        var after = (await data.Assets.GetByIdAsync(assetId))!;
        Assert.Null(after.SerialNumber);
        var plate = DataPlates.Read(after.ConfigurationJson)!;
        Assert.True(plate.IsSimulated);
        Assert.Equal("SIM-0O1I7", plate.SerialNumber);
        Assert.StartsWith("Data plate saved", vm.CameraStatusText);
        Assert.Contains("SIMULATED", vm.CameraStatusText);
    }

    [Fact]
    public async Task OcrFailure_SwitchingPhotos_EmptyValues_AndBadAssetJson_SaveNothing()
    {
        var ocr = new FakeRecognizer("S/N 12-A") { Throw = new InvalidOperationException("Windows has no OCR language installed.") };
        var (vm, data) = await CreateAsync(ocr);
        await using var _ = vm;
        var assetId = vm.ActiveWorkOrder!.AssetId;
        await ImportRealPhotoAsync(vm);
        await ImportRealPhotoAsync(vm);

        await vm.StartDataPlateReadingAsync();
        await vm.ReadDataPlateTextAsync();
        Assert.Contains("no OCR language", vm.CameraStatusText);
        Assert.Contains("type the values", vm.CameraStatusText);
        Assert.True(vm.IsReadingDataPlate); // the technician can still type the values

        // Every value empty: nothing saved.
        vm.DataPlateConfirmed = true;
        Assert.Null(await vm.SaveDataPlateAsync());
        Assert.Contains("every value is empty", vm.CameraStatusText);

        // Switching photos ends the reading without saving.
        ocr.Throw = null;
        await vm.ReadDataPlateTextAsync();
        Assert.Equal("12-A", vm.DataPlateSerial);
        vm.SelectedWorkOrderPhoto = vm.WorkOrderPhotos.First(p => p.Id != vm.SelectedWorkOrderPhoto!.Id);
        Assert.False(vm.IsReadingDataPlate);
        Assert.Contains("discarded", vm.CameraStatusText);
        Assert.Equal("", vm.DataPlateSerial);

        // Unreadable configuration JSON on the asset is never overwritten.
        var asset = (await data.Assets.GetByIdAsync(assetId))!;
        await data.Assets.UpdateAsync(asset with { ConfigurationJson = "{broken" });
        await vm.StartDataPlateReadingAsync();
        await vm.ReadDataPlateTextAsync();
        vm.DataPlateConfirmed = true;
        Assert.Null(await vm.SaveDataPlateAsync());
        Assert.Contains("not valid JSON", vm.CameraStatusText);
        var after = (await data.Assets.GetByIdAsync(assetId))!;
        Assert.Equal("{broken", after.ConfigurationJson);
        Assert.Null(after.SerialNumber);
    }
}
