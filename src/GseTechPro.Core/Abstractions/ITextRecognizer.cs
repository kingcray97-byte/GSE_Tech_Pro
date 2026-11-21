using GseTechPro.Core.Contracts;

namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Offline OCR of a photo or part of it (Phase 7.3, data plates). Implementations: Windows.Media.Ocr in the
/// App (OS provided, nothing bundled) and the simulator. Deterministic code only: the AI model never reads
/// serial or part numbers. Results are suggestions the technician confirms. Never blocks the UI thread.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>Reads text from an encoded image (JPEG or PNG). <paramref name="region"/> null = whole image.</summary>
    /// <exception cref="InvalidOperationException">No OCR engine or language is available (message says how to fix it).</exception>
    Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, OcrRegion? region, CancellationToken cancellationToken = default);
}
