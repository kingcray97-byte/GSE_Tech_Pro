using GseTechPro.Core.Models;

namespace GseTechPro.Core.Abstractions;

/// <summary>A camera the app can take photos with (Phase 7.1). The simulator is always listed first.</summary>
public sealed record CameraSourceInfo(string Id, string Name, bool IsSimulated);

/// <summary>One still photo, already encoded (JPEG from a real camera, PNG from the simulator).</summary>
public sealed record CapturedPhoto
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    /// <summary>"image/jpeg" or "image/png".</summary>
    public string MimeType { get; init; } = "image/jpeg";
    /// <summary>File extension with the dot: ".jpg" or ".png".</summary>
    public string Extension { get; init; } = ".jpg";
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>True for simulator photos: they must never be saved as if real (ADR-007 provenance).</summary>
    public bool IsSimulated { get; init; }
    public string TakenAt { get; init; } = Clock.UtcNow;
    /// <summary>Camera name, or the file name for an imported photo.</summary>
    public string SourceName { get; init; } = "";
}

/// <summary>
/// Still-photo capture (Phase 7.1). Implementations: the simulator (deterministic test card) and the
/// tablet camera (Windows <c>MediaCapture</c>, in the App). Never blocks the UI thread.
/// </summary>
public interface ICameraSource : IAsyncDisposable
{
    CameraSourceInfo Info { get; }

    /// <summary>Takes one photo.</summary>
    Task<CapturedPhoto> CapturePhotoAsync(CancellationToken cancellationToken = default);
}

/// <summary>Lists cameras (simulator first) and opens one.</summary>
public interface ICameraSourceFactory
{
    Task<IReadOnlyList<CameraSourceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<ICameraSource> OpenAsync(CameraSourceInfo info, CancellationToken cancellationToken = default);
}
