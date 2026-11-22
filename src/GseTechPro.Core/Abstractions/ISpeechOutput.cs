namespace GseTechPro.Core.Abstractions;

/// <summary>
/// Spoken read-back and answers (Phase 7.5b/c). The app uses Windows' offline voices (no bundle, no network). Speaking is
/// a courtesy for gloved, eyes-busy work: everything spoken is also on screen, and nothing waits on audio to finish.
/// </summary>
public interface ISpeechOutput
{
    /// <summary>Starts speaking <paramref name="text"/>, replacing anything still being spoken.</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Stops speaking.</summary>
    void Stop();
}
