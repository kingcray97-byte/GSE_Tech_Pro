using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Models;
using GseTechPro.Devices.Camera;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using CapturedPhoto = GseTechPro.Core.Abstractions.CapturedPhoto;

namespace GseTechPro.App.Services;

/// <summary>
/// Cameras for the Camera tool (Phase 7.1): the simulator first, then every video-capture device
/// Windows reports (the 7220's front and rear cameras). Real photos are still JPEGs taken with
/// <see cref="MediaCapture"/>; no live preview yet. Offline: nothing here uses the network.
/// </summary>
public sealed class WindowsCameraSourceFactory : ICameraSourceFactory
{
    public async Task<IReadOnlyList<CameraSourceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<CameraSourceInfo> { SimulatedCameraSource.SimulatorInfo };
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken);
            foreach (var d in devices.Where(d => d.IsEnabled))
            {
                string where = d.EnclosureLocation?.Panel switch
                {
                    Panel.Back => " (rear)",
                    Panel.Front => " (front)",
                    _ => "",
                };
                list.Add(new CameraSourceInfo(d.Id, d.Name + where, IsSimulated: false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No camera API (or blocked by Windows privacy settings): the simulator still works.
            System.Diagnostics.Debug.WriteLine($"[GSE Tech Pro] camera discovery: {ex.Message}");
        }
        return list;
    }

    public async Task<ICameraSource> OpenAsync(CameraSourceInfo info, CancellationToken cancellationToken = default)
    {
        if (info.IsSimulated)
            return new SimulatedCameraSource();

        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = info.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.Auto,
            }).AsTask(cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new InvalidOperationException(
                "Windows blocked the camera. Turn on Settings › Privacy & security › Camera › Let desktop apps access your camera.");
        }
        catch
        {
            capture.Dispose();
            throw;
        }
        return new WindowsCameraSource(info, capture);
    }

    private sealed class WindowsCameraSource(CameraSourceInfo info, MediaCapture capture) : ICameraSource
    {
        public CameraSourceInfo Info => info;

        public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default)
        {
            using var stream = new InMemoryRandomAccessStream();
            var props = ImageEncodingProperties.CreateJpeg();
            await capture.CapturePhotoToStreamAsync(props, stream).AsTask(cancellationToken);

            var bytes = new byte[stream.Size];
            stream.Seek(0);
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
                reader.ReadBytes(bytes);
                reader.DetachStream();
            }

            return new CapturedPhoto
            {
                Bytes = bytes,
                MimeType = "image/jpeg",
                Extension = ".jpg",
                Width = (int)props.Width,
                Height = (int)props.Height,
                IsSimulated = false,
                TakenAt = Clock.UtcNow,
                SourceName = info.Name,
            };
        }

        public ValueTask DisposeAsync()
        {
            capture.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
