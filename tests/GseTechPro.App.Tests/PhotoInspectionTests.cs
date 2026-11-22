using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Ai;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Camera;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 7.4: Inspect photo on the Camera page (POST /vision/inspect) through the real HTTP client.</summary>
public sealed class PhotoInspectionTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Answers /vision/inspect with a scripted body and records what the app sent.</summary>
    private sealed class VisionHandler(Func<HttpStatusCode> status, Func<string> body) : HttpMessageHandler
    {
        public JsonObject? LastBody { get; private set; }
        public string? LastPath { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = request.RequestUri?.AbsolutePath;
            if (request.Content is not null)
                LastBody = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
            return new HttpResponseMessage(status()) { Content = new StringContent(body(), Encoding.UTF8, "application/json") };
        }
    }

    private const string Answer = """
        {
          "description": "Wet oily area on the lower hose fitting near the lift cylinder.\nLook up: hydraulic hose fitting leak",
          "notes": ["SIMULATED photo from the simulator camera: the description is a test of the workflow, not of real equipment."],
          "citations": [
            { "kind": "manual-page", "display_text": "600i Maintenance Manual, page 19", "manual_id": "m-1", "page_start": 19 },
            { "kind": "manual-page", "display_text": "600i Maintenance Manual, page 42", "manual_id": "m-1", "page_start": 42 }
          ],
          "is_suggestion": true,
          "search_query": "leak at the lift hydraulic hose fitting leak",
          "route": "tablet",
          "model": "lfm3-omni"
        }
        """;

    private static async Task<(MainShellViewModel Vm, VisionHandler Handler)> CreateAsync(
        Func<HttpStatusCode>? status = null, Func<string>? body = null)
    {
        var handler = new VisionHandler(status ?? (() => HttpStatusCode.OK), body ?? (() => Answer));
        var client = new AiServiceClient(new HttpClient(handler), Token);
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-inspect-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), aiClient: client,
            cameraSourceFactory: new SimulatedCameraSourceFactory());
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("Camera");
        await vm.RefreshCameraSourcesAsync();
        await vm.CreateActiveWorkOrderAsync();
        return (vm, handler);
    }

    private static async Task SaveSimulatedPhotoAsync(MainShellViewModel vm, string caption)
    {
        await vm.TakePhotoAsync();
        vm.CameraCaption = caption;
        Assert.NotNull(await vm.SavePhotoToWorkOrderAsync());
    }

    [Fact]
    public async Task SavedPhoto_Inspected_ShowsDescriptionPagesNotesAndRouteBadge_AndSendsTheRightRequest()
    {
        var (vm, handler) = await CreateAsync();
        await using var _ = vm;
        var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        string? encodedPath = null;
        vm.PhotoInspectionEncoder = (path, _) => { encodedPath = path; return Task.FromResult((fakeJpeg, PhotoInspection.MediaTypeJpeg)); };

        // Not offered while a new photo waits to be saved.
        await vm.TakePhotoAsync();
        Assert.Equal(Visibility.Collapsed, vm.InspectPhotoButtonVisibility);
        vm.CameraCaption = "leak at the lift";
        await vm.SavePhotoToWorkOrderAsync();
        Assert.Equal(Visibility.Visible, vm.InspectPhotoButtonVisibility);

        vm.OpenPhotoInspection();
        Assert.Equal(Visibility.Visible, vm.PhotoInspectionPanelVisibility);
        Assert.Equal(Visibility.Collapsed, vm.InspectPhotoButtonVisibility);
        Assert.Equal("leak at the lift", vm.PhotoInspectionQuestion); // SIMULATED prefix not sent as the question
        Assert.Equal(Visibility.Collapsed, vm.PhotoInspectionResultVisibility);

        await vm.InspectPhotoAsync();

        Assert.Equal(vm.SelectedWorkOrderPhoto!.FullPath, encodedPath);
        Assert.Equal(AiServiceProtocol.VisionInspectPath, handler.LastPath);
        var sent = handler.LastBody!;
        Assert.Equal(Convert.ToBase64String(fakeJpeg), (string?)sent["image_base64"]);
        Assert.Equal("image/jpeg", (string?)sent["media_type"]);
        Assert.Equal("leak at the lift", (string?)sent["caption"]);
        Assert.True((bool?)sent["is_simulated_photo"]);
        Assert.Equal(vm.ActiveWorkOrder!.Id, (string?)sent["work_order_id"]);
        Assert.Equal("tablet-only", (string?)sent["routing"]?["mode"]);

        Assert.Equal(Visibility.Visible, vm.PhotoInspectionResultVisibility);
        Assert.StartsWith("Wet oily area", vm.PhotoInspectionDescription);
        Assert.Equal(2, vm.PhotoInspectionPages.Count);
        Assert.Equal(19, vm.PhotoInspectionPages[0].PageStart);
        Assert.Single(vm.PhotoInspectionNotes);
        Assert.Equal("This tablet", vm.PhotoInspectionRouteBadgeText);
        Assert.Contains("lfm3-omni", vm.PhotoInspectionRouteBadgeToolTip);
        Assert.Equal(Visibility.Visible, vm.PhotoInspectionRouteBadgeVisibility);
        Assert.Equal(Visibility.Collapsed, vm.PhotoInspectionCloudBadgeVisibility);
        Assert.Contains("2 manual pages", vm.PhotoInspectionStatusText);
        Assert.Contains("hydraulic hose fitting", vm.PhotoInspectionSearchText);
        Assert.Contains("not a measurement or a pass/fail", vm.PhotoInspectionLabel);
        Assert.False(vm.IsInspectingPhoto);

        // Close clears the answer; the button comes back.
        vm.ClosePhotoInspection();
        Assert.Equal(Visibility.Collapsed, vm.PhotoInspectionPanelVisibility);
        Assert.Null(vm.PhotoInspectionResult);
        Assert.Empty(vm.PhotoInspectionPages);
        Assert.Equal(Visibility.Visible, vm.InspectPhotoButtonVisibility);
    }

    [Fact]
    public async Task CloudAnswer_GetsTheCloudBadge()
    {
        var (vm, _) = await CreateAsync(body: () => Answer.Replace("\"route\": \"tablet\"", "\"route\": \"cloud\""));
        await using var __ = vm;
        vm.PhotoInspectionEncoder = (_, _) => Task.FromResult((new byte[] { 0xFF, 0xD8, 0xFF }, PhotoInspection.MediaTypeJpeg));
        await SaveSimulatedPhotoAsync(vm, "hose");
        vm.OpenPhotoInspection();
        await vm.InspectPhotoAsync();
        Assert.Equal("CLOUD", vm.PhotoInspectionRouteBadgeText);
        Assert.Equal(Visibility.Visible, vm.PhotoInspectionCloudBadgeVisibility);
        Assert.Equal(Visibility.Collapsed, vm.PhotoInspectionRouteBadgeVisibility);
    }

    [Fact]
    public async Task ServiceError_ShowsItsMessage_AndNoAnswer()
    {
        const string message = "Photo inspection is unavailable: the image part of the local model is not set up on this tablet.";
        var (vm, _) = await CreateAsync(
            status: () => HttpStatusCode.ServiceUnavailable,
            body: () => JsonSerializer.Serialize(new AiErrorEnvelope
            {
                Error = new AiError { Code = AiErrorCodes.AiUnavailable, Message = message, Detail = "vision_unavailable" },
            }, AiServiceJson.Options));
        await using var __ = vm;
        vm.PhotoInspectionEncoder = (_, _) => Task.FromResult((new byte[] { 0xFF, 0xD8, 0xFF }, PhotoInspection.MediaTypeJpeg));
        await SaveSimulatedPhotoAsync(vm, "hose");
        vm.OpenPhotoInspection();
        await vm.InspectPhotoAsync();
        Assert.Equal(message, vm.PhotoInspectionStatusText);
        Assert.Null(vm.PhotoInspectionResult);
        Assert.False(vm.IsInspectingPhoto);
        Assert.True(vm.InspectPhotoCommand.CanExecute(null)); // can try again
    }

    [Fact]
    public async Task PickingAnotherPhoto_ClosesTheAnswer_AndAnnotatingHidesTheButton()
    {
        var (vm, _) = await CreateAsync();
        await using var __ = vm;
        vm.PhotoInspectionEncoder = (_, _) => Task.FromResult((new byte[] { 0xFF, 0xD8, 0xFF }, PhotoInspection.MediaTypeJpeg));
        await SaveSimulatedPhotoAsync(vm, "first");
        await SaveSimulatedPhotoAsync(vm, "second");
        vm.OpenPhotoInspection();
        await vm.InspectPhotoAsync();
        Assert.NotNull(vm.PhotoInspectionResult);

        vm.SelectedWorkOrderPhoto = vm.WorkOrderPhotos.First(p => p.Id != vm.SelectedWorkOrderPhoto!.Id);
        Assert.False(vm.IsPhotoInspectionOpen);
        Assert.Null(vm.PhotoInspectionResult);
        Assert.Equal(Visibility.Visible, vm.InspectPhotoButtonVisibility);

        vm.StartAnnotating();
        Assert.Equal(Visibility.Collapsed, vm.InspectPhotoButtonVisibility);
    }

    [Fact]
    public async Task RealEncoder_ShrinksThePhotoToAJpegOfAtMost1024Pixels()
    {
        await using var cam = new SimulatedCameraSource();
        var png = (await cam.CapturePhotoAsync()).Bytes;
        // Make a large image so shrinking is exercised: scale the simulator PNG up to 2048 px wide.
        var big = await ScaleAsync(png, 2048);
        var file = Path.Combine(Path.GetTempPath(), $"inspect-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, big);
        try
        {
            var (bytes, media) = await PhotoInspectionImage.PrepareAsync(file);
            Assert.Equal(PhotoInspection.MediaTypeJpeg, media);
            Assert.True(bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF);
            var (w, h) = await SizeAsync(bytes);
            Assert.Equal(PhotoInspection.MaxImageLongSidePixels, (int)Math.Max(w, h));
            Assert.Equal(big, await File.ReadAllBytesAsync(file)); // the photo file is never changed
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<(uint W, uint H)> SizeAsync(byte[] image)
    {
        using var s = new InMemoryRandomAccessStream();
        await s.WriteAsync(image.AsBuffer());
        s.Seek(0);
        var d = await BitmapDecoder.CreateAsync(s);
        return (d.PixelWidth, d.PixelHeight);
    }

    private static async Task<byte[]> ScaleAsync(byte[] png, uint width)
    {
        using var s = new InMemoryRandomAccessStream();
        await s.WriteAsync(png.AsBuffer());
        s.Seek(0);
        var d = await BitmapDecoder.CreateAsync(s);
        uint height = (uint)Math.Round(d.PixelHeight * (width / (double)d.PixelWidth));
        using var bmp = await d.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        using var o = new InMemoryRandomAccessStream();
        var e = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, o);
        e.SetSoftwareBitmap(bmp);
        await e.FlushAsync();
        var bytes = new byte[o.Size];
        o.Seek(0);
        await o.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
