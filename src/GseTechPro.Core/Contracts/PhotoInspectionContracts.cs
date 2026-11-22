using GseTechPro.Core.Ai;
using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// Phase 7.4 photo inspection (<c>vision_inspect</c>, ARCHITECTURE §4.4): the app sends one saved photo to the local
/// AI service (<see cref="AiServiceProtocol.VisionInspectPath"/>), which asks the model to describe what it shows and
/// then searches the manuals for pages to compare. Binding rules (§4.5):
/// <list type="bullet">
/// <item>The answer is the model's <b>description</b>, always a suggestion: never a spec, a measurement or a pass/fail.</item>
/// <item>Manual pages come from the service's own search, not from model text, so a page cannot be invented.</item>
/// <item>Serial and data-plate text is never read by the model (that is Read data plate, 7.3).</item>
/// <item>A photo is a job detail: it goes to the cloud route only when the administrator allows job details there
///   (ADR-013 rule 3); otherwise the cloud is skipped for this request.</item>
/// </list>
/// </summary>
public static class PhotoInspection
{
    /// <summary>Largest photo the app sends, after its own downscale (bytes of the encoded image).</summary>
    public const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>The app downscales the long side to at most this many pixels before sending (CPU tablet, 2B model).</summary>
    public const int MaxImageLongSidePixels = 1024;

    /// <summary>Longest caption / question the technician can add.</summary>
    public const int MaxCaptionLength = 500;

    public const string MediaTypeJpeg = "image/jpeg";
    public const string MediaTypePng = "image/png";

    /// <summary>Label shown above every answer.</summary>
    public const string DescriptionLabel = "The AI's description of the photo: a suggestion, not a measurement or a pass/fail.";
}

/// <summary>POST /vision/inspect request.</summary>
public sealed record PhotoInspectionRequest
{
    /// <summary>The encoded photo (JPEG or PNG), base64, at most <see cref="PhotoInspection.MaxImageBytes"/> before encoding.</summary>
    public string ImageBase64 { get; init; } = "";

    /// <summary><see cref="PhotoInspection.MediaTypeJpeg"/> or <see cref="PhotoInspection.MediaTypePng"/>.</summary>
    public string MediaType { get; init; } = PhotoInspection.MediaTypeJpeg;

    /// <summary>What the technician wants looked at, or the photo's caption (optional).</summary>
    public string? Caption { get; init; }

    public string? WorkOrderId { get; init; }
    public string? AssetId { get; init; }

    /// <summary>The photo was taken with the simulator camera (said in the answer; never hidden).</summary>
    public bool IsSimulatedPhoto { get; init; }

    /// <summary>Route choice (ADR-013). Null = this tablet only.</summary>
    public AiRoutingOptions? Routing { get; init; }
}

/// <summary>POST /vision/inspect response.</summary>
public sealed record PhotoInspectionResponse
{
    /// <summary>The model's description of what the photo shows.</summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Notes the service adds by rule, not by the model: e.g. a number read from the photo is not a measurement,
    /// serial numbers are read with Read data plate, no manual page matched.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Manual pages to compare against, from the service's manual search.</summary>
    public IReadOnlyList<Citation> Citations { get; init; } = Array.Empty<Citation>();

    /// <summary>Always true: the description is an AI suggestion.</summary>
    public bool IsSuggestion { get; init; } = true;

    /// <summary>The words the manual search used (shown so the technician can search again).</summary>
    public string? SearchQuery { get; init; }

    /// <summary>Route that answered (ADR-013).</summary>
    public AiRoute? Route { get; init; }

    /// <summary>Model label that answered.</summary>
    public string? Model { get; init; }
}
