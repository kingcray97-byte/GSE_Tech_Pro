using GseTechPro.Core.Abstractions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace GseTechPro.App.Services;

/// <summary>
/// Read-back with Windows' offline voices (Phase 7.5b): <see cref="SpeechSynthesizer"/> makes the audio and a
/// <see cref="MediaPlayer"/> plays it. OS provided: no bundle, no license, no network. A failure to speak is ignored
/// (the same words are always on screen).
/// </summary>
public sealed class WindowsSpeechOutput : ISpeechOutput, IDisposable
{
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Speech };
    private readonly SpeechSynthesizer _synth = new();

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken);
            _player.Pause();
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[GSE Tech Pro] speech output: {ex.Message}");
        }
    }

    public void Stop()
    {
        try { _player.Pause(); } catch { /* nothing to stop */ }
    }

    public void Dispose()
    {
        _player.Dispose();
        _synth.Dispose();
    }
}
