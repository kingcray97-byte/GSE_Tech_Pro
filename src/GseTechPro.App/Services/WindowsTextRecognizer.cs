using System.Runtime.InteropServices.WindowsRuntime;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GseTechPro.App.Services;

/// <summary>
/// Data-plate OCR with Windows.Media.Ocr (Phase 7.3): provided by Windows, offline, nothing bundled and no
/// license to track. Uses the user's profile languages (English falls back to en-US). The photo region is
/// cropped and, when small, enlarged up to 2× so plate text reaches the size the engine reads well.
/// Results are suggestions: the technician confirms every value.
/// </summary>
public sealed class WindowsTextRecognizer : ITextRecognizer
{
    /// <summary>Largest side of the full decoded image we allow before cropping (keeps memory low on 8 GB).</summary>
    private const double MaxDecodedSide = 4096;
    private const double MaxUpscale = 2.0;

    public async Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, OcrRegion? region, CancellationToken cancellationToken = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine is null)
        {
            throw new InvalidOperationException(
                "Windows has no OCR language installed. In Settings > Time & language > Language & region, add English " +
                "(United States) with its optical character recognition feature, then try again.");
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(imageBytes.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

        double w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        var r = region ?? new OcrRegion(0, 0, 1, 1);
        double cropW = Math.Max(1, r.Width * w), cropH = Math.Max(1, r.Height * h);
        double maxOcr = OcrEngine.MaxImageDimension;
        double scale = Math.Min(MaxUpscale, Math.Min(maxOcr / Math.Max(cropW, cropH), MaxDecodedSide / Math.Max(w, h)));
        if (scale <= 0 || !double.IsFinite(scale))
            scale = 1;

        uint scaledW = (uint)Math.Max(1, Math.Round(w * scale)), scaledH = (uint)Math.Max(1, Math.Round(h * scale));
        uint x = (uint)Math.Clamp(Math.Round(r.X * scaledW), 0, scaledW - 1);
        uint y = (uint)Math.Clamp(Math.Round(r.Y * scaledH), 0, scaledH - 1);
        uint bw = (uint)Math.Clamp(Math.Round(r.Width * scaledW), 1, scaledW - x);
        uint bh = (uint)Math.Clamp(Math.Round(r.Height * scaledH), 1, scaledH - y);

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = x, Y = y, Width = bw, Height = bh },
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var lines = result.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return new TextRecognitionResult
        {
            Lines = lines,
            EngineName = $"Windows OCR ({engine.RecognizerLanguage.DisplayName})",
            IsSimulated = false,
        };
    }
}
