using System.Runtime.InteropServices.WindowsRuntime;
using GseTechPro.Core.Contracts;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GseTechPro.App.Services;

/// <summary>
/// Phase 7.4: turns a saved photo into what the AI service is sent: JPEG, long side at most
/// <see cref="PhotoInspection.MaxImageLongSidePixels"/> (a 2B model on a CPU tablet gains nothing from more pixels),
/// EXIF orientation applied. The photo file itself is never changed. Windows imaging only (no extra library).
/// </summary>
public static class PhotoInspectionImage
{
    private const double JpegQuality = 0.85;

    public static async Task<(byte[] Bytes, string MediaType)> PrepareAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] original = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(original.AsBuffer()).AsTask(cancellationToken);
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);

        double w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        double scale = Math.Min(1.0, PhotoInspection.MaxImageLongSidePixels / Math.Max(1.0, Math.Max(w, h)));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(w * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(h * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        using var output = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(JpegQuality, Windows.Foundation.PropertyType.Single) },
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options).AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);

        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken);
        if (bytes.Length > PhotoInspection.MaxImageBytes)
            throw new InvalidOperationException("The photo is still too large to send after shrinking it.");
        return (bytes, PhotoInspection.MediaTypeJpeg);
    }
}
