using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices.Camera;

/// <summary>
/// Simulator OCR (Phase 7.3, rule 4): returns the text of the simulator camera's drawn data plate. Every
/// value starts with "SIM" and the first line says it is not a real asset, so it can never pass as a real
/// serial or part number. The values include look-alike characters (0 / O, 1 / I) to exercise the hints.
/// The region is accepted and ignored: the simulated plate fills the photo.
/// </summary>
public sealed class SimulatedTextRecognizer : ITextRecognizer
{
    public const string EngineName = "Simulator OCR";

    public static IReadOnlyList<string> PlateLines { get; } =
    [
        "SIMULATED DATA PLATE - NOT A REAL ASSET",
        "MFG: SIMULATOR WORKS",
        "MODEL: SIM-100 S/N: SIM-0O1I7",
        "P/N SIM-PN-0001",
        "MFG DATE 2026-01",
    ];

    public Task<TextRecognitionResult> RecognizeAsync(byte[] imageBytes, OcrRegion? region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TextRecognitionResult { Lines = PlateLines, EngineName = EngineName, IsSimulated = true });
    }
}
